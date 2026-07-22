namespace ServerManager.Core;

public static class SafePathPolicy
{
    public static bool IsWithinRoot(string candidatePath, string rootPath)
    {
        var candidate = Normalize(candidatePath);
        var root = EnsureTrailingSeparator(Normalize(rootPath));
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveWithinRoot(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException("A non-empty relative path is required.", nameof(relativePath));
        }

        var resolved = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        if (!IsWithinRoot(resolved, rootPath))
        {
            throw new UnauthorizedAccessException("The resolved path escapes the registered server root.");
        }

        return resolved;
    }

    public static IReadOnlyList<string> GetRiskWarnings(string path)
    {
        var normalized = Normalize(path);
        var warnings = new List<string>();
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(downloads) &&
            IsWithinRoot(normalized, Path.Combine(downloads, "Downloads")))
        {
            warnings.Add("Downloads is not recommended as a permanent server location.");
        }

        if (IsWithinRoot(normalized, Path.GetTempPath()))
        {
            warnings.Add("Temporary folders can be cleaned automatically by Windows.");
        }

        if (normalized.Contains(
                $"{Path.DirectorySeparatorChar}OneDrive{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("OneDrive synchronization can corrupt or lock active server files.");
        }

        return warnings;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A path is required.", nameof(path));
        }

        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
