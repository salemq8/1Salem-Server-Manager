using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Hangar, PaperMC's plugin repository, over its documented v1 API using the current
/// single-segment project paths. The older /projects/{author}/{slug} forms are deprecated in
/// Hangar's own specification and are not used. See docs/CONTENT_PROVIDERS.md.
/// </summary>
public sealed class HangarContentProvider(HttpClient client) : IContentProvider
{
    public const string BaseAddress = "https://hangar.papermc.io/api/v1/";

    public ContentProviderId Id => ContentProviderId.Hangar;

    public bool CanServe(ServerContentProfile profile) =>
        profile is { SupportsPlugins: true } &&
        PluginPlatformPolicy.HangarPlatform(profile.Platform) is not null;

    /// <summary>
    /// Hangar is a plugin repository. It has no modpacks, data packs or resource packs, so it
    /// answers no for every other content type rather than returning an empty list.
    /// </summary>
    public bool CanServe(ServerContentProfile profile, ContentKind kind) =>
        kind == ContentKind.Plugin && CanServe(profile);

    public async Task<ContentSearchResult> SearchAsync(
        ContentSearchRequest request,
        ServerContentProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);
        var platform = PluginPlatformPolicy.HangarPlatform(profile.Platform);
        if (platform is null || request.Kind != ContentKind.Plugin)
        {
            return new ContentSearchResult([], request.Offset, request.Limit, 0, []);
        }

        var query = new List<string>
        {
            $"platform={platform}",
            $"offset={Math.Max(0, request.Offset)}",
            $"limit={Math.Clamp(request.Limit, 1, 25)}"
        };
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            query.Add($"q={Uri.EscapeDataString(request.Query.Trim())}");
            query.Add("prioritizeExactMatch=true");
        }

        // Hangar rejects "relevance" as a sort value, so relevance is expressed by not
        // sorting at all and letting the query decide the order.
        if (Sort(request.Sort) is { } sort)
        {
            query.Add($"sort={sort}");
        }

        if (request.CompatibleOnly && !string.IsNullOrWhiteSpace(profile.MinecraftVersion))
        {
            query.Add($"version={Uri.EscapeDataString(profile.MinecraftVersion)}");
        }

        using var document = await ContentHttp.GetJsonAsync(
            client,
            Id,
            $"projects?{string.Join('&', query)}",
            cancellationToken);
        var root = document.RootElement;
        var projects = new List<ContentProject>();
        foreach (var element in root.Array("result"))
        {
            projects.Add(ReadProject(element, profile));
        }

        var pagination = root.Property("pagination");
        return new ContentSearchResult(
            projects,
            (int)(pagination?.Int64("offset") ?? request.Offset),
            (int)(pagination?.Int64("limit") ?? request.Limit),
            (int)(pagination?.Int64("count") ?? projects.Count),
            []);
    }

    public async Task<ContentProject?> GetProjectAsync(
        string projectId,
        ServerContentProfile profile,
        ContentKind kind = ContentKind.Plugin,
        CancellationToken cancellationToken = default)
    {
        using var document = await ContentHttp.GetJsonAsync(
            client,
            Id,
            $"projects/{Uri.EscapeDataString(projectId)}",
            cancellationToken);
        return ReadProject(document.RootElement, profile, includeBody: true);
    }

    public async Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(
        string projectId,
        ServerContentProfile profile,
        ContentKind kind = ContentKind.Plugin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var platform = PluginPlatformPolicy.HangarPlatform(profile.Platform);
        if (platform is null || kind != ContentKind.Plugin)
        {
            return [];
        }

        // Hangar filters by platform and Minecraft version server-side, so compatibility is
        // decided by the provider rather than guessed here.
        var query = new List<string> { $"platform={platform}", "limit=25" };
        if (!string.IsNullOrWhiteSpace(profile.MinecraftVersion))
        {
            query.Add($"platformVersion={Uri.EscapeDataString(profile.MinecraftVersion)}");
        }

        using var document = await ContentHttp.GetJsonAsync(
            client,
            Id,
            $"projects/{Uri.EscapeDataString(projectId)}/versions?{string.Join('&', query)}",
            cancellationToken);

        var versions = new List<ContentVersion>();
        foreach (var element in document.RootElement.Array("result"))
        {
            var version = ReadVersion(element, projectId, platform);
            if (version is not null)
            {
                versions.Add(version);
            }
        }

        return versions;
    }

    public async Task<ContentVersion?> ResolveCompatibleVersionAsync(
        string projectId,
        ServerContentProfile profile,
        bool allowPrerelease = false,
        ContentKind kind = ContentKind.Plugin,
        CancellationToken cancellationToken = default)
    {
        var versions = await GetVersionsAsync(projectId, profile, kind, cancellationToken);
        return PluginCompatibilityPolicy.SelectBest(versions, profile, allowPrerelease, kind);
    }

    public async Task<ContentIdentification?> IdentifyAsync(
        ContentFileDigests digests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digests);
        if (string.IsNullOrWhiteSpace(digests.Sha256))
        {
            return null;
        }

        try
        {
            using var document = await ContentHttp.GetJsonAsync(
                client,
                Id,
                $"versions/hash/{digests.Sha256.ToLowerInvariant()}",
                cancellationToken);
            var root = document.RootElement;

            // This endpoint answers with the project, not the version, so the version stays
            // unknown instead of being guessed from the file name.
            var slug = root.Property("namespace")?.String("slug") ?? root.String("name");
            if (slug is null)
            {
                return null;
            }

            return new ContentIdentification(
                Id,
                slug,
                root.String("name"),
                null,
                null,
                ProjectUrl(root));
        }
        catch (ContentProviderException exception)
            when (exception.ErrorCode == ContentProviderException.NotFoundCode)
        {
            return null;
        }
    }

    public Task DownloadAsync(
        ContentVersion version,
        string stagingFilePath,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.File is null)
        {
            throw new ContentProviderException(
                Id,
                ContentProviderException.SchemaCode,
                version.ExternalDownloadUrl is null
                    ? "That release has no downloadable file."
                    : "That release is hosted outside Hangar, so it is not installed from here.");
        }

        return ContentHttp.DownloadAsync(
            client,
            Id,
            version.File.Url,
            stagingFilePath,
            version.File.SizeBytes,
            version.File.FileName,
            progress,
            cancellationToken);
    }

    private ContentProject ReadProject(
        JsonElement element,
        ServerContentProfile profile,
        bool includeBody = false)
    {
        var slug = element.Property("namespace")?.String("slug") ?? element.String("name") ?? string.Empty;
        var platform = PluginPlatformPolicy.HangarPlatform(profile.Platform);
        var supported = element.Property("supportedPlatforms");
        var gameVersions = new List<string>();
        var platforms = new List<string>();
        if (supported?.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in supported.Value.EnumerateObject())
            {
                platforms.Add(entry.Name);
                if (platform is not null &&
                    string.Equals(entry.Name, platform, StringComparison.OrdinalIgnoreCase) &&
                    entry.Value.ValueKind == JsonValueKind.Array)
                {
                    gameVersions.AddRange(
                        entry.Value.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString()!)
                            .Where(value => value.Length > 0));
                }
            }
        }

        var settings = element.Property("settings");
        var licenseElement = settings?.Property("license");
        var license = licenseElement?.String("name") ?? licenseElement?.String("type");
        var compatible = platform is not null &&
                         platforms.Contains(platform, StringComparer.OrdinalIgnoreCase) &&
                         (profile.MinecraftVersion is null ||
                          gameVersions.Contains(profile.MinecraftVersion, StringComparer.OrdinalIgnoreCase));

        return new ContentProject(
            Id,
            slug,
            slug,
            element.String("name") ?? slug,
            element.String("description"),
            element.Property("namespace")?.String("owner") ??
                element.Strings("memberNames").FirstOrDefault(),
            element.Url("avatarUrl"),
            ProjectUrl(element),
            element.Property("stats")?.Int64("downloads"),
            platforms,
            gameVersions,
            license,
            ContentKind.Plugin,
            includeBody ? element.String("mainPageContent") : null,
            compatible);
    }

    private ContentVersion? ReadVersion(JsonElement element, string projectId, string platform)
    {
        var name = element.String("name");
        if (name is null)
        {
            return null;
        }

        var platformVersions = new List<string>();
        var dependencies = element.Property("platformDependencies");
        if (dependencies?.ValueKind == JsonValueKind.Object &&
            dependencies.Value.TryGetProperty(platform, out var versions) &&
            versions.ValueKind == JsonValueKind.Array)
        {
            platformVersions.AddRange(
                versions.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .Where(value => value.Length > 0));
        }

        var platforms = new List<string>();
        if (dependencies?.ValueKind == JsonValueKind.Object)
        {
            platforms.AddRange(dependencies.Value.EnumerateObject().Select(entry => entry.Name));
        }

        ContentFile? file = null;
        Uri? externalUrl = null;
        var downloads = element.Property("downloads");
        if (downloads?.ValueKind == JsonValueKind.Object &&
            downloads.Value.TryGetProperty(platform, out var download))
        {
            var info = download.Property("fileInfo");
            var url = download.Url("downloadUrl");
            externalUrl = download.Url("externalUrl");
            if (url is not null && info is not null)
            {
                file = new ContentFile(
                    info.Value.String("name") ?? $"{projectId}-{name}.jar",
                    url,
                    info.Value.Int64("sizeBytes") ?? 0,
                    null,
                    info.Value.String("sha256Hash"),
                    null);
            }
        }

        var pluginDependencies = new List<ContentDependency>();
        var declared = element.Property("pluginDependencies");
        if (declared?.ValueKind == JsonValueKind.Object &&
            declared.Value.TryGetProperty(platform, out var entries) &&
            entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                pluginDependencies.Add(new ContentDependency(
                    entry.Bool("required", true)
                        ? ContentDependencyKind.Required
                        : ContentDependencyKind.Optional,
                    Id,
                    entry.String("name"),
                    null,
                    entry.String("name"),
                    entry.Url("externalUrl")));
            }
        }

        return new ContentVersion(
            Id,
            projectId,
            name,
            name,
            Channel(element.Property("channel")?.String("name")),
            element.Timestamp("createdAt"),
            platforms,
            platformVersions,
            file,
            pluginDependencies,
            externalUrl,
            element.String("description"),
            name);
    }

    private static Uri? ProjectUrl(JsonElement element)
    {
        // A Hangar project page lives under its owner: hangar.papermc.io/{owner}/{slug}.
        var space = element.Property("namespace");
        var owner = space?.String("owner");
        var slug = space?.String("slug");
        return owner is null || slug is null
            ? null
            : new Uri(
                $"https://hangar.papermc.io/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(slug)}");
    }

    private static string? Sort(ContentSortOrder sort) =>
        sort switch
        {
            ContentSortOrder.Downloads => "-downloads",
            ContentSortOrder.Updated => "-updated",
            ContentSortOrder.Newest => "-newest",
            _ => null
        };

    private static ContentReleaseChannel Channel(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "beta" or "pre-release" or "prerelease" => ContentReleaseChannel.Beta,
            "alpha" or "snapshot" => ContentReleaseChannel.Alpha,
            _ => ContentReleaseChannel.Release
        };
}

