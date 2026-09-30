using System.Text;

namespace ServerManager.Core.Minecraft;

public enum MinecraftRuleGroup
{
    Players = 1,
    World = 2,
    Damage = 3,
    Mobs = 4,
    Drops = 5
}

/// <summary>
/// A boolean gamerule the Gameplay page offers. <see cref="Key"/> is the classic camelCase name.
/// Since the gamerule registry (Minecraft 1.21.11 and the 26.x releases) rules are named
/// "minecraft:snake_case", and some were renamed on the way (doDaylightCycle is now advance_time);
/// <see cref="ModernName"/> holds that newer name when it is not simply the snake_case key. Which
/// name a server actually has is never assumed: it is read from its answers or its world files.
/// </summary>
public sealed record MinecraftGameRuleDefinition(string Key, MinecraftRuleGroup Group, bool Common, string? ModernName = null)
{
    public IReadOnlyList<string> Names { get; } = BuildNames(Key, ModernName);

    private static string[] BuildNames(string key, string? modernName)
    {
        var snake = MinecraftGameRuleCatalog.SnakeCase(key);
        var modern = modernName ?? snake;
        return new[] { key, snake, "minecraft:" + snake, modern, "minecraft:" + modern }
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>The gamerules shown on the Gameplay page, grouped, with the common ones first.</summary>
public static class MinecraftGameRuleCatalog
{
    public static IReadOnlyList<MinecraftGameRuleDefinition> All { get; } =
    [
        // PvP moved from server.properties to a gamerule in 1.21.9; older servers do not have it.
        new("pvp", MinecraftRuleGroup.Players, Common: true),
        new("keepInventory", MinecraftRuleGroup.Players, Common: true),
        new("doImmediateRespawn", MinecraftRuleGroup.Players, Common: true, ModernName: "immediate_respawn"),
        new("naturalRegeneration", MinecraftRuleGroup.Players, Common: true, ModernName: "natural_health_regeneration"),
        new("showDeathMessages", MinecraftRuleGroup.Players, Common: false),
        new("announceAdvancements", MinecraftRuleGroup.Players, Common: false, ModernName: "show_advancement_messages"),
        new("doDaylightCycle", MinecraftRuleGroup.World, Common: true, ModernName: "advance_time"),
        new("doWeatherCycle", MinecraftRuleGroup.World, Common: true, ModernName: "advance_weather"),

        // Newer versions replaced it with fire_spread_radius_around_player, a number rather than
        // on/off, so it is offered only where the switch really exists.
        new("doFireTick", MinecraftRuleGroup.World, Common: true),
        new("doInsomnia", MinecraftRuleGroup.World, Common: false, ModernName: "spawn_phantoms"),
        new("fallDamage", MinecraftRuleGroup.Damage, Common: true),
        new("fireDamage", MinecraftRuleGroup.Damage, Common: false),
        new("drowningDamage", MinecraftRuleGroup.Damage, Common: false),
        new("freezeDamage", MinecraftRuleGroup.Damage, Common: false),
        new("doMobSpawning", MinecraftRuleGroup.Mobs, Common: true, ModernName: "spawn_mobs"),
        new("mobGriefing", MinecraftRuleGroup.Mobs, Common: true),
        new("doPatrolSpawning", MinecraftRuleGroup.Mobs, Common: false, ModernName: "spawn_patrols"),
        new("doTraderSpawning", MinecraftRuleGroup.Mobs, Common: false, ModernName: "spawn_wandering_traders"),
        new("doMobLoot", MinecraftRuleGroup.Drops, Common: false, ModernName: "mob_drops"),
        new("doTileDrops", MinecraftRuleGroup.Drops, Common: false, ModernName: "block_drops"),
        new("doEntityDrops", MinecraftRuleGroup.Drops, Common: false, ModernName: "entity_drops")
    ];

    public static MinecraftGameRuleDefinition? Find(string? key) =>
        All.FirstOrDefault(rule => string.Equals(rule.Key, key, StringComparison.Ordinal));

    /// <summary>keepInventory -> keep_inventory.</summary>
    public static string SnakeCase(string camel)
    {
        ArgumentException.ThrowIfNullOrEmpty(camel);
        var builder = new StringBuilder(camel.Length + 4);
        foreach (var character in camel)
        {
            if (char.IsUpper(character))
            {
                builder.Append('_').Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The name this server uses for the rule, taken from the names the server or its world
    /// knows. Null means the server does not have the rule, so it is not offered.
    /// </summary>
    public static string? ResolveName(MinecraftGameRuleDefinition rule, IReadOnlyCollection<string> knownNames)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(knownNames);
        return rule.Names.FirstOrDefault(name => knownNames.Contains(name, StringComparer.Ordinal));
    }

    /// <summary>Reads a gamerule value as stored or answered: "true"/"false", or 1/0 in typed saves.</summary>
    public static bool TryParseValue(string? text, out bool value)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "true" or "1":
                value = true;
                return true;
            case "false" or "0":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    public static string Format(bool value) => value ? "true" : "false";
}
