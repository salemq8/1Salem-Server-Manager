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
/// A boolean gamerule the Gameplay page offers. <see cref="Key"/> is the classic camelCase name;
/// newer Minecraft versions may know the rule by its snake_case form instead. Which one a server
/// actually has is never assumed: it is read from the server's own answers or its level.dat.
/// </summary>
public sealed record MinecraftGameRuleDefinition(string Key, MinecraftRuleGroup Group, bool Common)
{
    public IReadOnlyList<string> Names { get; } =
        [Key, MinecraftGameRuleCatalog.SnakeCase(Key), "minecraft:" + MinecraftGameRuleCatalog.SnakeCase(Key)];
}

/// <summary>The gamerules shown on the Gameplay page, grouped, with the common ones first.</summary>
public static class MinecraftGameRuleCatalog
{
    public static IReadOnlyList<MinecraftGameRuleDefinition> All { get; } =
    [
        new("keepInventory", MinecraftRuleGroup.Players, Common: true),
        new("doImmediateRespawn", MinecraftRuleGroup.Players, Common: true),
        new("naturalRegeneration", MinecraftRuleGroup.Players, Common: true),
        new("showDeathMessages", MinecraftRuleGroup.Players, Common: false),
        new("announceAdvancements", MinecraftRuleGroup.Players, Common: false),
        new("doDaylightCycle", MinecraftRuleGroup.World, Common: true),
        new("doWeatherCycle", MinecraftRuleGroup.World, Common: true),
        new("doFireTick", MinecraftRuleGroup.World, Common: true),
        new("doInsomnia", MinecraftRuleGroup.World, Common: false),
        new("fallDamage", MinecraftRuleGroup.Damage, Common: true),
        new("fireDamage", MinecraftRuleGroup.Damage, Common: false),
        new("drowningDamage", MinecraftRuleGroup.Damage, Common: false),
        new("freezeDamage", MinecraftRuleGroup.Damage, Common: false),
        new("doMobSpawning", MinecraftRuleGroup.Mobs, Common: true),
        new("mobGriefing", MinecraftRuleGroup.Mobs, Common: true),
        new("doPatrolSpawning", MinecraftRuleGroup.Mobs, Common: false),
        new("doTraderSpawning", MinecraftRuleGroup.Mobs, Common: false),
        new("doMobLoot", MinecraftRuleGroup.Drops, Common: false),
        new("doTileDrops", MinecraftRuleGroup.Drops, Common: false),
        new("doEntityDrops", MinecraftRuleGroup.Drops, Common: false)
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
