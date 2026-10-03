using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Concurrency;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Backups;

/// <summary>Reports available free space on the volume hosting a path, for restore pre-checks.</summary>
public readonly record struct DriveSpaceInfo(bool IsReady, long AvailableFreeSpace);

public sealed class BackupService(
    IGameServerStore gameServerStore,
    IBackupStore backupStore,
    IProcessSupervisor processSupervisor,
    IEnumerable<IGameServerProvider> providers,
    IServerOperationCoordinator coordinator,
    PalworldRestClient? palworldRestClient = null,
    IAuditLogStore? auditLogStore = null,
    Func<string, DriveSpaceInfo>? driveSpaceProbe = null) : IBackupService
{
    private readonly IReadOnlyDictionary<GameType, IGameServerProvider> _providers =
        providers.ToDictionary(provider => provider.Game);
    private readonly Func<string, DriveSpaceInfo> _driveSpaceProbe =
        driveSpaceProbe ?? DefaultDriveSpaceProbe;

    // Required, not defaulted: GameServerOrchestrator's coordinator parameter is required for
    // the same reason (see GameServerOrchestrator.cs) -- Program.cs must pass the same
    // singleton instance it hands to GameServerOrchestrator, or Start/Stop/Restart and
    // Backup/Restore for the same server no longer serialize against each other. A silent
    // per-instance fallback here would mask that wiring mistake instead of failing loudly at
    // DI-resolution time.
    private readonly IServerOperationCoordinator _coordinator = coordinator;

    public async Task<BackupResult> CreateAsync(
        BackupRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var _ = await _coordinator.AcquireAsync(
            request.ServerId,
            "Backup",
            cancellationToken: cancellationToken);
        return await CreateWithinOperationAsync(request, cancellationToken);
    }

    // Runtime migration already owns the same operation lease across stop/backup/swap/start.
    // Internal visibility prevents API callers from bypassing that lease.
    internal async Task<BackupResult> CreateWithinOperationAsync(
        BackupRequest request, CancellationToken cancellationToken)
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

        // Filesystem backup capability is unconditional; REST save/flush is an optional,
        // best-effort quiescing step layered on top of it. A server must never go permanently
        // unbackuppable just because REST management happens to be off -- which is the default.
        var safeOffline = request.SafeOffline;
        string? restSaveNote = null;
        await gameServerStore.SetStateAsync(server.Id, ServerState.BackingUp, cancellationToken);
        try
        {
            // The REST save attempt, its post-save settle-wait, and the safe-offline stop are
            // all inside this same transactional boundary as archive creation: a failure in any
            // of them (PalworldRestClient is documented to never throw, but a save-settle
            // timeout or a process-stop failure legitimately can) must go through the same
            // Error-state/audit-log/best-effort-restart handling below as an archiving failure,
            // not escape unhandled.
            if (server.Game == GameType.Palworld && wasRunning)
            {
                var saveOutcome = palworldRestClient is null
                    ? new PalworldRestOperationResult(
                        false,
                        "RestClientUnavailable",
                        "Palworld REST management is not configured for this installation.",
                        DateTimeOffset.UtcNow)
                    : await palworldRestClient.SaveWorldAsync(server, cancellationToken);
                if (saveOutcome.Success)
                {
                    await WaitForPalworldSaveFilesToSettleAsync(server.RootPath, cancellationToken);
                }
                else
                {
                    // REST save/flush isn't available right now (commonly: REST management is
                    // disabled, which is the default). Fall back to the same safe-offline
                    // stop/backup/restart cycle Minecraft already uses, regardless of what the
                    // caller originally requested, so the backup still completes instead of
                    // failing outright.
                    safeOffline = true;
                    restSaveNote = $"{saveOutcome.Code}: {saveOutcome.Message}";
                }
            }

            if (safeOffline && wasRunning)
            {
                var stopped = await processSupervisor.StopAsync(server.Id, false, cancellationToken);
                if (!stopped.Success)
                {
                    throw new InvalidOperationException(stopped.Message);
                }

                await provider.CleanupAfterStopAsync(server, cancellationToken);
            }

            var archived = await CreateArchiveAsync(server, request, cancellationToken);
            var verification = await VerifyAsync(archived.BackupId, cancellationToken);
            if (!verification.IsValid)
            {
                var record = await backupStore.GetAsync(
                    archived.BackupId,
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

            if (safeOffline && wasRunning)
            {
                await provider.PrepareForStartAsync(server, cancellationToken);
                await processSupervisor.StartAsync(
                    server,
                    provider.CreateLaunchSpec(server),
                    cancellationToken);
            }

            var statusMessage = restSaveNote is null
                ? "Backup succeeded."
                : $"Backup succeeded without REST save ({restSaveNote}).";
            var result = archived with { StatusMessage = statusMessage };
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
                    $"BackupId={result.BackupId}; Verified=True; File={Path.GetFileName(result.ArchivePath)}; " +
                    $"Status={statusMessage}",
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
                    $"Backup failed: {exception.Message}",
                    cancellationToken);
            }

            await gameServerStore.SetStateWithErrorAsync(
                server.Id,
                ServerState.Error,
                exception.Message,
                cancellationToken);
            if (safeOffline && wasRunning &&
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

        await using var _ = await _coordinator.AcquireAsync(
            backup.ServerId,
            "Restore",
            cancellationToken: cancellationToken);

        if (!File.Exists(backup.ArchivePath))
        {
            return OperationResult.Fail(
                "BackupNotFound",
                "The backup archive file is missing from disk.");
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
        var diskCheck = CheckAvailableDiskSpace(
            server.RootPath,
            // Budget for both the content being restored AND the pre-restore "Safety" backup
            // archive created below (of the current live state, comparable in size to the
            // backup being restored) -- a check that only budgeted for the restored content
            // could still let the restore run out of disk space while writing that second
            // archive.
            verification.Manifest.TotalBytes * 2);
        if (!diskCheck.Success)
        {
            return diskCheck;
        }

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

        var includedPaths = verification.Manifest.IncludedPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var parent = Directory.GetParent(server.RootPath)!;
        var staging = Path.Combine(parent.FullName, $".1salem-restore-staging-{Guid.NewGuid():N}");
        var rollback = Path.Combine(
            server.RootPath,
            ".1salem",
            $"restore-rollback-{Guid.NewGuid():N}");
        var journalPath = Path.Combine(rollback, "journal.json");

        // processedPaths tracks only the paths whose live content has actually been swapped
        // during THIS attempt (the "current -> rollback" move for that path has completed).
        // Rollback must only ever reverse paths in this list — a path never reached before a
        // failure still holds its original, untouched content and must never be touched.
        var processedPaths = new List<string>();
        try
        {
            // Both the state transition and the pre-restore safety backup live inside the
            // transactional boundary: if either throws (e.g. disk exhaustion, an I/O or
            // permission error), the catch block below rolls back, records the failure, and
            // returns OperationResult.Fail instead of letting the exception escape unhandled
            // and leaving the server stuck in Restoring with no journal for
            // RecoverInterruptedRestoresAsync to find.
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

            ExtractVerified(backup.ArchivePath, staging);
            VerifyStagedContent(staging, verification.Manifest);
            Directory.CreateDirectory(rollback);
            WriteJournal(
                journalPath,
                new RestoreJournal(backup.Id, server.Id, processedPaths, false));

            foreach (var includedPath in includedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = SafePathPolicy.ResolveWithinRoot(server.RootPath, includedPath);
                var staged = SafePathPolicy.ResolveWithinRoot(staging, includedPath);
                var previous = SafePathPolicy.ResolveWithinRoot(rollback, includedPath);
                RejectReparsePoint(current, includedPath);

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

                // The live path's pre-restore state (if any) is now safely captured under
                // `rollback`. From here on it is both safe and necessary to reverse this
                // specific path if a later path in this loop fails.
                processedPaths.Add(includedPath);
                WriteJournal(
                    journalPath,
                    new RestoreJournal(backup.Id, server.Id, processedPaths, false));

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

            WriteJournal(
                journalPath,
                new RestoreJournal(backup.Id, server.Id, processedPaths, true));
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
                processedPaths,
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

    /// <summary>
    /// Repairs any restore transaction left behind by a crash, forced kill, or power loss that
    /// occurred after live files were swapped but before the transaction committed. Intended to
    /// be called once, early, during Agent startup, before any managed server is adopted or
    /// started. Never restarts a server itself — it only returns the filesystem to a consistent
    /// state and lets the normal startup/adoption flow decide what should run.
    /// </summary>
    public async Task<int> RecoverInterruptedRestoresAsync(
        CancellationToken cancellationToken = default)
    {
        var recovered = 0;
        IReadOnlyList<GameServerDefinition> servers;
        try
        {
            servers = await gameServerStore.ListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var server in servers)
        {
            var metaRoot = Path.Combine(server.RootPath, ".1salem");
            if (!Directory.Exists(metaRoot))
            {
                continue;
            }

            IGameServerProvider provider;
            try
            {
                provider = GetProvider(server.Game);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            foreach (var rollbackDir in Directory.EnumerateDirectories(metaRoot, "restore-rollback-*"))
            {
                var journal = TryReadJournal(Path.Combine(rollbackDir, "journal.json"));
                if (journal is null || journal.Completed || journal.ServerId != server.Id)
                {
                    continue;
                }

                await RollbackRestoreAsync(
                    server,
                    journal.ProcessedPaths,
                    rollbackDir,
                    provider,
                    restart: false,
                    cancellationToken);
                if (auditLogStore is not null)
                {
                    await auditLogStore.WriteAsync(
                        "Agent",
                        "BackupRestoreRecovered",
                        server.Id.ToString(),
                        true,
                        $"BackupId={journal.BackupId}; an interrupted restore left by a crash or " +
                        "forced shutdown was rolled back to the pre-restore state.",
                        cancellationToken);
                }

                recovered++;
            }
        }

        return recovered;
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
        IReadOnlyList<string> processedPaths,
        string rollback,
        IGameServerProvider provider,
        bool restart,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(rollback))
        {
            // Only ever reverse paths recorded as processed. A path never reached before the
            // failure still holds its original, untouched live content — reversing it here
            // would destroy a file the failed restore never actually changed.
            for (var index = processedPaths.Count - 1; index >= 0; index--)
            {
                var includedPath = processedPaths[index];
                var current = SafePathPolicy.ResolveWithinRoot(server.RootPath, includedPath);
                var previous = SafePathPolicy.ResolveWithinRoot(rollback, includedPath);

                // The restored content placed here during the failed attempt is discarded
                // outright rather than renamed aside: its pre-restore truth is already safely
                // captured under `rollback` (or, if nothing existed here before, discarding it
                // *is* the correct pre-restore state). Renaming it aside would leave stray
                // `*.failed-restore-*` files behind, which is itself a deviation from "exact
                // pre-restore state".
                if (File.Exists(current))
                {
                    File.Delete(current);
                }
                else if (Directory.Exists(current))
                {
                    Directory.Delete(current, true);
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
        }

        // Even when nothing was ever moved (e.g. the failure happened during the pre-restore
        // safety backup, before any live path was touched -- rollback was never created), a
        // server that was stopped for this attempt still needs to come back up.
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

        TryDeleteRollbackDirectory(rollback);
    }

    private static void TryDeleteRollbackDirectory(string rollback)
    {
        try
        {
            if (Directory.Exists(rollback))
            {
                Directory.Delete(rollback, true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Every processed path has already been restored above; leaving this now-empty
            // (or partially locked) folder behind is a harmless cleanup miss, not a data-loss
            // risk. A future recovery pass will find no journal marked incomplete here once
            // the underlying lock clears, since the journal itself lives inside this folder.
        }
    }

    private static void VerifyStagedContent(string staging, BackupManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            var path = SafePathPolicy.ResolveWithinRoot(staging, file.RelativePath);
            if (!File.Exists(path))
            {
                throw new InvalidDataException(
                    $"The extracted backup is missing an expected file: {file.RelativePath}");
            }

            var info = new FileInfo(path);
            if (info.Length != file.SizeBytes)
            {
                throw new InvalidDataException(
                    $"The extracted backup has the wrong size for {file.RelativePath}.");
            }

            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The extracted backup content does not match its manifest hash for " +
                    $"{file.RelativePath}.");
            }
        }
    }

    private static void RejectReparsePoint(string path, string includedPath)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Refusing to restore over a reparse point: {includedPath}");
        }
    }

    private OperationResult CheckAvailableDiskSpace(string serverRoot, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(serverRoot));
            if (string.IsNullOrEmpty(root))
            {
                return OperationResult.Ok();
            }

            var space = _driveSpaceProbe(root);
            var required = requiredBytes + BackupDestinationPolicy.MinimumFreeSpaceBytes;
            if (space.IsReady && space.AvailableFreeSpace < required)
            {
                return OperationResult.Fail(
                    "InsufficientDiskSpace",
                    $"Restoring this backup needs approximately {FormatBytes(required)} free " +
                    $"on {root}, but only {FormatBytes(space.AvailableFreeSpace)} is available.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best-effort pre-check only; if the drive can't be inspected, proceed and let the
            // restore itself fail with a specific I/O error instead of blocking on diagnostics.
        }

        return OperationResult.Ok();
    }

    private static DriveSpaceInfo DefaultDriveSpaceProbe(string root)
    {
        var drive = new DriveInfo(root);
        return new DriveSpaceInfo(drive.IsReady, drive.AvailableFreeSpace);
    }

    private static string FormatBytes(long bytes)
    {
        const double mebibyte = 1024d * 1024d;
        return $"{bytes / mebibyte:F1} MB";
    }

    private sealed record RestoreJournal(
        Guid BackupId,
        Guid ServerId,
        IReadOnlyList<string> ProcessedPaths,
        bool Completed);

    private static readonly JsonSerializerOptions JournalSerializerOptions =
        new(JsonSerializerDefaults.Web);

    private static void WriteJournal(string path, RestoreJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(journal, JournalSerializerOptions);
        var temporary = $"{path}.tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, true);
    }

    private static RestoreJournal? TryReadJournal(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RestoreJournal>(
                File.ReadAllText(path),
                JournalSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> GetIncludedPaths(
        GameServerDefinition server,
        bool includeLogs)
    {
        if (server.Game == GameType.Minecraft)
        {
            var configuredWorld = Games.Minecraft.MinecraftSoftwareSafety.LevelName(server.RootPath);
            var worldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                configuredWorld, configuredWorld + "_nether", configuredWorld + "_the_end"
            };
            foreach (var directory in Directory.Exists(server.RootPath)
                         ? Directory.EnumerateDirectories(server.RootPath, "world*")
                         : [])
            {
                worldNames.Add(Path.GetFileName(directory));
            }
            foreach (var worldName in worldNames)
            {
                yield return worldName;
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
