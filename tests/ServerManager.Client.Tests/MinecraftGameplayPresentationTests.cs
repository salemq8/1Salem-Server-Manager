using System.Globalization;
using System.Text.RegularExpressions;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core.Minecraft;

namespace ServerManager.Client.Tests;

/// <summary>
/// The Minecraft Gameplay page (Build 11): every offered rule and setting is a real one, fall
/// damage offers only what the server has, player actions need a live server, and every word
/// on the page exists in English and Arabic.
/// </summary>
public sealed class MinecraftGameplayPresentationTests
{
    [Fact]
    public void Sections_ShowEveryCatalogRuleAndSettingExactlyOnce()
    {
        var rows = MinecraftGameplayPresentation.Sections.SelectMany(section => section.Rows).ToArray();

        var rules = rows.Where(row => row.Kind is GameplayRowKind.GameRule or GameplayRowKind.FallDamage).Select(row => row.Key).ToArray();
        var properties = rows.Where(row => row.Kind == GameplayRowKind.Property).Select(row => row.Key).ToArray();

        Assert.Equal(MinecraftGameRuleCatalog.All.Select(rule => rule.Key).Order(), rules.Order());
        Assert.Equal(MinecraftGameplayPropertyPolicy.All.Select(property => property.Key).Order(), properties.Order());
        Assert.Equal(["General", "Players", "World", "Damage", "Mobs", "Advanced"], MinecraftGameplayPresentation.Sections.Select(section => section.Key));
    }

    [Fact]
    public void KeepInventory_IsTheRealGameruleOnThePlayersCard()
    {
        var players = Assert.Single(MinecraftGameplayPresentation.Sections, section => section.Key == "Players");

        Assert.Contains(new GameplayRowSpec(GameplayRowKind.GameRule, "keepInventory"), players.Rows);
        Assert.NotNull(MinecraftGameRuleCatalog.Find("keepInventory"));
    }

    [Fact]
    public void Advanced_HoldsOperatorLevelOnlineModeAndCommandBlocks()
    {
        var advanced = Assert.Single(MinecraftGameplayPresentation.Sections, section => section.Advanced);

        Assert.Equal(["enable-command-block", "online-mode", "op-permission-level"], advanced.Rows.Select(row => row.Key));
    }

    [Fact]
    public void FallDamage_OffersOnlyTheServersRealChoices()
    {
        var supported = MinecraftGameplayPresentation.FallDamageChoices(new MinecraftFallDamageCapability(true, [100, 0]));
        var forged = MinecraftGameplayPresentation.FallDamageChoices(new MinecraftFallDamageCapability(true, [100, 75, 50, 25, 0]));
        var missing = MinecraftGameplayPresentation.FallDamageChoices(new MinecraftFallDamageCapability(false, []));

        Assert.Equal([100, 0], supported);
        Assert.Equal([100, 0], forged);
        Assert.Empty(missing);
        Assert.Empty(MinecraftGameplayPresentation.FallDamageChoices(null));
    }

    [Fact]
    public void FallDamage_SelectionFollowsTheGamerule()
    {
        Assert.Equal(100, MinecraftGameplayPresentation.FallDamageSelection(Rule("fallDamage", true)));
        Assert.Equal(0, MinecraftGameplayPresentation.FallDamageSelection(Rule("fallDamage", false)));
        Assert.Equal(0, MinecraftGameplayPresentation.FallDamageSelection(Rule("fallDamage", true) with { PendingValue = false }));
        Assert.Null(MinecraftGameplayPresentation.FallDamageSelection(Rule("fallDamage", null)));
    }

    [Fact]
    public void UnknownRule_IsNeverShownOrSentAsOff()
    {
        var unknown = Rule("keepInventory", null) with { Source = MinecraftValueSource.Unknown };

        // WPF moves an unknown (null) switch to Off on a click; the click asks for On instead.
        Assert.Null(MinecraftGameplayPresentation.EffectiveValue(unknown));
        Assert.True(MinecraftGameplayPresentation.RequestedValue(null, false));
        Assert.True(MinecraftGameplayPresentation.RequestedValue(false, true));
        Assert.False(MinecraftGameplayPresentation.RequestedValue(true, false));
        WithCulture("en-US", () =>
            Assert.Equal("Not known until the world exists", MinecraftGameplayPresentation.RuleStatus(unknown)));
    }

    [Fact]
    public void UnsupportedRules_AreNotShown()
    {
        Assert.True(MinecraftGameplayPresentation.IsShown(Rule("keepInventory", false)));
        Assert.False(MinecraftGameplayPresentation.IsShown(Rule("freezeDamage", null) with { Supported = false }));
        Assert.False(MinecraftGameplayPresentation.IsShown(null));
    }

    [Fact]
    public void PvP_IsTheGameruleWhereTheServerHasItAndTheSettingElsewhere()
    {
        var modern = Snapshot(Rule("pvp", true) with { Source = MinecraftValueSource.WorldFile });
        var classic = Snapshot(Rule("pvp", null) with { Supported = false, Source = MinecraftValueSource.WorldFile });
        var noWorld = Snapshot(Rule("pvp", null) with { Source = MinecraftValueSource.Unknown });

        Assert.True(MinecraftGameplayPresentation.ShowsRuleRow("pvp", modern));
        Assert.True(MinecraftGameplayPresentation.IsReplacedByGameRule("pvp", modern));
        Assert.False(MinecraftGameplayPresentation.ShowsRuleRow("pvp", classic));
        Assert.False(MinecraftGameplayPresentation.IsReplacedByGameRule("pvp", classic));
        Assert.False(MinecraftGameplayPresentation.ShowsRuleRow("pvp", noWorld));
        Assert.False(MinecraftGameplayPresentation.IsReplacedByGameRule("pvp", noWorld));
        Assert.Equal(0, MinecraftGameplayPresentation.MissingRuleCount(classic));
    }

    [Fact]
    public void RulesWithoutASettingCounterpart_AreNeverReplacedOrHiddenByIt()
    {
        var snapshot = Snapshot(Rule("keepInventory", null) with { Source = MinecraftValueSource.Unknown });

        Assert.True(MinecraftGameplayPresentation.ShowsRuleRow("keepInventory", snapshot));
        Assert.False(MinecraftGameplayPresentation.IsReplacedByGameRule("difficulty", snapshot));
    }

    [Theory]
    [InlineData(MinecraftLiveControl.Live, true)]
    [InlineData(MinecraftLiveControl.Stopped, false)]
    [InlineData(MinecraftLiveControl.Starting, false)]
    [InlineData(MinecraftLiveControl.NoConsole, false)]
    public void PlayerActions_NeedALiveServer(MinecraftLiveControl control, bool available) =>
        Assert.Equal(available, MinecraftGameplayPresentation.PlayerActionsAvailable(control));

    [Fact]
    public void DestructivePlayerActions_AreConfirmed()
    {
        var confirmed = Enum.GetValues<MinecraftPlayerAction>().Where(MinecraftPlayerCommandPolicy.NeedsConfirmation);

        Assert.Equal(
            [MinecraftPlayerAction.Deop, MinecraftPlayerAction.WhitelistRemove, MinecraftPlayerAction.Kick, MinecraftPlayerAction.Ban],
            confirmed);
        var window = ReadSource("src", "ServerManager.Client", "MinecraftGameplayWindow.xaml.cs");
        Assert.Contains("MinecraftPlayerCommandPolicy.NeedsConfirmation(action)", window, StringComparison.Ordinal);
        Assert.Contains("ConfirmationDialog.Confirm(", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Changes_SendOnlyWhatDiffersFromTheSavedFile()
    {
        var saved = new Dictionary<string, string?> { ["pvp"] = "true", ["difficulty"] = "easy", ["max-players"] = null };
        var edited = new Dictionary<string, string> { ["pvp"] = "true", ["difficulty"] = "hard", ["max-players"] = "10" };

        var changes = MinecraftGameplayPresentation.Changes(saved, edited);

        Assert.Equal(2, changes.Count);
        Assert.Equal("hard", changes["difficulty"]);
        Assert.Equal("10", changes["max-players"]);
    }

    [Fact]
    public void RuleStatus_SaysWhereTheValueComesFrom()
    {
        WithCulture("en-US", () =>
        {
            Assert.Equal("Live value from the running server", MinecraftGameplayPresentation.RuleStatus(Rule("keepInventory", true)));
            Assert.Equal("Saved in the world", MinecraftGameplayPresentation.RuleStatus(Rule("keepInventory", true) with { Source = MinecraftValueSource.WorldFile }));
            Assert.Equal(
                "Turns on the next time 1Salem starts the server",
                MinecraftGameplayPresentation.RuleStatus(Rule("keepInventory", false) with { Source = MinecraftValueSource.WorldFile, PendingValue = true }));
        });
    }

    [Fact]
    public void EveryGameplayKey_HasEnglishAndArabic()
    {
        var keys = new List<string>
        {
            "ServerSettings.GameBodyMinecraft", "Gameplay.Open", "Gameplay.Title", "Gameplay.FallDamage", "Gameplay.FallDamage.Help",
            "Gameplay.FallDamage.100", "Gameplay.FallDamage.0", "Gameplay.FallDamage.Unavailable"
        };
        foreach (var section in MinecraftGameplayPresentation.Sections)
        {
            keys.Add(section.TitleKey);
            foreach (var row in section.Rows)
            {
                keys.Add(row.LabelKey);
                keys.Add(row.HelpKey);
            }
        }

        keys.AddRange(Enum.GetValues<MinecraftPlayerAction>().Select(action => $"Gameplay.Action.{action}"));
        keys.AddRange(Enum.GetValues<MinecraftPlayerAction>()
            .Where(MinecraftPlayerCommandPolicy.NeedsConfirmation)
            .Select(action => $"Gameplay.Confirm.{action}"));
        keys.AddRange(MinecraftGameplayPropertyPolicy.All
            .Where(property => property.Choices is not null)
            .SelectMany(property => property.Choices!)
            .Select(choice => $"Gameplay.Choice.{choice}"));

        // And every key the window and presentation name literally.
        var sources = ReadSource("src", "ServerManager.Client", "MinecraftGameplayWindow.xaml.cs") +
                      ReadSource("src", "ServerManager.Client", "Controls", "MinecraftGameplayPresentation.cs");
        keys.AddRange(Regex.Matches(sources, "\"(Gameplay\\.[A-Za-z.]+)\"").Select(match => match.Groups[1].Value)
            .Where(key => !key.EndsWith('.')));

        foreach (var key in keys.Distinct())
        {
            string english = string.Empty;
            string arabic = string.Empty;
            WithCulture("en-US", () => english = LocalizationService.HasKey(key) ? LocalizationService.Get(key) : string.Empty);
            WithCulture("ar-SA", () => arabic = LocalizationService.HasKey(key) ? LocalizationService.Get(key) : string.Empty);

            Assert.False(string.IsNullOrEmpty(english), $"English is missing {key}.");
            Assert.False(string.IsNullOrEmpty(arabic), $"Arabic is missing {key}.");
        }
    }

    [Fact]
    public void GameplayText_NeverPromisesScaledFallDamage()
    {
        WithCulture("en-US", () =>
        {
            foreach (var key in new[] { "Gameplay.FallDamage.Help", "Gameplay.FallDamage.100", "Gameplay.FallDamage.0" })
            {
                var text = LocalizationService.Get(key);
                Assert.DoesNotContain("75", text, StringComparison.Ordinal);
                Assert.DoesNotContain("50", text, StringComparison.Ordinal);
                Assert.DoesNotContain("25", text, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void SettingsTab_OpensTheGameplayPageForMinecraftOnly()
    {
        var code = ReadSource("src", "ServerManager.Client", "Controls", "ServerSettingsTab.xaml.cs");

        Assert.Contains("\"minecraft-gameplay\"", code, StringComparison.Ordinal);
        Assert.Contains("MinecraftGameplayWindow.Open(", code, StringComparison.Ordinal);
        Assert.Contains("_context.Source?.Game == GameType.Minecraft", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerTab.Players", code, StringComparison.Ordinal);
    }

    private static MinecraftGameRuleState Rule(string key, bool? value) =>
        new(key, key, true, value, MinecraftValueSource.Live, null);

    private static MinecraftGameplaySnapshot Snapshot(params MinecraftGameRuleState[] rules) =>
        new(Guid.NewGuid(), "26.3", ServerPlatform.Vanilla, MinecraftLiveControl.Stopped, true, rules, [],
            new MinecraftFallDamageCapability(false, []), DateTimeOffset.UtcNow);

    private static void WithCulture(string name, Action action)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(name);
            action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static string ReadSource(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BUILD_REVISION")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
