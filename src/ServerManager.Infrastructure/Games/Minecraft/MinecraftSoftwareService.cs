using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed record MinecraftSoftwareTimeouts(TimeSpan Startup, TimeSpan Stop, TimeSpan Poll)
{
    public static MinecraftSoftwareTimeouts Default { get; } = new(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(500));
}

/// <summary>Serialized, same-version software changes with a separate confirmed fresh-world path. World rollback is never invoked.</summary>
public sealed partial class MinecraftSoftwareService(
    IGameServerStore servers, IServerOperationCoordinator operations,
    IMinecraftSoftwareCatalog catalog, IMinecraftSoftwareBackup backups,
    MinecraftJarSwapService swaps, MinecraftServerProvider provider,
    IProcessSupervisor processes, IMinecraftConsoleChannel console,
    MinecraftSoftwareTimeouts? timeouts = null)
{
    private readonly MinecraftSoftwareTimeouts _timeouts = timeouts ?? MinecraftSoftwareTimeouts.Default;
    private static readonly ServerPlatform[] Platforms =
        [ServerPlatform.Vanilla, ServerPlatform.Paper, ServerPlatform.Purpur, ServerPlatform.Spigot, ServerPlatform.Folia];

    public async Task<MinecraftSoftwareStatus> GetAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var current = MinecraftSoftwareSafety.Inspect(server.RootPath, server.InstalledVersion);
        var level = MinecraftSoftwareSafety.LevelName(server.RootPath);
        var options = await Task.WhenAll(Platforms.Select(async platform =>
        {
            var version = current.Version ?? "";
            var reset = MinecraftSoftwareSafety.RequiresWorldReset(current.Platform, platform);
            var code = HasPendingMigration(server.RootPath) ? "InterruptedMigration" :
                reset ? null : MinecraftSoftwareSafety.MigrationBlock(current.Platform, platform, version, server.RootPath, level);
            if (code is null)
            {
                try
                {
                    code = string.IsNullOrWhiteSpace(version) || await catalog.ResolveAsync(platform, version, cancellationToken) is null
                        ? "ExactVersionUnavailable" : reset ? "WorldResetRequired" : "Ready";
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException)
                { code = "ProviderUnavailable"; }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { code = "ProviderUnavailable"; }
            }
            return new MinecraftSoftwareOption(platform, version, code is "Ready" or "WorldResetRequired", code, MinecraftSoftwareSafety.Message(code), reset);
        }));
        return new(serverId, current.Platform, current.Version, level, options);
    }

    public static bool HasPendingMigration(string root) => File.Exists(Path.Combine(root, ".1salem", "software-migration.pending.json"));

    public async Task<MinecraftSoftwareMigrationResult> MigrateAsync(Guid serverId,
        MinecraftSoftwareMigrationRequest request, CancellationToken cancellationToken = default)
    {
        await using var lease = await operations.AcquireAsync(serverId, "Server software migration", cancellationToken: cancellationToken);
        var server = await GetServerAsync(serverId, cancellationToken);
        RejectReparseTree(server.RootPath);
        if (HasPendingMigration(server.RootPath)) return Fail("InterruptedMigration", "An interrupted migration requires runtime/configuration recovery before another migration. No world restore is performed automatically.");
        var current = MinecraftSoftwareSafety.Inspect(server.RootPath, server.InstalledVersion);
        if (string.IsNullOrWhiteSpace(current.Version) || request.MinecraftVersion != current.Version)
            return Fail("VersionChangeRejected", "Software migration must keep the exact installed Minecraft version; upgrades and downgrades are not allowed here.");
        var level = MinecraftSoftwareSafety.LevelName(server.RootPath);
        if (MinecraftSoftwareSafety.RequiresWorldReset(current.Platform, request.TargetPlatform))
            return await ResetWorldAndMigrateAsync(server, request, level, cancellationToken);
        var blocked = MinecraftSoftwareSafety.MigrationBlock(current.Platform, request.TargetPlatform, current.Version, server.RootPath, level);
        if (blocked is not null) return Fail(blocked, MinecraftSoftwareSafety.Message(blocked));
        var artifact = await catalog.ResolveAsync(request.TargetPlatform, current.Version, cancellationToken);
        if (artifact is null || artifact.Platform != request.TargetPlatform || artifact.MinecraftVersion != current.Version)
            return Fail("ExactVersionUnavailable", MinecraftSoftwareSafety.Message("ExactVersionUnavailable"));
        var previous = await processes.GetSnapshotAsync(serverId, cancellationToken);
        var wasRunning = IsAlive(previous);
        if (wasRunning && console.GetState(serverId) != MinecraftConsoleState.Ready)
            return Fail("GracefulControlUnavailable", "This process has no verified live command channel. Stop it safely before migration; it will not be force-killed.");

        BackupResult? backup = null;
        JarSwapResult? swap = null;
        var staged = "";
        var pending = SafePathPolicy.ResolveWithinRoot(server.RootPath, ".1salem/software-migration.pending.json");
        var marker = SafePathPolicy.ResolveWithinRoot(server.RootPath, ".1salem/software.json");
        var configs = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var touched = false;
        var restored = false;
        processes.ConfigureRestartPolicy(serverId, RestartPolicy.Disabled);
        try
        {
            touched = true;
            if (wasRunning) await StopGracefullyAsync(serverId, cancellationToken);
            if (IsAlive(await processes.GetSnapshotAsync(serverId, cancellationToken))) throw new IOException("The server is still running.");
            await servers.SetStateAsync(serverId, ServerState.Updating, cancellationToken);
            // Use the normal verified/protected backup pipeline under the SAME operation lease.
            backup = await backups.CreateAsync(server, cancellationToken);
            var transactionRoot = SafePathPolicy.ResolveWithinRoot(server.RootPath, ".1salem/software-migrations/" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(transactionRoot);
            configs = CaptureConfiguration(server.RootPath);
            var configurationManifest = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var (path, bytes) in configs)
            {
                var saved = bytes is null ? null : Path.Combine(transactionRoot, $"config-{index++}.bin");
                if (saved is not null) await File.WriteAllBytesAsync(saved, bytes!, cancellationToken);
                configurationManifest[Path.GetRelativePath(server.RootPath, path)] = saved;
            }
            var originalHash = await Sha256Async(Path.Combine(server.RootPath, "server.jar"), cancellationToken);
            staged = await catalog.DownloadAsync(artifact, transactionRoot, cancellationToken);
            MinecraftSoftwareSafety.ValidateJar(staged);
            var newHash = await Sha256Async(staged, cancellationToken);
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(new
            {
                serverId, previousPlatform = current.Platform, previousVersion = current.Version,
                targetPlatform = request.TargetPlatform, originalSha256 = originalHash, targetSha256 = newHash,
                backupId = backup.BackupId, configurationManifest, phase = "staged", worldRollback = false
            }, MinecraftSoftwareSafety.Json), cancellationToken);
            await using (var input = File.OpenRead(staged))
            {
                var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken));
                swap = await swaps.SwapAsync(server.RootPath, staged, sha1, cancellationToken);
            }
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(new
            {
                serverId, previousPlatform = current.Platform, previousVersion = current.Version,
                targetPlatform = request.TargetPlatform, originalSha256 = originalHash, targetSha256 = newHash,
                backupId = backup.BackupId, configurationManifest, rollbackJar = swap.RollbackJarPath,
                phase = "verifying", worldRollback = false
            }, MinecraftSoftwareSafety.Json), cancellationToken);
            // Ready comes from this new run's real "Done" console event, not an open port or
            // a stale process snapshot. Always start to verify, even if previously stopped.
            await processes.StartAsync(server, provider.CreateSoftwareMigrationLaunchSpec(server), cancellationToken);
            await VerifyStartupAsync(serverId, cancellationToken);
            if (!wasRunning) await StopGracefullyAsync(serverId, cancellationToken);
            // Installation never changes the existing configuration. Keep the new runtime's
            // normal additions; snapshots are for failed-install recovery, not successful startup.
            // In particular, a running runtime can hold server.properties open against writes.
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new MinecraftSoftwareSafety.InstalledSoftware(
                request.TargetPlatform, current.Version, artifact.Build, newHash), MinecraftSoftwareSafety.Json), cancellationToken);
            await servers.UpsertAsync(server with { LastBackupAtUtc = backup.CreatedAtUtc },
                wasRunning ? ServerState.Running : ServerState.Stopped, cancellationToken);
            File.Delete(pending);
            return new(true, "Migrated", "Runtime changed and startup verified. Exact world paths were retained; no world restore was performed.", backup.BackupId, StartupVerified: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Request cancellation must not cancel recovery after a stopped server or swap.
            using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try
            {
                if (swap is not null)
                {
                    if (IsAlive(await processes.GetSnapshotAsync(serverId, recovery.Token)))
                        await StopGracefullyAsync(serverId, recovery.Token);
                    swaps.Rollback(swap);
                    RestoreConfiguration(configs);
                    restored = true;
                }
                if (touched)
                {
                    if (wasRunning && !IsAlive(await processes.GetSnapshotAsync(serverId, recovery.Token)))
                    {
                        await processes.StartAsync(server, provider.CreateSoftwareMigrationLaunchSpec(server), recovery.Token);
                        await VerifyStartupAsync(serverId, recovery.Token);
                    }
                    await servers.UpsertAsync(server, wasRunning ? ServerState.Running : ServerState.Stopped, recovery.Token);
                }
                if (File.Exists(pending)) File.Delete(pending);
            }
            catch (Exception recoveryError) when (recoveryError is not OutOfMemoryException)
            {
                await servers.SetStateWithErrorAsync(serverId, ServerState.Error,
                    "Software migration recovery needs attention. World data was not restored.", CancellationToken.None);
                return new(false, "RecoveryRequired", $"Migration failed ({ex.GetType().Name}); runtime recovery did not complete ({recoveryError.GetType().Name}). Keep the protected backup and migration journal; no world rollback occurred.", backup?.BackupId, restored);
            }
            return new(false, "MigrationFailed", $"Migration failed ({ex.GetType().Name}). Previous runtime/configuration retained or restored; world data was not rolled back.", backup?.BackupId, restored);
        }
        finally
        {
            if (!string.IsNullOrEmpty(staged) && File.Exists(staged)) File.Delete(staged);
            if (!HasPendingMigration(server.RootPath)) processes.ConfigureRestartPolicy(serverId,
                new RestartPolicy(server.AutoRestart, TimeSpan.FromSeconds(5), 3, TimeSpan.FromMinutes(10)));
        }
    }

    private async Task StopGracefullyAsync(Guid serverId, CancellationToken cancellationToken)
    {
        if (!IsAlive(await processes.GetSnapshotAsync(serverId, cancellationToken))) return;
        if (console.GetState(serverId) != MinecraftConsoleState.Ready)
            throw new InvalidOperationException("No verified graceful command channel; refusing to kill the process or replace its runtime.");
        _ = await console.ExchangeAsync(serverId, "stop", line => line.Contains("Stopping server", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10), cancellationToken);
        var watch = Stopwatch.StartNew();
        while (IsAlive(await processes.GetSnapshotAsync(serverId, cancellationToken)))
        {
            if (watch.Elapsed > _timeouts.Stop) throw new TimeoutException("Graceful shutdown did not complete; the process was not force-killed.");
            await Task.Delay(_timeouts.Poll, cancellationToken);
        }
    }

    private async Task VerifyStartupAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (console.GetState(serverId) != MinecraftConsoleState.Ready)
        {
            if (!IsAlive(await processes.GetSnapshotAsync(serverId, cancellationToken))) throw new IOException("The new runtime exited before becoming ready.");
            if (watch.Elapsed > _timeouts.Startup) throw new TimeoutException("The new runtime did not report startup readiness.");
            await Task.Delay(_timeouts.Poll, cancellationToken);
        }
        if (!IsAlive(await processes.GetSnapshotAsync(serverId, cancellationToken))) throw new IOException("The runtime exited during verification.");
    }

    private static bool IsAlive(ProcessSnapshot? snapshot) => snapshot is not null && snapshot.ExitCode is null &&
        snapshot.State is ServerState.Running or ServerState.Starting or ServerState.Stopping or ServerState.Restarting;

    private static Dictionary<string, byte[]?> CaptureConfiguration(string root)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "server.properties", "whitelist.json", "ops.json", "banned-players.json", "banned-ips.json",
                     "bukkit.yml", "spigot.yml", "paper.yml", "purpur.yml", "permissions.yml", "commands.yml", ".1salem/software.json", ".1salem/metadata.json" })
            paths.Add(SafePathPolicy.ResolveWithinRoot(root, name));
        var config = Path.Combine(root, "config");
        if (Directory.Exists(config))
            foreach (var path in Directory.EnumerateFiles(config, "*", SearchOption.AllDirectories)) paths.Add(path);
        return paths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null, StringComparer.OrdinalIgnoreCase);
    }

    private static void RestoreConfiguration(Dictionary<string, byte[]?> files)
    {
        foreach (var (path, bytes) in files)
        {
            if (bytes is null) { if (File.Exists(path)) File.Delete(path); }
            else File.WriteAllBytes(path, bytes);
        }
    }

    private static void RejectReparseTree(string root)
    {
        var full = new DirectoryInfo(Path.GetFullPath(root));
        for (var parent = full; parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Server paths cannot be reparse points.");
        var pending = new Stack<DirectoryInfo>();
        pending.Push(full);
        while (pending.TryPop(out var directory))
        {
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Server paths cannot contain reparse points.");
                if (item is DirectoryInfo child) pending.Push(child);
            }
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private async Task<GameServerDefinition> GetServerAsync(Guid id, CancellationToken cancellationToken)
    {
        var server = await servers.GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException("The server is not registered.");
        if (server.Game != GameType.Minecraft) throw new ArgumentException("Server software management is only available for Minecraft.");
        return server;
    }

    private static MinecraftSoftwareMigrationResult Fail(string code, string message) => new(false, code, message);
}
