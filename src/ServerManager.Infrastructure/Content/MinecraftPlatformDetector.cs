using System.Text.Json;
using System.Text.RegularExpressions;
using ServerManager.Contracts;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Works out which server software is installed, from the files that are actually there.
/// When the evidence does not say, the answer is Unknown: guessing would mean offering
/// plugins to a server that cannot load them.
/// </summary>
public static partial class MinecraftPlatformDetector
{
    /// <summary>
    /// Paper and its forks write version_history.json next to the server jar, for example
    /// "git-Purpur-2324 (MC: 1.21.8)". That single line names both the fork and the game
    /// version, so it is the most reliable evidence available.
    /// </summary>
    public static (ServerPlatform Platform, string? MinecraftVersion, string? PlatformVersion) Detect(
        string serverRoot)
    {
        if (string.IsNullOrWhiteSpace(serverRoot) || !Directory.Exists(serverRoot))
        {
            return (ServerPlatform.Unknown, null, null);
        }

        var history = Path.Combine(serverRoot, "version_history.json");
        if (File.Exists(history))
        {
            var detected = ReadVersionHistory(history);
            if (detected.Platform != ServerPlatform.Unknown)
            {
                return detected;
            }
        }

        // Fall back to the jar names the common installers leave behind.
        foreach (var file in Directory.EnumerateFiles(serverRoot, "*.jar", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            var platform = name switch
            {
                var value when value.Contains("purpur", StringComparison.Ordinal) => ServerPlatform.Purpur,
                var value when value.Contains("folia", StringComparison.Ordinal) => ServerPlatform.Folia,
                var value when value.Contains("paper", StringComparison.Ordinal) => ServerPlatform.Paper,
                var value when value.Contains("spigot", StringComparison.Ordinal) => ServerPlatform.Spigot,
                var value when value.Contains("craftbukkit", StringComparison.Ordinal) => ServerPlatform.Bukkit,
                _ => ServerPlatform.Unknown
            };
            if (platform != ServerPlatform.Unknown)
            {
                return (platform, null, null);
            }
        }

        // A plain server.jar with no fork marker is vanilla, which has no plugin API. Saying
        // so plainly is better than leaving it Unknown and implying we might be wrong.
        return File.Exists(Path.Combine(serverRoot, "server.jar"))
            ? (ServerPlatform.Vanilla, null, null)
            : (ServerPlatform.Unknown, null, null);
    }

    private static (ServerPlatform Platform, string? MinecraftVersion, string? PlatformVersion)
        ReadVersionHistory(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var current = document.RootElement.TryGetProperty("currentVersion", out var value)
                ? value.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(current))
            {
                return (ServerPlatform.Unknown, null, null);
            }

            var platform = current.Contains("purpur", StringComparison.OrdinalIgnoreCase)
                ? ServerPlatform.Purpur
                : current.Contains("folia", StringComparison.OrdinalIgnoreCase)
                    ? ServerPlatform.Folia
                    : current.Contains("paper", StringComparison.OrdinalIgnoreCase)
                        ? ServerPlatform.Paper
                        : ServerPlatform.Unknown;

            var match = MinecraftVersionPattern().Match(current);
            var minecraftVersion = match.Success ? match.Groups[1].Value : null;
            var build = BuildPattern().Match(current);
            return (platform, minecraftVersion, build.Success ? build.Groups[1].Value : null);
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return (ServerPlatform.Unknown, null, null);
        }
    }

    [GeneratedRegex(@"MC:\s*([0-9][0-9A-Za-z.\-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex MinecraftVersionPattern();

    [GeneratedRegex(@"-(\d+)\s*\(MC", RegexOptions.IgnoreCase)]
    private static partial Regex BuildPattern();
}
