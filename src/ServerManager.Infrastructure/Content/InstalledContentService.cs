using Microsoft.Extensions.Logging;
using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Reconciles what the manager recorded with what is actually on disk, across every content
/// type. The disk wins: a recorded item whose file is gone is reported missing, and a file
/// somebody added by hand is reported as exactly that rather than being claimed as ours.
/// </summary>
public sealed class InstalledContentService(
    ContentCatalogService catalog,
    IInstalledContentStore store,
    ILogger<InstalledContentService> logger)
{
    public async Task<IReadOnlyList<InstalledContent>> ListAsync(
        ServerContentProfile profile,
        bool identifyManualPlugins = false,
        bool checkForUpdates = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var recorded = (await store.ListAsync(profile.ServerId, cancellationToken))
            .ToDictionary(record => record.FileName, StringComparer.OrdinalIgnoreCase);
        var results = new List<InstalledContent>();
        var sha1ByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Each content type lives somewhere different: plugins in plugins\, data packs in the
        // world the server actually runs, resource packs in this app's own storage.
        var locations = new List<(ContentKind Kind, string Directory, string Pattern)>();
        if (Directory.Exists(profile.PluginsDirectory))
        {
            locations.Add((ContentKind.Plugin, profile.PluginsDirectory, "*.jar"));
        }

        foreach (var kind in new[] { ContentKind.DataPack, ContentKind.ResourcePack })
        {
            var (directory, _) = PackInstallService.ResolveDestination(profile, kind);
            if (directory is not null && Directory.Exists(directory))
            {
                locations.Add((kind, directory, "*.zip"));
            }
        }

        // The modpack a server was built from is the managed unit; the mods inside it are not
        // listed separately, because they were never installed one by one.
        var modpackDirectory = ContentPathPolicy.ResolveModpackDirectory(profile.ServerRoot);
        if (Directory.Exists(modpackDirectory))
        {
            locations.Add((ContentKind.Modpack, modpackDirectory, "*.mrpack"));
        }

        var (distributedUrl, _, _) = ServerWorldLocator.ReadResourcePackSettings(profile.ServerRoot);

        foreach (var (kind, directory, pattern) in locations)
        {
            foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(path);
                ContentFileDigests digests;
                long size;
                try
                {
                    digests = await FileDigests.ComputeAsync(path, false, cancellationToken);
                    size = new FileInfo(path).Length;
                }
                catch (IOException exception)
                {
                    logger.LogDebug(exception, "A content file could not be read while listing.");
                    continue;
                }

                sha1ByFile[fileName] = digests.Sha1;

                if (recorded.Remove(fileName, out var record))
                {
                    results.Add(record with
                    {
                        State = DescribeState(record, digests, profile, distributedUrl),
                        SizeBytes = size,
                        IsDistributed = IsDistributed(record, distributedUrl)
                    });
                    continue;
                }

                var manual = new InstalledContent(
                    profile.ServerId,
                    fileName,
                    InstalledContentState.InstalledManually,
                    kind,
                    LocalSha256: digests.Sha256,
                    SizeBytes: size,
                    ManagedByManager: false);

                if (identifyManualPlugins)
                {
                    manual = await IdentifyAsync(manual, digests, profile, cancellationToken);
                }

                results.Add(manual);
            }
        }

        // Anything still recorded has no file on disk any more.
        results.AddRange(recorded.Values.Select(record =>
            record with { State = InstalledContentState.MissingFile }));

        if (checkForUpdates)
        {
            results = (await WithUpdatesAsync(profile, results, sha1ByFile, cancellationToken)).ToList();
        }

        return results
            .OrderBy(record => record.Kind)
            .ThenBy(record => record.ProjectName ?? record.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// What a recorded item's state is right now: changed on disk, waiting for a reload or a
    /// restart, being advertised to clients, or simply current.
    /// </summary>
    private static InstalledContentState DescribeState(
        InstalledContent record,
        ContentFileDigests digests,
        ServerContentProfile profile,
        string? distributedUrl)
    {
        if (record.LocalSha256 is { Length: > 0 } expected &&
            !string.Equals(expected, digests.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return InstalledContentState.ModifiedLocally;
        }

        if (record.Kind == ContentKind.ResourcePack)
        {
            return IsDistributed(record, distributedUrl)
                ? InstalledContentState.Distributed
                : InstalledContentState.NotDistributed;
        }

        // A modpack decided this server's Minecraft version, so the version it was installed
        // for is not a mismatch to report; only a newer pack release says anything.
        if (record.Kind == ContentKind.Modpack)
        {
            return InstalledContentState.UpToDate;
        }

        // Installed for one Minecraft version and the server has since moved on.
        if (record.MinecraftVersionAtInstall is { Length: > 0 } installedFor &&
            profile.MinecraftVersion is { Length: > 0 } current &&
            !string.Equals(installedFor, current, StringComparison.OrdinalIgnoreCase))
        {
            return InstalledContentState.IncompatibleWithServer;
        }

        if (record.RestartRequired)
        {
            return record.Kind == ContentKind.DataPack
                ? InstalledContentState.ReloadRequired
                : InstalledContentState.RestartRequired;
        }

        return InstalledContentState.UpToDate;
    }

    private static bool IsDistributed(InstalledContent record, string? distributedUrl) =>
        record.Kind == ContentKind.ResourcePack &&
        record.DownloadUrl is { } url &&
        distributedUrl is { Length: > 0 } &&
        string.Equals(url.ToString(), distributedUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks the providers what a file added by hand is, by hash. Only a hash match counts as
    /// identity; a similar file name never does.
    /// </summary>
    private async Task<InstalledContent> IdentifyAsync(
        InstalledContent manual,
        ContentFileDigests digests,
        ServerContentProfile profile,
        CancellationToken cancellationToken)
    {
        foreach (var providerId in new[] { ContentProviderId.Modrinth, ContentProviderId.Hangar })
        {
            var provider = catalog.Find(providerId);
            if (provider is null || !provider.CanServe(profile, manual.Kind))
            {
                continue;
            }

            try
            {
                var identification = await provider.IdentifyAsync(digests, cancellationToken);
                if (identification is null)
                {
                    continue;
                }

                return manual with
                {
                    Provider = identification.Provider,
                    ProjectId = identification.ProjectId,
                    ProjectName = identification.ProjectName,
                    VersionId = identification.VersionId,
                    InstalledVersion = identification.VersionNumber,
                    ProjectUrl = identification.ProjectUrl
                };
            }
            catch (ContentProviderException exception)
            {
                logger.LogDebug(
                    exception,
                    "Identification by hash was unavailable from {Provider}.",
                    providerId);
            }
        }

        return manual;
    }

    /// <summary>
    /// Marks items with a newer release that still fits this server. Compatibility is judged
    /// against the server as it is now, so a newer release for a different Minecraft version
    /// is not an update and is never offered as one.
    /// </summary>
    private async Task<IReadOnlyList<InstalledContent>> WithUpdatesAsync(
        ServerContentProfile profile,
        IReadOnlyList<InstalledContent> installed,
        IReadOnlyDictionary<string, string> sha1ByFile,
        CancellationToken cancellationToken)
    {
        var results = installed.ToList();

        // Modrinth answers for many files at once from their hashes, which is both faster and
        // more reliable than matching by name. The loaders differ per content type, so this
        // is asked once per kind.
        if (catalog.Find(ContentProviderId.Modrinth) is ModrinthContentProvider modrinth)
        {
            foreach (var kind in new[] { ContentKind.Plugin, ContentKind.DataPack, ContentKind.ResourcePack })
            {
                if (!modrinth.CanServe(profile, kind))
                {
                    continue;
                }

                var candidates = results
                    .Where(record => record.Kind == kind)
                    .Where(record => record.State is not InstalledContentState.MissingFile)
                    .Where(record => record.Provider is null or ContentProviderId.Modrinth)
                    .Where(record => sha1ByFile.ContainsKey(record.FileName))
                    .ToArray();
                if (candidates.Length == 0)
                {
                    continue;
                }

                try
                {
                    var hashes = candidates.Select(record => sha1ByFile[record.FileName]).ToArray();
                    var updates = await modrinth.FindUpdatesAsync(hashes, profile, kind, cancellationToken);
                    foreach (var record in candidates)
                    {
                        var hash = sha1ByFile[record.FileName];
                        if (!updates.TryGetValue(hash, out var version) ||
                            string.Equals(version.VersionId, record.VersionId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var index = results.IndexOf(record);
                        results[index] = record with
                        {
                            State = InstalledContentState.UpdateAvailable,
                            AvailableVersionId = version.VersionId,
                            AvailableVersionNumber = version.VersionNumber
                        };
                    }
                }
                catch (ContentProviderException exception)
                {
                    logger.LogDebug(exception, "A Modrinth update check was unavailable.");
                }
            }
        }

        // A modpack is checked against its own project, and the answer is classified before
        // anything is offered: a newer pack that changes the Minecraft version or the loader
        // is a server migration, not an update.
        foreach (var record in results.ToArray())
        {
            if (record.Kind != ContentKind.Modpack ||
                record.ProjectId is null ||
                record.Provider is not { } modpackProvider ||
                record.State is InstalledContentState.MissingFile)
            {
                continue;
            }

            var provider = catalog.Find(modpackProvider);
            if (provider is null)
            {
                continue;
            }

            try
            {
                var versions = await provider.GetVersionsAsync(
                    record.ProjectId,
                    profile,
                    ContentKind.Modpack,
                    cancellationToken);
                var newest = versions
                    .Where(version => version.File is not null)
                    .Where(version => version.Channel == ContentReleaseChannel.Release)
                    .OrderByDescending(version => version.PublishedAtUtc ?? DateTimeOffset.MinValue)
                    .FirstOrDefault();
                if (newest is null ||
                    string.Equals(newest.VersionId, record.VersionId, StringComparison.Ordinal))
                {
                    continue;
                }

                var sameMinecraft = record.MinecraftVersionAtInstall is { Length: > 0 } installedFor &&
                                    newest.GameVersions.Contains(installedFor, StringComparer.OrdinalIgnoreCase);
                var sameLoader = record.Loader is { Length: > 0 } loader &&
                                 newest.Platforms.Contains(loader, StringComparer.OrdinalIgnoreCase);

                var index = results.IndexOf(record);
                results[index] = record with
                {
                    State = sameMinecraft && sameLoader
                        ? InstalledContentState.UpdateAvailable
                        : InstalledContentState.RequiresServerMigration,
                    AvailableVersionId = newest.VersionId,
                    AvailableVersionNumber = newest.VersionNumber
                };
            }
            catch (ContentProviderException exception)
            {
                logger.LogDebug(exception, "A modpack update check was unavailable.");
            }
        }

        // Hangar has no bulk route, so managed Hangar plugins are checked one by one.
        foreach (var record in results.ToArray())
        {
            if (record.Provider != ContentProviderId.Hangar ||
                record.ProjectId is null ||
                record.State is InstalledContentState.MissingFile)
            {
                continue;
            }

            var provider = catalog.Find(ContentProviderId.Hangar);
            if (provider is null || !provider.CanServe(profile, record.Kind))
            {
                continue;
            }

            try
            {
                var version = await provider.ResolveCompatibleVersionAsync(
                    record.ProjectId,
                    profile,
                    false,
                    record.Kind,
                    cancellationToken);
                if (version is null ||
                    string.Equals(version.VersionId, record.VersionId, StringComparison.Ordinal))
                {
                    continue;
                }

                var index = results.IndexOf(record);
                results[index] = record with
                {
                    State = InstalledContentState.UpdateAvailable,
                    AvailableVersionId = version.VersionId,
                    AvailableVersionNumber = version.VersionNumber
                };
            }
            catch (ContentProviderException exception)
            {
                logger.LogDebug(exception, "A Hangar update check was unavailable.");
            }
        }

        return results;
    }
}
