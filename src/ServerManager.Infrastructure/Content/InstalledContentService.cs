using Microsoft.Extensions.Logging;
using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Reconciles what the manager recorded with what is actually in the plugins folder. The
/// disk wins: a recorded plugin whose file is gone is reported missing, and a file the person
/// added by hand is reported as exactly that rather than being claimed as ours.
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

        if (!Directory.Exists(profile.PluginsDirectory))
        {
            // No folder yet simply means nothing is installed; the folder appears on the
            // first install.
            return recorded.Values
                .Select(record => record with { State = InstalledContentState.MissingFile })
                .ToArray();
        }

        foreach (var path in Directory.EnumerateFiles(
                     profile.PluginsDirectory,
                     "*.jar",
                     SearchOption.TopDirectoryOnly))
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
                logger.LogDebug(exception, "A plugin file could not be read while listing.");
                continue;
            }

            sha1ByFile[fileName] = digests.Sha1;

            if (recorded.Remove(fileName, out var record))
            {
                var state = record.LocalSha256 is { Length: > 0 } expected &&
                            !string.Equals(expected, digests.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? InstalledContentState.ModifiedLocally
                    : record.RestartRequired
                        ? InstalledContentState.RestartRequired
                        : InstalledContentState.UpToDate;
                results.Add(record with { State = state, SizeBytes = size });
                continue;
            }

            var manual = new InstalledContent(
                profile.ServerId,
                fileName,
                InstalledContentState.InstalledManually,
                ContentKind.Plugin,
                LocalSha256: digests.Sha256,
                SizeBytes: size,
                ManagedByManager: false);

            if (identifyManualPlugins)
            {
                manual = await IdentifyAsync(manual, digests, profile, cancellationToken);
            }

            results.Add(manual);
        }

        // Anything still recorded has no file on disk any more.
        results.AddRange(recorded.Values.Select(record =>
            record with { State = InstalledContentState.MissingFile }));

        if (checkForUpdates)
        {
            results = (await WithUpdatesAsync(profile, results, sha1ByFile, cancellationToken)).ToList();
        }

        return results
            .OrderBy(record => record.ProjectName ?? record.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Asks the providers what a manually added file is, by hash. Only a hash match is
    /// accepted as identity; a similar file name never is.
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
            if (provider is null || !provider.CanServe(profile))
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
    /// Marks the plugins with a newer release that still fits this server. A newer version
    /// that does not support the server's Minecraft version or platform is not an update and
    /// is not offered.
    /// </summary>
    private async Task<IReadOnlyList<InstalledContent>> WithUpdatesAsync(
        ServerContentProfile profile,
        IReadOnlyList<InstalledContent> installed,
        IReadOnlyDictionary<string, string> sha1ByFile,
        CancellationToken cancellationToken)
    {
        var results = installed.ToList();

        // Modrinth answers for many files at once from their hashes, which is both faster and
        // more reliable than matching by name.
        if (catalog.Find(ContentProviderId.Modrinth) is ModrinthContentProvider modrinth &&
            modrinth.CanServe(profile))
        {
            var candidates = results
                .Where(record => record.State is not InstalledContentState.MissingFile)
                .Where(record => record.Provider is null or ContentProviderId.Modrinth)
                .Where(record => sha1ByFile.ContainsKey(record.FileName))
                .ToArray();
            if (candidates.Length > 0)
            {
                try
                {
                    var hashes = candidates.Select(record => sha1ByFile[record.FileName]).ToArray();
                    var updates = await modrinth.FindUpdatesAsync(hashes, profile, cancellationToken);
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
                    logger.LogDebug(exception, "Modrinth update check was unavailable.");
                }
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
            if (provider is null || !provider.CanServe(profile))
            {
                continue;
            }

            try
            {
                var version = await provider.ResolveCompatibleVersionAsync(
                    record.ProjectId,
                    profile,
                    allowPrerelease: false,
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
                logger.LogDebug(exception, "Hangar update check was unavailable.");
            }
        }

        return results;
    }
}
