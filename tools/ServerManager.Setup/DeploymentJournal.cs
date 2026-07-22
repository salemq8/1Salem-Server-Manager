using System.IO;

namespace ServerManager.Setup;

public sealed class DeploymentJournal
{
    private readonly string _installRoot;
    private readonly string _backupRoot;
    private readonly HashSet<string> _createdFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _backups =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _createdDirectories =
        new(StringComparer.OrdinalIgnoreCase);

    public DeploymentJournal(string installRoot, string backupRoot)
    {
        _installRoot = EnsureRoot(installRoot);
        _backupRoot = Path.GetFullPath(backupRoot);
        Directory.CreateDirectory(_backupRoot);
    }

    public void DeployDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Payload component is missing: {source}");
        }

        EnsureDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            EnsureDirectory(
                Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            DeployFile(
                file,
                Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    public void DeployFile(string source, string destination)
    {
        EnsureInsideInstallRoot(destination);
        EnsureDirectory(Path.GetDirectoryName(destination)!);
        CaptureExistingFile(destination);
        File.Copy(source, destination, true);
    }

    public void WriteText(string destination, string value)
    {
        EnsureInsideInstallRoot(destination);
        EnsureDirectory(Path.GetDirectoryName(destination)!);
        CaptureExistingFile(destination);
        File.WriteAllText(destination, value);
    }

    public void Commit()
    {
        _createdFiles.Clear();
        _backups.Clear();
        _createdDirectories.Clear();
    }

    public async Task RollBackAsync(
        InstallerLog log,
        CancellationToken cancellationToken)
    {
        await log.WriteAsync(
            "Rollback",
            "Restoring application files created or replaced by this attempt. " +
            "ProgramData and game-server folders are outside the rollback scope.",
            cancellationToken);

        foreach (var path in _createdFiles.OrderByDescending(value => value.Length))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        foreach (var pair in _backups)
        {
            EnsureDirectory(Path.GetDirectoryName(pair.Key)!);
            File.Copy(pair.Value, pair.Key, true);
        }

        foreach (var directory in _createdDirectories.OrderByDescending(
                     value => value.Length))
        {
            if (Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private void CaptureExistingFile(string destination)
    {
        if (File.Exists(destination))
        {
            if (_backups.ContainsKey(destination))
            {
                return;
            }

            var relative = Path.GetRelativePath(_installRoot, destination);
            var backup = Path.Combine(_backupRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(destination, backup, true);
            _backups.Add(destination, backup);
            return;
        }

        _createdFiles.Add(destination);
    }

    private void EnsureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureInsideInstallRoot(fullPath);
        if (Directory.Exists(fullPath))
        {
            return;
        }

        var missing = new Stack<string>();
        var current = fullPath;
        while (!Directory.Exists(current))
        {
            EnsureInsideInstallRoot(current);
            missing.Push(current);
            current = Path.GetDirectoryName(current) ??
                throw new InvalidOperationException("The destination directory is invalid.");
        }

        while (missing.Count > 0)
        {
            var directory = missing.Pop();
            Directory.CreateDirectory(directory);
            _createdDirectories.Add(directory);
        }
    }

    private void EnsureInsideInstallRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.Equals(
                _installRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(_installRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to deploy or roll back outside {_installRoot}");
        }
    }

    private static string EnsureRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) +
        Path.DirectorySeparatorChar;
}
