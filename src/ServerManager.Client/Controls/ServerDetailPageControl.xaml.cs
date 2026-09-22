using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core;
using AutomationProperties = System.Windows.Automation.AutomationProperties;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

/// <summary>
/// One server, five destinations. The header carries identity, state and exactly one primary
/// action; everything rarer lives behind the overflow. Technical values stay inside each
/// tab's Advanced details rather than competing for attention here.
/// </summary>
public partial class ServerDetailPageControl : UserControl
{
    private readonly ServerDetailContext _context = ServerDetailContext.Shared;
    private readonly HttpClient _httpClient;
    private bool _busy;

    public ServerDetailPageControl()
    {
        InitializeComponent();
        _httpClient = _context.CreateClient(TimeSpan.FromMinutes(2));
        _context.Changed += (_, _) => Dispatcher.Invoke(Render);
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Render);
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(Render);
        Loaded += (_, _) => Render();
    }

    /// <summary>Raised when the person asks to go back to the server list.</summary>
    public event EventHandler? BackRequested;

    public void Show(Guid serverId, string tab = "Overview")
    {
        _context.Select(serverId);
        SelectTab(tab);
        Render();
    }

    /// <summary>
    /// Starts the shown server through exactly the path the header's own Start uses,
    /// including its availability check, so a card's "Start" really starts the server.
    /// </summary>
    public Task StartAsync() => RunActionAsync("start");

    /// <summary>
    /// Puts keyboard focus on the selected tab. Opening a server collapses the page the person
    /// came from, which otherwise leaves focus on an element that is no longer on screen.
    /// </summary>
    public void FocusSelectedTab()
    {
        var selected = new[] { TabOverview, TabConsole, TabBackups, TabContent, TabSettings }
            .FirstOrDefault(tab => tab.IsChecked == true);
        selected?.Focus();
    }

    // --- tabs -------------------------------------------------------------------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (OverviewTab is null || sender is not RadioButton { Tag: string key })
        {
            return;
        }

        ShowTab(key);
    }

    private void SelectTab(string key)
    {
        if (TabOverview is null)
        {
            return;
        }

        foreach (var tab in new[] { TabOverview, TabConsole, TabBackups, TabContent, TabSettings })
        {
            tab.IsChecked = string.Equals(tab.Tag as string, key, StringComparison.Ordinal);
        }

        ShowTab(key);
    }

    private void ShowTab(string key)
    {
        OverviewTab.Visibility = Vis(key, "Overview");
        ConsoleTab.Visibility = Vis(key, "Console");
        BackupsTab.Visibility = Vis(key, "Backups");
        ContentTab.Visibility = Vis(key, "Content");
        SettingsTab.Visibility = Vis(key, "Settings");

        // Tabs only fetch while visible: a hidden console should not keep polling logs.
        ConsoleTab.SetActive(key == "Console");
        BackupsTab.SetActive(key == "Backups");
        SettingsTab.SetActive(key == "Settings");
    }

    private static Visibility Vis(string key, string expected) =>
        string.Equals(key, expected, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    // --- header -----------------------------------------------------------------

    private void Render()
    {
        if (TabOverview is null)
        {
            return;
        }

        TabOverview.Content = LocalizationService.Get("ServerTab.Overview");
        TabConsole.Content = LocalizationService.Get("ServerTab.Console");
        TabBackups.Content = LocalizationService.Get("ServerTab.Backups");
        TabContent.Content = LocalizationService.Get("ServerTab.Content");
        TabSettings.Content = LocalizationService.Get("ServerTab.Settings");
        BackButton.ToolTip = LocalizationService.Get("Action.Back");
        AutomationProperties.SetName(BackButton, LocalizationService.Get("Action.Back"));
        AutomationProperties.SetName(
            OverflowButton,
            LocalizationService.Get("Action.MoreActions"));
        OverflowButton.ToolTip = LocalizationService.Get("Action.MoreActions");

        MenuCreateBackup.Header = LocalizationService.Get("Action.CreateBackup");
        MenuCopyAddress.Header = LocalizationService.Get("Action.CopyAddress");
        MenuOpenFolder.Header = LocalizationService.Get("Action.OpenFolder");
        MenuDiagnostics.Header = LocalizationService.Get("Action.Diagnostics");
        MenuForceStop.Header = LocalizationService.Get("Action.ForceStop");
        MenuDeleteServer.Header = LocalizationService.Get("Action.DeleteServer");

        var card = _context.Card;
        if (card is null)
        {
            ServerName.Text = LocalizationService.Get("Status.Unavailable");
            SetSummary("—", "—", "—");
            StatusBadge.Visibility = Visibility.Collapsed;
            PrimaryActionButton.IsEnabled = false;
            SecondaryActionButton.Visibility = Visibility.Collapsed;
            OverflowButton.IsEnabled = false;
            return;
        }

        StatusBadge.Visibility = Visibility.Visible;
        OverflowButton.IsEnabled = true;
        ServerName.Text = card.Name;
        AutomationProperties.SetName(this, card.Name);
        GameGlyph.Text = card.Game == GameType.Minecraft
            ? char.ConvertFromUtf32(0xE7F4)
            : char.ConvertFromUtf32(0xE7FC);

        StatusText.Text = card.StatusLabel;
        StatusDot.Fill = ToneBrush(card.StatusTone);
        StatusBadge.Background = ToneBrush(card.StatusTone, soft: true);
        SetSummary(card.Players, card.Uptime, card.Memory);

        RenderActions(card);
    }

    /// <summary>Players, uptime and memory in one quiet line under the name.</summary>
    private void SetSummary(string players, string uptime, string memory)
    {
        HeaderPlayersLabel.Text = LocalizationService.Get("Metric.Players") + ":";
        HeaderUptimeLabel.Text = LocalizationService.Get("Metric.Uptime") + ":";
        HeaderMemoryLabel.Text = LocalizationService.Get("Metric.Memory") + ":";
        HeaderPlayersValue.Text = players;
        HeaderUptimeValue.Text = uptime;
        HeaderMemoryValue.Text = memory;
    }

    private void RenderActions(ServerCardViewModel card)
    {
        var actions = _context.Actions;
        var running = card.Status == UiStatus.Running;

        // Exactly one primary. Stopped servers start; running servers restart, which is the
        // action an admin actually reaches for. Stop stays available but quiet.
        if (running)
        {
            PrimaryActionButton.Content = LocalizationService.Get("Action.Restart");
            PrimaryActionButton.Tag = "restart";
            PrimaryActionButton.IsEnabled = actions.CanRestart && !_busy;

            SecondaryActionButton.Visibility = Visibility.Visible;
            SecondaryActionButton.Content = LocalizationService.Get("Action.Stop");
            SecondaryActionButton.Tag = "stop";
            SecondaryActionButton.IsEnabled = actions.CanStop && !_busy;
        }
        else
        {
            PrimaryActionButton.Content = LocalizationService.Get("Action.Start");
            PrimaryActionButton.Tag = "start";
            PrimaryActionButton.IsEnabled = actions.CanStart && !_busy;
            SecondaryActionButton.Visibility = Visibility.Collapsed;
        }

        MenuCreateBackup.IsEnabled = actions.CanBackup && !_busy;
        MenuForceStop.IsEnabled = actions.CanForceStop && !_busy;
        MenuDeleteServer.IsEnabled = !_busy;
        MenuCopyAddress.IsEnabled = !string.IsNullOrWhiteSpace(card.InternetAddress)
            || !string.IsNullOrWhiteSpace(_context.Source?.LocalAddress);
    }

    private static Brush ToneBrush(UiStatusTone tone, bool soft = false)
    {
        var key = tone switch
        {
            UiStatusTone.Positive => soft ? "SuccessSoftBrush" : "SuccessBrush",
            UiStatusTone.Caution => soft ? "WarningSoftBrush" : "WarningBrush",
            UiStatusTone.Negative => soft ? "DangerSoftBrush" : "DangerBrush",
            _ => soft ? "SurfaceOverlayBrush" : "TextSecondaryBrush"
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    // --- actions ----------------------------------------------------------------

    private void Back_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        OverflowMenu.PlacementTarget = OverflowButton;
        OverflowMenu.IsOpen = true;
    }

    private async void PrimaryAction_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(PrimaryActionButton.Tag as string);

    private async void SecondaryAction_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync(SecondaryActionButton.Tag as string);

    private async void MenuCreateBackup_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("backups");

    private async void MenuDeleteServer_Click(object sender, RoutedEventArgs e) =>
        await ServerDeletion.RequestAsync(Window.GetWindow(this), _context.Card);

    private async void MenuForceStop_Click(object sender, RoutedEventArgs e)
    {
        // Force stop can lose unsaved world state, so it always asks first.
        var confirmed = MessageBox.Show(
            LocalizationService.Get("Confirm.ForceStop"),
            LocalizationService.Get("Action.ForceStop"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (confirmed)
        {
            await RunActionAsync("force-stop");
        }
    }

    private void MenuCopyAddress_Click(object sender, RoutedEventArgs e)
    {
        var address = _context.Card?.InternetAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            address = _context.Source?.LocalAddress;
        }

        if (!string.IsNullOrWhiteSpace(address))
        {
            SafeClipboard.TrySetText(address!);
        }
    }

    private async void MenuOpenFolder_Click(object sender, RoutedEventArgs e) =>
        await OpenServerFolderAsync();

    /// <summary>
    /// The install directory is only known to the agent, so it is looked up rather than
    /// guessed. Same resolution the legacy page used: the registered server's RootPath.
    /// </summary>
    private async Task OpenServerFolderAsync()
    {
        try
        {
            var servers = await _httpClient.GetFromJsonAsync<GameServerDefinition[]>(
                "/api/v1/servers") ?? [];
            var definition = servers.FirstOrDefault(item => item.Id == _context.ServerId);
            if (definition is null || string.IsNullOrWhiteSpace(definition.RootPath))
            {
                NotificationService.Publish(
                    NotificationKind.Information,
                    LocalizationService.Get("Action.OpenFolder"),
                    LocalizationService.Get("Error.FolderUnavailable"));
                return;
            }

            Directory.CreateDirectory(definition.RootPath);
            var info = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
            info.ArgumentList.Add(definition.RootPath);
            Process.Start(info);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get("Action.OpenFolder"),
                exception.Message);
        }
    }

    private void MenuDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        SelectTab("Settings");
        SettingsTab.RevealAdvanced();
    }

    /// <summary>
    /// Posts one of the agent's server actions. Availability was already checked when the
    /// control was enabled; this re-checks because the state can change between renders.
    /// </summary>
    private async Task RunActionAsync(string? action)
    {
        if (string.IsNullOrWhiteSpace(action) || _busy || _context.Card is null)
        {
            return;
        }

        if (!IsAllowed(action))
        {
            return;
        }

        _busy = true;
        Render();
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/servers/{_context.ServerId}/{action}",
                new { });
            if (!response.IsSuccessStatusCode)
            {
                NotificationService.Publish(
                    NotificationKind.Error,
                    LocalizationService.Get("Error.ActionFailed"),
                    $"{action}: {(int)response.StatusCode}");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get("Error.ActionFailed"),
                exception.Message);
        }
        finally
        {
            _busy = false;
            await DashboardFeed.Shared.RefreshAsync();
            Render();
        }
    }

    private bool IsAllowed(string action)
    {
        var a = _context.Actions;
        return action switch
        {
            "start" => a.CanStart,
            "stop" => a.CanStop,
            "restart" => a.CanRestart,
            "force-stop" => a.CanForceStop,
            "backups" => a.CanBackup,
            _ => false
        };
    }
}
