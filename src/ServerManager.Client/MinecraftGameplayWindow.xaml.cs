using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Input;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core.Minecraft;
using AutomationProperties = System.Windows.Automation.AutomationProperties;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Expander = System.Windows.Controls.Expander;
using Grid = System.Windows.Controls.Grid;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;

namespace ServerManager.Client;

/// <summary>
/// Gamerules, gameplay settings and players for one Minecraft server. Gamerules apply the moment
/// they are switched (live and read back, or kept for the next start); server.properties values
/// are staged and saved together. Nothing here restarts the server.
/// </summary>
public partial class MinecraftGameplayWindow : Window
{
    private readonly Guid _serverId;
    private readonly string _serverName;
    private readonly HttpClient _http = ServerDetailContext.Shared.CreateClient(TimeSpan.FromMinutes(2));
    private readonly Dictionary<string, string?> _saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _edited = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _invalid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CheckBox Toggle, TextBlock Status)> _ruleRows = new(StringComparer.Ordinal);
    private MinecraftGameplaySnapshot? _snapshot;
    private MinecraftPlayersState? _players;
    private ComboBox? _fallDamageBox;
    private TextBlock? _fallDamageStatus;
    private bool _rendering;
    private bool _busy;
    private bool _showingEditState;

    private MinecraftGameplayWindow(Guid serverId, string serverName)
    {
        _serverId = serverId;
        _serverName = serverName;
        InitializeComponent();

        // Arabic lays this window out right to left, the same as the rest of the app.
        FlowDirection = LayoutDirectionService.ForCulture(CultureInfo.CurrentUICulture);
        ApplyText();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _http.Dispose();
    }

    public static void Open(Window? owner, Guid serverId, string serverName)
    {
        var window = new MinecraftGameplayWindow(serverId, serverName) { Owner = owner };
        window.ShowDialog();
    }

    private void ApplyText()
    {
        Title = $"{LocalizationService.Get("Gameplay.Title")} — {_serverName}";
        TitleText.Text = LocalizationService.Get("Gameplay.Title");
        SubtitleText.Text = _serverName;
        RefreshButton.Content = LocalizationService.Get("Gameplay.Refresh");
        TabGameplay.Content = LocalizationService.Get("Gameplay.Tab.Gameplay");
        TabPlayers.Content = LocalizationService.Get("Gameplay.Tab.Players");
        SaveButton.Content = LocalizationService.Get("Gameplay.Save");
        CloseButton.Content = LocalizationService.Get("Action.Close");
        StatusText.Text = LocalizationService.Get("Gameplay.Loading");
        OnlineHeading.Text = LocalizationService.Get("Gameplay.Players.Online");
        ManageHeading.Text = LocalizationService.Get("Gameplay.Players.Manage");
        NameLabel.Content = LocalizationService.Get("Gameplay.Players.NameLabel");
        NameHelp.Text = LocalizationService.Get("Gameplay.Players.NameHelp");
        OperatorsHeading.Text = LocalizationService.Get("Gameplay.Players.Operators");
        WhitelistHeading.Text = LocalizationService.Get("Gameplay.Players.Whitelist");
        BannedHeading.Text = LocalizationService.Get("Gameplay.Players.Banned");
        AutomationProperties.SetName(NameBox, LocalizationService.Get("Gameplay.Players.NameLabel"));
        AutomationProperties.SetName(OnlineList, LocalizationService.Get("Gameplay.Players.Online"));
        AutomationProperties.SetName(OperatorsList, LocalizationService.Get("Gameplay.Players.Operators"));
        AutomationProperties.SetName(WhitelistList, LocalizationService.Get("Gameplay.Players.Whitelist"));
        AutomationProperties.SetName(BannedList, LocalizationService.Get("Gameplay.Players.Banned"));

        ActionButtons.Children.Clear();
        foreach (var action in Enum.GetValues<MinecraftPlayerAction>())
        {
            var button = new Button
            {
                Content = MinecraftGameplayPresentation.ActionLabel(action),
                Tag = action,
                Margin = new Thickness(0, 0, 8, 8),
                MinWidth = 110,
                IsEnabled = false,
                Style = (Style)FindResource(MinecraftPlayerCommandPolicy.NeedsConfirmation(action) &&
                                            action is MinecraftPlayerAction.Ban or MinecraftPlayerAction.Kick
                    ? "DangerButtonStyle"
                    : "SecondaryButtonStyle")
            };
            AutomationProperties.SetAutomationId(button, $"PlayerAction.{action}");
            button.Click += PlayerAction_Click;
            ActionButtons.Children.Add(button);
        }
    }

    private async Task LoadAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        StatusText.Text = LocalizationService.Get("Gameplay.Loading");
        try
        {
            _snapshot = await _http.GetFromJsonAsync<MinecraftGameplaySnapshot>(
                $"/api/v1/servers/{_serverId}/minecraft/gameplay");
            _players = await _http.GetFromJsonAsync<MinecraftPlayersState>(
                $"/api/v1/servers/{_serverId}/minecraft/players/live");
            RenderGameplay();
            RenderPlayers();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              System.Text.Json.JsonException or NotSupportedException)
        {
            StatusText.Text = LocalizationService.Format("Gameplay.LoadFailed", exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        UpdateSaveState();
        UpdatePlayerActions();
    }

    // ---- Gameplay -----------------------------------------------------------------------

    private void RenderGameplay()
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        _rendering = true;
        try
        {
            var platform = snapshot.Platform.ToString();
            SubtitleText.Text = string.IsNullOrWhiteSpace(snapshot.MinecraftVersion)
                ? LocalizationService.Format("Gameplay.SubtitleNoVersion", _serverName, platform)
                : LocalizationService.Format("Gameplay.Subtitle", _serverName, snapshot.MinecraftVersion, platform);
            var status = MinecraftGameplayPresentation.ControlMessage(snapshot.Control);
            if (!snapshot.GameRulesKnown)
            {
                status += " " + LocalizationService.Get("Gameplay.NoWorld");
            }

            StatusText.Text = status;

            _saved.Clear();
            foreach (var property in snapshot.Properties)
            {
                _saved[property.Key] = property.Value;
            }

            _edited.Clear();
            _invalid.Clear();
            _ruleRows.Clear();
            _fallDamageBox = null;
            _fallDamageStatus = null;
            GameplayPanel.Children.Clear();

            var hidden = snapshot.GameRules.Count(rule => !rule.Supported);
            foreach (var section in MinecraftGameplayPresentation.Sections)
            {
                var rows = new StackPanel();
                foreach (var row in section.Rows)
                {
                    var element = BuildRow(snapshot, row);
                    if (element is not null)
                    {
                        rows.Children.Add(element);
                    }
                }

                if (rows.Children.Count == 0)
                {
                    continue;
                }

                GameplayPanel.Children.Add(BuildSection(section, rows));
            }

            if (hidden > 0)
            {
                GameplayPanel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Format("Gameplay.Unsupported", hidden),
                    Style = (Style)FindResource("CaptionTextStyle"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 10, 0, 0)
                });
            }

            var level = _saved.TryGetValue("op-permission-level", out var opLevel) && !string.IsNullOrWhiteSpace(opLevel) ? opLevel : "4";
            OpLevelNote.Text = LocalizationService.Format("Gameplay.Players.OpLevel", level);
        }
        finally
        {
            _rendering = false;
        }

        UpdateSaveState();
    }

    private Border BuildSection(GameplaySectionSpec section, StackPanel rows)
    {
        var card = new Border
        {
            Style = (Style)FindResource("SectionCardStyle"),
            Padding = new Thickness(16, 12, 16, 4),
            Margin = new Thickness(0, 0, 0, 12)
        };
        AutomationProperties.SetAutomationId(card, $"GameplaySection.{section.Key}");
        var title = LocalizationService.Get(section.TitleKey);
        if (section.Advanced)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("Gameplay.Section.AdvancedBody"),
                Style = (Style)FindResource("CaptionTextStyle"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            body.Children.Add(rows);
            var expander = new Expander
            {
                Header = title,
                Content = body,
                Style = (Style)FindResource("AdvancedDisclosureStyle"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            AutomationProperties.SetAutomationId(expander, "GameplayAdvanced");
            AutomationProperties.SetName(expander, title);
            card.Child = expander;
            return card;
        }

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionTitleStyle") });
        panel.Children.Add(rows);
        card.Child = panel;
        return card;
    }

    private Grid? BuildRow(MinecraftGameplaySnapshot snapshot, GameplayRowSpec row)
    {
        var label = LocalizationService.Get(row.LabelKey);
        System.Windows.FrameworkElement control;
        TextBlock status;
        switch (row.Kind)
        {
            case GameplayRowKind.GameRule:
                {
                    var rule = snapshot.GameRules.FirstOrDefault(item => item.Key == row.Key);
                    if (!MinecraftGameplayPresentation.IsShown(rule))
                    {
                        return null;
                    }

                    var toggle = new CheckBox
                    {
                        Style = (Style)FindResource("SwitchStyle"),
                        IsThreeState = false,
                        IsChecked = MinecraftGameplayPresentation.EffectiveValue(rule!),
                        Tag = row.Key
                    };
                    toggle.Click += Rule_Click;
                    AutomationProperties.SetAutomationId(toggle, $"Rule.{row.Key}");
                    AutomationProperties.SetName(toggle, label);
                    status = Caption(MinecraftGameplayPresentation.RuleStatus(rule!));
                    _ruleRows[row.Key] = (toggle, status);
                    control = toggle;
                    break;
                }

            case GameplayRowKind.FallDamage:
                {
                    var rule = snapshot.GameRules.FirstOrDefault(item => item.Key == "fallDamage");
                    var choices = MinecraftGameplayPresentation.FallDamageChoices(snapshot.FallDamage);
                    if (rule is { Supported: false })
                    {
                        return null;
                    }

                    if (choices.Count == 0)
                    {
                        status = Caption(LocalizationService.Get("Gameplay.FallDamage.Unavailable"));
                        control = new TextBlock();
                        break;
                    }

                    var box = new ComboBox { MinWidth = 180 };
                    foreach (var percent in choices)
                    {
                        box.Items.Add(new ComboBoxItem { Content = MinecraftGameplayPresentation.FallDamageLabel(percent), Tag = percent });
                    }

                    var selected = MinecraftGameplayPresentation.FallDamageSelection(rule);
                    box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (int)item.Tag == selected);
                    box.SelectionChanged += FallDamage_SelectionChanged;
                    AutomationProperties.SetAutomationId(box, "FallDamage");
                    AutomationProperties.SetName(box, label);
                    status = Caption(rule is null ? string.Empty : MinecraftGameplayPresentation.RuleStatus(rule));
                    _fallDamageBox = box;
                    _fallDamageStatus = status;
                    control = box;
                    break;
                }

            default:
                {
                    var definition = MinecraftGameplayPropertyPolicy.Find(row.Key);
                    if (definition is null)
                    {
                        return null;
                    }

                    var value = _saved.TryGetValue(row.Key, out var saved) ? saved : null;
                    control = BuildPropertyControl(definition, value, label);
                    status = Caption(MinecraftGameplayPresentation.PropertyStatus(row.Key));
                    break;
                }
        }

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("BodyTextStyle"), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Text = LocalizationService.Get(row.HelpKey),
            Style = (Style)FindResource("CaptionTextStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        text.Children.Add(status);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    private System.Windows.FrameworkElement BuildPropertyControl(MinecraftPropertyDefinition definition, string? value, string label)
    {
        System.Windows.FrameworkElement control;
        switch (definition.Kind)
        {
            case MinecraftPropertyKind.Boolean:
                var toggle = new CheckBox
                {
                    Style = (Style)FindResource("SwitchStyle"),
                    IsChecked = value is null ? null : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
                    Tag = definition.Key
                };
                toggle.Click += (_, _) => Edit(definition.Key, toggle.IsChecked == true ? "true" : "false");
                control = toggle;
                break;

            case MinecraftPropertyKind.Choice:
                var box = new ComboBox { MinWidth = 180, Tag = definition.Key };
                foreach (var choice in definition.Choices!)
                {
                    box.Items.Add(new ComboBoxItem { Content = MinecraftGameplayPresentation.ChoiceLabel(choice), Tag = choice });
                }

                box.SelectedItem = box.Items.Cast<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals((string)item.Tag, value, StringComparison.OrdinalIgnoreCase));
                box.SelectionChanged += (_, _) =>
                {
                    if (box.SelectedItem is ComboBoxItem { Tag: string choice })
                    {
                        Edit(definition.Key, choice);
                    }
                };
                control = box;
                break;

            default:
                var number = new TextBox { Width = 90, Text = value ?? string.Empty, Tag = definition.Key };
                number.TextChanged += (_, _) =>
                {
                    if (MinecraftGameplayPropertyPolicy.TryNormalize(definition.Key, number.Text, out var normalized, out _))
                    {
                        _invalid.Remove(definition.Key);
                        Edit(definition.Key, normalized);
                    }
                    else if (!_rendering)
                    {
                        _invalid[definition.Key] = LocalizationService.Format(
                            "Gameplay.InvalidNumber",
                            definition.Minimum,
                            definition.Maximum);
                        UpdateSaveState();
                    }
                };
                control = number;
                break;
        }

        AutomationProperties.SetAutomationId(control, $"Property.{definition.Key}");
        AutomationProperties.SetName(control, label);
        return control;
    }

    private void Edit(string key, string value)
    {
        if (_rendering)
        {
            return;
        }

        _edited[key] = value;
        UpdateSaveState();
    }

    private IReadOnlyDictionary<string, string> PendingEdits() =>
        MinecraftGameplayPresentation.Changes(_saved, _edited);

    private void UpdateSaveState()
    {
        var changes = PendingEdits();
        SaveButton.IsEnabled = !_busy && _snapshot is not null && changes.Count > 0 && _invalid.Count == 0;
        if (_invalid.Count > 0)
        {
            SaveStatus.Text = _invalid.Values.First();
            _showingEditState = true;
        }
        else if (changes.Count > 0)
        {
            SaveStatus.Text = LocalizationService.Format("Gameplay.SaveUnsaved", changes.Count);
            _showingEditState = true;
        }
        else if (_showingEditState)
        {
            // Edits undone by hand: nothing is waiting any more.
            SaveStatus.Text = string.Empty;
            _showingEditState = false;
        }
    }

    private async void Rule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string key } toggle)
        {
            return;
        }

        await ChangeRuleAsync(key, toggle.IsChecked == true);
    }

    private async void FallDamage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || _fallDamageBox?.SelectedItem is not ComboBoxItem { Tag: int percent } ||
            MinecraftFallDamagePolicy.GameRuleValue(percent) is not { } value)
        {
            return;
        }

        await ChangeRuleAsync("fallDamage", value);
    }

    private async Task ChangeRuleAsync(string key, bool value)
    {
        if (_snapshot is null || _busy)
        {
            RestoreRule(key);
            return;
        }

        SetBusy(true);
        SetRuleEnabled(key, false);
        var label = LocalizationService.Get(key == "fallDamage" ? "Gameplay.FallDamage" : $"Gameplay.Rule.{key}");
        try
        {
            var response = await _http.PostAsJsonAsync(
                $"/api/v1/servers/{_serverId}/minecraft/gamerules",
                new MinecraftGameRuleChangeRequest(key, value));
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<MinecraftChangeResult>();
            switch (result?.Outcome)
            {
                case MinecraftChangeOutcome.AppliedLive:
                    UpdateRule(key, rule => rule with { Value = result.VerifiedValue ?? value, Source = MinecraftValueSource.Live, PendingValue = null });
                    StatusText.Text = LocalizationService.Format("Gameplay.Rule.Applied", label);
                    break;
                case MinecraftChangeOutcome.PendingNextStart:
                    UpdateRule(key, rule => rule with { PendingValue = rule.Value == value ? null : value });
                    StatusText.Text = LocalizationService.Format("Gameplay.Rule.Pending", label);
                    break;
                default:
                    if (result?.VerifiedValue is { } actual)
                    {
                        UpdateRule(key, rule => rule with { Value = actual, Source = MinecraftValueSource.Live });
                    }

                    StatusText.Text = LocalizationService.Format(
                        "Gameplay.Rule.Failed",
                        label,
                        MinecraftGameplayPresentation.ErrorText(result?.ErrorCode, result?.Message, label));
                    break;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              System.Text.Json.JsonException or NotSupportedException)
        {
            StatusText.Text = LocalizationService.Format("Gameplay.Rule.Failed", label, exception.Message);
        }
        finally
        {
            RestoreRule(key);
            SetRuleEnabled(key, true);
            SetBusy(false);
        }
    }

    private void UpdateRule(string key, Func<MinecraftGameRuleState, MinecraftGameRuleState> change)
    {
        if (_snapshot is null)
        {
            return;
        }

        _snapshot = _snapshot with
        {
            GameRules = _snapshot.GameRules.Select(rule => rule.Key == key ? change(rule) : rule).ToArray()
        };
    }

    /// <summary>Shows the rule as the server now has it, whatever the click did.</summary>
    private void RestoreRule(string key)
    {
        var rule = _snapshot?.GameRules.FirstOrDefault(item => item.Key == key);
        if (rule is null)
        {
            return;
        }

        _rendering = true;
        try
        {
            if (key == "fallDamage" && _fallDamageBox is not null)
            {
                var selected = MinecraftGameplayPresentation.FallDamageSelection(rule);
                _fallDamageBox.SelectedItem = _fallDamageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (int)item.Tag == selected);
                if (_fallDamageStatus is not null)
                {
                    _fallDamageStatus.Text = MinecraftGameplayPresentation.RuleStatus(rule);
                }
            }
            else if (_ruleRows.TryGetValue(key, out var row))
            {
                row.Toggle.IsChecked = MinecraftGameplayPresentation.EffectiveValue(rule);
                row.Status.Text = MinecraftGameplayPresentation.RuleStatus(rule);
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private void SetRuleEnabled(string key, bool enabled)
    {
        if (key == "fallDamage" && _fallDamageBox is not null)
        {
            _fallDamageBox.IsEnabled = enabled;
        }
        else if (_ruleRows.TryGetValue(key, out var row))
        {
            row.Toggle.IsEnabled = enabled;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var changes = PendingEdits();
        if (changes.Count == 0 || _busy)
        {
            return;
        }

        if (changes.TryGetValue("online-mode", out var onlineMode) && onlineMode == "false" &&
            !ConfirmationDialog.Confirm(
                this,
                LocalizationService.Get("Gameplay.OnlineOff.Title"),
                LocalizationService.Get("Gameplay.OnlineOff.Heading"),
                LocalizationService.Get("Gameplay.OnlineOff.Body"),
                null,
                LocalizationService.Get("Gameplay.OnlineOff.Confirm")))
        {
            return;
        }

        SetBusy(true);
        var saved = false;
        string message;
        try
        {
            var response = await _http.PostAsJsonAsync(
                $"/api/v1/servers/{_serverId}/minecraft/gameplay/properties",
                new MinecraftPropertiesChangeRequest(changes));
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<MinecraftPropertiesChangeResult>();
            if (result is { Success: true })
            {
                saved = true;
                message = result.RestartRequired
                    ? LocalizationService.Get(result.AppliedLive.Count > 0 ? "Gameplay.SavedPartlyLive" : "Gameplay.SavedRestart")
                    : LocalizationService.Get(result.AppliedLive.Count > 0 ? "Gameplay.SavedLive" : "Gameplay.SavedStopped");
            }
            else
            {
                message = LocalizationService.Format(
                    "Gameplay.SaveFailed",
                    MinecraftGameplayPresentation.ErrorText(result?.ErrorCode, result?.Message));
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              System.Text.Json.JsonException or NotSupportedException)
        {
            message = LocalizationService.Format("Gameplay.SaveFailed", exception.Message);
        }
        finally
        {
            SetBusy(false);
        }

        if (saved)
        {
            // Show what the file now holds, not what was typed.
            await LoadAsync();
        }

        SaveStatus.Text = message;
        _showingEditState = false;
    }

    // ---- Players ------------------------------------------------------------------------

    private void RenderPlayers()
    {
        if (_players is not { } players)
        {
            return;
        }

        OnlineSummary.Text = !players.OnlineKnown
            ? LocalizationService.Get("Gameplay.Players.OnlineUnknown")
            : players.Online.Count == 0
                ? LocalizationService.Get("Gameplay.Players.NoneOnline")
                : players.MaxPlayers is { } max
                    ? LocalizationService.Format("Gameplay.Players.OnlineCount", players.Online.Count, max)
                    : LocalizationService.Format("Gameplay.Players.OnlineCountNoMax", players.Online.Count);
        Fill(OnlineList, null, players.Online);
        Fill(OperatorsList, OperatorsEmpty, players.Operators);
        Fill(WhitelistList, WhitelistEmpty, players.Whitelisted);
        Fill(BannedList, BannedEmpty, players.Banned);
        UpdatePlayerActions();
    }

    /// <summary>Only real names go in a list; an empty one says so in words instead.</summary>
    private static void Fill(ListBox list, TextBlock? empty, IReadOnlyList<string> names)
    {
        list.ItemsSource = names;
        list.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (empty is not null)
        {
            empty.Text = LocalizationService.Get("Gameplay.Players.Empty");
            empty.Visibility = names.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void UpdatePlayerActions()
    {
        var control = _players?.Control ?? _snapshot?.Control ?? MinecraftLiveControl.Stopped;
        var live = MinecraftGameplayPresentation.PlayerActionsAvailable(control);
        var name = NameBox.Text.Trim();
        var valid = MinecraftPlayerCommandPolicy.IsValidName(name);
        foreach (var button in ActionButtons.Children.OfType<Button>())
        {
            button.IsEnabled = live && valid && !_busy;
        }

        PlayersHint.Text = !live
            ? LocalizationService.Get(control == MinecraftLiveControl.NoConsole
                ? "Gameplay.Players.NeedsRestart"
                : "Gameplay.Players.NeedsLive")
            : name.Length > 0 && !valid
                ? LocalizationService.Get("Gameplay.Players.InvalidName")
                : string.Empty;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePlayerActions();

    private void PlayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string name } && MinecraftPlayerCommandPolicy.IsValidName(name))
        {
            NameBox.Text = name;
        }
    }

    private async void PlayerAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MinecraftPlayerAction action } || _busy)
        {
            return;
        }

        var player = NameBox.Text.Trim();
        if (!MinecraftPlayerCommandPolicy.IsValidName(player))
        {
            PlayersHint.Text = LocalizationService.Get("Gameplay.Players.InvalidName");
            return;
        }

        if (MinecraftPlayerCommandPolicy.NeedsConfirmation(action) &&
            !ConfirmationDialog.Confirm(
                this,
                LocalizationService.Get("Gameplay.Confirm.Title"),
                LocalizationService.Format($"Gameplay.Confirm.{action}", player),
                LocalizationService.Get("Gameplay.Confirm.Body"),
                null,
                MinecraftGameplayPresentation.ActionLabel(action)))
        {
            return;
        }

        SetBusy(true);
        string message;
        try
        {
            var response = await _http.PostAsJsonAsync(
                $"/api/v1/servers/{_serverId}/minecraft/players/actions",
                new MinecraftPlayerActionRequest(action, player));
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<MinecraftChangeResult>();
            message = result switch
            {
                { Outcome: MinecraftChangeOutcome.AppliedLive, ErrorCode: "NoChange" } =>
                    LocalizationService.Format("Gameplay.Players.NoChange", result.Message ?? player),
                { Outcome: MinecraftChangeOutcome.AppliedLive } =>
                    LocalizationService.Format("Gameplay.Players.Done", MinecraftGameplayPresentation.ActionLabel(action), player),
                _ => MinecraftGameplayPresentation.ErrorText(result?.ErrorCode, result?.Message, player)
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              System.Text.Json.JsonException or NotSupportedException)
        {
            message = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }

        await ReloadPlayersAsync();
        PlayersHint.Text = message;
    }

    private async Task ReloadPlayersAsync()
    {
        try
        {
            _players = await _http.GetFromJsonAsync<MinecraftPlayersState>(
                $"/api/v1/servers/{_serverId}/minecraft/players/live");
            RenderPlayers();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              System.Text.Json.JsonException or NotSupportedException)
        {
            PlayersHint.Text = exception.Message;
        }
    }

    // ---- Chrome -------------------------------------------------------------------------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (GameplayPanel is null || PlayersPanel is null)
        {
            return;
        }

        var players = TabPlayers.IsChecked == true;
        GameplayPanel.Visibility = players ? Visibility.Collapsed : Visibility.Visible;
        PlayersPanel.Visibility = players ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Visibility = players ? Visibility.Collapsed : Visibility.Visible;
        SaveStatus.Visibility = SaveButton.Visibility;
        Scroller.ScrollToTop();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (PendingEdits().Count > 0 &&
            !ConfirmationDialog.Confirm(
                this,
                LocalizationService.Get("Gameplay.Discard.Title"),
                LocalizationService.Get("Gameplay.Discard.Heading"),
                LocalizationService.Get("Gameplay.Discard.Body"),
                null,
                LocalizationService.Get("Gameplay.Discard.Confirm")))
        {
            return;
        }

        SaveStatus.Text = string.Empty;
        await LoadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (PendingEdits().Count > 0 &&
            !ConfirmationDialog.Confirm(
                this,
                LocalizationService.Get("Gameplay.Discard.Title"),
                LocalizationService.Get("Gameplay.Discard.Heading"),
                LocalizationService.Get("Gameplay.Discard.Body"),
                null,
                LocalizationService.Get("Gameplay.Discard.Confirm")))
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+S saves the staged settings, like other editors.
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && SaveButton.IsEnabled &&
            SaveButton.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            Save_Click(SaveButton, new RoutedEventArgs());
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private TextBlock Caption(string text) =>
        new()
        {
            Text = text,
            Style = (Style)FindResource("CaptionTextStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
}
