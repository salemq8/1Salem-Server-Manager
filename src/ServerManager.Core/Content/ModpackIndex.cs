using ServerManager.Contracts;

namespace ServerManager.Core.Content;

/// <summary>One file a modpack wants placed, exactly as its index describes it.</summary>
public sealed record ModpackFile(
    string Path,
    IReadOnlyList<Uri> Downloads,
    string? Sha512,
    string? Sha1,
    long FileSize,
    string ClientEnvironment = "required",
    string ServerEnvironment = "required")
{
    /// <summary>
    /// Whether a dedicated server needs this file. Client-only files are skipped rather than
    /// downloaded and left to sit in a server that will never load them.
    /// </summary>
    public bool IsRequiredOnServer =>
        !string.Equals(ServerEnvironment, "unsupported", StringComparison.OrdinalIgnoreCase);
}

/// <summary>`modrinth.index.json`, parsed. Validation lives in <see cref="ModpackIndexPolicy"/>.</summary>
public sealed record ModpackIndex(
    int FormatVersion,
    string Game,
    string VersionId,
    string Name,
    string? Summary,
    IReadOnlyList<ModpackFile> Files,
    IReadOnlyDictionary<string, string> Dependencies)
{
    public string? MinecraftVersion =>
        Dependencies.TryGetValue("minecraft", out var version) ? version : null;

    public string? Loader => ContentTypePolicy.ReadLoader(Dependencies);

    public string? LoaderVersion
    {
        get
        {
            foreach (var key in new[] { "fabric-loader", "quilt-loader", "neoforge", "forge" })
            {
                if (Dependencies.TryGetValue(key, out var version))
                {
                    return version;
                }
            }

            return null;
        }
    }
}

public sealed record ModpackValidation(
    bool IsValid,
    string? ErrorCode,
    string? Reason,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Everything that must be true about a modpack index before a single file is fetched. Kept
/// free of I/O so the rules can be tested directly.
/// </summary>
public static class ModpackIndexPolicy
{
    /// <summary>The only domains Modrinth allows a pack to pull files from.</summary>
    private static readonly string[] AllowedHosts =
    [
        "cdn.modrinth.com",
        "github.com",
        "raw.githubusercontent.com",
        "gitlab.com"
    ];

    public const string IndexFileName = "modrinth.index.json";

    public static bool IsAllowedDownloadHost(Uri? url) =>
        url is { IsAbsoluteUri: true } &&
        string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        AllowedHosts.Any(host => string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase));

    public static ModpackValidation Validate(ModpackIndex? index)
    {
        var warnings = new List<string>();
        if (index is null)
        {
            return new ModpackValidation(false, "MalformedIndex", "The pack has no readable index.", warnings);
        }

        if (index.FormatVersion != 1)
        {
            return new ModpackValidation(
                false,
                "UnsupportedFormat",
                $"Pack index format {index.FormatVersion} is not supported.",
                warnings);
        }

        if (!string.Equals(index.Game, "minecraft", StringComparison.OrdinalIgnoreCase))
        {
            return new ModpackValidation(
                false,
                "UnsupportedGame",
                $"The pack is for '{index.Game}', not Minecraft.",
                warnings);
        }

        if (string.IsNullOrWhiteSpace(index.MinecraftVersion))
        {
            return new ModpackValidation(
                false,
                "NoMinecraftVersion",
                "The pack does not say which Minecraft version it needs.",
                warnings);
        }

        var loader = index.Loader;
        if (loader is null)
        {
            return new ModpackValidation(
                false,
                "NoLoader",
                "The pack does not name a mod loader.",
                warnings);
        }

        if (!ContentTypePolicy.CanInstallLoader(loader))
        {
            // Listed and explained, never half-installed.
            return new ModpackValidation(
                false,
                $"LoaderNotSupported:{loader}",
                $"Packs for {loader} need that project's own installer, which this app does not run.",
                warnings);
        }

        var serverFiles = index.Files.Where(file => file.IsRequiredOnServer).ToArray();
        if (serverFiles.Length > ArchiveSafetyPolicy.MaximumEntries)
        {
            return new ModpackValidation(
                false,
                "TooManyFiles",
                "The pack lists more files than this app will install.",
                warnings);
        }

        long total = 0;
        foreach (var file in serverFiles)
        {
            var verdict = ArchiveSafetyPolicy.CheckEntryPath(file.Path);
            if (!verdict.IsSafe)
            {
                return new ModpackValidation(
                    false,
                    "UnsafePath",
                    $"The pack wants to write outside the server folder ({verdict.Reason}).",
                    warnings);
            }

            if (file.Downloads.Count == 0)
            {
                return new ModpackValidation(
                    false,
                    "NoDownload",
                    $"The pack lists {file.Path} with no download address.",
                    warnings);
            }

            if (file.Downloads.Any(url => !IsAllowedDownloadHost(url)))
            {
                return new ModpackValidation(
                    false,
                    "UntrustedHost",
                    "The pack points at a download site outside the provider's allowed list.",
                    warnings);
            }

            if (string.IsNullOrWhiteSpace(file.Sha512) && string.IsNullOrWhiteSpace(file.Sha1))
            {
                return new ModpackValidation(
                    false,
                    "NoHash",
                    $"The pack gives no checksum for {file.Path}.",
                    warnings);
            }

            if (file.FileSize < 0 || file.FileSize > ArchiveSafetyPolicy.MaximumDownloadBytes(ContentKind.ResourcePack))
            {
                return new ModpackValidation(
                    false,
                    "FileTooLarge",
                    $"The pack lists an implausible size for {file.Path}.",
                    warnings);
            }

            total += file.FileSize;
        }

        if (total > ArchiveSafetyPolicy.MaximumModpackTotalBytes)
        {
            return new ModpackValidation(
                false,
                "PackTooLarge",
                "The pack is larger than this app will download in one go.",
                warnings);
        }

        var clientOnly = index.Files.Count - serverFiles.Length;
        if (clientOnly > 0)
        {
            warnings.Add($"ClientOnlyFilesSkipped:{clientOnly}");
        }

        return new ModpackValidation(true, null, null, warnings);
    }
}
