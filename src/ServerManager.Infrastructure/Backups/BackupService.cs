using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Backups;

public sealed class BackupService(
    IGameServerStore gameServerStore,
    IBackupStore backupStore,
    IProcessSupervisor processSupervisor,
    IEnumerable<IGameServerProvider> providers,
    PalworldRestClient? palworldRestClient = null,
    IAuditLogStore? auditLogStore = null) : IBackupService
{
    private readonly IReadOnlyDictionary<GameType, IGameServerProvider> _providers =
        providers.ToDictionary(provider => provider.Game);

    public async Task<BackupResult> CreateAsync(
        BackupRequest request,
        CancellationToken cancellationToken = default)
    {
        var server = await gameServerStore.GetAsync(request.ServerId, cancellationToken)
            ?? throw new KeyNotFoundException($"Server {request.ServerId} is not registered.");
        var provider = GetProvider(server.Game);
        var snapshot = await processSupervisor.GetSnapshotAsync(server.Id, cancellationToken);
        var wasRunning = snapshot is { State: ServerState.Running };
        var estimatedBytes = EstimateBackupBytes(
            server,
            request.IncludeLogs);
        var destination = BackupDestinationPolicy.Validate(
            server,
            request.DestinationRoot,
            estimatedBytes + BackupDestinationPolicy.MinimumFreeSpaceBytes);
        if (!destination.IsValid)
        {
            throw new InvalidOperationException(
                $"{destination.Code}: {destination.Message}");
        }

        request = request with { DestinationRoot = destination.ResolvedPath };
        if (server.Game == GameType.Palworld && wasRunning)
        {
            if (palworldRestClient is null)
            {
                throw new InvalidOperationException(
                    "Palworld Save World is unavailable; the backup was not started.");
            }

            var saved = await palworldRestClient.SaveWorldAsync(
                server,
                cancellationToken);
            if (!saved.Success)
            {
                throw new InvalidOperationException(
                    $"{saved.Code}: {saved.Message}");
            }

            await WaitForPalworldSaveFilesToSettleAsync(
                server.RootPath,
                cancellationToken);
        }

        if (request.SafeOffline && wasRunning)
        {
            var stopped = await processSupervisor.StopAsync(server.Id, false, cancellationToken);
            if (!stopped.Success)
            {
                throw new InvalidOperationException(stopped.Message);
            }

            await provider.CleanupAfterStopAsync(server, cancellationToken);
        }

        await gameServerStore.SetStateAsync(server.Id, ServerState.BackingUp, cancellationToken);
        try
        {
            var result = await CreateArchiveAsync(server, request, cancellationToken);
            var verification = await VerifyAsync(result.BackupId, cancellationToken);
            if (!verification.IsValid)
            {
                var record = await backupStore.GetAsync(
                    result.BackupId,
                    cancellationToken);
                if (record is not null)
                {
                    await backupStore.UpsertAsync(
                        record with { Status = BackupStatus.Corrupt },
                        cancellationToken);
                }

                throw new InvalidDataException(
                    verification.Error ??
                    "The newly created backup did not pass verification.");
            }

            if (request.SafeOffline && wasRunning)
            {
                await provider.PrepareForStartAsync(server, cancellationToken);
                await processSupervisor.StartAsync(
                    server,
                    provider.CreateLaunchSpec(server),
                    cancellationToken);
            }

            await gameServerStore.UpsertAsync(
                server with { LastBackupAtUtc = result.CreatedAtUtc },
                wasRunning ? ServerState.Running : ServerState.Stopped,
                cancellationToken);
            if (auditLogStore is not null)
            {
                await auditLogStore.WriteAsync(
                    request.IsScheduled ? "Scheduler" : "LocalAdministrator",
                    "BackupCreated",
                    server.Id.ToString(),
                    true,
                    $"BackupId={result.BackupId}; Verified=True; File={Path.GetFileName(result.ArchivePath)}",
                    cancellationToken);
            }

            return result;
        }
        catch (Exception exception)
        {
            if (auditLogStore is not null)
            {
                await auditLogStore.WriteAsync(
                    request.IsScheduled ? "Scheduler" : "LocalAdministrator",
                    "BackupCreated",
                    server.Id.ToString(),
                    false,
                    exception.Message,
                    cancellationToken);
            }

            await gameServerStore.SetStateWithErrorAsync(
                server.Id,
                ServerState.Error,
                exception.Message,
                cancellationToken);
            if (request.SafeOffline && wasRunning &&
                await processSupervisor.GetSnapshotAsync(server.Id, cancellationToken) is null)
            {
                try
                {
                    await provider.PrepareForStartAsync(server, cancellationToken);
                    await processSupervisor.StartAsync(
                        server,
                        provider.CreateLaunchSpec(server),
                        cancellationToken);
                }
                catch
                {
                    // Preserve the backup exception; process diagnostics record restart failure.
                }
            }

            throw;
        }
    }

    public async Task<OperationResult> RestoreAsync(
        Guid backupId,
        CancellationToken cancellationToken = default)
    {
        var backup = await backupStore.GetAsync(backupId, cancellationToken);
        if (backup is null)
        {
            return OperationResult.Fail("BackupNotFound", "The selected backup is not registered.");
        }

        var verification = await VerifyRecordAsync(backup, cancellationToken);
        if (!verification.IsValid || verification.Manifest is null)
        {
            return OperationResult.Fail(
                "BackupCorrupt",
                verification.Error ?? "Backup verification failed.");
        }

        var server = await gameServerStore.GetAsync(backup.ServerId, cancellationToken);
        if (server is null)
        {
            return OperationResult.Fail("ServerNotFound", "The backup server is not registered.");
        }

        var provider = GetProvider(server.Game);
        var wasRunning = await processSupervisor.GetSnapshotAsync(server.Id, cancellationToken)
            is { State: ServerState.Running };
        if (wasRunning)
        {
            var stopped = await processSupervisor.StopAsync(server.Id, false, cancellationToken);
            if (!stopped.Success)
            {
                return stopped;
            }

            await provider.CleanupAfterStopAsync(server, cancellationToken);
        }

        await gameServerStore.SetStateAsync(server.Id, ServerState.Restoring, cancellationToken);
        await CreateArchiveAsync(
            server,
            new BackupRequest(
                server.Id,
                Path.Combine(
                    Path.GetDirectoryName(backup.ArchivePath)!,
                    "Safety"),
                false,
                false),
            cancellationToken);

        var parent = Directory.GetParent(server.RootPath)!;
        var staging = Path.Combine(parent.FullName, $".1salem-restore-staging-{Guid.NewGuid():N}");
        var rollback = Path.Combine(
            server.RootPath,
            ".1salem",
            $"restore-rollback-{Guid.NewGuid():N}");
        try
        {
            ExtractVerified(backup.ArchivePath, staging);
            Directory.CreateDirectory(rollback);
            foreach (var includedPath in verification.Manifest.IncludedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = SafePathPolicy.ResolveWithinRoot(server.RootPath, includedPath);
                var staged = SafePathPolicy.ResolveWithinRoot(staging, includedPath);
                var previous = SafePathPolicy.ResolveWithinRoot(rollback, includedPath);
                if (File.Exists(current))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                    File.Move(current, previous, false);
                }
                else if (Directory.Exists(current))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                    Directory.Move(current, previous);
                }

                if (File.Exists(staged))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(current)!);
                    File.Move(staged, current, false);
                }
                else if (Directory.Exists(staged))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(current)!);
                    Directory.Move(staged, current);
                }
            }

            if (wasRunning)
            {
                await provider.PrepareForStartAsync(server, cancellationToken);
                await processSupervisor.StartAsync(
                    server,
                    provider.CreateLaunchSpec(server),
                    cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                if (await processSupervisor.GetSnapshotAsync(server.Id, cancellationToken)
                    is not { State: ServerState.Running })
                {
                    throw new InvalidOperationException(
                        "The server did not remain running after restore.");
                }
            }

            Directory.Delete(rollback, true);
            await gameServerStore.SetStateAsync(
                server.Id,
                wasRunning ? ServerState.Running : ServerState.Stopped,
                cancellationToken);
            if (auditLogStore is not null)
            {
                await auditLogStore.WriteAsync(
                    "LocalAdministrator",
                    "BackupRestored",
                    server.Id.ToString(),
                    true,
                    $"BackupId={backup.Id}; Verified=True",
                    cancellationToken);
            }

            return OperationResult.Ok();
        }
        catch (Exception exception)
        {
            await RollbackRestoreAsync(
                server,
                verification.Manifest.IncludedPaths,
                rollback,
                provider,
                wasRunning,
                cancellationToken);
            await gameServerStore.SetStateAsync(server.Id, ServerState.Error, cancellationToken);
            if (auditLogStore is not null)
            {
                await auditLogStore.WriteAsync(
                    "LocalAdministrator",
                    "BackupRestored",
                    server.Id.ToString(),
                    false,
                    exception.Message,
                    cancellationToken);
            }

            return OperationResult.Fail("RestoreFailed", exception.Message);
        }
        finally
        {
            DeleteOwnedDirectory(staging, parent.FullName, ".1salem-restore-staging-");
        }
    }

    public async Task<BackupVerificationResult> VerifyAsync(
        Guid backupId,
        CancellationToken cancellationToken = default)
    {
        var backup = await backupStore.GetAsync(backupId, cancellationToken);
        return backup is null
            ? new BackupVerificationResult(false, "The backup is not registered.", null)
            : await VerifyRecordAsync(backup, cancellationToken);
    }

    public Task<IReadOnlyList<BackupRecord>> ListAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        backupStore.ListAsync(serverId, cancellationToken);

    public async Task<OperationResult> DeleteAsync(
        Guid backupId,
        CancellationToken cancellationToken = default)
    {
        var backup = await backupStore.GetAsync(backupId, cancellationToken);
        if (backup is null)
        {
            return OperationResult.Fail("BackupNotFound", "The selected backup is not registered.");
        }

        if (backup.IsProtected)
        {
            return OperationResult.Fail(
                "BackupProtected",
                "Unprotect this backup before deleting it.");
        }

        if (File.Exists(backup.ArchivePath))
        {
            File.Delete(backup.ArchivePath);
        }

        var hashPath = $"{backup.ArchivePath}.sha256";
        if (File.Exists(hashPath))
        {
            File.Delete(hashPath);
        }

        await backupStore.DeleteAsync(backupId, cancellationToken);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> UpdateMetadataAsync(
        Guid backupId,
        string? displayName,
        string? notes,
        bool isProtected,
        CancellationToken cancellationToken = default)
    {
        var backup = await backupStore.GetAsync(backupId, cancellationToken);
        if (backup is null)
        {
            return OperationResult.Fail(
                "BackupNotFound",
                "The selected backup is not registered.");
        }

        displayName = string.IsNullOrWhiteSpace(displayName)
            ? null
            : displayName.Trim();
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (displayName?.Length > 120 || notes?.Length > 1000)
        {
            return OperationResult.Fail(
                "BackupMetadataInvalid",
                "Backup names are limited to 120 characters and notes to 1000 characters.");
        }

        await backupStore.UpsertAsync(
            backup with
            {
                DisplayName = displayName,
                Notes = notes,
                IsProtected = isProtected
            },
            cancellationToken);
        return OperationResult.Ok();
    }

    private async Task<BackupResult> CreateArchiveAsync(
        GameServerDefinition server,
        BackupRequest request,
        CancellationToken cancellationToken)
    {
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);
        Directory.CreateDirectory(destinationRoot);
        var backupId = Guid.NewGuid();
        var version = SanitizeFileName(server.InstalledVersion ?? "unknown");
        var archivePath = Path.Combine(
            destinationRoot,
            $"{server.Game}-{DateTimeOffset.UtcNow:yyyy-MM-dd_HH-mm-ss}-{version}-{backupId:N}.zip");
        var temporaryPath = $"{archivePath}.tmp";
        var includedPaths = GetIncludedPaths(server, request.IncludeLogs)
            .Where(path => File.Exists(Path.Combine(server.RootPath, path)) ||
                           Directory.Exists(Path.Combine(server.RootPath, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var manifestFiles = new List<BackupManifestFile>();

        try
        {
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (var relativeRoot in includedPaths)
                {
                    foreach (var file in EnumerateSafeFiles(server.RootPath, relativeRoot))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(server.RootPath, file);
                        await using var source = File.OpenRead(file);
                        var hash = Convert.ToHexString(
                            await SHA256.HashDataAsync(source, cancellationToken)).ToLowerInvariant();
                        source.Position = 0;
                        var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();
                        await source.CopyToAsync(entryStream, cancellationToken);
                        manifestFiles.Add(
                            new BackupManifestFile(relative, source.Length, hash));
                    }
                }

                var manifest = new BackupManifest(
                    backupId,
                    server.Game,
                    server.InstalledVersion ?? "unknown",
                    DateTimeOffset.UtcNow,
                    server.RootPath,
                    includedPaths,
                    manifestFiles.Count,
                    manifestFiles.Sum(file => file.SizeBytes),
                    Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
                    manifestFiles);
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(
                    manifestStream,
                    manifest,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true },
                    cancellationToken);
            }

            File.Move(temporaryPath, archivePath, false);
            await using var archiveFile = File.OpenRead(archivePath);
            var archiveHash = Convert.ToHexString(
                await SHA256.HashDataAsync(archiveFile, cancellationToken)).ToLowerInvariant();
            await File.WriteAllTextAsync(
                $"{archivePath}.sha256",
                archiveHash,
                cancellationToken);
            var info = new FileInfo(archivePath);
            await backupStore.UpsertAsync(
                new BackupRecord(
                    backupId,
                    server.Id,
                    archivePath,
                    server.InstalledVersion ?? "unknown",
                    archiveHash,
                    info.Length,
                    manifestFiles.Count,
                    BackupStatus.Completed,
                    DateTimeOffset.UtcNow,
                    request.ProtectAfterCreation,
                    request.DisplayName,
                    request.Notes,
                    request.IsScheduled),
                cancellationToken);
            return new BackupResult(
                backupId,
                archivePath,
                archiveHash,
                info.Length,
                manifestFiles.Count,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<BackupVerificationResult> VerifyRecordAsync(
        BackupRecord backup,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(backup.ArchivePath))
        {
            return new BackupVerificationResult(false, "The backup archive is missing.", null);
        }

        await using (var file = File.OpenRead(backup.ArchivePath))
        {
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
            if (!hash.Equals(backup.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new BackupVerificationResult(false, "The archive SHA-256 does not match.", null);
            }
        }

        try
        {
            using var archive = ZipFile.OpenRead(backup.ArchivePath);
            var manifestEntry = archive.GetEntry("manifest.json");
            if (manifestEntry is null)
            {
                return new BackupVerificationResult(false, "The backup manifest is missing.", null);
            }

            BackupManifest? manifest;
            await using (var stream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
                    stream,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web),
                    cancellationToken);
            }

            if (manifest is null || manifest.BackupId != backup.Id)
            {
                return new BackupVerificationResult(false, "The backup manifest is invalid.", null);
            }

            foreach (var manifestFile in manifest.Files)
            {
                var entry = archive.GetEntry(manifestFile.RelativePath);
                if (entry is null || entry.Length != manifestFile.SizeBytes)
                {
                    return new BackupVerificationResult(
                        false,
                        $"Backup entry is missing or has the wrong size: {manifestFile.RelativePath}",
                        manifest);
                }

                await using var stream = entry.Open();
                var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (!hash.Equals(manifestFile.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new BackupVerificationResult(
                        false,
                        $"Backup entry hash mismatch: {manifestFile.RelativePath}",
                        manifest);
                }
            }

            return new BackupVerificationResult(true, null, manifest);
        }
        catch (InvalidDataException exception)
        {
            return new BackupVerificationResult(false, exception.Message, null);
        }
    }

    private static void ExtractVerified(string archivePath, string staging)
    {
        Directory.CreateDirectory(staging);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries.Where(entry => entry.FullName != "manifest.json"))
        {
            var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
            if (!SafePathPolicy.IsWithinRoot(target, staging))
            {
                throw new InvalidDataException("Backup archive attempted path traversal.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
        }
    }

    private async Task RollbackRestoreAsync(
        GameServerDefinition server,
        IReadOnlyList<string> includedPaths,
        string rollback,
        IGameServerProvider provider,
        bool restart,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rollback))
        {
            return;
        }

        foreach (var includedPath in includedPaths.Reverse())
        {
            var current = SafePathPolicy.ResolveWithinRoot(server.RootPath, includedPath);
            var previous = SafePathPolicy.ResolveWithinRoot(rollback, includedPath);
            if (File.Exists(current))
            {
                File.Move(current, $"{current}.failed-restore-{Guid.NewGuid():N}", false);
            }
            else if (Directory.Exists(current))
            {
                Directory.Move(current, $"{current}.failed-restore-{Guid.NewGuid():N}");
            }

            if (File.Exists(previous))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(current)!);
                File.Move(previous, current, false);
            }
            else if (Directory.Exists(previous))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(current)!);
                Directory.Move(previous, current);
            }
        }

        if (restart)
        {
            try
            {
                await provider.PrepareForStartAsync(server, cancellationToken);
                await processSupervisor.StartAsync(
                    server,
                    provider.CreateLaunchSpec(server),
                    cancellationToken);
            }
            catch
            {
                // The restore result reports failure; diagnostics retain restart details.
            }
        }
    }

    private static IEnumerable<string> GetIncludedPaths(
        GameServerDefinition server,
        bool includeLogs)
    {
        if (server.Game == GameType.Minecraft)
        {
            foreach (var directory in Directory.Exists(server.RootPath)
                         ? Directory.EnumerateDirectories(server.RootPath, "world*")
                         : [])
            {
                yield return Path.GetFileName(directory);
            }

            foreach (var file in new[]
                     {
                         "server.properties",
                         "whitelist.json",
                         "ops.json",
                         "banned-players.json",
                         "banned-ips.json",
                         "eula.txt",
                         "user_jvm_args.txt",
                         ".1salem/metadata.json"
                     })
            {
                yield return file;
            }
        }
        else
        {
            yield return "Pal/Saved/SaveGames";
            yield return
                "Pal/Saved/Config/WindowsServer/PalWorldSettings.ini";
            yield return ".1salem/metadata.json";
        }

        if (includeLogs)
        {
            yield return "logs";
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root, string relativeRoot)
    {
        var target = SafePathPolicy.ResolveWithinRoot(root, relativeRoot);
        if (File.Exists(target))
        {
            yield return target;
            yield break;
        }

        if (!Directory.Exists(target))
        {
            yield break;
        }

        var directories = new Stack<DirectoryInfo>();
        directories.Push(new DirectoryInfo(target));
        while (directories.TryPop(out var directory))
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Backup rejected reparse point: {directory.FullName}");
            }

            foreach (var file in directory.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Backup rejected reparse point: {file.FullName}");
                }

                yield return file.FullName;
            }

            foreach (var child in directory.EnumerateDirectories())
            {
                directories.Push(child);
            }
        }
    }

    private IGameServerProvider GetProvider(GameType game) =>
        _providers.TryGetValue(game, out var provider)
            ? provider
            : throw new InvalidOperationException($"No {game} provider is registered.");

    private static string SanitizeFileName(string value)
    {
        foreach (var character in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(character, '_');
        }

        return value;
    }

    private static void DeleteOwnedDirectory(string path, string parent, string prefix)
    {
        if (Directory.Exists(path) &&
            SafePathPolicy.IsWithinRoot(path, parent) &&
            Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
        {
            Directory.Delete(path, true);
        }
    }

    private static long EstimateBackupBytes(
        GameServerDefinition server,
        bool includeLogs)
    {
        long total = 0;
        try
        {
            foreach (var relativeRoot in GetIncludedPaths(server, includeLogs))
            {
                foreach (var file in EnumerateSafeFiles(
                             server.RootPath,
                             relativeRoot))
                {
                    try
                    {
                        total = checked(total + new FileInfo(file).Length);
                    }
                    catch (Exception exception) when (
                        exception is IOException or
                        UnauthorizedAccessException or
                        OverflowException)
                    {
                        return long.MaxValue / 2;
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            return long.MaxValue / 2;
        }

        return total;
    }

    private static async Task WaitForPalworldSaveFilesToSettleAsync(
        string serverRoot,
        CancellationToken cancellationToken)
    {
        var saveRoot = SafePathPolicy.ResolveWithinRoot(
            serverRoot,
            Path.Combine("Pal", "Saved", "SaveGames"));
        long? previousSize = null;
        var stableSamples = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = Directory.Exists(saveRoot)
                ? Directory.EnumerateFiles(
                        saveRoot,
                        "*",
                        SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length)
                : 0;
            if (size == previousSize)
            {
                stableSamples++;
                if (stableSamples >= 2)
                {
                    return;
                }
            }
            else
            {
                stableSamples = 0;
            }

            previousSize = size;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new IOException(
            "Palworld save files did not settle within 15 seconds.");
    }
}
