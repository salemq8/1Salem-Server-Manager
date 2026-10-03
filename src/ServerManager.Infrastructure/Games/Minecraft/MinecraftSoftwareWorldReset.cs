using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed partial class MinecraftSoftwareService
{
    /// <summary>Stops only. The Client must ask for the sole destructive confirmation AFTER this succeeds.</summary>
    public async Task<MinecraftSoftwareMigrationResult> PrepareAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        await using var lease = await operations.AcquireAsync(serverId, "Prepare server software change", cancellationToken: cancellationToken);
        var server = await GetServerAsync(serverId, cancellationToken);
        if (HasPendingMigration(server.RootPath)) return Fail("InterruptedMigration", "An interrupted software change requires attention before preparing another one.");
        var snapshot = await processes.GetSnapshotAsync(serverId, cancellationToken);
        if (!IsAlive(snapshot))
        {
            if (snapshot is null && server.State is not (ServerState.Stopped or ServerState.NotInstalled))
                return Fail("StopStateUnverified", "The server process state could not be verified. No stop or restart was attempted.");
            await servers.SetStateAsync(serverId, ServerState.Stopped, cancellationToken);
            return new(true, "StoppedForSoftwareChange", "The server is stopped. Ask for explicit world-deletion confirmation before proceeding.");
        }
        if (console.GetState(serverId) != MinecraftConsoleState.Ready)
            return Fail("GracefulControlUnavailable", "This process has no verified live command channel. Stop it safely first; preparation will not force-kill or restart it.");
        processes.ConfigureRestartPolicy(serverId, RestartPolicy.Disabled);
        try
        {
            await StopGracefullyAsync(serverId, cancellationToken);
            await servers.SetStateAsync(serverId, ServerState.Stopped, cancellationToken);
            return new(true, "StoppedForSoftwareChange", "The server stopped safely. Ask for explicit world-deletion confirmation before proceeding.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            return Fail("GracefulStopFailed", "Graceful stop was not verified. No world files were changed, and no process was force-killed or restarted.");
        }
    }

    // Invoked only while MigrateAsync owns the shared operation lease.
    private async Task<MinecraftSoftwareMigrationResult> ResetWorldAndMigrateAsync(GameServerDefinition server,
        MinecraftSoftwareMigrationRequest request, string levelName, CancellationToken cancellationToken)
    {
        if (!request.ConfirmWorldDeletion)
            return Fail("WorldDeletionConfirmationRequired", "Explicit confirmation is required after safely stopping the server. Nothing was deleted.");
        var snapshot = await processes.GetSnapshotAsync(server.Id, cancellationToken);
        if (IsAlive(snapshot) || (snapshot is null && server.State is not (ServerState.Stopped or ServerState.NotInstalled)))
            return Fail("ServerMustBeStopped", "Safely stop the server before confirming world deletion. No world files were changed.");

        string[] worldRoots;
        try
        {
            worldRoots = ResolveWorldDeletionRoots(server.RootPath, levelName);
            var existingBackups = await backups.GetExistingBackupPathsAsync(server, cancellationToken);
            ValidateWorldDeletionRoots(server.RootPath, worldRoots, existingBackups);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Fail("WorldDeletionUnsafe", "World deletion was refused: " + ex.Message); }

        var artifact = await catalog.ResolveAsync(request.TargetPlatform, request.MinecraftVersion, cancellationToken);
        if (artifact is null || artifact.Platform != request.TargetPlatform || artifact.MinecraftVersion != request.MinecraftVersion)
            return Fail("ExactVersionUnavailable", MinecraftSoftwareSafety.Message("ExactVersionUnavailable"));

        var pending = SafePathPolicy.ResolveWithinRoot(server.RootPath, ".1salem/software-migration.pending.json");
        var transactionRoot = SafePathPolicy.ResolveWithinRoot(server.RootPath, ".1salem/software-migrations/" + Guid.NewGuid().ToString("N"));
        var configs = CaptureConfiguration(server.RootPath);
        JarSwapResult? swap = null;
        string? staged = null;
        var deletionStarted = false;
        var startAttempted = false;
        processes.ConfigureRestartPolicy(server.Id, RestartPolicy.Disabled);
        try
        {
            Directory.CreateDirectory(transactionRoot);
            // A network/checksum/JAR failure must occur before any destructive action.
            staged = await catalog.DownloadAsync(artifact, transactionRoot, cancellationToken);
            MinecraftSoftwareSafety.ValidateJar(staged);
            var newHash = await Sha256Async(staged, cancellationToken);
            var configurationManifest = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var (path, bytes) in configs)
            {
                var saved = bytes is null ? null : Path.Combine(transactionRoot, $"config-{index++}.bin");
                if (saved is not null) await File.WriteAllBytesAsync(saved, bytes!, cancellationToken);
                configurationManifest[Path.GetRelativePath(server.RootPath, path)] = saved;
            }
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(new
            {
                serverId = server.Id, request.TargetPlatform, request.MinecraftVersion, worldRoots,
                confirmedWorldDeletion = true, worldRollback = false, configurationManifest, phase = "delete-confirmed-worlds"
            }, MinecraftSoftwareSafety.Json), cancellationToken);

            // Re-resolve and revalidate immediately before deletion, not just at UI discovery.
            if (!worldRoots.SequenceEqual(ResolveWorldDeletionRoots(server.RootPath, MinecraftSoftwareSafety.LevelName(server.RootPath)), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected level-name changed during preparation.");
            ValidateWorldDeletionRoots(server.RootPath, worldRoots, await backups.GetExistingBackupPathsAsync(server, cancellationToken));
            if (IsAlive(await processes.GetSnapshotAsync(server.Id, cancellationToken)))
                throw new InvalidOperationException("The server is running; world deletion is forbidden.");
            deletionStarted = true;
            foreach (var root in worldRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            await using (var input = File.OpenRead(staged))
            {
                var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken));
                swap = await swaps.SwapAsync(server.RootPath, staged, sha1, cancellationToken);
            }
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(new
            {
                serverId = server.Id, request.TargetPlatform, request.MinecraftVersion, worldRoots,
                confirmedWorldDeletion = true, worldRollback = false, configurationManifest,
                rollbackJar = swap.RollbackJarPath, phase = "start-fresh-world"
            }, MinecraftSoftwareSafety.Json), cancellationToken);
            var launch = provider.CreateSoftwareMigrationLaunchSpec(server);
            startAttempted = true;
            await processes.StartAsync(server, launch, cancellationToken);
            await VerifyStartupAsync(server.Id, cancellationToken);
            RestoreConfiguration(configs);
            await File.WriteAllTextAsync(SafePathPolicy.ResolveWithinRoot(server.RootPath, ".1salem/software.json"),
                JsonSerializer.Serialize(new MinecraftSoftwareSafety.InstalledSoftware(request.TargetPlatform,
                    request.MinecraftVersion, artifact.Build, newHash), MinecraftSoftwareSafety.Json), cancellationToken);
            await servers.UpsertAsync(server, ServerState.Running, cancellationToken);
            File.Delete(pending);
            processes.ConfigureRestartPolicy(server.Id, new RestartPolicy(server.AutoRestart, TimeSpan.FromSeconds(5), 3, TimeSpan.FromMinutes(10)));
            return new(true, "WorldResetAndMigrated", "The explicitly confirmed old world directories were deleted. New software started with a fresh world. Existing backups and server settings were preserved; no world conversion or restore occurred.", StartupVerified: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (startAttempted)
            {
                // A new runtime may already have written fresh world data. Never reverse its
                // runtime or roll back those worlds automatically after a startup failure.
                RestoreConfiguration(configs);
                await servers.SetStateWithErrorAsync(server.Id, ServerState.Error,
                    "Fresh-world software startup failed; manual attention required. No world or runtime rollback was attempted.", CancellationToken.None);
                return Fail("FreshWorldStartupFailed", $"New software startup failed ({ex.GetType().Name}). No automatic world/runtime rollback or retry was performed. Existing backups remain intact.");
            }
            var rolledBack = false;
            try
            {
                if (swap is not null) { swaps.Rollback(swap); rolledBack = true; }
                RestoreConfiguration(configs);
                await servers.SetStateAsync(server.Id, ServerState.Stopped, CancellationToken.None);
                if (File.Exists(pending)) File.Delete(pending);
                return new(false, "FreshWorldInstallationFailed", deletionStarted
                    ? "Installation failed after confirmed world deletion. Only runtime/configuration were retained or restored. The server remains stopped; deleted worlds were not restored or regenerated."
                    : "Installation failed before world deletion. Existing worlds were preserved and the server remains stopped.", RuntimeRolledBack: rolledBack);
            }
            catch (Exception recoveryError) when (recoveryError is not OutOfMemoryException)
            {
                return Fail("RecoveryRequired", "Runtime/configuration recovery needs attention. The server was not restarted and worlds were not restored.");
            }
        }
        finally
        {
            if (staged is not null && File.Exists(staged)) File.Delete(staged);
        }
    }

    public static string[] ResolveWorldDeletionRoots(string serverRoot, string levelName)
    {
        var root = Path.GetFullPath(serverRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".1salem", "backups", "backup", "config", "plugins", "mods", "libraries", "versions", "logs", "cache", "crash-reports", "resourcepacks" };
        if (string.IsNullOrWhiteSpace(levelName) || levelName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            levelName.Contains('/') || levelName.Contains('\\') || levelName.Contains(':') ||
            levelName.StartsWith('.') || levelName.EndsWith('.') || levelName.EndsWith(' ') || reserved.Contains(levelName) ||
            System.Text.RegularExpressions.Regex.IsMatch(levelName, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidDataException("The level-name is unsafe or identifies protected server state.");
        return new[] { levelName, levelName + "_nether", levelName + "_the_end" }.Select(name =>
        {
            var candidate = Path.GetFullPath(Path.Combine(root, name));
            if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase) ||
                !SafePathPolicy.IsWithinRoot(candidate, root)) throw new InvalidDataException("A world deletion target escapes the registered root.");
            return candidate;
        }).ToArray();
    }

    private static void ValidateWorldDeletionRoots(string serverRoot, string[] worldRoots, IReadOnlyList<string> backupPaths)
    {
        RejectReparseTree(serverRoot);
        foreach (var root in worldRoots)
        {
            if (File.Exists(root)) throw new InvalidDataException("A world target is a file, not a directory.");
            foreach (var backup in backupPaths)
                if (SafePathPolicy.IsWithinRoot(Path.GetFullPath(backup), root))
                    throw new InvalidDataException("An existing registered backup is inside a world deletion target; it must be preserved.");
            if (!Directory.Exists(root)) continue;
            foreach (var item in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(item);
                var extension = Path.GetExtension(name);
                if (name.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
                    new[] { ".bak", ".backup", ".zip", ".7z", ".tar", ".tgz" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("A possible existing backup/archive is inside a world deletion target; deletion is refused to preserve it.");
            }
        }
    }
}
