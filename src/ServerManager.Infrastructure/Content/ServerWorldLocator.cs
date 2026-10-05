using System.Text;
using ServerManager.Core.Content;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Content;

public sealed record WorldLocation(
    bool Found,
    string? LevelName,
    string? WorldDirectory,
    string? DataPackDirectory,
    string? Reason);

/// <summary>
/// Finds the world a server is actually running, and edits only the resource-pack keys in
/// its server.properties. The world is read from the server's own configuration: nothing here
/// assumes a folder called "world".
/// </summary>
public static class ServerWorldLocator
{
    public const string LevelNameKey = "level-name";

    public static WorldLocation Locate(string serverRoot)
    {
        var propertiesPath = Path.Combine(Path.GetFullPath(serverRoot), "server.properties");
        if (!File.Exists(propertiesPath))
        {
            return new WorldLocation(false, null, null, null, "NoServerProperties");
        }

        string content;
        try
        {
            content = MinecraftPropertiesSerializer.ReadFile(propertiesPath);
        }
        catch (IOException)
        {
            return new WorldLocation(false, null, null, null, "PropertiesUnreadable");
        }

        var values = MinecraftPropertiesSerializer.Parse(content);
        if (!values.TryGetValue(LevelNameKey, out var levelName) ||
            string.IsNullOrWhiteSpace(levelName))
        {
            // Minecraft defaults to "world", but assuming that could put a data pack into the
            // wrong world, so this is reported instead of guessed.
            return new WorldLocation(false, null, null, null, "NoLevelName");
        }

        try
        {
            var dataPacks = ContentPathPolicy.ResolveDataPackDirectory(serverRoot, levelName.Trim());
            return new WorldLocation(
                true,
                levelName.Trim(),
                Path.GetDirectoryName(dataPacks),
                dataPacks,
                null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or UnauthorizedAccessException)
        {
            return new WorldLocation(false, levelName, null, null, "UnsafeLevelName");
        }
    }

    /// <summary>
    /// Updates only the resource-pack keys, leaving every other line, comment and ordering in
    /// server.properties exactly as it was.
    /// </summary>
    public static void WriteResourcePackSettings(
        string serverRoot,
        string? url,
        string? sha1,
        bool require,
        string? prompt)
    {
        var path = Path.Combine(Path.GetFullPath(serverRoot), "server.properties");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The server has no server.properties.", path);
        }

        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["resource-pack"] = url ?? string.Empty,
            ["resource-pack-sha1"] = sha1?.ToLowerInvariant() ?? string.Empty,
            ["require-resource-pack"] = require ? "true" : "false"
        };
        if (prompt is not null)
        {
            updates["resource-pack-prompt"] = prompt;
        }

        var lines = File.ReadAllLines(path).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.StartsWith('#') || !line.Contains('='))
            {
                continue;
            }

            var key = line[..line.IndexOf('=')].Trim();
            if (updates.TryGetValue(key, out var value))
            {
                lines[index] = $"{key}={value}";
                seen.Add(key);
            }
        }

        foreach (var (key, value) in updates.Where(pair => !seen.Contains(pair.Key)))
        {
            lines.Add($"{key}={value}");
        }

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    /// <summary>What the server currently tells clients to download, if anything.</summary>
    public static (string? Url, string? Sha1, bool Require) ReadResourcePackSettings(string serverRoot)
    {
        var path = Path.Combine(Path.GetFullPath(serverRoot), "server.properties");
        if (!File.Exists(path))
        {
            return (null, null, false);
        }

        try
        {
            var values = MinecraftPropertiesSerializer.Parse(MinecraftPropertiesSerializer.ReadFile(path));
            values.TryGetValue("resource-pack", out var url);
            values.TryGetValue("resource-pack-sha1", out var sha1);
            values.TryGetValue("require-resource-pack", out var require);
            return (
                string.IsNullOrWhiteSpace(url) ? null : url,
                string.IsNullOrWhiteSpace(sha1) ? null : sha1,
                string.Equals(require, "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return (null, null, false);
        }
    }
}
