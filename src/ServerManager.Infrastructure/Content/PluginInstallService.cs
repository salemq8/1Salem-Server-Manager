using Microsoft.Extensions.Logging;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Content;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Installs, updates, rolls back and removes plugins. Every mutation takes the server's
/// operation lock, stages the download away from the live plugins folder, verifies it, and
/// only then swaps it in. A failure anywhere leaves the folder as it was.
/// </summary>
public sealed class PluginInstallService(
    ContentCatalogService catalog,
    IInstalledContentStore store,
    IServerOperationCoordinator coordinator,
    IAuditLogStore auditLog,
    ILogger<PluginInstallService> logger,
    TimeSpan? lockTimeout = null)
{
    // How long to wait for a server that is busy with a backup, a start or another install.
    private readonly TimeSpan _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30);

    /// <summary>
    /// Works out everything an install would bring in, before anything is downloaded.
    /// Required dependencies are resolved recursively, with cycles and repeats collapsed;
    /// optional ones are listed as warnings and never installed on their own initiative.
    /// </summary>
    public async Task<ContentInstallPlan> PlanAsync(
        ServerContentProfile profile,
        ContentInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        if (!profile.SupportsPlugins)
        {
            return new ContentInstallPlan([], [], true, "UnsupportedServer");
        }

        var provider = catalog.Find(request.Provider);
        if (provider is null || !provider.CanServe(profile))
        {
            return new ContentInstallPlan([], [], true, "ProviderUnavailable");
        }

        var items = new List<ContentInstallPlanItem>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string ProjectId, bool IsDependency)>();
        queue.Enqueue((request.ProjectId, false));

        while (queue.Count > 0)
        {
            var (projectId, isDependency) = queue.Dequeue();
            if (!seen.Add($"{request.Provider}:{projectId}"))
            {
                continue;
            }

            ContentVersion? version;
            ContentProject? project;
            try
            {
                project = await provider.GetProjectAsync(projectId, profile, cancellationToken: cancellationToken);
                version = !isDependency && request.VersionId is { Length: > 0 } pinned
                    ? (await provider.GetVersionsAsync(projectId, profile, cancellationToken: cancellationToken))
                        .FirstOrDefault(candidate =>
                            string.Equals(candidate.VersionId, pinned, StringComparison.Ordinal))
                    : await provider.ResolveCompatibleVersionAsync(
                        projectId,
                        profile,
                        allowPrerelease: false,
                        cancellationToken: cancellationToken);
            }
            catch (ContentProviderException exception)
            {
                return new ContentInstallPlan([], [], true, exception.ErrorCode);
            }

            if (version is null)
            {
                // A dependency with nothing compatible stops the whole install rather than
                // leaving a plugin that cannot run.
                return new ContentInstallPlan(
                    [],
                    warnings,
                    true,
                    isDependency ? $"DependencyIncompatible:{project?.Name ?? projectId}" : "NoCompatibleVersion");
            }

            if (version.File is null)
            {
                return new ContentInstallPlan(
                    [],
                    warnings,
                    true,
                    version.ExternalDownloadUrl is null ? "NoDownloadableFile" : "ExternalDownload");
            }

            items.Add(new ContentInstallPlanItem(
                request.Provider,
                projectId,
                project?.Name ?? projectId,
                version.VersionId,
                version.VersionNumber,
                ContentPathPolicy.SanitizeFileName(version.File.FileName, project?.Slug ?? projectId),
                version.File.SizeBytes,
                isDependency));

            if (!request.IncludeRequiredDependencies)
            {
                continue;
            }

            foreach (var dependency in version.Dependencies)
            {
                switch (dependency.Kind)
                {
                    case ContentDependencyKind.Required when dependency.ProjectId is { Length: > 0 } id:
                        if (dependency.ExternalUrl is not null && dependency.Provider != request.Provider)
                        {
                            warnings.Add($"ExternalDependency:{dependency.Name ?? id}");
                            continue;
                        }

                        queue.Enqueue((id, true));
                        break;
                    case ContentDependencyKind.Required:
                        // Named but not resolvable: say so instead of installing something
                        // that will not start.
                        return new ContentInstallPlan(
                            [],
                            warnings,
                            true,
                            $"DependencyUnresolvable:{dependency.Name ?? "unknown"}");
                    case ContentDependencyKind.Incompatible when dependency.Name is { Length: > 0 } name:
                        warnings.Add($"Incompatible:{name}");
                        break;
                    case ContentDependencyKind.Optional when dependency.Name is { Length: > 0 } optional:
                        warnings.Add($"Optional:{optional}");
                        break;
                }
            }
        }

        return new ContentInstallPlan(items, warnings);
    }

    public async Task<ContentOperationResult> InstallAsync(
        ServerContentProfile profile,
        ContentInstallRequest request,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        progress?.Report(new ContentInstallProgress(
            ContentInstallStage.CheckingCompatibility,
            "CheckingCompatibility"));
        var plan = await PlanAsync(profile, request, cancellationToken);
        if (plan.Blocked || plan.Items.Count == 0)
        {
            return ContentOperationResult.Fail(
                plan.BlockedReason ?? "NoCompatibleVersion",
                "Nothing compatible could be installed.");
        }

        progress?.Report(new ContentInstallProgress(
            ContentInstallStage.ResolvingDependencies,
            "ResolvingDependencies"));

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
            var installed = new List<InstalledContent>();
            var staged = new List<(string StagingPath, string Destination, ContentInstallPlanItem Item, ContentVersion Version)>();
            var stagingRoot = ContentPathPolicy.ResolveStagingDirectory(profile.ServerRoot);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(profile.PluginsDirectory);

            try
            {
                var provider = catalog.Find(request.Provider)!;

                // Download and check everything first: nothing reaches the plugins folder
                // until every file in the plan has passed.
                foreach (var item in plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var versions = await provider.GetVersionsAsync(item.ProjectId, profile, cancellationToken: cancellationToken);
                    var version = versions.FirstOrDefault(candidate =>
                        string.Equals(candidate.VersionId, item.VersionId, StringComparison.Ordinal));
                    if (version?.File is null)
                    {
                        return ContentOperationResult.Fail(
                            "VersionUnavailable",
                            "The release disappeared from the provider before it could be installed.");
                    }

                    var stagingPath = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.jar");
                    await provider.DownloadAsync(version, stagingPath, progress, cancellationToken);

                    progress?.Report(new ContentInstallProgress(
                        ContentInstallStage.VerifyingHash,
                        "VerifyingHash",
                        item.FileName));
                    var digests = await FileDigests.ComputeAsync(stagingPath, true, cancellationToken);
                    var providerMatch = FileDigests.Matches(version.File.Sha512, digests.Sha512!) ??
                                        FileDigests.Matches(version.File.Sha256, digests.Sha256);
                    if (providerMatch == false)
                    {
                        return ContentOperationResult.Fail(
                            "HashMismatch",
                            "The downloaded file did not match the provider's hash.");
                    }

                    progress?.Report(new ContentInstallProgress(
                        ContentInstallStage.ValidatingArchive,
                        "ValidatingArchive",
                        item.FileName));
                    var validation = PluginJarValidator.Validate(stagingPath);
                    if (!validation.IsValid)
                    {
                        return ContentOperationResult.Fail("InvalidJar", validation.Reason!);
                    }

                    var destination = ContentPathPolicy.ResolveInstallPath(
                        profile.PluginsDirectory,
                        item.FileName);
                    staged.Add((stagingPath, destination, item, version));

                    installed.Add(new InstalledContent(
                        profile.ServerId,
                        item.FileName,
                        profile.IsRunning
                            ? InstalledContentState.RestartRequired
                            : InstalledContentState.UpToDate,
                        ContentKind.Plugin,
                        item.Provider,
                        item.ProjectId,
                        item.VersionId,
                        item.ProjectName,
                        item.VersionNumber,
                        profile.MinecraftVersion,
                        profile.Platform,
                        null,
                        version.File.Sha512,
                        version.File.Sha256,
                        digests.Sha256,
                        DateTimeOffset.UtcNow,
                        profile.IsRunning,
                        null,
                        null,
                        null,
                        null,
                        version.File.SizeBytes,
                        ManagedByManager: true));
                }

                progress?.Report(new ContentInstallProgress(
                    ContentInstallStage.Installing,
                    "Installing"));

                // The commit: past this point a cancellation is not honoured, because half an
                // install is worse than one more file.
                var committed = new List<string>();
                try
                {
                    foreach (var (stagingPath, destination, _, _) in staged)
                    {
                        File.Move(stagingPath, destination, true);
                        committed.Add(destination);
                    }
                }
                catch (IOException exception)
                {
                    foreach (var path in committed)
                    {
                        TryDelete(path);
                    }

                    logger.LogWarning(exception, "Plugin install could not be committed.");
                    return ContentOperationResult.Fail(
                        "InstallFailed",
                        "The plugin files could not be written to the server's plugins folder.");
                }

                progress?.Report(new ContentInstallProgress(
                    ContentInstallStage.RecordingMetadata,
                    "RecordingMetadata"));
                foreach (var record in installed)
                {
                    await store.UpsertAsync(record, cancellationToken);
                }

                await auditLog.WriteAsync(
                    "content",
                    "PluginInstalled",
                    $"server:{profile.ServerId}",
                    true,
                    string.Join(", ", installed.Select(item => $"{item.ProjectName} {item.InstalledVersion}")),
                    cancellationToken);

                progress?.Report(new ContentInstallProgress(
                    ContentInstallStage.Completed,
                    profile.IsRunning ? "RestartRequired" : "Installed"));
                return ContentOperationResult.Ok(profile.IsRunning, installed);
            }
            catch (ContentProviderException exception)
            {
                return ContentOperationResult.Fail(exception.ErrorCode, exception.Message);
            }
            catch (OperationCanceledException)
            {
                return ContentOperationResult.Fail("Cancelled", "The install was cancelled.");
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(exception, "Plugin install was refused by the filesystem.");
                return ContentOperationResult.Fail(
                    "AccessDenied",
                    "Windows refused access to the server's plugins folder.");
            }
            finally
            {
                // Staging never outlives the operation, whether it succeeded or not.
                foreach (var (stagingPath, _, _, _) in staged)
                {
                    TryDelete(stagingPath);
                }

                TryCleanDirectory(stagingRoot);
            }
        }
    }

    /// <summary>
    /// Replaces a managed plugin with a newer compatible release, keeping the old file so the
    /// change can be undone until the new one is known to work.
    /// </summary>
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
            return ContentOperationResult.Fail(
                "NotManaged",
                "That plugin was not installed by 1Salem Server Manager.");
        }

        var provider = catalog.Find(existing.Provider.Value);
        if (provider is null || !provider.CanServe(profile))
        {
            return ContentOperationResult.Fail(
                "ProviderUnavailable",
                "That plugin's provider cannot serve this server right now.");
        }

        ContentVersion? target;
        try
        {
            target = versionId is { Length: > 0 }
                ? (await provider.GetVersionsAsync(existing.ProjectId, profile, cancellationToken: cancellationToken))
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.VersionId, versionId, StringComparison.Ordinal))
                : await provider.ResolveCompatibleVersionAsync(
                    existing.ProjectId,
                    profile,
                    allowPrerelease: false,
                    cancellationToken: cancellationToken);
        }
        catch (ContentProviderException exception)
        {
            return ContentOperationResult.Fail(exception.ErrorCode, exception.Message);
        }

        if (target?.File is null)
        {
            return ContentOperationResult.Fail(
                "NoCompatibleVersion",
                "No compatible update is available for this server.");
        }

        if (string.Equals(target.VersionId, existing.VersionId, StringComparison.Ordinal))
        {
            return ContentOperationResult.Fail("AlreadyCurrent", "That plugin is already up to date.");
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
            var stagingPath = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.jar");

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

                var validation = PluginJarValidator.Validate(stagingPath);
                if (!validation.IsValid)
                {
                    return ContentOperationResult.Fail("InvalidJar", validation.Reason!);
                }

                var newFileName = ContentPathPolicy.SanitizeFileName(
                    target.File.FileName,
                    existing.ProjectId);
                var destination = ContentPathPolicy.ResolveInstallPath(
                    profile.PluginsDirectory,
                    newFileName);
                var currentPath = ContentPathPolicy.ResolveInstallPath(
                    profile.PluginsDirectory,
                    existing.FileName);

                // Keep the old JAR where rollback can find it before anything is replaced.
                var preserved = Path.Combine(
                    rollbackRoot,
                    $"{Path.GetFileNameWithoutExtension(existing.FileName)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.jar");
                if (File.Exists(currentPath))
                {
                    File.Copy(currentPath, preserved, true);
                }

                try
                {
                    File.Move(stagingPath, destination, true);
                    if (!string.Equals(currentPath, destination, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(currentPath))
                    {
                        File.Delete(currentPath);
                    }
                }
                catch (IOException exception)
                {
                    logger.LogWarning(exception, "Plugin update could not be committed.");
                    return ContentOperationResult.Fail(
                        "UpdateFailed",
                        profile.IsRunning
                            ? "The plugin file is in use. Stop the server and try again."
                            : "The plugin file could not be replaced.");
                }

                var updated = existing with
                {
                    FileName = newFileName,
                    VersionId = target.VersionId,
                    InstalledVersion = target.VersionNumber,
                    MinecraftVersionAtInstall = profile.MinecraftVersion,
                    PlatformAtInstall = profile.Platform,
                    ProviderSha512 = target.File.Sha512,
                    ProviderSha256 = target.File.Sha256,
                    LocalSha256 = digests.Sha256,
                    SizeBytes = target.File.SizeBytes,
                    InstalledAtUtc = DateTimeOffset.UtcNow,
                    RestartRequired = profile.IsRunning,
                    PreviousVersionId = existing.VersionId,
                    PreviousFileName = Path.GetFileName(preserved),
                    State = profile.IsRunning
                        ? InstalledContentState.RestartRequired
                        : InstalledContentState.UpToDate
                };

                if (!string.Equals(newFileName, existing.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    await store.RemoveAsync(profile.ServerId, existing.FileName, cancellationToken);
                }

                await store.UpsertAsync(updated, cancellationToken);
                await auditLog.WriteAsync(
                    "content",
                    "PluginUpdated",
                    $"server:{profile.ServerId}",
                    true,
                    $"{existing.ProjectName}: {existing.InstalledVersion} -> {target.VersionNumber}",
                    cancellationToken);
                return ContentOperationResult.Ok(profile.IsRunning, [updated]);
            }
            catch (ContentProviderException exception)
            {
                return ContentOperationResult.Fail(exception.ErrorCode, exception.Message);
            }
            finally
            {
                TryDelete(stagingPath);
                TryCleanDirectory(stagingRoot);
            }
        }
    }

    /// <summary>Puts the previous JAR back, after checking it is still the file we kept.</summary>
    public async Task<ContentOperationResult> RollbackAsync(
        ServerContentProfile profile,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var existing = await store.GetAsync(profile.ServerId, fileName, cancellationToken);
        if (existing?.PreviousFileName is null)
        {
            return ContentOperationResult.Fail(
                "NoRollback",
                "There is no earlier version of that plugin to go back to.");
        }

        var rollbackRoot = ContentPathPolicy.ResolveRollbackDirectory(profile.ServerRoot);
        var preserved = Path.Combine(rollbackRoot, Path.GetFileName(existing.PreviousFileName));
        if (!File.Exists(preserved) || !SafePathPolicy.IsWithinRoot(preserved, rollbackRoot))
        {
            return ContentOperationResult.Fail(
                "RollbackMissing",
                "The earlier version of that plugin is no longer stored.");
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
            var validation = PluginJarValidator.Validate(preserved);
            if (!validation.IsValid)
            {
                return ContentOperationResult.Fail("InvalidJar", validation.Reason!);
            }

            var digests = await FileDigests.ComputeAsync(preserved, false, cancellationToken);
            var destination = ContentPathPolicy.ResolveInstallPath(
                profile.PluginsDirectory,
                Path.GetFileName(preserved));
            var currentPath = ContentPathPolicy.ResolveInstallPath(
                profile.PluginsDirectory,
                existing.FileName);

            try
            {
                File.Copy(preserved, destination, true);
                if (!string.Equals(currentPath, destination, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(currentPath))
                {
                    File.Delete(currentPath);
                }
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Plugin rollback could not be committed.");
                return ContentOperationResult.Fail(
                    "RollbackFailed",
                    profile.IsRunning
                        ? "The plugin file is in use. Stop the server and try again."
                        : "The earlier plugin file could not be restored.");
            }

            await store.RemoveAsync(profile.ServerId, existing.FileName, cancellationToken);
            var restored = existing with
            {
                FileName = Path.GetFileName(destination),
                VersionId = existing.PreviousVersionId,
                InstalledVersion = null,
                LocalSha256 = digests.Sha256,
                ProviderSha256 = null,
                ProviderSha512 = null,
                PreviousVersionId = null,
                PreviousFileName = null,
                InstalledAtUtc = DateTimeOffset.UtcNow,
                RestartRequired = profile.IsRunning,
                State = profile.IsRunning
                    ? InstalledContentState.RestartRequired
                    : InstalledContentState.UnknownVersion
            };
            await store.UpsertAsync(restored, cancellationToken);
            await auditLog.WriteAsync(
                "content",
                "PluginRolledBack",
                $"server:{profile.ServerId}",
                true,
                existing.ProjectName,
                cancellationToken);
            return ContentOperationResult.Ok(profile.IsRunning, [restored]);
        }
    }

    /// <summary>
    /// Removes the plugin JAR and leaves its configuration folder alone. Deleting someone's
    /// permissions or economy data is a separate, deliberate decision, not a side effect of
    /// uninstalling.
    /// </summary>
    public async Task<ContentOperationResult> UninstallAsync(
        ServerContentProfile profile,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string target;
        try
        {
            target = ContentPathPolicy.ResolveInstallPath(profile.PluginsDirectory, fileName);
        }
        catch (UnauthorizedAccessException)
        {
            return ContentOperationResult.Fail("InvalidFileName", "That is not a plugin in this server.");
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
            if (File.Exists(target))
            {
                try
                {
                    File.Delete(target);
                }
                catch (IOException exception)
                {
                    logger.LogWarning(exception, "Plugin file could not be removed.");
                    return ContentOperationResult.Fail(
                        "FileInUse",
                        profile.IsRunning
                            ? "The server is using that file. Stop the server and try again."
                            : "The plugin file could not be removed.");
                }
                catch (UnauthorizedAccessException)
                {
                    return ContentOperationResult.Fail(
                        "AccessDenied",
                        "Windows refused to remove that plugin file.");
                }
            }

            await store.RemoveAsync(profile.ServerId, fileName, cancellationToken);
            await auditLog.WriteAsync(
                "content",
                "PluginUninstalled",
                $"server:{profile.ServerId}",
                true,
                $"{fileName} (configuration kept)",
                cancellationToken);
            return ContentOperationResult.Ok(profile.IsRunning);
        }
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

    private static void TryCleanDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
            {
                Directory.Delete(path);
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

