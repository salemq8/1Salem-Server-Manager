using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed class MinecraftVersionCatalog(HttpClient httpClient) : IMinecraftVersionCatalog
{
    public static readonly Uri ManifestUri =
        new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly SemaphoreSlim _metadataGate = new(6, 6);
    private IReadOnlyList<MinecraftVersionDescriptor>? _cachedReleases;
    private DateTimeOffset _cacheExpiresAtUtc;

    public async Task<IReadOnlyList<MinecraftVersionDescriptor>> GetReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cachedReleases is { } cached &&
            DateTimeOffset.UtcNow < _cacheExpiresAtUtc)
        {
            return cached;
        }

        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedReleases is { } current &&
                DateTimeOffset.UtcNow < _cacheExpiresAtUtc)
            {
                return current;
            }

            var manifest = await GetManifestAsync(cancellationToken);
            var supported = manifest.Versions
                .Where(version => version.Type == "release")
                .Take(25)
                .Select(entry => ResolveLimitedAsync(entry, cancellationToken));
            _cachedReleases = await Task.WhenAll(supported);
            _cacheExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
            return _cachedReleases;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    public async Task<MinecraftVersionDescriptor> GetVersionAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("A Minecraft version is required.", nameof(version));
        }

        if (_cachedReleases is { } cached)
        {
            var cachedVersion = cached.FirstOrDefault(candidate =>
                candidate.Id.Equals(version, StringComparison.OrdinalIgnoreCase));
            if (cachedVersion is not null)
            {
                return cachedVersion;
            }
        }

        var manifest = await GetManifestAsync(cancellationToken);
        var entry = manifest.Versions.FirstOrDefault(candidate =>
            candidate.Type == "release" &&
            candidate.Id.Equals(version, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Minecraft release {version} was not found.");
        return await ResolveAsync(entry, cancellationToken);
    }

    private async Task<VersionManifest> GetManifestAsync(
        CancellationToken cancellationToken) =>
        await httpClient.GetFromJsonAsync<VersionManifest>(
            ManifestUri,
            cancellationToken)
        ?? throw new InvalidDataException(
            "The official Minecraft version manifest was empty.");

    private async Task<MinecraftVersionDescriptor> ResolveLimitedAsync(
        VersionEntry entry,
        CancellationToken cancellationToken)
    {
        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            return await ResolveAsync(entry, cancellationToken);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private async Task<MinecraftVersionDescriptor> ResolveAsync(
        VersionEntry entry,
        CancellationToken cancellationToken)
    {
        var metadata = await httpClient.GetFromJsonAsync<VersionMetadata>(
            entry.Url,
            cancellationToken)
            ?? throw new InvalidDataException($"Minecraft metadata for {entry.Id} was empty.");
        var server = metadata.Downloads.Server
            ?? throw new InvalidDataException($"Minecraft release {entry.Id} has no server download.");
        return new MinecraftVersionDescriptor(
            entry.Id,
            entry.Type,
            entry.Url,
            server.Url,
            server.Sha1,
            server.Size,
            metadata.JavaVersion?.MajorVersion ?? InferJavaMajor(entry.Id));
    }

    private static int InferJavaMajor(string version)
    {
        if (Version.TryParse(version, out var parsed))
        {
            if (parsed >= new Version(1, 20, 5))
            {
                return 21;
            }

            if (parsed >= new Version(1, 18))
            {
                return 17;
            }
        }

        return 8;
    }

    private sealed record VersionManifest(
        [property: JsonPropertyName("latest")] LatestVersion Latest,
        [property: JsonPropertyName("versions")] IReadOnlyList<VersionEntry> Versions);

    private sealed record LatestVersion(
        [property: JsonPropertyName("release")] string Release,
        [property: JsonPropertyName("snapshot")] string Snapshot);

    private sealed record VersionEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")] Uri Url,
        [property: JsonPropertyName("sha1")] string Sha1);

    private sealed record VersionMetadata(
        [property: JsonPropertyName("downloads")] VersionDownloads Downloads,
        [property: JsonPropertyName("javaVersion")] JavaVersion? JavaVersion);

    private sealed record VersionDownloads(
        [property: JsonPropertyName("server")] DownloadArtifact? Server);

    private sealed record DownloadArtifact(
        [property: JsonPropertyName("sha1")] string Sha1,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("url")] Uri Url);

    private sealed record JavaVersion(
        [property: JsonPropertyName("majorVersion")] int MajorVersion);
}
