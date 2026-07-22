using System.Text;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Files;

public sealed class RegisteredFileService(
    IGameServerStore serverStore,
    IAuditLogStore auditLogStore) : IRegisteredFileService
{
    private const int MaximumTextBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> TextExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".json", ".properties", ".ini", ".cfg", ".conf",
            ".yaml", ".yml", ".xml", ".cmd", ".bat"
        };

    public async Task<IReadOnlyList<ManagedFileEntry>> ListAsync(
        Guid serverId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var path = Resolve(server.RootPath, relativePath, true);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Directory does not exist: {relativePath}");
        }

        RejectReparsePoints(server.RootPath, path);
        return Directory.EnumerateFileSystemEntries(path)
            .Take(2_000)
            .Select(item =>
            {
                var attributes = File.GetAttributes(item);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                return new ManagedFileEntry(
                    Path.GetFileName(item),
                    Path.GetRelativePath(server.RootPath, item),
                    isDirectory,
                    isDirectory ? 0 : new FileInfo(item).Length,
                    isDirectory
                        ? Directory.GetLastWriteTimeUtc(item)
                        : File.GetLastWriteTimeUtc(item));
            })
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ManagedTextFile> ReadTextAsync(
        Guid serverId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var path = Resolve(server.RootPath, relativePath, false);
        ValidateTextFile(path);
        RejectReparsePoints(server.RootPath, path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The managed file does not exist.", path);
        }

        if (info.Length > MaximumTextBytes)
        {
            throw new IOException("Text editor files are limited to 2 MiB.");
        }

        return new ManagedTextFile(
            Path.GetRelativePath(server.RootPath, path),
            await File.ReadAllTextAsync(path, cancellationToken),
            info.LastWriteTimeUtc);
    }

    public async Task<OperationResult> WriteTextAsync(
        Guid serverId,
        string relativePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Encoding.UTF8.GetByteCount(content) > MaximumTextBytes)
        {
            return OperationResult.Fail("FileTooLarge", "Text content is limited to 2 MiB.");
        }

        var server = await GetServerAsync(serverId, cancellationToken);
        var path = Resolve(server.RootPath, relativePath, false);
        ValidateTextFile(path);
        RejectReparsePoints(server.RootPath, path);
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent))
        {
            return OperationResult.Fail("ParentMissing", "The parent folder does not exist.");
        }

        if (File.Exists(path))
        {
            await BackupBeforeChangeAsync(server, path, cancellationToken);
        }

        var temporaryPath = path + ".1salem-new";
        await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
        File.Move(temporaryPath, path, true);
        await auditLogStore.WriteAsync(
            "FileManager",
            "TextFileWritten",
            serverId.ToString(),
            true,
            Path.GetRelativePath(server.RootPath, path),
            cancellationToken);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> RenameAsync(
        Guid serverId,
        string relativePath,
        string newName,
        CancellationToken cancellationToken = default)
    {
        ValidateFileName(newName);
        var server = await GetServerAsync(serverId, cancellationToken);
        var source = Resolve(server.RootPath, relativePath, false);
        RejectReparsePoints(server.RootPath, source);
        var destination = Resolve(
            server.RootPath,
            Path.Combine(Path.GetDirectoryName(relativePath) ?? string.Empty, newName),
            false);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            return OperationResult.Fail("DestinationExists", "A file with that name already exists.");
        }

        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
        else if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            return OperationResult.Fail("FileMissing", "The source path does not exist.");
        }

        await auditLogStore.WriteAsync(
            "FileManager",
            "PathRenamed",
            serverId.ToString(),
            true,
            $"{relativePath} -> {newName}",
            cancellationToken);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> DeleteAsync(
        Guid serverId,
        string relativePath,
        string confirmationText,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var path = Resolve(server.RootPath, relativePath, false);
        RejectReparsePoints(server.RootPath, path);
        var expected = $"DELETE {Path.GetFileName(path)}";
        if (!string.Equals(confirmationText, expected, StringComparison.Ordinal))
        {
            return OperationResult.Fail(
                "ConfirmationRequired",
                $"Type exactly: {expected}");
        }

        if (!File.Exists(path))
        {
            return OperationResult.Fail(
                "FileDeleteOnly",
                "Only individual files can be deleted; folders are protected.");
        }

        await BackupBeforeChangeAsync(server, path, cancellationToken);
        File.Delete(path);
        await auditLogStore.WriteAsync(
            "FileManager",
            "FileDeleted",
            serverId.ToString(),
            true,
            relativePath,
            cancellationToken);
        return OperationResult.Ok();
    }

    private static string Resolve(string rootPath, string relativePath, bool allowRoot)
    {
        if (allowRoot && string.IsNullOrWhiteSpace(relativePath))
        {
            return Path.GetFullPath(rootPath);
        }

        return SafePathPolicy.ResolveWithinRoot(rootPath, relativePath);
    }

    private static void RejectReparsePoints(string rootPath, string path)
    {
        var current = Path.GetFullPath(rootPath);
        if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException("Registered roots cannot be reparse points.");
        }

        var relative = Path.GetRelativePath(current, path);
        foreach (var part in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    "Reparse points are blocked by the managed file service.");
            }
        }
    }

    private static void ValidateTextFile(string path)
    {
        if (!TextExtensions.Contains(Path.GetExtension(path)))
        {
            throw new InvalidOperationException(
                "Only recognized text configuration and log files can be opened.");
        }
    }

    private static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > 200 ||
            !Path.GetFileName(name).Equals(name, StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Enter a valid file or folder name.", nameof(name));
        }
    }

    private static async Task BackupBeforeChangeAsync(
        GameServerDefinition server,
        string path,
        CancellationToken cancellationToken)
    {
        var backupRoot = Path.Combine(
            server.RootPath,
            "backups",
            "config-edits",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        var relative = Path.GetRelativePath(server.RootPath, path);
        var backupPath = Path.Combine(backupRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            true);
        await using var destination = new FileStream(
            backupPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            true);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private async Task<GameServerDefinition> GetServerAsync(
        Guid serverId,
        CancellationToken cancellationToken) =>
        await serverStore.GetAsync(serverId, cancellationToken) ??
        throw new KeyNotFoundException("The requested server is not registered.");
}
