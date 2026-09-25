using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ServerManager.Client.Shell;
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
    private bool _active;
    private bool _loading;

    public ServerSettingsTab()
    {
        InitializeComponent();
        _httpClient = _context.CreateClient(TimeSpan.FromMinutes(2));
        _context.Changed += (_, _) => Dispatcher.Invoke(Localize);
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Localize);
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(Localize);
        Loaded += (_, _) => Localize();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            Localize();
            LoadGeneral();
        }
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
        LocalizeConnectCard();

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

    /// <summary>
    /// 1Salem Connect is a development preview with nothing configured in this build. The card
    /// only says so: its buttons are disabled in the markup and have no handlers, so there is
    /// no path by which it could appear to enable, invite or revoke anyone.
    /// </summary>
    private void LocalizeConnectCard()
    {
        ConnectTitle.Text = LocalizationService.Get("ServerSettings.Connect");
        ConnectBadge.Text = LocalizationService.Get("ServerSettings.ConnectPreview");
        ConnectBody.Text = LocalizationService.Get("ServerSettings.ConnectBody");
        ConnectEnableButton.Content = LocalizationService.Get("ServerSettings.ConnectEnable");
        ConnectInviteButton.Content = LocalizationService.Get("ServerSettings.ConnectInvite");
        ConnectFriendsButton.Content = LocalizationService.Get("ServerSettings.ConnectFriends");
        ConnectRevokeButton.Content = LocalizationService.Get("ServerSettings.ConnectRevoke");
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
