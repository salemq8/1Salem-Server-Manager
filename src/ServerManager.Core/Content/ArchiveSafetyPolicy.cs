using ServerManager.Contracts;

namespace ServerManager.Core.Content;

/// <summary>One archive entry's verdict, and why.</summary>
public sealed record ArchiveEntryVerdict(bool IsSafe, string? Reason);

/// <summary>
/// The rules every archive in the Content Hub goes through: plugin JARs, data pack ZIPs,
/// resource pack ZIPs and modpack .mrpack files all pass through here, so none of them can
/// have its own weaker copy of these checks.
/// </summary>
public static class ArchiveSafetyPolicy
{
    /// <summary>Entries allowed in one archive, whatever its type.</summary>
    public const int MaximumEntries = 20_000;

    /// <summary>
    /// A ratio this high is a compression bomb rather than a pack. Checked per entry, and
    /// the total uncompressed size is checked as well, because compressed size alone proves
    /// nothing.
    /// </summary>
    public const int MaximumCompressionRatio = 1000;

    /// <summary>The download ceiling for one file of this kind.</summary>
    public static long MaximumDownloadBytes(ContentKind kind) =>
        kind switch
        {
            // Modpacks are only an index plus overrides; the mods come separately.
            ContentKind.Modpack => 64L * 1024 * 1024,
            ContentKind.ResourcePack => 512L * 1024 * 1024,
            ContentKind.DataPack => 256L * 1024 * 1024,
            _ => 256L * 1024 * 1024
        };

    /// <summary>How large the archive may be once expanded.</summary>
    public static long MaximumExtractedBytes(ContentKind kind) =>
        kind switch
        {
            ContentKind.Modpack => 512L * 1024 * 1024,
            ContentKind.ResourcePack => 2L * 1024 * 1024 * 1024,
            _ => 1L * 1024 * 1024 * 1024
        };

    /// <summary>Everything a whole modpack may pull in across all of its files.</summary>
    public const long MaximumModpackTotalBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>A pack index or metadata document that must stay small to be sane.</summary>
    public const long MaximumMetadataBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Whether an archive entry path, or a path a pack index asks to write, may be used.
    /// Rejects traversal, rooted and drive-qualified paths, UNC, device names and anything
    /// that would not stay inside the destination.
    /// </summary>
    public static ArchiveEntryVerdict CheckEntryPath(string? entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return new ArchiveEntryVerdict(false, "empty path");
        }

        var name = entryPath.Replace('\\', '/');

        if (name.StartsWith("//", StringComparison.Ordinal))
        {
            return new ArchiveEntryVerdict(false, "UNC path");
        }

        if (name.StartsWith('/'))
        {
            return new ArchiveEntryVerdict(false, "absolute path");
        }

        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':')
        {
            return new ArchiveEntryVerdict(false, "drive-qualified path");
        }

        // A device path such as \\.\CON becomes //./CON here, so the UNC check above already
        // caught it; what is left to reject is any "." or ".." segment.
        if (name.Split('/').Any(segment => segment is "." or ".."))
        {
            return new ArchiveEntryVerdict(false, "path traversal");
        }

        foreach (var character in name)
        {
            if (char.IsControl(character) || character is '<' or '>' or '|' or '"' or '?' or '*' or ':')
            {
                return new ArchiveEntryVerdict(false, "illegal character");
            }
        }

        // Windows device names are refused even with an extension.
        foreach (var segment in name.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = Path.GetFileNameWithoutExtension(segment).ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                      stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                 char.IsAsciiDigit(stem[3])))
            {
                return new ArchiveEntryVerdict(false, "reserved device name");
            }
        }

        return new ArchiveEntryVerdict(true, null);
    }

    /// <summary>
    /// The destination for one entry, re-checked against the root after normalization so a
    /// path that passed the text checks still cannot land outside.
    /// </summary>
    public static string ResolveEntryDestination(string destinationRoot, string entryPath)
    {
        var verdict = CheckEntryPath(entryPath);
        if (!verdict.IsSafe)
        {
            throw new UnauthorizedAccessException($"Unsafe archive entry ({verdict.Reason}): {entryPath}");
        }

        var root = Path.GetFullPath(destinationRoot);
        var resolved = Path.GetFullPath(Path.Combine(root, entryPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!SafePathPolicy.IsWithinRoot(resolved, root))
        {
            throw new UnauthorizedAccessException(
                $"Archive entry escapes the destination: {entryPath}");
        }

        return resolved;
    }

    /// <summary>True when an entry's expansion ratio looks like a compression bomb.</summary>
    public static bool IsSuspiciousRatio(long compressedLength, long uncompressedLength) =>
        compressedLength > 0 &&
        uncompressedLength > 0 &&
        uncompressedLength / compressedLength > MaximumCompressionRatio;
}
