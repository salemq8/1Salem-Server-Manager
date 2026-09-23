using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Content;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Data packs and resource packs. Both are ZIP archives from a provider, but they land in
/// very different places and mean different things: a data pack goes into the world the
/// server is actually running and needs a reload, while a resource pack is kept for the
/// person and only reaches players if they choose to have the server advertise it.
/// </summary>
public sealed class PackInstallService(
    ContentCatalogService catalog,
    IInstalledContentStore store,
    IServerOperationCoordinator coordinator,
    IAuditLogStore auditLog,
    ILogger<PackInstallService> logger,
    TimeSpan? lockTimeout = null)
{
    private readonly TimeSpan _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where this kind of pack belongs on this server. A data pack's home is decided by the
    /// server's own level-name, so a server with no readable world says so instead of
    /// guessing at "world".
    /// </summary>
    public static (string? Directory, string? ErrorCode) ResolveDestination(
        ServerContentProfile profile,
        ContentKind kind)
    {
        ArgumentNullException.ThrowIfNull(profile);
        switch (kind)
        {
            case ContentKind.DataPack:
                var world = ServerWorldLocator.Locate(profile.ServerRoot);
                return world.Found
                    ? (world.DataPackDirectory, null)
                    : (null, world.Reason ?? "NoWorld");
            case ContentKind.ResourcePack:
                return (ContentPathPolicy.ResolveResourcePackDirectory(profile.ServerRoot), null);
            default:
                return (null, "UnsupportedKind");
        }
    }

    public async Task<ContentOperationResult> InstallAsync(
        ServerContentProfile profile,
        ContentInstallRequest request,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not (ContentKind.DataPack or ContentKind.ResourcePack))
        {
            return ContentOperationResult.Fail("UnsupportedKind", "That content type is installed elsewhere.");
        }

        if (!ContentTypePolicy.IsSupportedBy(request.Kind, profile))
        {
            return ContentOperationResult.Fail("UnsupportedServer", "This server cannot take that content.");
        }

        var (destination, destinationError) = ResolveDestination(profile, request.Kind);
        if (destination is null)
        {
            return ContentOperationResult.Fail(destinationError!, "The destination could not be determined.");
        }

        var provider = catalog.Find(request.Provider);
        if (provider is null || !provider.CanServe(profile, request.Kind))
        {
            return ContentOperationResult.Fail("ProviderUnavailable", "That provider cannot serve this server.");
        }

        progress?.Report(new ContentInstallProgress(
            ContentInstallStage.CheckingCompatibility,
            "CheckingCompatibility"));

        ContentVersion? version;
        ContentProject? project;
        try
        {
            project = await provider.GetProjectAsync(request.ProjectId, profile, request.Kind, cancellationToken);
            version = request.VersionId is { Length: > 0 } pinned
                ? (await provider.GetVersionsAsync(request.ProjectId, profile, request.Kind, cancellationToken))
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.VersionId, pinned, StringComparison.Ordinal))
                : await provider.ResolveCompatibleVersionAsync(
                    request.ProjectId,
                    profile,
                    false,
                    request.Kind,
                    cancellationToken);
        }
        catch (ContentProviderException exception)
        {
            return ContentOperationResult.Fail(exception.ErrorCode, exception.Message);
        }

        if (version?.File is null)
        {
            return ContentOperationResult.Fail(
                "NoCompatibleVersion",
                "No release fits this server's Minecraft version.");
        }

        IAsyncDisposable handle;
        try
        {
            handle = await coordinator.AcquireAsync(
                profile.ServerId,
                "InstallContent",
                _lockTimeout,
                cancellationToken);
        }
        catch (ServerBusyException exception)
        {
            return ContentOperationResult.Fail("ServerBusy", exception.Message);
        }

        await using (handle)
        {
            var stagingRoot = ContentPathPolicy.ResolveStagingDirectory(profile.ServerRoot);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(destination);
            var stagingPath = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.zip");

            try
            {
                await provider.DownloadAsync(version, stagingPath, progress, cancellationToken);

                progress?.Report(new ContentInstallProgress(
                    ContentInstallStage.VerifyingHash,
                    "VerifyingHash",
                    version.File.FileName));
                var digests = await FileDigests.ComputeAsync(stagingPath, true, cancellationToken);
                var providerMatch = FileDigests.Matches(version.File.Sha512, digests.Sha512!) ??
                                    FileDigests.Matches(version.File.Sha256, digests.Sha256);
                if (providerMatch == false)
                {
                    return ContentOperationResult.Fail(
                        "HashMismatch",
                        "The download did not match the provider's hash.");
                }

                progress?.Report(new ContentInstallProgress(
                    ContentInstallStage.ValidatingArchive,
                    "ValidatingArchive",
                    version.File.FileName));
                var validation = await ValidatePackAsync(stagingPath, request.Kind, cancellationToken);
                if (!validation.IsValid)
                {
                    return ContentOperationResult.Fail("InvalidArchive", validation.Reason!);
                }

                var fileName = ContentPathPolicy.SanitizeFileName(
                    version.File.FileName,
                    project?.Slug ?? request.ProjectId,
                    ContentTypePolicy.FileExtension(request.Kind));
                var target = ContentPathPolicy.ResolveInstallPath(destination, fileName);

                progress?.Report(new ContentInstallProgress(ContentInstallStage.Installing, "Installing"));
                File.Move(stagingPath, target, true);

                var record = new InstalledContent(
                    profile.ServerId,
                    fileName,
                    ContentTypePolicy.PendingState(request.Kind, profile.IsRunning),
                    request.Kind,
                    request.Provider,
                    request.ProjectId,
                    version.VersionId,
                    project?.Name ?? request.ProjectId,
                    version.VersionNumber,
                    profile.MinecraftVersion,
                    profile.Platform,
                    project?.ProjectUrl,
                    version.File.Sha512,
                    version.File.Sha256,
                    digests.Sha256,
                    DateTimeOffset.UtcNow,
                    request.Kind == ContentKind.DataPack && profile.IsRunning,
                    null,
                    null,
                    null,
                    null,
                    version.File.SizeBytes,
                    true,
                    version.File.Url,
                    version.File.Sha1,
                    Path.GetRelativePath(profile.ServerRoot, target));

                await store.UpsertAsync(record, cancellationToken);
                await auditLog.WriteAsync(
                    "content",
                    request.Kind == ContentKind.DataPack ? "DataPackInstalled" : "ResourcePackInstalled",
                    $"server:{profile.ServerId}",
                    true,
                    $"{record.ProjectName} {record.InstalledVersion}",
                    cancellationToken);

                progress?.Report(new ContentInstallProgress(ContentInstallStage.Completed, "Installed"));
                return ContentOperationResult.Ok(
                    request.Kind == ContentKind.DataPack && profile.IsRunning,
                    [record]);
            }
            catch (ContentProviderException exception)
            {
                return ContentOperationResult.Fail(exception.ErrorCode, exception.Message);
            }
            catch (OperationCanceledException)
            {
                return ContentOperationResult.Fail("Cancelled", "The install was cancelled.");
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "A pack could not be written.");
                return ContentOperationResult.Fail("InstallFailed", "The pack could not be written.");
            }
            catch (UnauthorizedAccessException)
            {
                return ContentOperationResult.Fail("AccessDenied", "Windows refused access to that folder.");
            }
            finally
            {
                TryDelete(stagingPath);
            }
        }
    }

    public async Task<ContentOperationResult> UpdateAsync(
        ServerContentProfile profile,
        string fileName,
        string? versionId = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var existing = await store.GetAsync(profile.ServerId, fileName, cancellationToken);
        if (existing?.Provider is null || existing.ProjectId is null)
        {
            return ContentOperationResult.Fail("NotManaged", "That pack was not installed by this app.");
        }

        var (destination, destinationError) = ResolveDestination(profile, existing.Kind);
        if (destination is null)
        {
            return ContentOperationResult.Fail(destinationError!, "The destination could not be determined.");
        }

        var provider = catalog.Find(existing.Provider.Value);
        if (provider is null || !provider.CanServe(profile, existing.Kind))
        {
            return ContentOperationResult.Fail("ProviderUnavailable", "That provider is not available.");
        }

        ContentVersion? target;
        try
        {
            target = versionId is { Length: > 0 }
                ? (await provider.GetVersionsAsync(existing.ProjectId, profile, existing.Kind, cancellationToken))
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.VersionId, versionId, StringComparison.Ordinal))
                : await provider.ResolveCompatibleVersionAsync(
                    existing.ProjectId,
                    profile,
                    false,
                    existing.Kind,
                    cancellationToken);
        }
        catch (ContentProviderException exception)
        {
            return ContentOperationResult.Fail(exception.ErrorCode, exception.Message);
        }

        if (target?.File is null)
        {
            return ContentOperationResult.Fail(
                "NoCompatibleVersion",
                "No compatible update exists for this server's Minecraft version.");
        }

        if (string.Equals(target.VersionId, existing.VersionId, StringComparison.Ordinal))
        {
            return ContentOperationResult.Fail("AlreadyCurrent", "That pack is already up to date.");
        }

        IAsyncDisposable handle;
        try
        {
            handle = await coordinator.AcquireAsync(
                profile.ServerId,
                "UpdateContent",
                _lockTimeout,
                cancellationToken);
        }
        catch (ServerBusyException exception)
        {
            return ContentOperationResult.Fail("ServerBusy", exception.Message);
        }

        await using (handle)
        {
            var stagingRoot = ContentPathPolicy.ResolveStagingDirectory(profile.ServerRoot);
            var rollbackRoot = ContentPathPolicy.ResolveRollbackDirectory(profile.ServerRoot);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(rollbackRoot);
            var stagingPath = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.zip");

            try
            {
                await provider.DownloadAsync(target, stagingPath, progress, cancellationToken);
                var digests = await FileDigests.ComputeAsync(stagingPath, true, cancellationToken);
                var providerMatch = FileDigests.Matches(target.File.Sha512, digests.Sha512!) ??
                                    FileDigests.Matches(target.File.Sha256, digests.Sha256);
                if (providerMatch == false)
                {
                    return ContentOperationResult.Fail(
                        "HashMismatch",
                        "The downloaded update did not match the provider's hash.");
                }

                var validation = await ValidatePackAsync(stagingPath, existing.Kind, cancellationToken);
                if (!validation.IsValid)
                {
                    return ContentOperationResult.Fail("InvalidArchive", validation.Reason!);
                }

                var newFileName = ContentPathPolicy.SanitizeFileName(
                    target.File.FileName,
                    existing.ProjectId,
                    ContentTypePolicy.FileExtension(existing.Kind));
                var newPath = ContentPathPolicy.ResolveInstallPath(destination, newFileName);
                var currentPath = ContentPathPolicy.ResolveInstallPath(destination, existing.FileName);

                // The previous pack is kept before anything is replaced, so rollback has a
                // real file to go back to.
                var preserved = Path.Combine(
                    rollbackRoot,
                    $"{Path.GetFileNameWithoutExtension(existing.FileName)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.zip");
                if (File.Exists(currentPath))
                {
                    File.Copy(currentPath, preserved, true);
                }

                File.Move(stagingPath, newPath, true);
                if (!string.Equals(currentPath, newPath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(currentPath))
                {
                    File.Delete(currentPath);
                }

                var updated = existing with
                {
                    FileName = newFileName,
                    VersionId = target.VersionId,
                    InstalledVersion = target.VersionNumber,
                    MinecraftVersionAtInstall = profile.MinecraftVersion,
                    ProviderSha512 = target.File.Sha512,
                    ProviderSha256 = target.File.Sha256,
                    ProviderSha1 = target.File.Sha1,
                    DownloadUrl = target.File.Url,
                    LocalSha256 = digests.Sha256,
                    SizeBytes = target.File.SizeBytes,
                    InstalledAtUtc = DateTimeOffset.UtcNow,
                    RestartRequired = existing.Kind == ContentKind.DataPack && profile.IsRunning,
                    PreviousVersionId = existing.VersionId,
                    PreviousFileName = Path.GetFileName(preserved),
                    RelativePath = Path.GetRelativePath(profile.ServerRoot, newPath),
                    State = ContentTypePolicy.PendingState(existing.Kind, profile.IsRunning)
                };

                if (!string.Equals(newFileName, existing.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    await store.RemoveAsync(profile.ServerId, existing.FileName, cancellationToken);
                }

                await store.UpsertAsync(updated, cancellationToken);
                await auditLog.WriteAsync(
                    "content",
                    "PackUpdated",
                    $"server:{profile.ServerId}",
                    true,
                    $"{existing.ProjectName}: {existing.InstalledVersion} -> {target.VersionNumber}",
                    cancellationToken);
                return ContentOperationResult.Ok(
                    existing.Kind == ContentKind.DataPack && profile.IsRunning,
                    [updated]);
            }
            catch (ContentProviderException exception)
            {
                return ContentOperationResult.Fail(exception.ErrorCode, exception.Message);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "A pack update could not be committed.");
                return ContentOperationResult.Fail("UpdateFailed", "The pack file could not be replaced.");
            }
            finally
            {
                TryDelete(stagingPath);
            }
        }
    }

    public async Task<ContentOperationResult> RollbackAsync(
        ServerContentProfile profile,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var existing = await store.GetAsync(profile.ServerId, fileName, cancellationToken);
        if (existing?.PreviousFileName is null)
        {
            return ContentOperationResult.Fail("NoRollback", "There is no earlier version to go back to.");
        }

        var (destination, destinationError) = ResolveDestination(profile, existing.Kind);
        if (destination is null)
        {
            return ContentOperationResult.Fail(destinationError!, "The destination could not be determined.");
        }

        var rollbackRoot = ContentPathPolicy.ResolveRollbackDirectory(profile.ServerRoot);
        var preserved = Path.Combine(rollbackRoot, Path.GetFileName(existing.PreviousFileName));
        if (!File.Exists(preserved) || !SafePathPolicy.IsWithinRoot(preserved, rollbackRoot))
        {
            return ContentOperationResult.Fail("RollbackMissing", "The earlier version is no longer stored.");
        }

        IAsyncDisposable handle;
        try
        {
            handle = await coordinator.AcquireAsync(
                profile.ServerId,
                "RollbackContent",
                _lockTimeout,
                cancellationToken);
        }
        catch (ServerBusyException exception)
        {
            return ContentOperationResult.Fail("ServerBusy", exception.Message);
        }

        await using (handle)
        {
            var validation = await ValidatePackAsync(preserved, existing.Kind, cancellationToken);
            if (!validation.IsValid)
            {
                return ContentOperationResult.Fail("InvalidArchive", validation.Reason!);
            }

            try
            {
                var restoredName = Path.GetFileName(preserved);
                var restoredPath = ContentPathPolicy.ResolveInstallPath(destination, restoredName);
                var currentPath = ContentPathPolicy.ResolveInstallPath(destination, existing.FileName);
                File.Copy(preserved, restoredPath, true);
                if (!string.Equals(currentPath, restoredPath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(currentPath))
                {
                    File.Delete(currentPath);
                }

                var digests = await FileDigests.ComputeAsync(restoredPath, false, cancellationToken);
                await store.RemoveAsync(profile.ServerId, existing.FileName, cancellationToken);
                var restored = existing with
                {
                    FileName = restoredName,
                    VersionId = existing.PreviousVersionId,
                    InstalledVersion = null,
                    LocalSha256 = digests.Sha256,
                    ProviderSha512 = null,
                    ProviderSha256 = null,
                    PreviousVersionId = null,
                    PreviousFileName = null,
                    InstalledAtUtc = DateTimeOffset.UtcNow,
                    RelativePath = Path.GetRelativePath(profile.ServerRoot, restoredPath),
                    State = ContentTypePolicy.PendingState(existing.Kind, profile.IsRunning)
                };
                await store.UpsertAsync(restored, cancellationToken);
                await auditLog.WriteAsync(
                    "content",
                    "PackRolledBack",
                    $"server:{profile.ServerId}",
                    true,
                    existing.ProjectName,
                    cancellationToken);
                return ContentOperationResult.Ok(
                    existing.Kind == ContentKind.DataPack && profile.IsRunning,
                    [restored]);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "A pack rollback could not be committed.");
                return ContentOperationResult.Fail("RollbackFailed", "The earlier pack could not be restored.");
            }
        }
    }

    /// <summary>
    /// Removes a pack this app installed. Only that file is touched: the world's own files,
    /// and any pack somebody else put there, are left exactly as they are.
    /// </summary>
    public async Task<ContentOperationResult> UninstallAsync(
        ServerContentProfile profile,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var existing = await store.GetAsync(profile.ServerId, fileName, cancellationToken);
        var kind = existing?.Kind ?? ContentKind.DataPack;
        var (destination, destinationError) = ResolveDestination(profile, kind);
        if (destination is null)
        {
            return ContentOperationResult.Fail(destinationError!, "The destination could not be determined.");
        }

        IAsyncDisposable handle;
        try
        {
            handle = await coordinator.AcquireAsync(
                profile.ServerId,
                "UninstallContent",
                _lockTimeout,
                cancellationToken);
        }
        catch (ServerBusyException exception)
        {
            return ContentOperationResult.Fail("ServerBusy", exception.Message);
        }

        await using (handle)
        {
            string target;
            try
            {
                target = ContentPathPolicy.ResolveInstallPath(destination, fileName);
            }
            catch (UnauthorizedAccessException)
            {
                return ContentOperationResult.Fail("InvalidFileName", "That is not a pack in this server.");
            }

            // A resource pack being removed must stop being advertised, or clients would be
            // sent to a file that no longer exists.
            if (kind == ContentKind.ResourcePack && existing?.DownloadUrl is { } url)
            {
                var (currentUrl, _, _) = ServerWorldLocator.ReadResourcePackSettings(profile.ServerRoot);
                if (string.Equals(currentUrl, url.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    ServerWorldLocator.WriteResourcePackSettings(profile.ServerRoot, null, null, false, null);
                }
            }

            if (File.Exists(target))
            {
                try
                {
                    File.Delete(target);
                }
                catch (IOException)
                {
                    return ContentOperationResult.Fail(
                        "FileInUse",
                        profile.IsRunning
                            ? "The server is using that file. Stop the server and try again."
                            : "The file could not be removed.");
                }
            }

            await store.RemoveAsync(profile.ServerId, fileName, cancellationToken);
            await auditLog.WriteAsync(
                "content",
                "PackUninstalled",
                $"server:{profile.ServerId}",
                true,
                fileName,
                cancellationToken);
            return ContentOperationResult.Ok(kind == ContentKind.DataPack && profile.IsRunning);
        }
    }

    /// <summary>
    /// Points this server's clients at a resource pack. Only a provider-hosted HTTPS address
    /// can be used: this app does not host files, open ports or touch the tunnel, so a pack
    /// with no provider URL is refused rather than half-configured.
    /// </summary>
    public async Task<ContentOperationResult> DistributeAsync(
        ServerContentProfile profile,
        ResourcePackDistributionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        var existing = await store.GetAsync(profile.ServerId, request.FileName, cancellationToken);
        if (existing is null || existing.Kind != ContentKind.ResourcePack)
        {
            return ContentOperationResult.Fail("NotManaged", "That resource pack is not managed here.");
        }

        if (existing.DownloadUrl is not { } url ||
            !ContentDownloadPolicy.IsAllowedDownloadUrl(
                existing.Provider ?? ContentProviderId.Modrinth,
                url))
        {
            return ContentOperationResult.Fail(
                "NeedsReachableUrl",
                "Clients need a reachable address for the pack, and this one has none.");
        }

        if (string.IsNullOrWhiteSpace(existing.ProviderSha1))
        {
            // Minecraft verifies the pack against this hash; without it the server would log
            // an invalid-sha1 warning on every start.
            return ContentOperationResult.Fail(
                "NoSha1",
                "The provider did not publish the SHA-1 this needs.");
        }

        if (existing.SizeBytes > 250L * 1024 * 1024)
        {
            return ContentOperationResult.Fail(
                "PackTooLarge",
                "Minecraft refuses server resource packs larger than 250 MiB.");
        }

        await using var handle = await coordinator.AcquireAsync(
            profile.ServerId,
            "ContentSettings",
            _lockTimeout,
            cancellationToken);
        try
        {
            ServerWorldLocator.WriteResourcePackSettings(
                profile.ServerRoot,
                url.ToString(),
                existing.ProviderSha1,
                request.Require,
                request.Prompt);
        }
        catch (Exception exception) when (exception is IOException or FileNotFoundException)
        {
            logger.LogWarning(exception, "server.properties could not be updated.");
            return ContentOperationResult.Fail("SettingsFailed", "server.properties could not be updated.");
        }

        await auditLog.WriteAsync(
            "content",
            "ResourcePackDistributed",
            $"server:{profile.ServerId}",
            true,
            existing.ProjectName,
            cancellationToken);

        // Clients pick this up when they next connect, so a running server needs a restart
        // only for the setting itself to be read.
        return ContentOperationResult.Ok(
            profile.IsRunning,
            [existing with { State = InstalledContentState.Distributed, IsDistributed = true }]);
    }

    public async Task<ContentOperationResult> StopDistributingAsync(
        ServerContentProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var handle = await coordinator.AcquireAsync(
            profile.ServerId,
            "ContentSettings",
            _lockTimeout,
            cancellationToken);
        try
        {
            ServerWorldLocator.WriteResourcePackSettings(profile.ServerRoot, null, null, false, null);
        }
        catch (Exception exception) when (exception is IOException or FileNotFoundException)
        {
            return ContentOperationResult.Fail("SettingsFailed", "server.properties could not be updated.");
        }

        await auditLog.WriteAsync(
            "content",
            "ResourcePackWithdrawn",
            $"server:{profile.ServerId}",
            true,
            null,
            cancellationToken);
        return ContentOperationResult.Ok(profile.IsRunning);
    }

    /// <summary>
    /// Structure only: the archive is safe to unpack and really looks like the pack type it
    /// claims to be. Nothing inside is executed and nothing is extracted here.
    /// </summary>
    private static async Task<ArchiveInspection> ValidatePackAsync(
        string archivePath,
        ContentKind kind,
        CancellationToken cancellationToken)
    {
        var inspection = SafeArchive.Inspect(archivePath, kind);
        if (!inspection.IsValid)
        {
            return inspection;
        }

        var metadata = await SafeArchive.ReadTextEntryAsync(archivePath, "pack.mcmeta", cancellationToken);
        if (metadata is null)
        {
            return new ArchiveInspection(
                false,
                kind == ContentKind.DataPack
                    ? "The archive has no pack.mcmeta, so it is not a data pack."
                    : "The archive has no pack.mcmeta, so it is not a resource pack.");
        }

        try
        {
            using var document = JsonDocument.Parse(metadata);
            if (!document.RootElement.TryGetProperty("pack", out _))
            {
                return new ArchiveInspection(false, "The pack.mcmeta file is not in the expected shape.");
            }
        }
        catch (JsonException)
        {
            return new ArchiveInspection(false, "The pack.mcmeta file could not be read.");
        }

        return inspection;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
