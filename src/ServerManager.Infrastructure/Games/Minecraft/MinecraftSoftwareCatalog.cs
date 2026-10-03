using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Minecraft;

/// <summary>Official providers only. Exact-version lookups never fall back to another release.</summary>
public sealed partial class MinecraftSoftwareCatalog(HttpClient http, IMinecraftVersionCatalog vanilla) : IMinecraftSoftwareCatalog
{
    public async Task<MinecraftSoftwareArtifact?> ResolveAsync(ServerPlatform platform, string exactVersion,
        CancellationToken cancellationToken = default)
    {
        if (!VersionPattern().IsMatch(exactVersion)) throw new ArgumentException("Invalid Minecraft version.");
        if (platform == ServerPlatform.Vanilla)
        {
            var release = await vanilla.GetVersionAsync(exactVersion, cancellationToken);
            if (release.Id != exactVersion) return null;
            return new(platform, exactVersion, exactVersion, release.ServerDownloadUrl, "SHA1", release.Sha1, release.SizeBytes);
        }
        if (platform is ServerPlatform.Paper or ServerPlatform.Folia)
        {
            var project = platform == ServerPlatform.Paper ? "paper" : "folia";
            using var document = await GetJsonAsync(
                $"https://fill.papermc.io/v3/projects/{project}/versions/{Uri.EscapeDataString(exactVersion)}/builds", cancellationToken);
            if (document is null) return null;
            foreach (var build in document.RootElement.EnumerateArray())
            {
                if (build.GetProperty("channel").GetString() != "STABLE") continue;
                if (!build.GetProperty("downloads").TryGetProperty("server:default", out var download)) continue;
                var url = new Uri(download.GetProperty("url").GetString()!);
                ValidateDownloadUrl(platform, url);
                var hash = download.GetProperty("checksums").GetProperty("sha256").GetString()!;
                ValidateHash("SHA256", hash);
                return new(platform, exactVersion, build.GetProperty("id").ToString(), url, "SHA256", hash,
                    download.TryGetProperty("size", out var size) ? size.GetInt64() : null);
            }
            return null;
        }
        if (platform == ServerPlatform.Purpur)
        {
            using var document = await GetJsonAsync(
                $"https://api.purpurmc.org/v2/purpur/{Uri.EscapeDataString(exactVersion)}/latest", cancellationToken);
            if (document is null) return null;
            var root = document.RootElement;
            if (root.GetProperty("version").GetString() != exactVersion || root.GetProperty("result").GetString() != "SUCCESS")
                return null;
            var build = root.GetProperty("build").ToString();
            if (!int.TryParse(build, out var id) || id <= 0) throw new InvalidDataException("Invalid Purpur build.");
            var hash = root.GetProperty("md5").GetString()!;
            ValidateHash("MD5", hash);
            return new(platform, exactVersion, build,
                new Uri($"https://api.purpurmc.org/v2/purpur/{Uri.EscapeDataString(exactVersion)}/{build}/download"), "MD5", hash);
        }
        return null; // Spigot publishes BuildTools, not an official distributable server binary.
    }

    public async Task<string> DownloadAsync(MinecraftSoftwareArtifact artifact, string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        ValidateDownloadUrl(artifact.Platform, artifact.DownloadUrl);
        ValidateHash(artifact.HashAlgorithm, artifact.Hash);
        Directory.CreateDirectory(stagingDirectory);
        var path = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".jar");
        try
        {
            using var request = Request(artifact.DownloadUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            ValidateDownloadUrl(artifact.Platform, response.RequestMessage!.RequestUri!);
            const long limit = 512L * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Runtime download exceeds the size limit.");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                long count = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    count += read;
                    if (count > limit) throw new InvalidDataException("Runtime download exceeds the size limit.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (artifact.SizeBytes.HasValue && count != artifact.SizeBytes.Value)
                    throw new InvalidDataException("Runtime size verification failed.");
            }
            await using var input = File.OpenRead(path);
            var hash = artifact.HashAlgorithm switch
            {
                "SHA256" => await SHA256.HashDataAsync(input, cancellationToken),
                "SHA1" => await SHA1.HashDataAsync(input, cancellationToken),
                "MD5" => await MD5.HashDataAsync(input, cancellationToken),
                _ => throw new InvalidDataException("Unsupported provider checksum.")
            };
            if (!Convert.ToHexString(hash).Equals(artifact.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The official runtime checksum does not match.");
            MinecraftSoftwareSafety.ValidateJar(path);
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = Request(new Uri(url));
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Provider metadata exceeds the size limit.");
        return JsonDocument.Parse(bytes);
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("1Salem-Server-Manager/1.5 (+https://github.com/salemq8/1Salem-Server-Manager)");
        return request;
    }

    private static void ValidateHash(string algorithm, string hash)
    {
        var length = algorithm switch { "SHA256" => 64, "SHA1" => 40, "MD5" => 32, _ => 0 };
        if (length == 0 || hash.Length != length || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("The provider did not supply a valid checksum.");
    }

    public static void ValidateDownloadUrl(ServerPlatform platform, Uri uri)
    {
        var allowed = platform switch
        {
            ServerPlatform.Paper or ServerPlatform.Folia => uri.Host is "fill-data.papermc.io" or "fill.papermc.io",
            ServerPlatform.Purpur => uri.Host == "api.purpurmc.org",
            ServerPlatform.Vanilla => uri.Host is "piston-data.mojang.com" or "launcher.mojang.com",
            _ => false
        };
        if (!allowed || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidDataException("The runtime download is not on the official provider allowlist.");
    }

    [GeneratedRegex(@"^[0-9][0-9A-Za-z.\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
