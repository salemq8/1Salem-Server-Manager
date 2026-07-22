using System.IO.Compression;
using System.Security.Cryptography;

namespace ServerManager.Infrastructure.Updates;

public static class UpdatePackageSecurity
{
    private static readonly HashSet<string> BlockedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".vbs", ".vbe",
            ".js", ".jse", ".wsf", ".wsh", ".hta", ".reg"
        };

    private static readonly HashSet<string> AllowedRoots =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Client",
            "Agent",
            "Maintenance"
        };

    public static async Task VerifyFileAsync(
        string packagePath,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var information = new FileInfo(packagePath);
        if (!information.Exists)
        {
            throw new FileNotFoundException("The staged update package was not found.", packagePath);
        }

        if (information.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"Update package size mismatch. Expected {expectedSize}, received {information.Length}.");
        }

        await using var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(expectedSha256)))
        {
            throw new InvalidDataException("Update package SHA-256 verification failed.");
        }
    }

    public static IReadOnlyList<string> ValidateArchive(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count == 0)
        {
            throw new InvalidDataException("The update package is empty.");
        }

        var files = new List<string>();
        var normalizedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.StartsWith('/') ||
                Path.IsPathFullyQualified(normalized) ||
                normalized.Split('/').Any(part => part is "." or ".."))
            {
                throw new InvalidDataException(
                    $"Unsafe update ZIP path rejected: {entry.FullName}");
            }

            var root = normalized.Split('/', 2)[0];
            if (!AllowedRoots.Contains(root))
            {
                throw new InvalidDataException(
                    $"Update entry is outside the Client/Agent/Maintenance roots: {entry.FullName}");
            }

            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException(
                    $"Symbolic links are not allowed in update packages: {entry.FullName}");
            }

            if (entry.Name.Length == 0)
            {
                continue;
            }

            if (!normalizedNames.Add(normalized))
            {
                throw new InvalidDataException(
                    $"Duplicate or case-colliding update ZIP path rejected: {entry.FullName}");
            }

            if (BlockedExtensions.Contains(Path.GetExtension(entry.Name)))
            {
                throw new InvalidDataException(
                    $"Executable script rejected from update package: {entry.FullName}");
            }

            var segments = normalized.Split('/');
            var extension = Path.GetExtension(entry.Name);
            if (segments.Any(segment => segment.Equals("SaveGames", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("ProgramData", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("backups", StringComparison.OrdinalIgnoreCase)) ||
                normalized.EndsWith("playit.toml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".sav", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".db", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".pem", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".key", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Server data or secret-like file rejected from update package: {entry.FullName}");
            }

            files.Add(normalized);
        }

        if (!files.Any(item =>
                item.Equals(
                    "Client/1Salem.ServerManager.exe",
                    StringComparison.OrdinalIgnoreCase)) ||
            !files.Any(item =>
                item.Equals(
                    "Agent/1Salem.ServerManager.Agent.exe",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The update package must contain both the Client and Agent application binaries.");
        }

        return files;
    }

    public static void ExtractSafe(string packagePath, string destinationRoot)
    {
        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        var prefix = root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(root, normalized));
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Update entry escaped the staging directory: {entry.FullName}");
            }

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return unixMode == UnixSymbolicLink;
    }
}
