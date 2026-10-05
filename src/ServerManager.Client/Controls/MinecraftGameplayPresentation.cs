using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core.Minecraft;

namespace ServerManager.Client.Controls;

public enum GameplayRowKind
{
    Property = 1,
    GameRule = 2,
    FallDamage = 3
}

/// <summary>One row on the Gameplay page: a server.properties value, a gamerule, or fall damage.</summary>
public sealed record GameplayRowSpec(GameplayRowKind Kind, string Key)
{
    public string LabelKey => Kind switch
    {
        GameplayRowKind.Property => $"Gameplay.Property.{Key}",
        GameplayRowKind.GameRule => $"Gameplay.Rule.{Key}",
        _ => "Gameplay.FallDamage"
    };

    public string HelpKey => LabelKey + ".Help";
}

public sealed record GameplaySectionSpec(string Key, bool Advanced, IReadOnlyList<GameplayRowSpec> Rows)
{
    public string TitleKey => $"Gameplay.Section.{Key}";
}

/// <summary>
/// What the Gameplay page shows, decided without WPF so it can be tested: which rows go in which
/// card, which fall-damage choices are real, and what each state means in words.
/// </summary>
public static class MinecraftGameplayPresentation
{
    public static IReadOnlyList<GameplaySectionSpec> Sections { get; } =
    [
        new("General", false,
        [
            Property("difficulty"), Property("gamemode"), Property("force-gamemode"),
            Property("hardcore"), Rule("pvp"), Property("pvp"), Property("allow-flight")
        ]),
        new("Players", false,
        [
            Property("max-players"), Property("white-list"),
            Rule("keepInventory"), Rule("doImmediateRespawn"), Rule("naturalRegeneration"),
            Rule("showDeathMessages"), Rule("announceAdvancements")
        ]),
        new("World", false,
        [
            Property("spawn-protection"), Property("view-distance"), Property("simulation-distance"),
            Rule("doDaylightCycle"), Rule("doWeatherCycle"), Rule("doFireTick"), Rule("doInsomnia"), Rule("doTileDrops")
        ]),
        new("Damage", false,
        [
            new GameplayRowSpec(GameplayRowKind.FallDamage, "fallDamage"),
            Rule("fireDamage"), Rule("drowningDamage"), Rule("freezeDamage")
        ]),
        new("Mobs", false,
        [
            Rule("doMobSpawning"), Rule("mobGriefing"), Rule("doPatrolSpawning"), Rule("doTraderSpawning"),
            Rule("doMobLoot"), Rule("doEntityDrops")
        ]),
        new("Advanced", true,
        [
            Property("enable-command-block"), Property("online-mode"), Property("op-permission-level")
        ])
    ];

    /// <summary>The fall-damage choices to offer: exactly the server's real ones, nothing scaled.</summary>
    public static IReadOnlyList<int> FallDamageChoices(MinecraftFallDamageCapability? capability) =>
        capability is { Supported: true } ? capability.Percentages.Where(percent => percent is 100 or 0).ToArray() : [];

    public static string FallDamageLabel(int percent) =>
        LocalizationService.Get(percent == 0 ? "Gameplay.FallDamage.0" : "Gameplay.FallDamage.100");

    /// <summary>What the rule will be: a saved change wins over the current value.</summary>
    public static bool? EffectiveValue(MinecraftGameRuleState rule) => rule.PendingValue ?? rule.Value;

    /// <summary>
    /// What a click on a rule's switch asks for. A rule whose value is not known shows neither on
    /// nor off (the switch would otherwise go from unknown to Off), so a click on it asks for On.
    /// </summary>
    public static bool RequestedValue(bool? before, bool? clicked) => before is null || clicked == true;

    public static int? FallDamageSelection(MinecraftGameRuleState? rule) =>
        rule is null ? null : EffectiveValue(rule) switch
        {
            true => 100,
            false => 0,
            _ => null
        };

    /// <summary>Whether a rule gets a row: rules this server's version does not have are left out.</summary>
    public static bool IsShown(MinecraftGameRuleState? rule) => rule is { Supported: true };

    /// <summary>
    /// Whether a gamerule row is shown. A rule that replaced a server.properties value (PvP since
    /// Minecraft 1.21.9) is shown only once the server or its world confirms it exists; until
    /// then the server.properties row stands in, so one setting never gets two controls.
    /// </summary>
    public static bool ShowsRuleRow(string ruleKey, MinecraftGameplaySnapshot snapshot)
    {
        var rule = snapshot.GameRules.FirstOrDefault(item => item.Key == ruleKey);
        return IsShown(rule) &&
               (MinecraftGameplayPropertyPolicy.Find(ruleKey) is null || rule!.Source != MinecraftValueSource.Unknown);
    }

    /// <summary>A server.properties value this server no longer reads because a gamerule replaced it.</summary>
    public static bool IsReplacedByGameRule(string propertyKey, MinecraftGameplaySnapshot snapshot) =>
        MinecraftGameRuleCatalog.Find(propertyKey) is not null && ShowsRuleRow(propertyKey, snapshot);

    /// <summary>Rules left out because this version lacks them (not counting ones a setting stands in for).</summary>
    public static int MissingRuleCount(MinecraftGameplaySnapshot snapshot) =>
        snapshot.GameRules.Count(rule => !rule.Supported && MinecraftGameplayPropertyPolicy.Find(rule.Key) is null);

    public static string RuleStatus(MinecraftGameRuleState rule) =>
        rule.PendingValue switch
        {
            true => LocalizationService.Get("Gameplay.Pending.On"),
            false => LocalizationService.Get("Gameplay.Pending.Off"),
            _ => LocalizationService.Get(rule.Source switch
            {
                MinecraftValueSource.Live => "Gameplay.Source.Live",
                MinecraftValueSource.WorldFile => "Gameplay.Source.World",
                _ => "Gameplay.Source.Unknown"
            })
        };

    public static string PropertyStatus(string key) =>
        LocalizationService.Get(MinecraftGameplayPropertyPolicy.CanApplyLive(key)
            ? "Gameplay.Property.SavedLive"
            : "Gameplay.Property.Saved");

    public static string ControlMessage(MinecraftLiveControl control) =>
        LocalizationService.Get(control switch
        {
            MinecraftLiveControl.Live => "Gameplay.Control.Live",
            MinecraftLiveControl.Starting => "Gameplay.Control.Starting",
            MinecraftLiveControl.NoConsole => "Gameplay.Control.NoConsole",
            _ => "Gameplay.Control.Stopped"
        });

    public static bool PlayerActionsAvailable(MinecraftLiveControl control) => control == MinecraftLiveControl.Live;

    public static string ChoiceLabel(string value) =>
        LocalizationService.HasKey($"Gameplay.Choice.{value}")
            ? LocalizationService.Get($"Gameplay.Choice.{value}")
            : value;

    /// <summary>The values that differ from what is saved: only these are sent.</summary>
    public static IReadOnlyDictionary<string, string> Changes(
        IReadOnlyDictionary<string, string?> saved,
        IReadOnlyDictionary<string, string> edited) =>
        edited
            .Where(pair => !saved.TryGetValue(pair.Key, out var current) || !string.Equals(current, pair.Value, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public static string ActionLabel(MinecraftPlayerAction action) =>
        LocalizationService.Get($"Gameplay.Action.{action}");

    public static string ErrorText(string? errorCode, string? message, string? subject = null) =>
        errorCode is not null && LocalizationService.HasKey($"Gameplay.Error.{errorCode}")
            ? LocalizationService.Format($"Gameplay.Error.{errorCode}", subject ?? string.Empty, message ?? string.Empty)
            : message ?? errorCode ?? string.Empty;

    private static GameplayRowSpec Property(string key) => new(GameplayRowKind.Property, key);

    private static GameplayRowSpec Rule(string key) => new(GameplayRowKind.GameRule, key);
}
