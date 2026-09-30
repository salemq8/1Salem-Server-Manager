using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ServerManager.Client;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Connect.UiReview;

/// <summary>
/// Drives the real Minecraft Gameplay window against the fake Agent: Keep Inventory is the real
/// gamerule and applies live, fall damage offers only 100% and 0%, a rule the version lacks is not
/// offered, settings are staged and saved, player actions need a live server, and the page works
/// right to left and in both themes.
/// </summary>
internal static class GameplayScenarios
{
    private static readonly Guid ServerId = Guid.Parse("7e57c0de-0000-4000-8000-000000000003");

    public static async Task RunAsync(FakeAgent agent, Report report)
    {
        var live = true;
        var keepInventory = false;
        MinecraftChangeResult SetKeepInventory()
        {
            keepInventory = true;
            return new MinecraftChangeResult(MinecraftChangeOutcome.AppliedLive, VerifiedValue: true);
        }

        agent.Extra = (method, path) =>
        {
            var prefix = $"/api/v1/servers/{ServerId}/minecraft/";
            (int Status, object? Payload)? result = (method, path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : null) switch
            {
                ("GET", "gameplay") => (200, Snapshot(live, keepInventory)),
                ("GET", "players/live") => (200, Players(live)),
                ("POST", "gamerules") => (200, SetKeepInventory()),
                ("POST", "gameplay/properties") => (200, new MinecraftPropertiesChangeResult(true, null, null, true, ["difficulty"])),
                ("POST", "players/actions") => (200, new MinecraftChangeResult(MinecraftChangeOutcome.AppliedLive)),
                _ => null
            };
            return Task.FromResult(result);
        };

        var window = Create("Review Vanilla");
        Report.Show(window);
        await Report.SettleAsync(1500);

        var rtl = System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
        report.Check(window.FlowDirection == (rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight), $"window flows {window.FlowDirection}");

        var keep = Find<CheckBox>(window, "Rule.keepInventory");
        report.Check(keep is { IsChecked: false }, "Keep Inventory is shown with the server's live value (off)");
        report.Check(!string.IsNullOrWhiteSpace(keep is null ? null : AutomationProperties.GetName(keep)), "Keep Inventory switch has an accessible name");

        var fall = Find<ComboBox>(window, "FallDamage");
        var fallLabels = fall?.Items.OfType<ComboBoxItem>().Select(item => item.Content as string ?? string.Empty).ToArray() ?? [];
        report.Check(fallLabels.Length == 2 && fallLabels.All(label => !label.Contains("75") && !label.Contains("50") && !label.Contains("25")),
            $"fall damage offers only its real choices: {string.Join(" | ", fallLabels)}");
        report.Check(Find<CheckBox>(window, "Rule.freezeDamage") is null, "a rule this version lacks (freezeDamage) is not offered");
        report.Check(Find<CheckBox>(window, "Rule.fallDamage") is null, "fall damage is one control, not a second switch");
        report.Check(Find<Expander>(window, "GameplayAdvanced") is not null, "Advanced settings sit behind a disclosure");
        var sections = new[] { "General", "Players", "World", "Damage", "Mobs", "Advanced" }
            .Count(key => Find<FrameworkElement>(window, "GameplaySection." + key) is not null);
        report.Check(sections == 6, $"grouped cards shown ({sections} of 6)");
        await report.CaptureAsync("gameplay-01-live-dark", window);

        // Keep Inventory: switching it posts the real gamerule and shows the server's confirmation.
        if (keep is not null)
        {
            keep.IsChecked = true;
            keep.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Report.SettleAsync();
            var posted = agent.Bodies.Any(body => body.Path.EndsWith("/minecraft/gamerules", StringComparison.Ordinal) &&
                                                  body.Text.Contains("\"key\":\"keepInventory\"", StringComparison.Ordinal) &&
                                                  body.Text.Contains("\"value\":true", StringComparison.Ordinal));
            var status = ((TextBlock)window.FindName("StatusText")).Text;
            report.Check(posted && keep.IsChecked == true && keepInventory, $"Keep Inventory applied live: {status}");
        }

        // Settings are staged: nothing is sent until Save.
        var before = agent.Bodies.Count(body => body.Path.EndsWith("/gameplay/properties", StringComparison.Ordinal));
        var difficulty = Find<ComboBox>(window, "Property.difficulty");
        var save = (Button)window.FindName("SaveButton");
        if (difficulty is not null)
        {
            difficulty.SelectedItem = difficulty.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == "hard");
            await Report.SettleAsync(300);
            var staged = save.IsEnabled && agent.Bodies.Count(body => body.Path.EndsWith("/gameplay/properties", StringComparison.Ordinal)) == before;
            save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Report.SettleAsync();
            var sent = agent.Bodies.Any(body => body.Path.EndsWith("/gameplay/properties", StringComparison.Ordinal) &&
                                                body.Text.Contains("\"difficulty\":\"hard\"", StringComparison.Ordinal));
            report.Check(staged && sent, $"settings staged then saved: {((TextBlock)window.FindName("SaveStatus")).Text}");
        }

        // Players: live list, and actions only for a valid name.
        ((RadioButton)window.FindName("TabPlayers")).IsChecked = true;
        await Report.SettleAsync();
        var online = (ListBox)window.FindName("OnlineList");
        var name = (TextBox)window.FindName("NameBox");
        var op = Find<Button>(window, "PlayerAction.Op");
        name.Text = "no way";
        await Report.SettleAsync(200);
        var blockedInvalid = op is { IsEnabled: false };
        name.Text = "Steve";
        await Report.SettleAsync(200);
        report.Check(online.Items.Count == 2 && blockedInvalid && op is { IsEnabled: true }, "players listed live; actions need a valid name");
        op?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Report.SettleAsync();
        report.Check(agent.Bodies.Any(body => body.Path.EndsWith("/players/actions", StringComparison.Ordinal) &&
                                              body.Text.Contains("\"player\":\"Steve\"", StringComparison.Ordinal)),
            $"Make operator sent for Steve: {((TextBlock)window.FindName("PlayersHint")).Text}");
        await report.CaptureAsync("gameplay-02-players-dark", window);

        ThemeService.Apply(AppTheme.Light);
        ((RadioButton)window.FindName("TabGameplay")).IsChecked = true;
        await report.CaptureAsync("gameplay-03-live-light", window, keyboard: false);
        window.Close();

        // Stopped: values from the world file, the change waits for the next start, no player actions.
        live = false;
        var stopped = Create("Review Vanilla");
        Report.Show(stopped);
        await Report.SettleAsync(1500);
        var stoppedKeep = Find<CheckBox>(stopped, "Rule.keepInventory");
        report.Check(stoppedKeep is not null, "Keep Inventory shown while stopped (from the world)");
        ((RadioButton)stopped.FindName("TabPlayers")).IsChecked = true;
        ((TextBox)stopped.FindName("NameBox")).Text = "Steve";
        await Report.SettleAsync(300);
        var anyEnabled = ((WrapPanel)stopped.FindName("ActionButtons")).Children.OfType<Button>().Any(button => button.IsEnabled);
        report.Check(!anyEnabled, $"player actions off while stopped: {((TextBlock)stopped.FindName("PlayersHint")).Text}");
        ((RadioButton)stopped.FindName("TabGameplay")).IsChecked = true;
        await report.CaptureAsync("gameplay-04-stopped-light", stopped, keyboard: false);
        stopped.Close();
        ThemeService.Apply(AppTheme.Dark);
    }

    private static Window Create(string serverName) =>
        (Window)Activator.CreateInstance(
            typeof(MinecraftGameplayWindow),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [ServerId, serverName],
            null)!;

    private static T? Find<T>(DependencyObject root, string automationId)
        where T : DependencyObject =>
        Report.Descendants(root).OfType<T>().FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == automationId) ??
        Logical(root).OfType<T>().FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == automationId);

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Logical(child)) yield return nested;
        }
    }

    private static MinecraftGameplaySnapshot Snapshot(bool live, bool keepInventory)
    {
        var source = live ? MinecraftValueSource.Live : MinecraftValueSource.WorldFile;
        var rules = ServerManager.Core.Minecraft.MinecraftGameRuleCatalog.All
            .Select(rule => rule.Key == "freezeDamage"
                ? new MinecraftGameRuleState(rule.Key, null, false, null, source, null)
                : new MinecraftGameRuleState(rule.Key, rule.Key, true, rule.Key == "keepInventory" ? keepInventory : true, source,
                    !live && rule.Key == "keepInventory" ? !keepInventory : null))
            .ToArray();
        var properties = new Dictionary<string, string>
        {
            ["difficulty"] = "easy", ["gamemode"] = "survival", ["force-gamemode"] = "false", ["hardcore"] = "false",
            ["pvp"] = "true", ["allow-flight"] = "false", ["max-players"] = "20", ["white-list"] = "false",
            ["spawn-protection"] = "16", ["view-distance"] = "10", ["simulation-distance"] = "10",
            ["enable-command-block"] = "false", ["online-mode"] = "true", ["op-permission-level"] = "4"
        };
        return new MinecraftGameplaySnapshot(
            ServerId,
            "1.21.8",
            ServerPlatform.Vanilla,
            live ? MinecraftLiveControl.Live : MinecraftLiveControl.Stopped,
            true,
            rules,
            properties.Select(pair => new MinecraftPropertyState(pair.Key, pair.Value, pair.Key is "difficulty" or "white-list")).ToArray(),
            new MinecraftFallDamageCapability(true, [100, 0]),
            DateTimeOffset.UtcNow);
    }

    private static MinecraftPlayersState Players(bool live) =>
        new(
            live ? MinecraftLiveControl.Live : MinecraftLiveControl.Stopped,
            live,
            live ? ["Alex", "Steve"] : [],
            20,
            ["Alex"],
            [],
            ["Griefer"],
            DateTimeOffset.UtcNow);
}
