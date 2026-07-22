using System.Text;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Palworld;

public sealed record PalworldConfigurationDocument(
    string Path,
    string Content,
    Encoding Encoding);

public static class PalworldConfigurationFile
{
    public static readonly string RelativePath = Path.Combine(
        "Pal",
        "Saved",
        "Config",
        "WindowsServer",
        "PalWorldSettings.ini");

    public static string ResolvePath(string serverRoot) =>
        SafePathPolicy.ResolveWithinRoot(serverRoot, RelativePath);

    public static async Task<PalworldConfigurationDocument> ReadAsync(
        string serverRoot,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(serverRoot);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The live Palworld configuration file was not found. Start Palworld once so WindowsServer/PalWorldSettings.ini is created.",
                path);
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var encoding = DetectEncoding(bytes);
        var preamble = encoding.GetPreamble();
        var offset = HasPrefix(bytes, preamble) ? preamble.Length : 0;
        var content = encoding.GetString(bytes, offset, bytes.Length - offset);
        _ = PalworldSettingsSerializer.ParseValues(content);
        return new PalworldConfigurationDocument(path, content, encoding);
    }

    public static async Task<string> CreateSafetyBackupAsync(
        string serverRoot,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var document = await ReadAsync(serverRoot, cancellationToken);
        var safeReason = string.Concat(reason.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')).Trim('-');
        if (string.IsNullOrEmpty(safeReason))
        {
            safeReason = "configuration";
        }

        var historyRoot = SafePathPolicy.ResolveWithinRoot(
            serverRoot,
            Path.Combine("backups", "config-history"));
        Directory.CreateDirectory(historyRoot);
        var backupPath = Path.Combine(
            historyRoot,
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{safeReason}-{Guid.NewGuid():N}.ini");
        await using var source = new FileStream(
            document.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            backupPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        return backupPath;
    }

    public static async Task WriteAtomicAsync(
        PalworldConfigurationDocument original,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        _ = PalworldSettingsSerializer.ParseValues(content);

        var directory = Path.GetDirectoryName(original.Path)
            ?? throw new InvalidOperationException(
                "The Palworld configuration directory could not be resolved.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(original.Path)}.1salem-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                content,
                original.Encoding,
                cancellationToken);
            var verifyBytes = await File.ReadAllBytesAsync(temporary, cancellationToken);
            var verifyEncoding = DetectEncoding(verifyBytes);
            var preamble = verifyEncoding.GetPreamble();
            var offset = HasPrefix(verifyBytes, preamble) ? preamble.Length : 0;
            var verifiedContent = verifyEncoding.GetString(
                verifyBytes,
                offset,
                verifyBytes.Length - offset);
            _ = PalworldSettingsSerializer.ParseValues(verifiedContent);

            if (OperatingSystem.IsWindows())
            {
                File.Replace(temporary, original.Path, null, true);
            }
            else
            {
                File.Move(temporary, original.Path, true);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task RestoreAtomicAsync(
        string serverRoot,
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var historyRoot = SafePathPolicy.ResolveWithinRoot(
            serverRoot,
            Path.Combine("backups", "config-history"));
        var resolvedBackup = Path.GetFullPath(backupPath);
        if (!SafePathPolicy.IsWithinRoot(resolvedBackup, historyRoot) ||
            !File.Exists(resolvedBackup))
        {
            throw new InvalidOperationException(
                "The selected configuration history item is not valid for this server.");
        }

        var current = await ReadAsync(serverRoot, cancellationToken);
        var bytes = await File.ReadAllBytesAsync(resolvedBackup, cancellationToken);
        var encoding = DetectEncoding(bytes);
        var preamble = encoding.GetPreamble();
        var offset = HasPrefix(bytes, preamble) ? preamble.Length : 0;
        var content = encoding.GetString(bytes, offset, bytes.Length - offset);
        _ = PalworldSettingsSerializer.ParseValues(content);
        await WriteAtomicAsync(current with { Encoding = encoding }, content, cancellationToken);
    }

    public static IReadOnlyList<string> ListHistory(string serverRoot)
    {
        var historyRoot = SafePathPolicy.ResolveWithinRoot(
            serverRoot,
            Path.Combine("backups", "config-history"));
        return Directory.Exists(historyRoot)
            ? Directory.EnumerateFiles(historyRoot, "*.ini", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray()
            : [];
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            return new UTF8Encoding(true, true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new UnicodeEncoding(false, true, true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new UnicodeEncoding(true, true, true);
        }

        return new UTF8Encoding(false, true);
    }

    private static bool HasPrefix(byte[] bytes, byte[] prefix)
    {
        if (prefix.Length == 0 || bytes.Length < prefix.Length)
        {
            return false;
        }

        for (var index = 0; index < prefix.Length; index++)
        {
            if (bytes[index] != prefix[index])
            {
                return false;
            }
        }

        return true;
    }
}
