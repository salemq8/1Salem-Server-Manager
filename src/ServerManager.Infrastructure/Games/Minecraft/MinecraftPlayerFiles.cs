using System.Text.Json;
using ServerManager.Core.Minecraft;

namespace ServerManager.Infrastructure.Games.Minecraft;

/// <summary>Read-only, shared access to registered server data. Never follows a world link outside its root.</summary>
public static class MinecraftPlayerFiles
{
    public static string? ResolveWorld(string root)
    {
        try
        {
            // A missing file uses Minecraft's default world name. An existing but unreadable
            // or unsafe file must not silently select a different world's player data.
            var properties = ReadPropertiesCore(root);
            var name = properties.GetValueOrDefault("level-name", "world");
            if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains(':')) return null;
            var path = Path.GetFullPath(Path.Combine(root, name));
            return IsSafeChild(root, path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static bool IsSafeChild(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        path = Path.GetFullPath(path);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static IReadOnlyDictionary<string, string> ReadProperties(string root)
    {
        try
        {
            return ReadPropertiesCore(root);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return new Dictionary<string, string>(); }
    }

    private static IReadOnlyDictionary<string, string> ReadPropertiesCore(string root)
    {
        const int limit = 1024 * 1024;
        var path = Path.Combine(root, "server.properties");
        if (!IsSafeChild(root, path)) throw new InvalidDataException("Unsafe server.properties path.");
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) { return new Dictionary<string, string>(); }
        using (stream)
        {
            if (stream.Length > limit) throw new InvalidDataException("server.properties exceeds the read limit.");
            using var reader = new StreamReader(stream);
            var chars = new char[limit + 1];
            var count = reader.ReadBlock(chars, 0, chars.Length);
            if (count > limit) throw new InvalidDataException("server.properties exceeds the read limit.");
            return MinecraftPropertiesSerializer.Parse(new string(chars, 0, count));
        }
    }

    public static JsonDocument? ReadJson(string root, string path)
    {
        try
        {
            if (!IsSafeChild(root, path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 8 * 1024 * 1024) return null;
            return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static IReadOnlyList<string> PlayerDataDirectories(string world) => [Path.Combine(world, "players", "data"), Path.Combine(world, "playerdata")];
    public static IReadOnlyList<string> StatsDirectories(string world) => [Path.Combine(world, "players", "stats"), Path.Combine(world, "stats")];
}
