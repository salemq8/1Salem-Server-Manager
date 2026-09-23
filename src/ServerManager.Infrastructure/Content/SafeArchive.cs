using System.IO.Compression;
using System.Text;
using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Infrastructure.Content;

public sealed record ArchiveInspection(
    bool IsValid,
    string? Reason,
    int EntryCount = 0,
    long UncompressedBytes = 0);

/// <summary>
/// The one place archives are opened. Data packs, resource packs and modpacks all come
/// through here, so every one of them gets the same traversal, entry-count and expansion
/// checks rather than a weaker copy per content type.
/// </summary>
public static class SafeArchive
{
    /// <summary>
    /// Reads the archive's structure without writing anything. Nothing is extracted and no
    /// contained code is run.
    /// </summary>
    public static ArchiveInspection Inspect(string archivePath, ContentKind kind)
    {
        if (!File.Exists(archivePath))
        {
            return new ArchiveInspection(false, "The downloaded file is missing.");
        }

        if (new FileInfo(archivePath).Length == 0)
        {
            return new ArchiveInspection(false, "The download was empty.");
        }

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (InvalidDataException)
        {
            return new ArchiveInspection(false, "The download is not a valid archive.");
        }
        catch (IOException exception)
        {
            return new ArchiveInspection(false, $"The download could not be read: {exception.Message}");
        }

        using (archive)
        {
            if (archive.Entries.Count == 0)
            {
                return new ArchiveInspection(false, "The archive is empty.");
            }

            if (archive.Entries.Count > ArchiveSafetyPolicy.MaximumEntries)
            {
                return new ArchiveInspection(false, "The archive contains too many files.");
            }

            var ceiling = ArchiveSafetyPolicy.MaximumExtractedBytes(kind);
            long uncompressed = 0;
            foreach (var entry in archive.Entries)
            {
                // A directory entry ends in a slash and carries no content.
                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                if (!isDirectory)
                {
                    var verdict = ArchiveSafetyPolicy.CheckEntryPath(entry.FullName);
                    if (!verdict.IsSafe)
                    {
                        return new ArchiveInspection(
                            false,
                            $"The archive contains an unsafe path ({verdict.Reason}).");
                    }
                }

                if (IsSymbolicLink(entry))
                {
                    return new ArchiveInspection(false, "The archive contains a symbolic link.");
                }

                uncompressed += entry.Length;
                if (uncompressed > ceiling)
                {
                    return new ArchiveInspection(false, "The archive expands to an unreasonable size.");
                }

                if (ArchiveSafetyPolicy.IsSuspiciousRatio(entry.CompressedLength, entry.Length))
                {
                    return new ArchiveInspection(false, "The archive contains a compression bomb.");
                }
            }

            return new ArchiveInspection(true, null, archive.Entries.Count, uncompressed);
        }
    }

    /// <summary>Reads one small text entry, such as a pack index or pack.mcmeta.</summary>
    public static async Task<string?> ReadTextEntryAsync(
        string archivePath,
        string entryName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry(entryName) ??
                        archive.Entries.FirstOrDefault(candidate =>
                            string.Equals(candidate.FullName, entryName, StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Length > ArchiveSafetyPolicy.MaximumMetadataBytes)
            {
                return null;
            }

            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts entries under <paramref name="sourcePrefix"/> into a destination the caller
    /// owns. Every entry's destination is resolved and re-checked, so nothing can be written
    /// outside, and the running total is enforced while extracting rather than afterwards.
    /// </summary>
    public static async Task<int> ExtractAsync(
        string archivePath,
        string destinationRoot,
        ContentKind kind,
        string? sourcePrefix = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        var ceiling = ArchiveSafetyPolicy.MaximumExtractedBytes(kind);
        long written = 0;
        var count = 0;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                continue;
            }

            if (sourcePrefix is { Length: > 0 })
            {
                if (!name.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                name = name[sourcePrefix.Length..].TrimStart('/');
                if (name.Length == 0)
                {
                    continue;
                }
            }

            var destination = ArchiveSafetyPolicy.ResolveEntryDestination(root, name);
            written += entry.Length;
            if (written > ceiling)
            {
                throw new InvalidDataException("The archive expands to an unreasonable size.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var target = new FileStream(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
            await source.CopyToAsync(target, cancellationToken);
            count++;
        }

        return count;
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
}
