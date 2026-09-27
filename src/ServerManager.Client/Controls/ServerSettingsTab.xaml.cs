using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Threading;
using ServerManager.Client.Shell;
using ServerManager.Client.Transport;
using ServerManager.Contracts;
using ServerManager.Core;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

/// <summary>
/// Settings for this server only — the application's own preferences stay in the app Settings
/// destination. Raw configuration keys are never shown; each group speaks in outcomes.
/// </summary>
public partial class ServerSettingsTab : UserControl
{
    private readonly ServerDetailContext _context = ServerDetailContext.Shared;
    private readonly HttpClient _httpClient;
    private readonly ConnectOwnerClient _connectClient = new(TimeSpan.FromSeconds(30));
    private readonly DispatcherTimer _connectTimer;
    private bool _active;
    private bool _loading;
    private bool _connectLoading;
    private ServerConnectResponse? _connectStatus;
    private ConnectServerViewModel? _connectView;
    private string? _connectError;
    private Guid _connectServerId;

    public ServerSettingsTab()
    {
        InitializeComponent();
        _httpClient = _context.CreateClient(TimeSpan.FromMinutes(2));
        _connectTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(6)
        };
        _connectTimer.Tick += async (_, _) => await LoadConnectAsync();
        _context.Changed += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_connectServerId != _context.ServerId)
            {
                // Never render one server's access state while switching to another server.
                _connectServerId = _context.ServerId;
                _connectStatus = null;
                _connectError = null;
            }
            Localize();
            UpdateConnectPolling();
        });
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Localize);
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(Localize);
        Loaded += (_, _) =>
        {
            Localize();
            UpdateConnectPolling();
        };
        Unloaded += (_, _) => _connectTimer.Stop();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            Localize();
            LoadGeneral();
        }

        UpdateConnectPolling();
    }

    /// <summary>Jumps straight to Advanced, used by the header's Diagnostics entry.</summary>
    public void RevealAdvanced()
    {
        SelectGroup("Advanced");
        if (AdvancedExpander is not null)
        {
            AdvancedExpander.IsExpanded = true;
        }
    }

    private void SelectGroup(string tag)
    {
        foreach (var item in GroupList.Items.OfType<ListBoxItem>())
        {
            item.IsSelected = string.Equals(item.Tag as string, tag, StringComparison.Ordinal);
        }
    }

    private string CurrentGroup =>
        (GroupList.SelectedItem as ListBoxItem)?.Tag as string ?? "General";

    private void Group_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GeneralGroup is null)
        {
            return;
        }

        Localize();
        UpdateConnectPolling();
    }

    private void Localize()
    {
        if (GroupList is null || GeneralGroup is null)
        {
            return;
        }

        foreach (var item in GroupList.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is string tag)
            {
                item.Content = LocalizationService.Get($"ServerSettings.{tag}");
            }
        }

        GeneralTitle.Text = LocalizationService.Get("ServerSettings.General");
        AutoStartLabel.Text = LocalizationService.Get("ServerSettings.AutoStart");
        AutoRestartLabel.Text = LocalizationService.Get("ServerSettings.AutoRestart");
        SaveGeneralButton.Content = LocalizationService.Get("Action.Save");
        UpdatesTitle.Text = LocalizationService.Get("ServerSettings.Updates");
        CheckUpdatesButton.Content = LocalizationService.Get("Action.CheckUpdates");
        AdvancedTitle.Text = LocalizationService.Get("ServerSettings.Advanced");
        AdvancedIntro.Text = LocalizationService.Get("ServerSettings.AdvancedIntro");
        AdvancedExpander.Header = LocalizationService.Get("Advanced.Title");
        CopyDiagnosticsButton.Content = LocalizationService.Get("Action.CopyDiagnostics");
        OpenFilesButton.Content = LocalizationService.Get("Action.OpenFolder");
        // The Safe File Manager's only entry point; without a label it rendered as a blank button.
        FileManagerButton.Content = LocalizationService.Get("ServerSettings.Files");
        RenderConnectCard();

        var group = CurrentGroup;
        var isPalworld = _context.Source?.Game == GameType.Palworld;
        GeneralGroup.Visibility = group == "General" ? Visibility.Visible : Visibility.Collapsed;
        ConnectCard.Visibility = group == "Network" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedCard.Visibility = group == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsCard.Visibility = group == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
        GroupCard.Visibility = group is "Game" or "Network" or "Resources"
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The one embedded legacy editor is Palworld-specific.
        ResourcesEditorCard.Visibility = group == "Resources" && isPalworld
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (GroupCard.Visibility == Visibility.Visible)
        {
            RenderPortedGroup(group);
        }

        if (group == "Advanced")
        {
            RenderAdvanced();
        }

        BindEmbeddedEditors(group);

        var updateStatus = _context.Source?.UpdateStatus;
        UpdatesStatus.Text = string.IsNullOrWhiteSpace(updateStatus)
            ? LocalizationService.Get("ServerSettings.UpdatesUnknown")
            : updateStatus!;
        CheckUpdatesButton.IsEnabled = _context.Card is not null;
        SaveGeneralButton.IsEnabled = _context.Card is not null && !_loading;
    }

    /// <summary>
    /// Each group names what it covers and offers its real actions. The memory-policy editor
    /// is embedded below this card; the full-width ones open at full size, so nothing a
    /// person could previously change has become unreachable.
    /// </summary>
    private void RenderPortedGroup(string group)
    {
        GroupTitle.Text = LocalizationService.Get($"ServerSettings.{group}");
        GroupBody.Text = LocalizationService.Get($"ServerSettings.{group}Body");
        GroupPrimaryButton.Visibility = Visibility.Collapsed;
        GroupSecondaryButton.Visibility = Visibility.Collapsed;

        var isPalworld = _context.Source?.Game == GameType.Palworld;

        switch (group)
        {
            case "Game":
                // Both games' world/gameplay editors are full-width designs, so they open at
                // full size rather than being squeezed into this column.
                GroupPrimaryButton.Visibility = Visibility.Visible;
                GroupPrimaryButton.Content = LocalizationService.Get("Action.Configure");
                GroupPrimaryButton.Tag = "legacy-settings";
                GroupPrimaryButton.IsEnabled = _context.Card is not null;
                break;

            case "Network":
                GroupPrimaryButton.Visibility = Visibility.Visible;
                GroupPrimaryButton.Content = LocalizationService.Get("Action.CopyAddress");
                GroupPrimaryButton.Tag = "copy-address";
                GroupPrimaryButton.IsEnabled =
                    !string.IsNullOrWhiteSpace(_context.Source?.LocalAddress) ||
                    !string.IsNullOrWhiteSpace(_context.Card?.InternetAddress);

                GroupSecondaryButton.Visibility = Visibility.Visible;
                GroupSecondaryButton.Content = LocalizationService.Get("Action.Configure");
                GroupSecondaryButton.Tag = "legacy-network";
                GroupSecondaryButton.IsEnabled = _context.Card is not null;
                break;

            case "Resources":
                GroupPrimaryButton.Visibility = Visibility.Visible;
                GroupPrimaryButton.Content = LocalizationService.Get("Action.ResourceGovernor");
                GroupPrimaryButton.Tag = "resource-governor";
                GroupPrimaryButton.IsEnabled = true;
                break;
        }
    }

    private void RenderConnectCard()
    {
        ConnectTitle.Text = LocalizationService.Get("ServerSettings.Connect");
        var game = _context.Source?.Game ?? GameType.Minecraft;
        _connectView = ConnectPresentation.Server(game, _connectStatus);
        ConnectBadge.Text = _connectView.State;
        ConnectBody.Text = _connectError ?? _connectView.Detail;
        ConnectEnableButton.Content = _connectView.PrimaryLabel;
        ConnectEnableButton.IsEnabled = !_connectLoading && _connectView.CanRunPrimaryAction;
        ConnectInviteButton.Content = LocalizationService.Get("ServerSettings.ConnectInvite");
        ConnectInviteButton.IsEnabled = !_connectLoading && _connectView.CanInvite;
        ConnectFriendsButton.Content = _connectView.PendingCount > 0
            ? LocalizationService.Format("ServerSettings.ConnectFriendsPending", _connectView.PendingCount)
            : LocalizationService.Get("ServerSettings.ConnectFriends");
        ConnectFriendsButton.IsEnabled = !_connectLoading && _connectView.CanManageFriends;
    }

    /// <summary>Poll only while this tab's Network group is actually on screen.</summary>
    private void UpdateConnectPolling()
    {
        if (_active && IsVisible && CurrentGroup == "Network")
        {
            _connectTimer.Start();
            _ = LoadConnectAsync();
        }
        else
        {
            _connectTimer.Stop();
        }
    }

    private async Task LoadConnectAsync()
    {
        if (_connectLoading || !_active || !IsVisible || CurrentGroup != "Network")
        {
            return;
        }

        if (_context.Card is null)
        {
            _connectStatus = null;
            RenderConnectCard();
            return;
        }

        _connectLoading = true;
        _connectError = null;
        RenderConnectCard();
        try
        {
            _connectStatus = await _connectClient.GetServerAsync(_context.ServerId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _connectStatus = null;
            _connectError = DiagnosticsService.Redact(exception.Message);
        }
        finally
        {
            _connectLoading = false;
            RenderConnectCard();
        }
    }

    private async void ConnectEnable_Click(object sender, RoutedEventArgs e)
    {
        if (_connectView?.PrimaryAction is not (ConnectServerAction.Enable or ConnectServerAction.Disable))
        {
            return;
        }

        await RunConnectActionAsync(() => _connectView.PrimaryAction == ConnectServerAction.Enable
            ? _connectClient.EnableAsync(_context.ServerId)
            : _connectClient.DisableAsync(_context.ServerId));
    }

    private async void ConnectInvite_Click(object sender, RoutedEventArgs e)
    {
        new ConnectInviteWindow(_context.ServerId) { Owner = Window.GetWindow(this) }.ShowDialog();
        await LoadConnectAsync();
    }

    private async void ConnectFriends_Click(object sender, RoutedEventArgs e)
    {
        new ConnectFriendsWindow(_context.ServerId) { Owner = Window.GetWindow(this) }.ShowDialog();
        await LoadConnectAsync();
    }

    private async Task RunConnectActionAsync(Func<Task> action)
    {
        if (_connectLoading)
        {
            return;
        }

        _connectLoading = true;
        _connectError = null;
        RenderConnectCard();
        try
        {
            await action();
            _connectStatus = await _connectClient.GetServerAsync(_context.ServerId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _connectError = DiagnosticsService.Redact(exception.Message);
        }
        finally
        {
            _connectLoading = false;
            RenderConnectCard();
        }
    }

    private void RenderAdvanced()
    {
        var source = _context.Source;
        if (source is null)
        {
            AdvancedBody.Text = LocalizationService.Get("Status.Unavailable");
            return;
        }

        AdvancedBody.Text = string.Join(
            Environment.NewLine,
            $"ServerId  {source.ServerId}",
            $"{LocalizationService.Get("Advanced.RootProcess")}  {source.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "—"}  {source.RootExecutableName}",
            $"{LocalizationService.Get("Advanced.GameProcess")}  {source.GameProcessId?.ToString(CultureInfo.InvariantCulture) ?? "—"}  {source.GameExecutableName}",
            $"{LocalizationService.Get("Advanced.ChildProcesses")}  {source.ChildProcessCount}",
            $"{LocalizationService.Get("Advanced.Threads")}  {source.ThreadCount}",
            $"{LocalizationService.Get("Advanced.Port")}  {source.Port}",
            $"Playit  {source.PlayitState ?? "—"}",
            $"{LocalizationService.Get("Metric.Memory")}  {source.WorkingSetBytes} / {source.PeakWorkingSetBytes}");
    }

    private async void LoadGeneral()
    {
        var source = _context.Source;
        if (source is null)
        {
            return;
        }

        _loading = true;
        AutoStartToggle.IsChecked = source.AutoStart;
        AutoRestartToggle.IsChecked = source.AutoRestart;
        _loading = false;
        await Task.CompletedTask;
    }

    private async void SaveGeneral_Click(object sender, RoutedEventArgs e)
    {
        if (_context.Card is null)
        {
            return;
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/servers/{_context.ServerId}/automation",
                new ServerAutomationUpdateRequest(
                    AutoStartToggle.IsChecked == true,
                    AutoRestartToggle.IsChecked == true));
            GeneralStatus.Text = response.IsSuccessStatusCode
                ? LocalizationService.Get("Action.Done")
                : LocalizationService.Get("Error.ActionFailed");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            GeneralStatus.Text = exception.Message;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<UpdateCheckResult>(
                $"/api/v1/servers/{_context.ServerId}/updates");
            UpdatesStatus.Text = result is null
                ? LocalizationService.Get("ServerSettings.UpdatesUnknown")
                : result.IsUpdateAvailable
                    ? LocalizationService.Format("ServerSettings.UpdateAvailable", result.LatestVersion ?? "—")
                    : LocalizationService.Get("ServerSettings.UpToDate");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            UpdatesStatus.Text = LocalizationService.Get("Error.ServiceUnavailable");
        }
    }

    /// <summary>
    /// Points each embedded editor at the current server, and only while its group is on
    /// screen so a hidden editor does not keep polling the agent.
    /// </summary>
    private void BindEmbeddedEditors(string group)
    {
        var id = _context.Card is null ? (Guid?)null : _context.ServerId;
        MemoryEditor.SetServer(
            group == "Resources" && ResourcesEditorCard.Visibility == Visibility.Visible ? id : null);
        DiagnosticsEditor.SetServer(group == "Advanced" ? id : null);
    }

    private void GroupPrimary_Click(object sender, RoutedEventArgs e) =>
        RunGroupAction(GroupPrimaryButton.Tag as string);

    private void GroupSecondary_Click(object sender, RoutedEventArgs e) =>
        RunGroupAction(GroupSecondaryButton.Tag as string);

    private void RunGroupAction(string? action)
    {
        switch (action)
        {
            case "copy-address":
                var address = _context.Card?.InternetAddress;
                if (string.IsNullOrWhiteSpace(address))
                {
                    address = _context.Source?.LocalAddress;
                }

                if (!string.IsNullOrWhiteSpace(address))
                {
                    SafeClipboard.TrySetText(address!);
                }

                break;

            case "resource-governor":
                new ResourceGovernorWindow { Owner = Window.GetWindow(this) }.ShowDialog();
                break;

            case "legacy-settings":
                OpenLegacy(LegacyServerEditorWindow.SettingsTab, "ServerSettings.Game");
                break;

            case "legacy-network":
                OpenLegacy(LegacyServerEditorWindow.NetworkTab, "ServerSettings.Network");
                break;
        }
    }

    private void OpenLegacy(int tabIndex, string titleKey)
    {
        if (_context.Source is not { } source)
        {
            return;
        }

        // The legacy editors are opened for the server that is on screen, by id: with several
        // servers of one game registered, "the Minecraft server" is no longer an address.
        LegacyServerEditorWindow.Open(
            Window.GetWindow(this),
            source.Game,
            tabIndex,
            titleKey,
            "ServerSettings.LegacyIntro",
            source.ServerId);
    }

    private void FileManager_Click(object sender, RoutedEventArgs e) =>
        OpenLegacy(LegacyServerEditorWindow.FilesTab, "ServerSettings.Files");

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AdvancedBody.Text))
        {
            SafeClipboard.TrySetText(AdvancedBody.Text);
        }
    }

    private async void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var servers = await _httpClient.GetFromJsonAsync<GameServerDefinition[]>(
                "/api/v1/servers") ?? [];
            var definition = servers.FirstOrDefault(item => item.Id == _context.ServerId);
            if (definition is null)
            {
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
}
