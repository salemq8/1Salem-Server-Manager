using System.Text;

namespace ServerManager.Core.Content;

/// <summary>
/// Turns provider-supplied names into a destination we control. A provider's file name,
/// project title and slug are treated as untrusted text, never as a path: the manager always
/// generates the final location from the validated server root.
/// </summary>
public static class ContentPathPolicy
{
    public const string PluginsDirectoryName = "plugins";
    private const int MaximumFileNameLength = 120;

    // Names Windows still refuses to write even with an extension.
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    /// <summary>The server's plugins folder, resolved inside the registered root.</summary>
    public static string ResolvePluginsDirectory(string serverRoot) =>
        SafePathPolicy.ResolveWithinRoot(Path.GetFullPath(serverRoot), PluginsDirectoryName);

    /// <summary>
    /// The active world's data pack folder. The world's name comes from that server's own
    /// server.properties, so this is never assumed to be "world": a server with
    /// level-name=survival keeps its data packs in survival\datapacks.
    /// </summary>
    public static string ResolveDataPackDirectory(string serverRoot, string levelName)
    {
        if (string.IsNullOrWhiteSpace(levelName))
        {
            throw new ArgumentException("A world name is required.", nameof(levelName));
        }

        // level-name is server-owned text, so it is treated as untrusted: one path segment,
        // no separators, resolved back inside the root.
        if (levelName.IndexOfAny(['/', '\\', ':']) >= 0 ||
            levelName.Split('/', '\\').Any(segment => segment is "." or ".."))
        {
            throw new UnauthorizedAccessException("The world name is not a single folder name.");
        }

        return SafePathPolicy.ResolveWithinRoot(
            Path.GetFullPath(serverRoot),
            Path.Combine(levelName, "datapacks"));
    }

    /// <summary>
    /// Where downloaded resource packs are kept. A server does not load these itself; they
    /// are stored for the person and can be pointed at clients later.
    /// </summary>
    public static string ResolveResourcePackDirectory(string serverRoot) =>
        SafePathPolicy.ResolveWithinRoot(
            Path.GetFullPath(serverRoot),
            Path.Combine(".1salem", "content", "resourcepacks"));

    /// <summary>
    /// Where the .mrpack a server was built from is kept. It is small, and keeping it means
    /// the server can say exactly what it came from and be checked against it later.
    /// </summary>
    public static string ResolveModpackDirectory(string serverRoot) =>
        SafePathPolicy.ResolveWithinRoot(
            Path.GetFullPath(serverRoot),
            Path.Combine(".1salem", "content", "modpack"));

    /// <summary>Where downloads are staged before anything touches the plugins folder.</summary>
    public static string ResolveStagingDirectory(string serverRoot) =>
        SafePathPolicy.ResolveWithinRoot(
            Path.GetFullPath(serverRoot),
            Path.Combine(".1salem", "content", "staging"));

    /// <summary>Where the previous JAR is kept so an update can be rolled back.</summary>
    public static string ResolveRollbackDirectory(string serverRoot) =>
        SafePathPolicy.ResolveWithinRoot(
            Path.GetFullPath(serverRoot),
            Path.Combine(".1salem", "content", "rollback"));

    /// <summary>
    /// Reduces a provider file name to something safe to write. Any directory part is
    /// dropped rather than honoured, so "../../evil.jar" becomes "evil.jar" and can only
    /// ever land inside the plugins folder.
    /// </summary>
    public static string SanitizeFileName(
        string? providerFileName,
        string fallbackStem,
        string extension = ".jar")
    {
        var candidate = providerFileName ?? string.Empty;

        // Cut everything up to the last separator of either flavour, so no path survives.
        var lastSeparator = candidate.LastIndexOfAny(['/', '\\', ':']);
        if (lastSeparator >= 0)
        {
            candidate = candidate[(lastSeparator + 1)..];
        }

        var builder = new StringBuilder(candidate.Length);
        foreach (var character in candidate)
        {
            var allowed = char.IsAsciiLetterOrDigit(character) ||
                          character is '.' or '-' or '_' or '+' or '(' or ')' or ' ';
            builder.Append(allowed ? character : '-');
        }

        var safe = builder.ToString().Trim().Trim('.', '-', ' ');
        while (safe.Contains("..", StringComparison.Ordinal))
        {
            safe = safe.Replace("..", ".", StringComparison.Ordinal);
        }

        if (!safe.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            safe = $"{safe}{extension}";
        }

        var stem = Path.GetFileNameWithoutExtension(safe);
        if (string.IsNullOrWhiteSpace(stem) ||
            ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            stem = SanitizeStem(fallbackStem);
            safe = $"{stem}{extension}";
        }

        if (safe.Length > MaximumFileNameLength)
        {
            var room = Math.Max(1, MaximumFileNameLength - extension.Length);
            stem = stem[..Math.Min(stem.Length, room)].TrimEnd('.', '-', ' ');
            safe = $"{stem}{extension}";
        }

        return safe;
    }

    /// <summary>
    /// The final install path. It is resolved inside the plugins folder and re-checked, so a
    /// name that somehow still escaped would be refused here instead of being written.
    /// </summary>
    public static string ResolveInstallPath(string pluginsDirectory, string safeFileName)
    {
        if (string.IsNullOrWhiteSpace(safeFileName) ||
            safeFileName.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new UnauthorizedAccessException("A plugin file name may not contain a path.");
        }

        var directory = Path.GetFullPath(pluginsDirectory);
        var resolved = Path.GetFullPath(Path.Combine(directory, safeFileName));
        if (!SafePathPolicy.IsWithinRoot(resolved, directory) ||
            !string.Equals(
                Path.GetDirectoryName(resolved),
                directory.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "The resolved plugin path escapes the server's plugins folder.");
        }

        return resolved;
    }

    /// <summary>
    /// The folder a plugin keeps its configuration in. Uninstall leaves this alone: removing
    /// someone's permissions or economy data is a separate, deliberate choice.
    /// </summary>
    public static string? ResolvePluginDataDirectory(string pluginsDirectory, string pluginName)
    {
        var stem = SanitizeStem(pluginName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        var resolved = Path.GetFullPath(Path.Combine(Path.GetFullPath(pluginsDirectory), stem));
        return SafePathPolicy.IsWithinRoot(resolved, pluginsDirectory) ? resolved : null;
    }

    private static string SanitizeStem(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        return builder.ToString().Trim('-').Length == 0
            ? "plugin"
            : builder.ToString().Trim('-');
    }
}

