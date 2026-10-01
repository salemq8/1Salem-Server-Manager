using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServerManager.Connect.App.Updates;

/// <summary>One downloadable file of a release: where it is, how big, and its SHA-256.</summary>
public sealed record ConnectUpdateFile(string FileName, Uri Url, long Size, string Sha256);

/// <summary>
/// The update metadata the release pipeline publishes with every GitHub release as
/// <c>1SalemConnect-update.json</c> (tools/build-release.ps1). Everything a friend's app needs to
/// decide and to verify: the release, its installer and its portable package.
/// </summary>
public sealed record ConnectUpdateManifest(
    ConnectBuild Build,
    string Channel,
    string ReleaseTag,
    Uri ReleaseUrl,
    DateTimeOffset? PublishedUtc,
    ConnectUpdateFile Installer,
    ConnectUpdateFile Portable);

/// <summary>Where releases come from. Only the official repository, only over HTTPS.</summary>
public static class ConnectUpdateSource
{
    public const string Repository = "salemq8/1Salem-Server-Manager";
    public const string ManifestFileName = "1SalemConnect-update.json";
    public const string InstallerFileName = "1SalemConnect-Setup.exe";
    public const string PortableFileName = "1SalemConnect-Portable.zip";
    public const string Channel = "Stable";
    public const string Product = "1Salem Connect";

    /// <summary>GitHub serves the newest published release's asset here, without the rate-limited API.</summary>
    public static Uri LatestManifestUrl { get; } =
        new($"https://github.com/{Repository}/releases/latest/download/{ManifestFileName}");

    public static string TagFor(ConnectBuild build) =>
        string.Create(CultureInfo.InvariantCulture, $"v{build.ProductVersion}-build-{build.BuildRevision}");

    public static Uri ReleaseUrlFor(string tag) => new($"https://github.com/{Repository}/releases/tag/{tag}");

    public static Uri AssetUrlFor(string tag, string fileName) =>
        new($"https://github.com/{Repository}/releases/download/{tag}/{fileName}");

    /// <summary>
    /// Hosts a download may pass through. Release assets redirect from github.com to GitHub's own
    /// content hosts; anything else (or plain HTTP, or a port) is refused before connecting.
    /// </summary>
    public static bool IsAllowedHost(Uri address) =>
        address.IsAbsoluteUri &&
        address.Scheme == Uri.UriSchemeHttps &&
        address.IsDefaultPort &&
        string.IsNullOrEmpty(address.UserInfo) &&
        (address.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         address.IdnHost.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Why a manifest was not accepted, for Diagnostics; the friend sees one plain sentence.</summary>
public sealed class ConnectUpdateManifestException(string message) : Exception(message);

/// <summary>
/// Reads <c>1SalemConnect-update.json</c> and accepts it only when every field is exactly what
/// the release pipeline writes: the official repository, the release tag derived from the
/// version and build, HTTPS asset URLs inside that release, real sizes and SHA-256 values.
/// Duplicate members are refused, so a value cannot be shadowed.
/// </summary>
public static partial class ConnectUpdateManifestReader
{
    public const int MaxBytes = 64 * 1024;

    /// <summary>Installers and portable packages are well under this; anything bigger is refused.</summary>
    public const long MaxFileSize = 1024L * 1024 * 1024;

    public static ConnectUpdateManifest Read(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxBytes)
        {
            throw new ConnectUpdateManifestException("The update information is too large.");
        }

        // PowerShell 5.1 may write a byte-order mark.
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            utf8 = utf8[3..];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException exception)
        {
            throw new ConnectUpdateManifestException("The update information is not valid JSON: " + exception.Message);
        }

        using (document)
        {
            var root = Object(document.RootElement, "manifest");
            if (Number(root, "schema") != 1)
            {
                throw new ConnectUpdateManifestException("The update information uses an unknown format.");
            }

            Expect(String(root, "product"), ConnectUpdateSource.Product, "product");
            var channel = Expect(String(root, "channel"), ConnectUpdateSource.Channel, "channel");
            var version = String(root, "productVersion");
            var buildRevision = Number(root, "buildRevision");
            if (buildRevision > int.MaxValue ||
                !ConnectBuild.TryCreate(version, (int)buildRevision, out var build))
            {
                throw new ConnectUpdateManifestException("The update information names an invalid version or build.");
            }

            var tag = Expect(String(root, "releaseTag"), ConnectUpdateSource.TagFor(build), "releaseTag");
            var releaseUrl = Expect(Url(root, "releaseUrl"), ConnectUpdateSource.ReleaseUrlFor(tag), "releaseUrl");
            DateTimeOffset? published = null;
            if (root.TryGetProperty("publishedUtc", out var publishedElement) &&
                publishedElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(publishedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                published = parsed;
            }

            return new ConnectUpdateManifest(
                build,
                channel,
                tag,
                releaseUrl,
                published,
                ReadFile(root, "installer", tag, ConnectUpdateSource.InstallerFileName),
                ReadFile(root, "portable", tag, ConnectUpdateSource.PortableFileName));
        }
    }

    private static ConnectUpdateFile ReadFile(JsonElement root, string name, string tag, string expectedFileName)
    {
        var element = Object(Member(root, name), name);
        var fileName = Expect(String(element, "fileName"), expectedFileName, name + ".fileName");
        var url = Expect(Url(element, "url"), ConnectUpdateSource.AssetUrlFor(tag, fileName), name + ".url");
        var size = Number(element, "size");
        if (size < 1 || size > MaxFileSize)
        {
            throw new ConnectUpdateManifestException($"The update information gives an impossible size for {fileName}.");
        }

        var sha256 = String(element, "sha256");
        if (!Sha256Pattern().IsMatch(sha256))
        {
            throw new ConnectUpdateManifestException($"The update information has no valid SHA-256 for {fileName}.");
        }

        return new ConnectUpdateFile(fileName, url, size, sha256.ToUpperInvariant());
    }

    private static JsonElement Object(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConnectUpdateManifestException($"'{name}' must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new ConnectUpdateManifestException($"'{property.Name}' appears twice in the update information.");
            }
        }

        return element;
    }

    private static JsonElement Member(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw new ConnectUpdateManifestException($"The update information has no '{name}'.");

    private static string String(JsonElement element, string name)
    {
        var value = Member(element, name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new ConnectUpdateManifestException($"'{name}' must be a non-empty string.");
    }

    private static long Number(JsonElement element, string name)
    {
        var value = Member(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : throw new ConnectUpdateManifestException($"'{name}' must be a whole number.");
    }

    private static Uri Url(JsonElement element, string name) =>
        Uri.TryCreate(String(element, name), UriKind.Absolute, out var url) && ConnectUpdateSource.IsAllowedHost(url)
            ? url
            : throw new ConnectUpdateManifestException($"'{name}' is not an official HTTPS address.");

    private static T Expect<T>(T actual, T expected, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new ConnectUpdateManifestException($"'{name}' is not what the official release would contain.");
        }

        return actual;
    }

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
