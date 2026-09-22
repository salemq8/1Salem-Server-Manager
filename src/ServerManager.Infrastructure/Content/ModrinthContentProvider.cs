using System.Net.Http.Json;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Modrinth, over its documented v2 API. Public reads only: no account, no token, nothing
/// stored. Endpoint shapes and semantics are recorded in docs/CONTENT_PROVIDERS.md.
/// </summary>
public sealed class ModrinthContentProvider(HttpClient client) : IContentProvider
{
    public const string BaseAddress = "https://api.modrinth.com/v2/";

    public ContentProviderId Id => ContentProviderId.Modrinth;

    public bool CanServe(ServerContentProfile profile) =>
        profile is { SupportsPlugins: true } &&
        PluginPlatformPolicy.ModrinthLoaders(profile.Platform).Count > 0;

    public async Task<ContentSearchResult> SearchAsync(
        ContentSearchRequest request,
        ServerContentProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);
        if (!CanServe(profile))
        {
            return new ContentSearchResult([], request.Offset, request.Limit, 0, []);
        }

        var loaders = PluginPlatformPolicy.ModrinthLoaders(profile.Platform);

        // Facets are AND between the inner arrays and OR inside one, so this reads as:
        // "a plugin, for any loader this server runs, (and for this Minecraft version)".
        var facets = new List<string>
        {
            Facet(["project_type:plugin"]),
            Facet([.. loaders.Select(loader => $"categories:{loader}")])
        };
        if (request.CompatibleOnly && !string.IsNullOrWhiteSpace(profile.MinecraftVersion))
        {
            facets.Add(Facet([$"versions:{profile.MinecraftVersion}"]));
        }

        var query = new List<string>
        {
            $"facets=[{string.Join(',', facets)}]",
            $"index={Index(request.Sort)}",
            $"offset={Math.Max(0, request.Offset)}",
            $"limit={Math.Clamp(request.Limit, 1, 100)}"
        };
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            query.Insert(0, $"query={Uri.EscapeDataString(request.Query.Trim())}");
        }

        using var document = await ContentHttp.GetJsonAsync(
            client,
            Id,
            $"search?{string.Join('&', query)}",
            cancellationToken);
        var root = document.RootElement;
        var projects = new List<ContentProject>();
        foreach (var hit in root.Array("hits"))
        {
            projects.Add(ReadProject(hit, profile));
        }

        return new ContentSearchResult(
            projects,
            (int)(root.Int64("offset") ?? request.Offset),
            (int)(root.Int64("limit") ?? request.Limit),
            (int)(root.Int64("total_hits") ?? projects.Count),
            []);
    }

    public async Task<ContentProject?> GetProjectAsync(
        string projectId,
        ServerContentProfile profile,
        CancellationToken cancellationToken = default)
    {
        using var document = await ContentHttp.GetJsonAsync(
            client,
            Id,
            $"project/{Uri.EscapeDataString(projectId)}",
            cancellationToken);
        return ReadProject(document.RootElement, profile, includeBody: true);
    }

    public async Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(
        string projectId,
        ServerContentProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var loaders = PluginPlatformPolicy.ModrinthLoaders(profile.Platform);
        var query = new List<string>();
        if (loaders.Count > 0)
        {
            query.Add($"loaders={Encode(loaders)}");
        }

        if (!string.IsNullOrWhiteSpace(profile.MinecraftVersion))
        {
            query.Add($"game_versions={Encode([profile.MinecraftVersion])}");
        }

        var suffix = query.Count > 0 ? $"?{string.Join('&', query)}" : string.Empty;
        using var document = await ContentHttp.GetJsonAsync(
            client,
            Id,
            $"project/{Uri.EscapeDataString(projectId)}/version{suffix}",
            cancellationToken);

        var versions = new List<ContentVersion>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var version = ReadVersion(element);
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
        CancellationToken cancellationToken = default)
    {
        var versions = await GetVersionsAsync(projectId, profile, cancellationToken);
        return PluginCompatibilityPolicy.SelectBest(versions, profile, allowPrerelease);
    }

    public async Task<ContentIdentification?> IdentifyAsync(
        ContentFileDigests digests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digests);
        if (string.IsNullOrWhiteSpace(digests.Sha1))
        {
            return null;
        }

        try
        {
            using var document = await ContentHttp.GetJsonAsync(
                client,
                Id,
                $"version_file/{digests.Sha1.ToLowerInvariant()}?algorithm=sha1",
                cancellationToken);
            var root = document.RootElement;
            var projectId = root.String("project_id");
            var versionId = root.String("id");
            if (projectId is null || versionId is null)
            {
                return null;
            }

            return new ContentIdentification(
                Id,
                projectId,
                root.String("name"),
                versionId,
                root.String("version_number"),
                ProjectUrl(projectId));
        }
        catch (ContentProviderException exception)
            when (exception.ErrorCode == ContentProviderException.NotFoundCode)
        {
            // Not a Modrinth file, which is a normal answer rather than a failure.
            return null;
        }
    }

    /// <summary>
    /// Asks Modrinth, for a set of installed file hashes, which newer release fits this
    /// server. Using the provider's own update route keeps the answer authoritative instead
    /// of guessing from file names.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ContentVersion>> FindUpdatesAsync(
        IReadOnlyCollection<string> sha1Hashes,
        ServerContentProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sha1Hashes);
        ArgumentNullException.ThrowIfNull(profile);
        if (sha1Hashes.Count == 0 || !CanServe(profile))
        {
            return new Dictionary<string, ContentVersion>();
        }

        var payload = new
        {
            hashes = sha1Hashes.Select(hash => hash.ToLowerInvariant()).ToArray(),
            algorithm = "sha1",
            loaders = PluginPlatformPolicy.ModrinthLoaders(profile.Platform).ToArray(),
            game_versions = string.IsNullOrWhiteSpace(profile.MinecraftVersion)
                ? Array.Empty<string>()
                : new[] { profile.MinecraftVersion }
        };

        using var content = JsonContent.Create(payload);
        using var document = await ContentHttp.PostJsonAsync(
            client,
            Id,
            "version_files/update",
            content,
            cancellationToken);

        var results = new Dictionary<string, ContentVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var version = ReadVersion(property.Value);
            if (version is not null)
            {
                results[property.Name] = version;
            }
        }

        return results;
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
                "That release has no downloadable file.");
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
        // Search hits use project_id; the project endpoint uses id.
        var projectId = element.String("project_id") ?? element.String("id") ?? string.Empty;
        var slug = element.String("slug") ?? projectId;

        // A server plugin is reported as project_type "mod", so the loaders are what identify
        // it. Only the loaders this server could actually run are shown on the card.
        var loaders = element.Strings("categories")
            .Concat(element.Strings("loaders"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var runnable = PluginPlatformPolicy.ModrinthLoaders(profile.Platform);
        var platforms = loaders
            .Where(loader => runnable.Contains(loader, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var gameVersions = element.Strings("versions").Concat(element.Strings("game_versions"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var licenseElement = element.Property("license");
        var license = licenseElement?.ValueKind == JsonValueKind.Object
            ? licenseElement.Value.String("name") ?? licenseElement.Value.String("id")
            : element.String("license");

        var compatible = platforms.Length > 0 &&
                         (profile.MinecraftVersion is null ||
                          gameVersions.Contains(profile.MinecraftVersion, StringComparer.OrdinalIgnoreCase));

        return new ContentProject(
            Id,
            projectId,
            slug,
            element.String("title") ?? slug,
            element.String("description"),
            element.String("author"),
            element.Url("icon_url"),
            ProjectUrl(slug),
            element.Int64("downloads"),
            platforms,
            gameVersions,
            license,
            ContentKind.Plugin,
            includeBody ? element.String("body") : null,
            compatible);
    }

    private ContentVersion? ReadVersion(JsonElement element)
    {
        var versionId = element.String("id");
        var projectId = element.String("project_id");
        if (versionId is null || projectId is null)
        {
            return null;
        }

        ContentFile? file = null;
        foreach (var candidate in element.Array("files"))
        {
            var url = candidate.Url("url");
            var name = candidate.String("filename");
            if (url is null || name is null)
            {
                continue;
            }

            var hashes = candidate.Property("hashes");
            var parsed = new ContentFile(
                name,
                url,
                candidate.Int64("size") ?? 0,
                hashes?.String("sha512"),
                null,
                hashes?.String("sha1"),
                candidate.Bool("primary"));

            // Prefer the primary file; otherwise keep the first usable one.
            if (parsed.IsPrimary || file is null)
            {
                file = parsed;
            }
        }

        var dependencies = new List<ContentDependency>();
        foreach (var dependency in element.Array("dependencies"))
        {
            dependencies.Add(new ContentDependency(
                DependencyKind(dependency.String("dependency_type")),
                Id,
                dependency.String("project_id"),
                dependency.String("version_id"),
                dependency.String("file_name")));
        }

        return new ContentVersion(
            Id,
            projectId,
            versionId,
            element.String("version_number") ?? versionId,
            Channel(element.String("version_type")),
            element.Timestamp("date_published"),
            element.Strings("loaders"),
            element.Strings("game_versions"),
            file,
            dependencies,
            null,
            element.String("changelog"),
            element.String("name"));
    }

    private static Uri ProjectUrl(string slug) =>
        new($"https://modrinth.com/plugin/{Uri.EscapeDataString(slug)}");

    private static string Facet(IReadOnlyList<string> values) =>
        "[" + string.Join(',', values.Select(value => $"\"{value}\"")) + "]";

    private static string Encode(IReadOnlyList<string> values) =>
        Uri.EscapeDataString("[" + string.Join(',', values.Select(value => $"\"{value}\"")) + "]");

    private static string Index(ContentSortOrder sort) =>
        sort switch
        {
            ContentSortOrder.Downloads => "downloads",
            ContentSortOrder.Updated => "updated",
            ContentSortOrder.Newest => "newest",
            _ => "relevance"
        };

    private static ContentReleaseChannel Channel(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "beta" => ContentReleaseChannel.Beta,
            "alpha" => ContentReleaseChannel.Alpha,
            _ => ContentReleaseChannel.Release
        };

    private static ContentDependencyKind DependencyKind(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "optional" => ContentDependencyKind.Optional,
            "incompatible" => ContentDependencyKind.Incompatible,
            "embedded" => ContentDependencyKind.Embedded,
            _ => ContentDependencyKind.Required
        };
}
