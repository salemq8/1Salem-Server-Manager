using ServerManager.Contracts;
using ServerManager.Core.Minecraft;

namespace ServerManager.Core.Tests;

/// <summary>Build 11 Gameplay &amp; players: the pure rules behind gamerules, properties and player commands.</summary>
public sealed class MinecraftGameplayPolicyTests
{
    [Fact]
    public void Catalog_UsesTheRealMinecraftGameRuleNames()
    {
        var keys = MinecraftGameRuleCatalog.All.Select(rule => rule.Key).ToArray();

        Assert.Equal(
            [
                "pvp", "keepInventory", "doImmediateRespawn", "naturalRegeneration", "showDeathMessages", "announceAdvancements",
                "doDaylightCycle", "doWeatherCycle", "doFireTick", "doInsomnia",
                "fallDamage", "fireDamage", "drowningDamage", "freezeDamage",
                "doMobSpawning", "mobGriefing", "doPatrolSpawning", "doTraderSpawning",
                "doMobLoot", "doTileDrops", "doEntityDrops"
            ],
            keys);
        Assert.Equal("keep_inventory", MinecraftGameRuleCatalog.SnakeCase("keepInventory"));
        Assert.Equal("do_daylight_cycle", MinecraftGameRuleCatalog.SnakeCase("doDaylightCycle"));
    }

    [Theory]
    [InlineData("keepInventory", "minecraft:keep_inventory")]
    [InlineData("doDaylightCycle", "minecraft:advance_time")]
    [InlineData("doWeatherCycle", "minecraft:advance_weather")]
    [InlineData("doMobSpawning", "minecraft:spawn_mobs")]
    [InlineData("doInsomnia", "minecraft:spawn_phantoms")]
    [InlineData("naturalRegeneration", "minecraft:natural_health_regeneration")]
    [InlineData("announceAdvancements", "minecraft:show_advancement_messages")]
    [InlineData("doImmediateRespawn", "minecraft:immediate_respawn")]
    [InlineData("doPatrolSpawning", "minecraft:spawn_patrols")]
    [InlineData("doTraderSpawning", "minecraft:spawn_wandering_traders")]
    [InlineData("doMobLoot", "minecraft:mob_drops")]
    [InlineData("doTileDrops", "minecraft:block_drops")]
    [InlineData("doEntityDrops", "minecraft:entity_drops")]
    [InlineData("pvp", "minecraft:pvp")]
    public void RegistryNames_OfMinecraft26_AreKnown(string key, string registryName)
    {
        var rule = MinecraftGameRuleCatalog.Find(key)!;

        Assert.Contains(registryName, rule.Names);
        Assert.Equal(registryName, MinecraftGameRuleCatalog.ResolveName(rule, [registryName, "minecraft:fire_spread_radius_around_player"]));
    }

    [Theory]
    [InlineData("[12:00:00] [Server thread/INFO]: Gamerule keepInventory is currently set to: true", "keepInventory", false, true)]
    [InlineData("[12:00:00] [Server thread/INFO]: Gamerule minecraft:keep_inventory is currently set to: true", "keep_inventory", false, true)]
    [InlineData("[12:00:00] [Server thread/INFO]: Gamerule doFireTick is currently set to: true", "keepInventory", false, false)]
    [InlineData("[12:00:00] [Server thread/INFO]: gamerule doFireTick<--[HERE]", "doFireTick", false, true)]
    [InlineData("[12:00:00] [Server thread/INFO]: gamerule doFireTick<--[HERE]", "keepInventory", false, false)]
    [InlineData("[12:00:00] [Server thread/INFO]: Incorrect argument for command", "keepInventory", false, false)]
    [InlineData("[12:00:00] [Server thread/INFO]: Gamerule keepInventory is now set to: true", "keepInventory", true, true)]
    [InlineData("[12:00:00] [Server thread/INFO]: Gamerule keepInventory is currently set to: true", "keepInventory", true, false)]
    public void GameRuleAnswers_BelongToTheRuleThatWasAsked(string line, string name, bool set, bool expected) =>
        Assert.Equal(expected, MinecraftConsoleReplies.AnswersGameRule(line, name, set));

    [Fact]
    public void FireTick_IsNotMappedOntoTheNumericFireSpreadRule()
    {
        var fire = MinecraftGameRuleCatalog.Find("doFireTick")!;

        Assert.Null(MinecraftGameRuleCatalog.ResolveName(fire, ["minecraft:fire_spread_radius_around_player"]));
    }

    [Fact]
    public void ResolveName_TakesWhateverNameThisServerActuallyHas()
    {
        var keep = MinecraftGameRuleCatalog.Find("keepInventory")!;

        Assert.Equal("keepInventory", MinecraftGameRuleCatalog.ResolveName(keep, ["keepInventory", "doFireTick"]));
        Assert.Equal("keep_inventory", MinecraftGameRuleCatalog.ResolveName(keep, ["keep_inventory"]));
        Assert.Equal("minecraft:keep_inventory", MinecraftGameRuleCatalog.ResolveName(keep, ["minecraft:keep_inventory"]));
        Assert.Null(MinecraftGameRuleCatalog.ResolveName(keep, ["doFireTick"]));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Values_ParseFromAnswersAndSaves(string text, bool expected)
    {
        Assert.True(MinecraftGameRuleCatalog.TryParseValue(text, out var value));
        Assert.Equal(expected, value);
        Assert.False(MinecraftGameRuleCatalog.TryParseValue("maybe", out _));
    }

    [Theory]
    [InlineData("[12:00:00] [Server thread/INFO]: Gamerule keepInventory is currently set to: false", "keepInventory", "false")]
    [InlineData("[12:00:00 INFO]: Gamerule keep_inventory is currently set to: true", "keep_inventory", "true")]
    public void GameRuleQueryAnswers_AreRead(string line, string name, string value)
    {
        Assert.True(MinecraftConsoleReplies.TryParseGameRuleQuery(line, out var parsedName, out var parsedValue));
        Assert.Equal(name, parsedName);
        Assert.Equal(value, parsedValue);
    }

    [Fact]
    public void Replies_IgnoreChatLinesThatImitateThem()
    {
        const string chat = "[12:00:00] [Server thread/INFO]: <Bob> Gamerule keepInventory is currently set to: true";

        Assert.False(MinecraftConsoleReplies.TryParseGameRuleQuery(chat, out _, out _));
        Assert.Null(MinecraftConsoleReplies.ClassifyPlayerReply(
            MinecraftPlayerAction.Op, "Bob", "[12:00:00] [Server thread/INFO]: <Bob> Made Bob a server operator"));
    }

    [Fact]
    public void SetAnswers_AndErrors_AreTold()
    {
        Assert.True(MinecraftConsoleReplies.TryParseGameRuleSet(
            "[12:00:00] [Server thread/INFO]: Gamerule keepInventory is now set to: true", out var name, out var value));
        Assert.Equal(("keepInventory", "true"), (name, value));
        Assert.True(MinecraftConsoleReplies.IsCommandError("[12:00:00] [Server thread/INFO]: Incorrect argument for command"));
        Assert.True(MinecraftConsoleReplies.IsCommandError("[12:00:00] [Server thread/INFO]: ...amerule notARule<--[HERE]"));
        Assert.True(MinecraftConsoleReplies.IsReady("[12:00:00] [Server thread/INFO]: Done (4.201s)! For help, type \"help\""));
    }

    [Theory]
    [InlineData("[12:00:00] [Server thread/INFO]: There are 2 of a max of 20 players online: Alex, Steve", 2, 20, "Alex,Steve")]
    [InlineData("[12:00:00 INFO]: There are 0 out of maximum 10 players online.", 0, 10, "")]
    [InlineData("[12:00:00] [Server thread/INFO]: There are 0 of a max of 20 players online: ", 0, 20, "")]
    public void ListAnswers_AreRead(string line, int online, int max, string names)
    {
        Assert.True(MinecraftConsoleReplies.TryParseList(line, out var parsedOnline, out var parsedMax, out var parsedNames));
        Assert.Equal(online, parsedOnline);
        Assert.Equal(max, parsedMax);
        Assert.Equal(names, string.Join(',', parsedNames));
    }

    [Theory]
    [InlineData(MinecraftPlayerAction.Op, "Made Steve a server operator", MinecraftPlayerReply.Applied)]
    [InlineData(MinecraftPlayerAction.Deop, "Made Steve no longer a server operator", MinecraftPlayerReply.Applied)]
    [InlineData(MinecraftPlayerAction.WhitelistAdd, "Added Steve to the whitelist", MinecraftPlayerReply.Applied)]
    [InlineData(MinecraftPlayerAction.WhitelistRemove, "Removed Steve from the whitelist", MinecraftPlayerReply.Applied)]
    [InlineData(MinecraftPlayerAction.Kick, "Kicked Steve: Kicked by an operator", MinecraftPlayerReply.Applied)]
    [InlineData(MinecraftPlayerAction.Ban, "Banned Steve: Banned by an operator", MinecraftPlayerReply.Applied)]
    [InlineData(MinecraftPlayerAction.Pardon, "Unbanned Steve", MinecraftPlayerReply.Applied)]
    [InlineData(MinecraftPlayerAction.Op, "Nothing changed. The player already is an operator", MinecraftPlayerReply.NoChange)]
    [InlineData(MinecraftPlayerAction.WhitelistAdd, "Player is already whitelisted", MinecraftPlayerReply.NoChange)]
    [InlineData(MinecraftPlayerAction.Kick, "No player was found", MinecraftPlayerReply.UnknownPlayer)]
    [InlineData(MinecraftPlayerAction.Op, "That player does not exist", MinecraftPlayerReply.UnknownPlayer)]
    public void PlayerAnswers_AreClassified(MinecraftPlayerAction action, string message, MinecraftPlayerReply expected) =>
        Assert.Equal(expected, MinecraftConsoleReplies.ClassifyPlayerReply(action, "Steve", $"[12:00:00] [Server thread/INFO]: {message}"));

    [Theory]
    [InlineData(MinecraftPlayerAction.Op, "op Steve_01")]
    [InlineData(MinecraftPlayerAction.Deop, "deop Steve_01")]
    [InlineData(MinecraftPlayerAction.WhitelistAdd, "whitelist add Steve_01")]
    [InlineData(MinecraftPlayerAction.WhitelistRemove, "whitelist remove Steve_01")]
    [InlineData(MinecraftPlayerAction.Kick, "kick Steve_01")]
    [InlineData(MinecraftPlayerAction.Ban, "ban Steve_01")]
    [InlineData(MinecraftPlayerAction.Pardon, "pardon Steve_01")]
    public void PlayerCommands_AreFormedFromValidatedNamesOnly(MinecraftPlayerAction action, string expected) =>
        Assert.Equal(expected, MinecraftPlayerCommandPolicy.BuildCommand(action, "Steve_01"));

    [Theory]
    [InlineData("St")]
    [InlineData("ThisNameIsTooLong17")]
    [InlineData("Steve stop")]
    [InlineData("Steve\nstop")]
    [InlineData("Steve;op")]
    [InlineData("")]
    public void UnsafeOrInvalidNames_NeverBecomeCommands(string name)
    {
        Assert.False(MinecraftPlayerCommandPolicy.IsValidName(name));
        Assert.Throws<ArgumentException>(() => MinecraftPlayerCommandPolicy.BuildCommand(MinecraftPlayerAction.Ban, name));
    }

    [Fact]
    public void TakingActions_AreConfirmed()
    {
        Assert.True(MinecraftPlayerCommandPolicy.NeedsConfirmation(MinecraftPlayerAction.Ban));
        Assert.True(MinecraftPlayerCommandPolicy.NeedsConfirmation(MinecraftPlayerAction.Kick));
        Assert.True(MinecraftPlayerCommandPolicy.NeedsConfirmation(MinecraftPlayerAction.Deop));
        Assert.True(MinecraftPlayerCommandPolicy.NeedsConfirmation(MinecraftPlayerAction.WhitelistRemove));
        Assert.False(MinecraftPlayerCommandPolicy.NeedsConfirmation(MinecraftPlayerAction.Op));
        Assert.False(MinecraftPlayerCommandPolicy.NeedsConfirmation(MinecraftPlayerAction.Pardon));
    }

    [Theory]
    [InlineData("difficulty", " Hard ", "hard")]
    [InlineData("pvp", "False", "false")]
    [InlineData("view-distance", "12", "12")]
    [InlineData("op-permission-level", "2", "2")]
    public void Properties_AreValidatedAndNormalized(string key, string value, string expected)
    {
        Assert.True(MinecraftGameplayPropertyPolicy.TryNormalize(key, value, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("difficulty", "impossible")]
    [InlineData("view-distance", "64")]
    [InlineData("op-permission-level", "5")]
    [InlineData("pvp", "yes")]
    [InlineData("level-seed", "1")]
    [InlineData("motd", "hello")]
    public void BadPropertyValuesOrUnmanagedKeys_AreRefused(string key, string value) =>
        Assert.False(MinecraftGameplayPropertyPolicy.TryNormalize(key, value, out _, out _));

    [Fact]
    public void OnlyDifficultyAndWhitelist_HaveRealLiveCommands()
    {
        Assert.Equal("difficulty hard", MinecraftGameplayPropertyPolicy.LiveCommand("difficulty", "hard"));
        Assert.Equal("whitelist on", MinecraftGameplayPropertyPolicy.LiveCommand("white-list", "true"));
        Assert.Null(MinecraftGameplayPropertyPolicy.LiveCommand("pvp", "false"));
        Assert.Null(MinecraftGameplayPropertyPolicy.LiveCommand("max-players", "30"));
    }

    [Fact]
    public void FallDamage_OffersOnlyWhatTheServerReallyHas()
    {
        var supported = MinecraftFallDamagePolicy.For(fallDamageRuleSupported: true);
        var missing = MinecraftFallDamagePolicy.For(fallDamageRuleSupported: false);

        Assert.Equal([100, 0], supported.Percentages);
        Assert.DoesNotContain(75, supported.Percentages);
        Assert.DoesNotContain(50, supported.Percentages);
        Assert.DoesNotContain(25, supported.Percentages);
        Assert.False(missing.Supported);
        Assert.Empty(missing.Percentages);
        Assert.True(MinecraftFallDamagePolicy.GameRuleValue(100));
        Assert.False(MinecraftFallDamagePolicy.GameRuleValue(0));
        Assert.Null(MinecraftFallDamagePolicy.GameRuleValue(50));
    }
}
