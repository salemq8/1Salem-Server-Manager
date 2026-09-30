using System.Text.RegularExpressions;
using ServerManager.Contracts;

namespace ServerManager.Core.Minecraft;

/// <summary>How a player command was answered.</summary>
public enum MinecraftPlayerReply
{
    Applied = 1,
    NoChange = 2,
    UnknownPlayer = 3,
    Refused = 4
}

/// <summary>
/// Reads the Minecraft server's console answers. Only the message after the log prefix
/// ("[12:00:00] [Server thread/INFO]: " or Paper's "[12:00:00 INFO]: ") is matched, and every
/// pattern is anchored at its start, so a chat line such as "&lt;Bob&gt; Gamerule ..." never counts.
/// </summary>
public static partial class MinecraftConsoleReplies
{
    /// <summary>The message part of a console line.</summary>
    public static string Message(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        var index = line.IndexOf("]: ", StringComparison.Ordinal);
        return (index >= 0 ? line[(index + 3)..] : line).Trim();
    }

    /// <summary>"Done (12.3s)! For help, type "help"": the server is ready for commands.</summary>
    public static bool IsReady(string? line)
    {
        var message = Message(line);
        return message.StartsWith("Done (", StringComparison.Ordinal) &&
               message.Contains("For help", StringComparison.Ordinal);
    }

    /// <summary>"Gamerule keepInventory is currently set to: false".</summary>
    public static bool TryParseGameRuleQuery(string? line, out string name, out string value) =>
        TryMatch(GameRuleQueryRegex(), line, out name, out value);

    /// <summary>"Gamerule keepInventory is now set to: true".</summary>
    public static bool TryParseGameRuleSet(string? line, out string name, out string value) =>
        TryMatch(GameRuleSetRegex(), line, out name, out value);

    /// <summary>
    /// Whether a line answers "gamerule name" (query) or "gamerule name value" (set): that rule's
    /// value line, or an error line naming it (Minecraft follows "Incorrect argument for command"
    /// with "gamerule name&lt;--[HERE]"). Answers carry no id, so a late line from an earlier
    /// command, such as the second line of its error, is never taken for this one.
    /// </summary>
    public static bool AnswersGameRule(string? line, string name, bool set)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var parsed = set
            ? TryParseGameRuleSet(line, out var answered, out _)
            : TryParseGameRuleQuery(line, out answered, out _);
        if (parsed)
        {
            return string.Equals(WithoutNamespace(answered), WithoutNamespace(name), StringComparison.Ordinal);
        }

        return IsCommandError(line) && Message(line).Contains(WithoutNamespace(name), StringComparison.Ordinal);
    }

    private static string WithoutNamespace(string name) =>
        name.StartsWith("minecraft:", StringComparison.Ordinal) ? name["minecraft:".Length..] : name;

    /// <summary>The server did not understand the command (for example a gamerule it does not have).</summary>
    public static bool IsCommandError(string? line)
    {
        var message = Message(line);
        return message.StartsWith("Unknown or incomplete command", StringComparison.Ordinal) ||
               message.StartsWith("Incorrect argument for command", StringComparison.Ordinal) ||
               message.StartsWith("Unknown game rule", StringComparison.OrdinalIgnoreCase) ||
               message.StartsWith("Invalid ", StringComparison.Ordinal) ||
               message.Contains("<--[HERE]", StringComparison.Ordinal);
    }

    /// <summary>
    /// "There are 2 of a max of 20 players online: Alex, Steve" (Paper: "out of maximum").
    /// </summary>
    public static bool TryParseList(string? line, out int online, out int max, out IReadOnlyList<string> names)
    {
        var match = ListRegex().Match(Message(line));
        if (!match.Success)
        {
            online = 0;
            max = 0;
            names = [];
            return false;
        }

        online = int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
        max = int.Parse(match.Groups["m"].Value, System.Globalization.CultureInfo.InvariantCulture);
        names = match.Groups["names"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.Length is > 0 and <= 32)
            .ToArray();
        return true;
    }

    /// <summary>
    /// The answer to a player command, or null when <paramref name="line"/> is not one. Covers
    /// op, deop, whitelist add/remove, kick, ban and pardon as vanilla words them.
    /// </summary>
    public static MinecraftPlayerReply? ClassifyPlayerReply(MinecraftPlayerAction action, string player, string? line)
    {
        var message = Message(line);
        if (message.Length == 0)
        {
            return null;
        }

        if (message.StartsWith("That player does not exist", StringComparison.Ordinal) ||
            message.StartsWith("No player was found", StringComparison.Ordinal))
        {
            return MinecraftPlayerReply.UnknownPlayer;
        }

        if (message.StartsWith("Nothing changed", StringComparison.Ordinal) ||
            message.StartsWith("Player is already whitelisted", StringComparison.Ordinal) ||
            message.StartsWith("Player is not whitelisted", StringComparison.Ordinal))
        {
            return MinecraftPlayerReply.NoChange;
        }

        if (IsCommandError(line))
        {
            return MinecraftPlayerReply.Refused;
        }

        var applied = action switch
        {
            MinecraftPlayerAction.Op => $"Made {player} a server operator",
            MinecraftPlayerAction.Deop => $"Made {player} no longer a server operator",
            MinecraftPlayerAction.WhitelistAdd => $"Added {player} to the whitelist",
            MinecraftPlayerAction.WhitelistRemove => $"Removed {player} from the whitelist",
            MinecraftPlayerAction.Kick => $"Kicked {player}",
            MinecraftPlayerAction.Ban => $"Banned {player}",
            MinecraftPlayerAction.Pardon => $"Unbanned {player}",
            _ => null
        };
        return applied is not null && message.StartsWith(applied, StringComparison.OrdinalIgnoreCase)
            ? MinecraftPlayerReply.Applied
            : null;
    }

    /// <summary>
    /// The answer to a live property command ("difficulty hard", "whitelist on"): true applied,
    /// false refused, null not an answer to it.
    /// </summary>
    public static bool? ClassifyPropertyReply(string key, string? line)
    {
        var message = Message(line);
        if (IsCommandError(line))
        {
            return false;
        }

        return key switch
        {
            "difficulty" when message.StartsWith("The difficulty has been set to", StringComparison.Ordinal) ||
                              message.StartsWith("The difficulty did not change", StringComparison.Ordinal) => true,
            "white-list" when message.StartsWith("Whitelist is now turned", StringComparison.Ordinal) ||
                              message.StartsWith("Whitelist is already turned", StringComparison.Ordinal) => true,
            _ => null
        };
    }

    private static bool TryMatch(Regex regex, string? line, out string name, out string value)
    {
        var match = regex.Match(Message(line));
        name = match.Success ? match.Groups["name"].Value : string.Empty;
        value = match.Success ? match.Groups["value"].Value : string.Empty;
        return match.Success;
    }

    [GeneratedRegex(@"^Gamerule (?<name>[A-Za-z0-9_:.]+) is currently set to: (?<value>\S+)$")]
    private static partial Regex GameRuleQueryRegex();

    [GeneratedRegex(@"^Gamerule (?<name>[A-Za-z0-9_:.]+) is now set to: (?<value>\S+)$")]
    private static partial Regex GameRuleSetRegex();

    [GeneratedRegex(@"^There are (?<n>\d+) (?:of a max of|out of maximum) (?<m>\d+) players online[.:]?\s*(?<names>.*)$")]
    private static partial Regex ListRegex();
}
