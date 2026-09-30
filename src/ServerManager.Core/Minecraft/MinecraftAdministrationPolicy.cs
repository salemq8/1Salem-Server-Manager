using System.Globalization;
using System.Text.RegularExpressions;
using ServerManager.Contracts;

namespace ServerManager.Core.Minecraft;

/// <summary>
/// Builds the only player commands the manager sends. A player name must be a Java Edition name
/// (3-16 letters, digits or underscores) before any command text is formed, so nothing else can
/// reach the console through a name.
/// </summary>
public static partial class MinecraftPlayerCommandPolicy
{
    public static bool IsValidName(string? name) => name is not null && JavaNameRegex().IsMatch(name);

    public static string BuildCommand(MinecraftPlayerAction action, string player)
    {
        if (!IsValidName(player))
        {
            throw new ArgumentException("A Minecraft Java player name has 3-16 letters, digits or underscores.", nameof(player));
        }

        return action switch
        {
            MinecraftPlayerAction.Op => $"op {player}",
            MinecraftPlayerAction.Deop => $"deop {player}",
            MinecraftPlayerAction.WhitelistAdd => $"whitelist add {player}",
            MinecraftPlayerAction.WhitelistRemove => $"whitelist remove {player}",
            MinecraftPlayerAction.Kick => $"kick {player}",
            MinecraftPlayerAction.Ban => $"ban {player}",
            MinecraftPlayerAction.Pardon => $"pardon {player}",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown player action.")
        };
    }

    /// <summary>Actions that take something away from a player are confirmed first.</summary>
    public static bool NeedsConfirmation(MinecraftPlayerAction action) =>
        action is MinecraftPlayerAction.Deop or MinecraftPlayerAction.WhitelistRemove or
            MinecraftPlayerAction.Kick or MinecraftPlayerAction.Ban;

    /// <summary>Kicking needs the player on the server; the rest work for any known name.</summary>
    public static bool NeedsPlayerOnline(MinecraftPlayerAction action) => action == MinecraftPlayerAction.Kick;

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$")]
    private static partial Regex JavaNameRegex();
}

public enum MinecraftPropertyKind
{
    Boolean = 1,
    Choice = 2,
    Number = 3
}

/// <summary>A server.properties value the Gameplay page manages.</summary>
public sealed record MinecraftPropertyDefinition(
    string Key,
    MinecraftPropertyKind Kind,
    bool Advanced,
    IReadOnlyList<string>? Choices = null,
    int Minimum = 0,
    int Maximum = 0);

/// <summary>
/// The server.properties values on the Gameplay page and how each one may change. They are read
/// by Minecraft when it starts, so a saved change takes effect at the next start; difficulty and
/// the whitelist also have real commands and are applied live when the server is answering.
/// </summary>
public static class MinecraftGameplayPropertyPolicy
{
    public static IReadOnlyList<MinecraftPropertyDefinition> All { get; } =
    [
        new("difficulty", MinecraftPropertyKind.Choice, Advanced: false, Choices: ["peaceful", "easy", "normal", "hard"]),
        new("gamemode", MinecraftPropertyKind.Choice, Advanced: false, Choices: ["survival", "creative", "adventure", "spectator"]),
        new("force-gamemode", MinecraftPropertyKind.Boolean, Advanced: false),
        new("hardcore", MinecraftPropertyKind.Boolean, Advanced: false),
        new("pvp", MinecraftPropertyKind.Boolean, Advanced: false),
        new("allow-flight", MinecraftPropertyKind.Boolean, Advanced: false),
        new("max-players", MinecraftPropertyKind.Number, Advanced: false, Minimum: 1, Maximum: 1000),
        new("white-list", MinecraftPropertyKind.Boolean, Advanced: false),
        new("spawn-protection", MinecraftPropertyKind.Number, Advanced: false, Minimum: 0, Maximum: 64),
        new("view-distance", MinecraftPropertyKind.Number, Advanced: false, Minimum: 2, Maximum: 32),
        new("simulation-distance", MinecraftPropertyKind.Number, Advanced: false, Minimum: 2, Maximum: 32),
        new("enable-command-block", MinecraftPropertyKind.Boolean, Advanced: true),
        new("online-mode", MinecraftPropertyKind.Boolean, Advanced: true),
        new("op-permission-level", MinecraftPropertyKind.Number, Advanced: true, Minimum: 1, Maximum: 4)
    ];

    public static MinecraftPropertyDefinition? Find(string? key) =>
        All.FirstOrDefault(property => string.Equals(property.Key, key, StringComparison.Ordinal));

    /// <summary>Checks and normalizes a value for server.properties ("True" -> "true", " 8 " -> "8").</summary>
    public static bool TryNormalize(string key, string? value, out string normalized, out string? error)
    {
        normalized = string.Empty;
        var property = Find(key);
        if (property is null)
        {
            error = $"'{key}' is not a gameplay setting.";
            return false;
        }

        var text = value?.Trim() ?? string.Empty;
        switch (property.Kind)
        {
            case MinecraftPropertyKind.Boolean when bool.TryParse(text, out var flag):
                normalized = flag ? "true" : "false";
                error = null;
                return true;
            case MinecraftPropertyKind.Choice when property.Choices!.Contains(text.ToLowerInvariant()):
                normalized = text.ToLowerInvariant();
                error = null;
                return true;
            case MinecraftPropertyKind.Number when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                                                   number >= property.Minimum && number <= property.Maximum:
                normalized = number.ToString(CultureInfo.InvariantCulture);
                error = null;
                return true;
            default:
                error = property.Kind switch
                {
                    MinecraftPropertyKind.Boolean => $"'{key}' must be true or false.",
                    MinecraftPropertyKind.Choice => $"'{key}' must be one of: {string.Join(", ", property.Choices!)}.",
                    _ => $"'{key}' must be a whole number from {property.Minimum} to {property.Maximum}."
                };
                return false;
        }
    }

    /// <summary>The real command that applies a saved value to the running server, if there is one.</summary>
    public static string? LiveCommand(string key, string normalized) =>
        key switch
        {
            "difficulty" => $"difficulty {normalized}",
            "white-list" => normalized == "true" ? "whitelist on" : "whitelist off",
            _ => null
        };

    public static bool CanApplyLive(string key) => key is "difficulty" or "white-list";
}

/// <summary>
/// Which fall-damage choices are real. Vanilla and the Bukkit family (Paper, Purpur, Spigot,
/// Bukkit, Folia) only have the boolean fallDamage gamerule. Scaling it to 75, 50 or 25% would
/// mean re-applying the per-player fall_damage_multiplier attribute after every join and respawn,
/// which is exactly the repeated-command workaround this app does not do. So the page offers
/// Normal (100%) and Disabled (0%), and only when the server has the rule.
/// </summary>
public static class MinecraftFallDamagePolicy
{
    public static MinecraftFallDamageCapability For(bool fallDamageRuleSupported) =>
        fallDamageRuleSupported
            ? new MinecraftFallDamageCapability(true, [100, 0])
            : new MinecraftFallDamageCapability(false, []);

    /// <summary>The fallDamage gamerule value for an offered percentage; null for anything not offered.</summary>
    public static bool? GameRuleValue(int percent) =>
        percent switch
        {
            100 => true,
            0 => false,
            _ => null
        };
}
