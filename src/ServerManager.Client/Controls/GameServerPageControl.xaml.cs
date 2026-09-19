using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Client.Shell;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ServerManager.Client.Controls;

public partial class GameServerPageControl : System.Windows.Controls.UserControl, IDisposable
{
    private const long Mebibyte = 1024L * 1024;
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromMinutes(30));
    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private readonly List<LogEntry> _logs = [];
    private readonly List<string> _commandHistory = [];
    private DateTimeOffset? _clearViewBeforeUtc;
    private DashboardSnapshot? _dashboard;
    private ServerDashboardCard? _server;
    private MinecraftConfigurationResponse? _minecraftConfiguration;
    private PalworldConfigurationResponse? _palworldConfiguration;
    private MemoryPerformancePolicySnapshot? _palworldMemoryPolicy;
    private IReadOnlyList<ServerActivityItem> _palworldActivity = [];
    private DateTimeOffset _lastPalworldOverviewDetailRefreshUtc;
    private bool _refreshing;
    private bool _configurationLoaded;
    private bool _suppressSettingsDirty;
    private bool _gameSettingsDirty;
    private bool _memorySettingsDirty;
    private bool _automationSettingsDirty;
    private bool _hasPendingSettings;
    private bool _changingSelectedTab;
    private TabItem? _previousSelectedTab;
    private int _historyIndex;

    public GameServerPageControl()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync(false);
        Loaded += async (_, _) =>
        {
            await RefreshAsync(true);
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
        ServerDiagnosticsControl.OpenLogsRequested += (_, _) =>
            ServerTabs.SelectedItem = ConsoleTab;
        PalworldWorldSettingsControl.UnsavedStateChanged += (_, _) =>
            RenderSettingsState();
        RegisterDirtyTracking(MinecraftSettingsPanel);
        RegisterDirtyTracking(PalworldSettingsPanel);
        RegisterDirtyTracking(MemoryPanel);
        RegisterDirtyTracking(AutomationPanel);
        ServerTabs.SelectionChanged += ServerTabs_SelectionChanged;
        PalworldOverviewDashboard.RefreshRequested += async (_, _) => await RefreshAsync(true);
        PalworldOverviewDashboard.StartRequested += async (_, _) => await PostActionAsync("start");
        PalworldOverviewDashboard.SaveWorldRequested += async (_, _) => await SavePalworldWorldAsync();
        PalworldOverviewDashboard.AnnouncementRequested += async (_, message) =>
            await AnnounceToPalworldAsync(message);
        PalworldOverviewDashboard.GracefulStopRequested += async (_, _) =>
            await PostActionAsync("stop", new ServerStopRequest());
        PalworldOverviewDashboard.RestartRequested += async (_, _) => await PostActionAsync("restart");
        PalworldOverviewDashboard.BackupRequested += async (_, _) => await PostActionAsync("backups");
        PalworldOverviewDashboard.CopyLocalAddressRequested += (_, _) => CopyLocalAddress();
        PalworldOverviewDashboard.CopyInternetAddressRequested += (_, _) => CopyInternetAddress();
    }

    public GameType Game { get; private set; } = GameType.Minecraft;

    public event EventHandler<GameType>? CreateRequested;

    public event EventHandler? RemoteAccessRequested;

    public void Configure(GameType game)
    {
        Game = game;
        TitleText.Text = game == GameType.Minecraft ? "Minecraft" : "Palworld";
        SubtitleText.Text = game == GameType.Minecraft
            ? "Vanilla Minecraft server overview, console, settings, players, files, backups, updates, resources, and network."
            : "Palworld server overview, logs, settings, players, files, backups, updates, resources, and network.";
        NoServerText.Text = $"No {game} server is registered.";
        CreateButton.Content = $"Create {game} Server";
        MinecraftSettingsPanel.Visibility =
            game == GameType.Minecraft ? Visibility.Visible : Visibility.Collapsed;
        MemoryPanel.Visibility =
            game == GameType.Minecraft ? Visibility.Visible : Visibility.Collapsed;
        PalworldSettingsPanel.Visibility =
            game == GameType.Palworld ? Visibility.Visible : Visibility.Collapsed;
        PalworldManagementPanel.Visibility =
            game == GameType.Palworld ? Visibility.Visible : Visibility.Collapsed;
        PalworldOverviewDashboard.Visibility =
            game == GameType.Palworld ? Visibility.Visible : Visibility.Collapsed;
        LegacyOverviewPanel.Visibility =
            game == GameType.Minecraft ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility =
            game == GameType.Minecraft ? Visibility.Visible : Visibility.Collapsed;
        if (game == GameType.Palworld)
        {
            PalworldOverviewDashboard.SetState(
                PalworldOverviewStateFactory.Loading(DateTimeOffset.UtcNow),
                addHistorySample: false);
        }
        MinecraftPlayersPanel.Visibility =
            game == GameType.Minecraft ? Visibility.Visible : Visibility.Collapsed;
        PalworldPlayersPanel.Visibility =
            game == GameType.Palworld ? Visibility.Visible : Visibility.Collapsed;
        PalworldWorldSettingsControl.Visibility =
            game == GameType.Palworld ? Visibility.Visible : Visibility.Collapsed;
        ServerSettingsTab.Visibility = Visibility.Visible;
        PalworldSaveBackupControl.Visibility =
            game == GameType.Palworld ? Visibility.Visible : Visibility.Collapsed;
        LegacyBackupsPanel.Visibility =
            game == GameType.Minecraft ? Visibility.Visible : Visibility.Collapsed;
        PalworldMemoryPerformanceControl.Visibility =
            game == GameType.Palworld ? Visibility.Visible : Visibility.Collapsed;
        LegacyResourcesPanel.Visibility =
            game == GameType.Minecraft ? Visibility.Visible : Visibility.Collapsed;
        SendCommandButton.Content =
            game == GameType.Minecraft ? "Send Command" : "Send (when supported)";
        ApplyTabOrder(game);
    }

    private void ApplyTabOrder(GameType game)
    {
        TabItem[] ordered = game == GameType.Palworld
            ?
            [
                OverviewTab,
                ConsoleTab,
                ServerSettingsTab,
                PlayersTab,
                FilesTab,
                SaveBackupsTab,
                UpdatesTab,
                PerformanceTab,
                NetworkTab,
                DiagnosticsTab
            ]
            :
            [
                OverviewTab,
                ConsoleTab,
                ServerSettingsTab,
                PlayersTab,
                FilesTab,
                SaveBackupsTab,
                UpdatesTab,
                PerformanceTab,
                NetworkTab,
                DiagnosticsTab
            ];
        ServerTabs.Items.Clear();
        foreach (var tab in ordered)
        {
            ServerTabs.Items.Add(tab);
        }

        ServerTabs.SelectedIndex = 0;
        _previousSelectedTab = ServerTabs.SelectedItem as TabItem;
    }

    private void RegisterDirtyTracking(FrameworkElement panel)
    {
        panel.AddHandler(
            System.Windows.Controls.TextBox.TextChangedEvent,
            new TextChangedEventHandler(SettingsField_Changed),
            true);
        panel.AddHandler(
            System.Windows.Controls.ComboBox.SelectionChangedEvent,
            new SelectionChangedEventHandler(SettingsField_Changed),
            true);
        panel.AddHandler(
            System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler(SettingsField_Changed),
            true);
        panel.AddHandler(
            System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
            new RoutedEventHandler(SettingsField_Changed),
            true);
        panel.AddHandler(
            PasswordBox.PasswordChangedEvent,
            new RoutedEventHandler(SettingsField_Changed),
            true);
    }

    private void SettingsField_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsDirty || !_configurationLoaded)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (ReferenceEquals(source, LevelNameBox) &&
            _minecraftConfiguration is not null)
        {
            LevelSeedBox.IsEnabled =
                _minecraftConfiguration.CanChangeSeed ||
                !LevelNameBox.Text.Equals(
                    _minecraftConfiguration.LevelName,
                    StringComparison.OrdinalIgnoreCase);
        }

        if (source is not null && MemoryPanel.IsAncestorOf(source))
        {
            _memorySettingsDirty = true;
        }
        else if (source is not null && AutomationPanel.IsAncestorOf(source))
        {
            _automationSettingsDirty = true;
        }
        else
        {
            _gameSettingsDirty = true;
        }

        RenderSettingsState();
    }

    private async void ServerTabs_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, ServerTabs) || _changingSelectedTab)
        {
            return;
        }

        var requested = ServerTabs.SelectedItem as TabItem;
        var previous = _previousSelectedTab;
        if (previous == ServerSettingsTab &&
            requested != ServerSettingsTab &&
            HasUnsavedSettings())
        {
            _changingSelectedTab = true;
            ServerTabs.SelectedItem = previous;
            _changingSelectedTab = false;
            if (!await ConfirmNavigationAwayAsync())
            {
                return;
            }

            _changingSelectedTab = true;
            ServerTabs.SelectedItem = requested;
            _changingSelectedTab = false;
        }

        _previousSelectedTab = requested;
    }

    public async Task RefreshNowAsync() => await RefreshAsync(true);

    public bool HasUnsavedChanges => HasUnsavedSettings();

    public async Task<bool> ConfirmNavigationAwayAsync()
    {
        if (!HasUnsavedSettings())
        {
            return true;
        }

        var choice = MessageBox.Show(
            "You have unsaved changes.\n\nYes: save for the next restart\nNo: discard changes\nCancel: stay on Settings",
            "Unsaved Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (choice == MessageBoxResult.Yes &&
            !await SaveAllDirtySettingsAsync())
        {
            return false;
        }

        if (choice == MessageBoxResult.No)
        {
            DiscardAllSettingsChanges();
        }

        return true;
    }

    public void SelectTab(int index) =>
        ServerTabs.SelectedIndex = Math.Clamp(index, 0, ServerTabs.Items.Count - 1);

    public void Dispose()
    {
        _timer.Stop();
        PalworldWorldSettingsControl.Dispose();
        ConfigurationRestorePoints.Dispose();
        ServerDiagnosticsControl.Dispose();
        PalworldMemoryPerformanceControl.Dispose();
        PalworldSaveBackupControl.Dispose();
        _httpClient.Dispose();
    }

    private async Task RefreshAsync(bool force)
    {
        var window = Window.GetWindow(this);
        if (_refreshing ||
            (!force &&
             (!IsVisible ||
              window is null ||
              !window.IsVisible ||
              window.WindowState == WindowState.Minimized)))
        {
            return;
        }

        _refreshing = true;
        try
        {
            _dashboard = await _httpClient.GetFromJsonAsync<DashboardSnapshot>(
                "/api/v1/dashboard");
            _server = _dashboard?.Servers.FirstOrDefault(server => server.Game == Game);
            if (Game == GameType.Palworld && _server is not null)
            {
                await RefreshPalworldOverviewDetailsAsync(force);
            }
            RenderServer();
            if (_server is not null)
            {
                await RefreshLogsAsync();
                await RefreshBackupsAsync();
                if (!_configurationLoaded)
                {
                    if (Game == GameType.Minecraft)
                    {
                        await LoadMinecraftConfigurationAsync();
                    }
                    else
                    {
                        await LoadPalworldConfigurationAsync();
                    }
                }

                await RefreshPlayersAsync();
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            ConsoleStatusText.Text = $"Agent unavailable: {exception.Message}";
            if (Game == GameType.Palworld)
            {
                PalworldOverviewDashboard.SetState(
                    PalworldOverviewStateFactory.Unavailable(
                        _server?.Name ?? "Palworld",
                        LocalizationService.Get("Shell.AgentUnavailable"),
                        DateTimeOffset.UtcNow),
                    addHistorySample: false);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RenderServer()
    {
        NoServerPanel.Visibility =
            _server is null ? Visibility.Visible : Visibility.Collapsed;
        ServerTabs.Visibility =
            _server is null ? Visibility.Collapsed : Visibility.Visible;
        if (_server is null)
        {
            PalworldWorldSettingsControl.SetServer(null);
            ConfigurationRestorePoints.SetServer(null);
            ServerDiagnosticsControl.SetServer(null);
            PalworldMemoryPerformanceControl.SetServer(null);
            PalworldSaveBackupControl.SetServer(null);
            return;
        }

        ConfigurationRestorePoints.SetServer(_server.ServerId);
        ServerDiagnosticsControl.SetServer(_server.ServerId);
        if (Game == GameType.Palworld)
        {
            PalworldWorldSettingsControl.SetServer(_server.ServerId);
            PalworldMemoryPerformanceControl.SetServer(_server.ServerId);
            PalworldSaveBackupControl.SetServer(_server.ServerId);
        }

        ServerNameText.Text = _server.Name;
        StateText.Text = _server.State.ToString();
        AddressText.Text = _server.LocalAddress ?? $"Port {_server.Port}";
        NetworkAddressText.Text = AddressText.Text;
        InternetAddressText.Text =
            _server.InternetAddress ??
            "External access is not configured. Open Remote Access.";
        InternetNetworkAddressText.Text = InternetAddressText.Text;
        TunnelStatusText.Text =
            $"Playit: {_server.PlayitState ?? "Unavailable"} · " +
            (_server.PublicTunnelOnline
                ? "public tunnel online and verified"
                : _server.PublicTunnelVerified
                    ? "mapping verified; tunnel is not online"
                    : "public reachability is not verified");
        TunnelTargetText.Text = Game == GameType.Palworld
            ? $"Tunnel target: UDP → 127.0.0.1:{_server.Port}"
            : $"Tunnel target: TCP → 127.0.0.1:{_server.Port}";
        RestartPlayitButton.Visibility =
            _server.PlayitOnline ? Visibility.Collapsed : Visibility.Visible;
        HealthIndicatorsText.Text =
            $"Server Running: {StatusWord(_server.State == ServerState.Running)}  ·  " +
            $"Local Port Open: {StatusWord(_server.LocalPortOpen)}  ·  " +
            $"Playit Online: {StatusWord(_server.PlayitOnline)}  ·  " +
            $"Public Tunnel Online: {StatusWord(_server.PublicTunnelOnline)}  ·  " +
            $"REST Management Connected: {StatusWord(_server.RestManagementConnected)}";
        PlayitNetworkStatusText.Text = TunnelStatusText.Text;
        AddressRecommendationText.Text =
            $"Recommended inside home: {_server.LocalAddress ?? "local address unavailable"}\n" +
            $"Recommended outside home: {_server.InternetAddress ?? "external access not configured"}\n" +
            "When connecting through the public tunnel from the same home network, traffic may leave the network " +
            "and return through a distant Playit relay. Use the Local Address for devices inside the home network.";
        VersionText.Text =
            $"{_server.InstalledVersion ?? "unknown"} · {_server.RuntimeVersion ?? "runtime unknown"}";
        var displayedUptime = _server.PalworldManagement?.UptimeSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : _server.Uptime;
        PlayersText.Text =
            $"{_server.PlayersOnline?.ToString() ?? "—"}/" +
            $"{_server.MaximumPlayers?.ToString() ?? "—"} · " +
            FormatDuration(displayedUptime);
        ResourcesText.Text =
            $"Game PID {_server.GameProcessId?.ToString() ?? "—"} · " +
            $"{_server.CpuPercent:F1}% · {FormatMebibytes(_server.WorkingSetBytes)}";
        BackupText.Text = _server.LastBackupAtUtc is { } backup
            ? $"Last backup\n{backup.ToLocalTime():g}"
            : "Last backup\nNo backup yet";
        BackupText.ToolTip = BackupText.Text;
        UpdateStatusText.Text =
            $"Update status\n{_server.UpdateStatus ?? "Not checked"}";
        UpdateStatusText.ToolTip = UpdateStatusText.Text;
        DetailedResourcesText.Text =
            $"CPU {_server.CpuPercent:F1}%  |  Working set {FormatMebibytes(_server.WorkingSetBytes)}  |  " +
            $"Private {FormatMebibytes(_server.PrivateMemoryBytes)}  |  " +
            $"Peak {FormatMebibytes(_server.PeakWorkingSetBytes)}  |  " +
            $"Root {_server.RootExecutableName ?? "process"} PID {_server.ProcessId?.ToString() ?? "—"}  |  " +
            $"Game {_server.GameExecutableName ?? "process"} PID {_server.GameProcessId?.ToString() ?? "—"}  |  " +
            $"Children {_server.ChildProcessCount}  |  Uptime {FormatDuration(displayedUptime)}";
        DetailedResourcesText.Text +=
            $"  |  Threads {_server.ThreadCount}  |  Priority {_server.Priority}  |  " +
            $"Affinity {(_server.CpuAffinityMask?.ToString() ?? "All processors")}";
        var totalMemory = _dashboard?.TotalMemoryBytes ?? 0;
        ServerResourceGraph.AddSample(
            _server.CpuPercent,
            totalMemory <= 0 ? 0 : _server.WorkingSetBytes * 100d / totalMemory);
        if (Game == GameType.Palworld && _dashboard is not null)
        {
            PalworldOverviewDashboard.SetState(
                PalworldOverviewStateFactory.Create(
                    _server,
                    _dashboard,
                    _palworldMemoryPolicy,
                    GetSystemDriveTotalBytes(),
                    DateTimeOffset.UtcNow));
            PalworldOverviewDashboard.SetActivity(_palworldActivity);
        }
        StartButton.IsEnabled = _server.Actions.CanStart;
        StartButton.Visibility =
            _server.Actions.CanStart ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = _server.Actions.CanStop;
        StopButton.Visibility =
            _server.Actions.CanStop ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.IsEnabled = _server.Actions.CanRestart;
        ForceStopButton.IsEnabled = _server.Actions.CanForceStop;
        BackupButton.IsEnabled = _server.Actions.CanBackup;
        SendCommandButton.IsEnabled = _server.Actions.CanSendCommand;
        FailurePanel.Visibility =
            _server.State is ServerState.Error or ServerState.Crashed
                ? Visibility.Visible
                : Visibility.Collapsed;
        FailureText.Text = _server.LastError ?? _server.LastConsoleLine ?? string.Empty;
        ActiveMemoryText.Text = Game == GameType.Minecraft
            ? $"Active: Xms {_server.ActiveMinimumMemoryMb?.ToString() ?? "—"} MB, " +
              $"Xmx {_server.ActiveMaximumMemoryMb?.ToString() ?? "—"} MB · " +
              $"Pending: Xms {_server.PendingMinimumMemoryMb?.ToString() ?? "—"} MB, " +
              $"Xmx {_server.PendingMaximumMemoryMb?.ToString() ?? "—"} MB"
            : string.Empty;
        MemorySummaryText.Text = _dashboard is null
            ? string.Empty
            : $"System {_dashboard.TotalMemoryBytes / (double)(1024L * 1024 * 1024):F1} GB · " +
              $"Available {_dashboard.AvailableMemoryBytes / (double)(1024L * 1024 * 1024):F1} GB · " +
              $"Current process {FormatMebibytes(_server.WorkingSetBytes)} · " +
              $"Peak {FormatMebibytes(_server.PeakWorkingSetBytes)}";
        PalworldSettingsText.Text =
            $"Server: {_server.Name}\nPort: {_server.Port}\n" +
            $"Process RAM: {FormatMebibytes(_server.WorkingSetBytes)}\n" +
            $"Soft RAM warning: {FormatMebibytes((_dashboard?.TotalMemoryBytes ?? 0) * 3 / 4)}\n" +
            $"Priority: {_server.Priority}\n" +
            $"Affinity: {(_server.CpuAffinityMask?.ToString() ?? "All processors")}\n" +
            $"Password: {(_palworldConfiguration?.HasServerPassword == true ? "configured" : "not configured")}\n" +
            $"Admin password: {(_palworldConfiguration?.HasAdminPassword == true ? "configured" : "not configured")}";
        if (!_automationSettingsDirty)
        {
            _suppressSettingsDirty = true;
            try
            {
                AutoStartBox.IsChecked = _server.AutoStart;
                AutoRestartBox.IsChecked = _server.AutoRestart;
            }
            finally
            {
                _suppressSettingsDirty = false;
            }
        }
        RenderPalworldManagement();
    }

    private void RenderPalworldManagement()
    {
        if (_server is null || Game != GameType.Palworld)
        {
            return;
        }

        var management = _server.PalworldManagement;
        var managementMessage = management is null
            ? "Unavailable: live management data has not been collected yet."
            : $"{management.State}: {management.StatusMessage}";
        if (_palworldConfiguration is not null)
        {
            managementMessage +=
                $"\nLive INI: {(_palworldConfiguration.IniRestApiEnabled ? "enabled" : "disabled")} " +
                $"on 127.0.0.1:{_palworldConfiguration.IniRestApiPort}.";
            if (_palworldConfiguration.ManagementStateMismatch)
            {
                managementMessage +=
                    " Configuration mismatch detected; choose Repair Configuration.";
            }
        }

        PalworldManagementStatusText.Text = managementMessage;
        var configured =
            _palworldConfiguration?.IniRestApiEnabled == true ||
            management?.RestApiEnabled == true;
        var online = management?.State == PalworldManagementState.Online;
        EnablePalworldManagementButton.Visibility =
            configured ? Visibility.Collapsed : Visibility.Visible;
        RetryPalworldManagementButton.Visibility =
            configured ? Visibility.Visible : Visibility.Collapsed;
        TestPalworldManagementButton.Visibility =
            configured ? Visibility.Visible : Visibility.Collapsed;
        DisablePalworldManagementButton.Visibility =
            configured ? Visibility.Visible : Visibility.Collapsed;
        RepairPalworldManagementButton.Visibility =
            _palworldConfiguration?.ManagementStateMismatch == true ||
            configured && !online
                ? Visibility.Visible
                : Visibility.Collapsed;
        PalworldLivePlayersText.Text =
            $"{management?.PlayersOnline?.ToString() ?? "—"}/" +
            $"{management?.MaximumPlayers?.ToString() ?? "—"}";
        PalworldFpsText.Text = management?.ServerFps is { } fps
            ? $"{fps:F1} FPS"
            : "Unavailable";
        PalworldUptimeText.Text = management?.UptimeSeconds is { } uptime
            ? FormatDuration(TimeSpan.FromSeconds(uptime))
            : "Unavailable";
        PalworldProcessTreeText.Text =
            $"root {_server.RootExecutableName ?? "process"} " +
            $"{_server.ProcessId?.ToString() ?? "—"} / " +
            $"game {_server.GameExecutableName ?? "process"} " +
            $"{_server.GameProcessId?.ToString() ?? "—"} / " +
            $"{_server.ChildProcessCount} child process(es)";
        PalworldIdentityText.Text =
            management?.State == PalworldManagementState.Online
                ? $"Name: {management.ServerName ?? _server.Name}\n" +
                  $"Description: {management.Description ?? "No description"}\n" +
                  $"Version: {management.ServerVersion ?? _server.InstalledVersion ?? "unknown"} · " +
                  $"Frame time: {management.ServerFrameTimeMilliseconds?.ToString("F2") ?? "—"} ms"
                : "Server name, description, FPS, players, and official uptime are unavailable until the local management connection succeeds.";
        PalworldPlayersStatusText.Text = management?.State ==
                                          PalworldManagementState.Online
            ? $"{management.PlayersOnline ?? management.Players.Count} player(s) online · " +
              $"local {_server.LocalAddress ?? "unavailable"} · " +
              $"Internet {_server.InternetAddress ?? "not configured"}"
            : $"{management?.StatusMessage ?? "Player data unavailable."} " +
              $"Local: {_server.LocalAddress ?? "unavailable"}; " +
              $"Internet: {_server.InternetAddress ?? "not configured"}.";
        PalworldPlayersGrid.ItemsSource = management?.Players ?? [];
    }

    private async Task RefreshLogsAsync()
    {
        if (_server is null || PauseLogsBox.IsChecked == true)
        {
            return;
        }

        var entries = await _httpClient.GetFromJsonAsync<LogEntry[]>(
            $"/api/v1/servers/{_server.ServerId}/logs") ?? [];
        _logs.Clear();
        _logs.AddRange(entries.TakeLast(500));
        RenderLogs();
    }

    private void RenderLogs()
    {
        // Toggle-button events can fire while XAML is still constructing this
        // control, before the console fields declared later in the document exist.
        if (LogSearchBox is null ||
            LogLevelBox is null ||
            ShowTimestampsBox is null ||
            ConsoleList is null ||
            AutoScrollBox is null)
        {
            return;
        }

        var query = LogSearchBox.Text.Trim();
        var filter = (LogLevelBox.SelectedItem as ComboBoxItem)
            ?.Content
            ?.ToString();
        var showTimestamps = ShowTimestampsBox.IsChecked == true;
        ConsoleList.ItemsSource = _logs
            .Where(entry =>
                (_clearViewBeforeUtc is null ||
                 entry.TimestampUtc > _clearViewBeforeUtc) &&
                (filter switch
                {
                    "Errors" => entry.IsStandardError ||
                                entry.Level.Contains(
                                    "error",
                                    StringComparison.OrdinalIgnoreCase),
                    "Warnings" => entry.Level.Contains(
                        "warn",
                        StringComparison.OrdinalIgnoreCase),
                    "Info" => entry.Level.Contains(
                        "info",
                        StringComparison.OrdinalIgnoreCase),
                    _ => true
                }) &&
                (string.IsNullOrEmpty(query) ||
                 entry.Message.Contains(
                     query,
                     StringComparison.OrdinalIgnoreCase) ||
                 entry.Level.Contains(
                     query,
                     StringComparison.OrdinalIgnoreCase)))
            .Select(entry =>
                $"{(showTimestamps ? $"{entry.TimestampUtc.ToLocalTime():HH:mm:ss} " : string.Empty)}" +
                $"[{(entry.IsStandardError ? "stderr" : "stdout")}:{entry.Level}] {entry.Message}")
            .ToArray();
        if (AutoScrollBox.IsChecked == true && ConsoleList.Items.Count > 0)
        {
            ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
        }
    }

    private async Task LoadMinecraftConfigurationAsync()
    {
        if (_server is null)
        {
            return;
        }

        _minecraftConfiguration =
            await _httpClient.GetFromJsonAsync<MinecraftConfigurationResponse>(
                $"/api/v1/servers/{_server.ServerId}/minecraft/configuration");
        if (_minecraftConfiguration is null)
        {
            return;
        }

        ApplyMinecraftConfigurationToFields(_minecraftConfiguration);
        _configurationLoaded = true;
        _gameSettingsDirty = false;
        _memorySettingsDirty = false;
        _automationSettingsDirty = false;
        RenderSettingsState();
    }

    private void ApplyMinecraftConfigurationToFields(
        MinecraftConfigurationResponse configuration)
    {
        _suppressSettingsDirty = true;
        try
        {
            NameBox.Text = configuration.Name;
            PortBox.Text = configuration.Port.ToString();
            MaxPlayersBox.Text = configuration.MaxPlayers.ToString();
            JavaPathBox.Text = configuration.JavaExecutablePath ?? string.Empty;
            MotdBox.Text = configuration.Motd;
            ViewDistanceBox.Text = configuration.ViewDistance.ToString();
            SimulationDistanceBox.Text =
                configuration.SimulationDistance.ToString();
            SelectCombo(GameModeBox, configuration.GameMode);
            SelectCombo(DifficultyBox, configuration.Difficulty);
            OnlineModeBox.IsChecked = configuration.OnlineMode;
            WhitelistBox.IsChecked = configuration.WhitelistEnabled;
            HardcoreBox.IsChecked = configuration.Hardcore;
            PvpBox.IsChecked = configuration.Pvp;
            SpawnProtectionBox.Text =
                configuration.SpawnProtection.ToString();
            CommandBlocksBox.IsChecked =
                configuration.EnableCommandBlocks;
            AllowFlightBox.IsChecked = configuration.AllowFlight;
            SpawnAnimalsBox.IsChecked = configuration.SpawnAnimals;
            SpawnMonstersBox.IsChecked = configuration.SpawnMonsters;
            SpawnNpcsBox.IsChecked = configuration.SpawnNpcs;
            GenerateStructuresBox.IsChecked =
                configuration.GenerateStructures;
            LevelNameBox.Text = configuration.LevelName;
            LevelSeedBox.Text = configuration.LevelSeed;
            LevelSeedBox.IsEnabled = configuration.CanChangeSeed;
            LevelSeedHelpText.Text = configuration.CanChangeSeed
                ? "The seed can be changed because this world has not been created yet."
                : "Locked for this existing world. Choose a new level name to create a world with a different seed.";
            LevelTypeBox.Text = configuration.LevelType;
            MinecraftUnknownSettingsText.Text =
                configuration.UnknownPropertyKeys is { Count: > 0 } unknown
                    ? $"Preserved unsupported/newer properties ({unknown.Count}): {string.Join(", ", unknown)}"
                    : "No unsupported properties detected. Unknown keys are preserved automatically.";
            XmsBox.Text = configuration.MinimumMemoryMb.ToString();
            XmxBox.Text = configuration.MaximumMemoryMb.ToString();
            AutoStartBox.IsChecked = configuration.AutoStart;
            AutoRestartBox.IsChecked = configuration.AutoRestart;
        }
        finally
        {
            _suppressSettingsDirty = false;
        }
    }

    private void ApplyPalworldConfigurationToFields(
        PalworldConfigurationResponse configuration)
    {
        _suppressSettingsDirty = true;
        try
        {
            PalworldNameBox.Text = configuration.ServerName;
            PalworldDescriptionBox.Text = configuration.Description;
            PalworldPortBox.Text = configuration.Port.ToString();
            PalworldMaxPlayersBox.Text = configuration.MaxPlayers.ToString();
            PalworldRconPortBox.Text = configuration.RconPort.ToString();
            PalworldCommunityBox.IsChecked = configuration.CommunityServer;
            PalworldRconBox.IsChecked = configuration.RconEnabled;
            AutoStartBox.IsChecked = configuration.AutoStart;
            AutoRestartBox.IsChecked = configuration.AutoRestart;
            PalworldPasswordBox.Clear();
            PalworldAdminPasswordBox.Clear();
        }
        finally
        {
            _suppressSettingsDirty = false;
        }
    }

    private bool HasUnsavedSettings() =>
        _gameSettingsDirty ||
        _memorySettingsDirty ||
        _automationSettingsDirty ||
        PalworldWorldSettingsControl.HasUnsavedChanges;

    private void RenderSettingsState()
    {
        var unsaved = new List<string>();
        if (_gameSettingsDirty)
        {
            unsaved.Add("game");
        }

        if (_memorySettingsDirty)
        {
            unsaved.Add("memory");
        }

        if (_automationSettingsDirty)
        {
            unsaved.Add("automation");
        }

        if (PalworldWorldSettingsControl.HasUnsavedChanges)
        {
            unsaved.Add("world");
        }

        var active = _configurationLoaded ? "loaded" : "loading";
        var pending = _hasPendingSettings
            ? "saved; restart required"
            : "none recorded";
        var dirty = unsaved.Count == 0
            ? "none"
            : string.Join(", ", unsaved);
        ConfigurationStateText.Text =
            $"Active settings: {active} · Pending settings: {pending} · Unsaved changes: {dirty}";
        ServerSettingsTab.Header = unsaved.Count == 0
            ? "Settings"
            : "Settings ●";
    }

    private async Task<bool> SaveAllDirtySettingsAsync()
    {
        if (_gameSettingsDirty)
        {
            var saved = Game == GameType.Minecraft
                ? await SaveMinecraftSettingsAsync(false)
                : await SavePalworldSettingsAsync(false);
            if (!saved)
            {
                return false;
            }
        }

        if (_memorySettingsDirty && !await SaveMemoryAsync(false))
        {
            return false;
        }

        if (PalworldWorldSettingsControl.HasUnsavedChanges &&
            !await PalworldWorldSettingsControl.SaveForNextRestartAsync())
        {
            return false;
        }

        if (_automationSettingsDirty && !await SaveAutomationAsync())
        {
            return false;
        }

        return true;
    }

    private void DiscardAllSettingsChanges()
    {
        if (Game == GameType.Minecraft && _minecraftConfiguration is not null)
        {
            ApplyMinecraftConfigurationToFields(_minecraftConfiguration);
        }
        else if (_palworldConfiguration is not null)
        {
            ApplyPalworldConfigurationToFields(_palworldConfiguration);
        }

        PalworldWorldSettingsControl.DiscardChanges();
        _gameSettingsDirty = false;
        _memorySettingsDirty = false;
        _automationSettingsDirty = false;
        SettingsStatusText.Text = "Unsaved changes discarded.";
        RenderSettingsState();
    }

    private async Task RefreshPlayersAsync()
    {
        if (_server is null)
        {
            return;
        }

        if (Game == GameType.Palworld)
        {
            var management =
                await _httpClient.GetFromJsonAsync<PalworldManagementSnapshot>(
                    $"/api/v1/servers/{_server.ServerId}/palworld/management");
            if (management is not null)
            {
                _server = _server with { PalworldManagement = management };
                RenderPalworldManagement();
            }

            return;
        }

        var players = await _httpClient.GetFromJsonAsync<MinecraftPlayersSnapshot>(
            $"/api/v1/servers/{_server.ServerId}/minecraft/players");
        if (players is null)
        {
            return;
        }

        OnlinePlayersList.ItemsSource = players.OnlinePlayers;
        WhitelistPlayersList.ItemsSource = players.WhitelistedPlayers;
        OperatorsList.ItemsSource = players.Operators;
        BannedPlayersList.ItemsSource = players.BannedPlayers;
        BannedIpsList.ItemsSource = players.BannedIps;
    }

    private async void EnablePalworldManagement_Click(
        object sender,
        RoutedEventArgs e) =>
        await ActivatePalworldManagementAsync(
            "enable",
            "Enable Palworld's official localhost-only management API?",
            "Confirm Palworld Management");

    private async void RepairPalworldManagement_Click(
        object sender,
        RoutedEventArgs e) =>
        await ActivatePalworldManagementAsync(
            "repair",
            "Repair the live PalWorldSettings.ini management configuration and verify the localhost REST connection?",
            "Repair Palworld Management");

    private async Task ActivatePalworldManagementAsync(
        string action,
        string prompt,
        string title)
    {
        if (_server is null)
        {
            return;
        }

        var runningNote = _server.State == ServerState.Running
            ? "The Palworld server will restart. Connected players will be disconnected."
            : "The setting will be saved and verified the next time Palworld starts.";
        if (MessageBox.Show(
                $"{prompt}\n\n" +
                $"{runningNote}\n\n" +
                "The admin password remains protected by Windows and is never displayed.",
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SetManagementButtonsEnabled(false);
        PalworldManagementStatusText.Text =
            "Creating a safety copy, updating the live INI, restarting if needed, and verifying localhost REST...";
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/servers/{_server.ServerId}/palworld/management/{action}",
                new PalworldRestEnableRequest(true));
            var result =
                await response.Content.ReadFromJsonAsync<PalworldRestEnableResponse>();
            PalworldManagementStatusText.Text = result is null
                ? await ReadErrorAsync(response)
                : $"{result.Stage}: {result.Message}" +
                  (result.RolledBack ? "\nThe previous configuration was restored." : string.Empty) +
                  (string.IsNullOrWhiteSpace(result.ErrorCode)
                      ? string.Empty
                      : $"\nCode: {result.ErrorCode}");
            if (result is not null)
            {
                _server = _server with { PalworldManagement = result.Status };
            }

            await RefreshAsync(true);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            PalworldManagementStatusText.Text =
                $"Management activation failed: {exception.Message}";
        }
        finally
        {
            SetManagementButtonsEnabled(true);
        }
    }

    private async void RetryPalworldManagement_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_server is null)
        {
            return;
        }

        SetManagementButtonsEnabled(false);
        PalworldManagementStatusText.Text =
            "Retrying the localhost REST connection...";
        try
        {
            using var response = await _httpClient.PostAsync(
                $"/api/v1/servers/{_server.ServerId}/palworld/management/retry",
                null);
            var result =
                await response.Content.ReadFromJsonAsync<PalworldManagementSnapshot>();
            if (result is not null)
            {
                _server = _server with { PalworldManagement = result };
                RenderPalworldManagement();
            }
            else
            {
                PalworldManagementStatusText.Text = await ReadErrorAsync(response);
            }
        }
        finally
        {
            SetManagementButtonsEnabled(true);
        }
    }

    private async void TestPalworldManagement_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_server is null)
        {
            return;
        }

        SetManagementButtonsEnabled(false);
        try
        {
            using var response = await _httpClient.PostAsync(
                $"/api/v1/servers/{_server.ServerId}/palworld/management/test",
                null);
            var result =
                await response.Content.ReadFromJsonAsync<PalworldRestOperationResult>();
            PalworldManagementStatusText.Text = result is null
                ? await ReadErrorAsync(response)
                : $"{result.Code}: {result.Message}";
        }
        finally
        {
            SetManagementButtonsEnabled(true);
        }
    }

    private async void DisablePalworldManagement_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_server is null ||
            MessageBox.Show(
                "Disable Palworld local management? The server may restart and live players will be disconnected.",
                "Disable Palworld Management",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SetManagementButtonsEnabled(false);
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/servers/{_server.ServerId}/palworld/management/disable",
                new PalworldManagementActionRequest(true));
            var result =
                await response.Content.ReadFromJsonAsync<PalworldRestEnableResponse>();
            PalworldManagementStatusText.Text = result is null
                ? await ReadErrorAsync(response)
                : $"{result.Stage}: {result.Message}";
            if (result is not null)
            {
                _server = _server with { PalworldManagement = result.Status };
            }

            await RefreshAsync(true);
        }
        finally
        {
            SetManagementButtonsEnabled(true);
        }
    }

    private void SetManagementButtonsEnabled(bool enabled)
    {
        EnablePalworldManagementButton.IsEnabled = enabled;
        RetryPalworldManagementButton.IsEnabled = enabled;
        RepairPalworldManagementButton.IsEnabled = enabled;
        TestPalworldManagementButton.IsEnabled = enabled;
        DisablePalworldManagementButton.IsEnabled = enabled;
    }

    private async Task LoadPalworldConfigurationAsync()
    {
        if (_server is null)
        {
            return;
        }

        _palworldConfiguration =
            await _httpClient.GetFromJsonAsync<PalworldConfigurationResponse>(
                $"/api/v1/servers/{_server.ServerId}/palworld/configuration");
        if (_palworldConfiguration is null)
        {
            return;
        }

        ApplyPalworldConfigurationToFields(_palworldConfiguration);
        _configurationLoaded = true;
        _gameSettingsDirty = false;
        _automationSettingsDirty = false;
        RenderSettingsState();
        RenderServer();
    }

    private async Task RefreshBackupsAsync()
    {
        if (_server is null)
        {
            return;
        }

        BackupsList.ItemsSource = await _httpClient.GetFromJsonAsync<BackupRecord[]>(
            $"/api/v1/servers/{_server.ServerId}/backups") ?? [];
    }

    private async Task RefreshPalworldOverviewDetailsAsync(bool force)
    {
        if (_server is null ||
            (!force &&
             DateTimeOffset.UtcNow - _lastPalworldOverviewDetailRefreshUtc <
             TimeSpan.FromSeconds(10)))
        {
            return;
        }

        _lastPalworldOverviewDetailRefreshUtc = DateTimeOffset.UtcNow;
        try
        {
            _palworldMemoryPolicy =
                await _httpClient.GetFromJsonAsync<MemoryPerformancePolicySnapshot>(
                    "/api/v1/resources/memory-performance");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            _palworldMemoryPolicy = null;
        }

        try
        {
            using var response = await _httpClient.GetAsync(
                $"/api/v1/servers/{_server.ServerId}/activity?limit=" +
                BoundedServerActivityFeed.MaximumItems);
            _palworldActivity = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<ServerActivityItem[]>() ?? []
                : [];
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            _palworldActivity = [];
        }
    }

    private static long GetSystemDriveTotalBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            return string.IsNullOrWhiteSpace(root)
                ? 0
                : new DriveInfo(root).TotalSize;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private async Task SavePalworldWorldAsync()
    {
        if (_server is null)
        {
            return;
        }

        PalworldOverviewDashboard.SetBusy(true);
        try
        {
            using var response = await _httpClient.PostAsync(
                $"/api/v1/servers/{_server.ServerId}/palworld/save",
                null);
            var result = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<PalworldRestOperationResult>()
                : null;
            PalworldOverviewDashboard.SetOperationStatus(
                result?.Message ?? await ReadErrorAsync(response),
                !response.IsSuccessStatusCode || result?.Success == false);
            await RefreshAsync(true);
        }
        finally
        {
            PalworldOverviewDashboard.SetBusy(false);
        }
    }

    private async Task AnnounceToPalworldAsync(string message)
    {
        if (_server is null)
        {
            return;
        }

        PalworldOverviewDashboard.SetBusy(true);
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/servers/{_server.ServerId}/palworld/announcement",
                new PalworldAnnouncementRequest(message));
            var result = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<PalworldRestOperationResult>()
                : null;
            PalworldOverviewDashboard.SetOperationStatus(
                result?.Message ?? await ReadErrorAsync(response),
                !response.IsSuccessStatusCode || result?.Success == false);
            await RefreshAsync(true);
        }
        finally
        {
            PalworldOverviewDashboard.SetBusy(false);
        }
    }

    private async Task PostActionAsync(string action, object? body = null)
    {
        if (_server is null)
        {
            return;
        }

        using var response = body is null
            ? await _httpClient.PostAsync(
                $"/api/v1/servers/{_server.ServerId}/{action}",
                null)
            : await _httpClient.PostAsJsonAsync(
                $"/api/v1/servers/{_server.ServerId}/{action}",
                body);
        ConsoleStatusText.Text = response.IsSuccessStatusCode
            ? "Operation accepted."
            : await ReadErrorAsync(response);
        if (Game == GameType.Palworld)
        {
            PalworldOverviewDashboard.SetOperationStatus(
                ConsoleStatusText.Text,
                !response.IsSuccessStatusCode);
        }
        await RefreshAsync(true);
    }

    private async void Start_Click(object sender, RoutedEventArgs e) =>
        await PostActionAsync("start");

    private void MoreActions_Click(object sender, RoutedEventArgs e)
    {
        if (MoreActionsButton.ContextMenu is null)
        {
            return;
        }

        MoreActionsButton.ContextMenu.PlacementTarget = MoreActionsButton;
        MoreActionsButton.ContextMenu.IsOpen = true;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) =>
        await PostActionAsync("stop", new ServerStopRequest());

    private async void Restart_Click(object sender, RoutedEventArgs e) =>
        await PostActionAsync("restart");

    private async void ForceStop_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Force stop can lose unsaved server data. Use it only after graceful stop fails.",
                "Confirm Force Stop",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await PostActionAsync("stop", new ServerStopRequest(true));
        }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e) =>
        await PostActionAsync("backups");

    private void Create_Click(object sender, RoutedEventArgs e) =>
        CreateRequested?.Invoke(this, Game);

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(true);

    private void OpenRemoteAccess_Click(object sender, RoutedEventArgs e) =>
        RemoteAccessRequested?.Invoke(this, EventArgs.Empty);

    private async void RestartPlayit_Click(object sender, RoutedEventArgs e)
    {
        RestartPlayitButton.IsEnabled = false;
        try
        {
            using var response = await _httpClient.PostAsync(
                "/api/v1/playit/restart",
                null);
            NetworkStatusText.Text = response.IsSuccessStatusCode
                ? "Playit restart requested."
                : await ReadErrorAsync(response);
            await RefreshAsync(true);
        }
        finally
        {
            RestartPlayitButton.IsEnabled = true;
        }
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e) => CopyLocalAddress();

    private void CopyLocalAddress()
    {
        if (!string.IsNullOrWhiteSpace(_server?.LocalAddress))
        {
            NetworkStatusText.Text =
                ServerManager.Client.Shell.SafeClipboard.TrySetText(_server.LocalAddress)
                    ? "Address copied."
                    : "The clipboard is busy. Try again.";
            PalworldOverviewDashboard.SetOperationStatus(NetworkStatusText.Text);
        }
    }

    private void CopyInternetAddress_Click(object sender, RoutedEventArgs e) => CopyInternetAddress();

    private void CopyInternetAddress()
    {
        if (!string.IsNullOrWhiteSpace(_server?.InternetAddress))
        {
            NetworkStatusText.Text =
                ServerManager.Client.Shell.SafeClipboard.TrySetText(
                    _server.InternetAddress)
                    ? "Internet address copied."
                    : "The clipboard is busy. Try again.";
            PalworldOverviewDashboard.SetOperationStatus(NetworkStatusText.Text);
        }
        else
        {
            NetworkStatusText.Text =
                "No Internet address is configured for this server.";
            PalworldOverviewDashboard.SetOperationStatus(NetworkStatusText.Text, true);
        }
    }

    private void CopyIp_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_dashboard?.LocalIpv4))
        {
            NetworkStatusText.Text =
                ServerManager.Client.Shell.SafeClipboard.TrySetText(_dashboard.LocalIpv4)
                    ? "IP address copied."
                    : "The clipboard is busy. Try again.";
        }
    }

    private void CopyPort_Click(object sender, RoutedEventArgs e)
    {
        if (_server is not null)
        {
            NetworkStatusText.Text =
                ServerManager.Client.Shell.SafeClipboard.TrySetText(
                    _server.Port.ToString())
                    ? "Port copied."
                    : "The clipboard is busy. Try again.";
        }
    }

    private async void TestPort_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null)
        {
            return;
        }

        var protocol = Game == GameType.Palworld ? "UDP" : "TCP";
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/v1/network/test-port",
            new PortTestRequest(_server.Port, protocol));
        var result = await response.Content.ReadFromJsonAsync<PortTestResponse>();
        NetworkStatusText.Text = _server.State == ServerState.Running &&
                                 result?.IsAvailable == false
            ? $"{protocol} port {_server.Port} is bound by the running server."
            : result?.Message ?? response.ReasonPhrase;
    }

    private async void SendCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null || string.IsNullOrWhiteSpace(CommandBox.Text))
        {
            return;
        }

        var command = CommandBox.Text.Trim();
        if (IsDestructiveCommand(command) &&
            MessageBox.Show(
                $"Send the destructive command '{command}'?",
                "Confirm Console Command",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _commandHistory.Add(command);
        if (_commandHistory.Count > 100)
        {
            _commandHistory.RemoveAt(0);
        }
        _historyIndex = _commandHistory.Count;
        await PostActionAsync("console", new ConsoleCommandRequest(command));
        CommandBox.Clear();
    }

    private void CommandBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendCommand_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && _commandHistory.Count > 0)
        {
            _historyIndex = Math.Max(0, _historyIndex - 1);
            CommandBox.Text = _commandHistory[_historyIndex];
            CommandBox.CaretIndex = CommandBox.Text.Length;
        }
        else if (e.Key == Key.Down && _commandHistory.Count > 0)
        {
            _historyIndex = Math.Min(_commandHistory.Count, _historyIndex + 1);
            CommandBox.Text = _historyIndex == _commandHistory.Count
                ? string.Empty
                : _commandHistory[_historyIndex];
        }
    }

    private void LogSearch_TextChanged(object sender, TextChangedEventArgs e) => RenderLogs();

    private void ConsoleView_Changed(object sender, RoutedEventArgs e) =>
        RenderLogs();

    private void FavoriteCommand_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (FavoriteCommandBox.SelectedItem is ComboBoxItem
            {
                Tag: string command
            } &&
            !string.IsNullOrWhiteSpace(command))
        {
            CommandBox.Text = command;
            CommandBox.CaretIndex = command.Length;
            CommandBox.Focus();
        }
    }

    private void CopySelectedLogs_Click(object sender, RoutedEventArgs e)
    {
        if (ConsoleList.SelectedItems.Count > 0)
        {
            _ = ServerManager.Client.Shell.SafeClipboard.TrySetText(string.Join(
                Environment.NewLine,
                ConsoleList.SelectedItems.Cast<string>()));
        }
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (ConsoleList.Items.Count > 0)
        {
            ServerManager.Client.Shell.SafeClipboard.TrySetText(string.Join(
                Environment.NewLine,
                ConsoleList.Items.Cast<string>()));
        }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        _clearViewBeforeUtc = DateTimeOffset.UtcNow;
        RenderLogs();
        ConsoleStatusText.Text =
            "The view was cleared. Agent log history was not deleted.";
    }

    private static bool IsDestructiveCommand(string command)
    {
        var verb = command.Split(' ', 2)[0];
        return verb.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("ban", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("ban-ip", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("kick", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("whitelist", StringComparison.OrdinalIgnoreCase) &&
               command.Contains(" off", StringComparison.OrdinalIgnoreCase);
    }

    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"{Game}-console-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = "Log files|*.log|Text files|*.txt"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            File.WriteAllLines(dialog.FileName, ConsoleList.Items.Cast<string>());
            ConsoleStatusText.Text = $"Exported {dialog.FileName}";
        }
    }

    private async void SaveMemory_Click(object sender, RoutedEventArgs e) =>
        _ = await SaveMemoryAsync(sender is Button { Tag: "restart" });

    private async Task<bool> SaveMemoryAsync(bool restart)
    {
        ClearFieldError(XmsBox, XmsErrorText);
        ClearFieldError(XmxBox, XmxErrorText);
        if (_server is null ||
            !int.TryParse(XmsBox.Text, out var xms) ||
            !int.TryParse(XmxBox.Text, out var xmx))
        {
            if (!int.TryParse(XmsBox.Text, out _))
            {
                SetFieldError(
                    XmsBox,
                    XmsErrorText,
                    "Enter Xms as whole megabytes, for example 2048.");
            }

            if (!int.TryParse(XmxBox.Text, out _))
            {
                SetFieldError(
                    XmxBox,
                    XmxErrorText,
                    "Enter Xmx as whole megabytes, for example 4096.");
            }

            SettingsStatusText.Text = "Enter valid Xms and Xmx values in MB.";
            return false;
        }

        if (xms < 256 || xmx < xms)
        {
            SetFieldError(
                XmsBox,
                XmsErrorText,
                xms < 256
                    ? "Use at least 256 MB."
                    : "Xms cannot exceed Xmx.");
            SetFieldError(
                XmxBox,
                XmxErrorText,
                xmx < xms
                    ? "Xmx must be greater than or equal to Xms."
                    : null);
            SettingsStatusText.Text =
                "Correct the highlighted Minecraft memory values.";
            return false;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/servers/{_server.ServerId}/minecraft/memory",
            new MinecraftMemoryUpdateRequest(xms, xmx, restart));
        var result = await response.Content.ReadFromJsonAsync<OperationResult>();
        var succeeded = response.IsSuccessStatusCode && result?.Success == true;
        SettingsStatusText.Text = succeeded
            ? restart
                ? "Memory saved, the real Java process restarted, and startup was verified."
                : "Memory saved for the next restart. Active Java memory is unchanged."
            : result?.Message ?? await ReadErrorAsync(response);
        PublishSettingsNotification(
            succeeded,
            "Minecraft memory",
            SettingsStatusText.Text);
        if (!succeeded)
        {
            return false;
        }

        _memorySettingsDirty = false;
        _hasPendingSettings = !restart;
        RenderSettingsState();
        await RefreshAsync(true);
        return true;
    }

    private void MemoryPreset_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        var preset = (MemoryPresetBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        (var xms, var xmx) = preset switch
        {
            "Light" => (1024, 3072),
            "Balanced" => (2048, 4096),
            "Performance" => (4096, 8192),
            _ => (0, 0)
        };
        if (xms > 0)
        {
            XmsBox.Text = xms.ToString();
            XmxBox.Text = xmx.ToString();
        }
    }

    private async void SaveMinecraftSettings_Click(
        object sender,
        RoutedEventArgs e) =>
        _ = await SaveMinecraftSettingsAsync(
            sender is Button { Tag: "restart" });

    private async Task<bool> SaveMinecraftSettingsAsync(bool restart)
    {
        ClearMinecraftFieldErrors();
        if (_server is null ||
            !int.TryParse(PortBox.Text, out var port) ||
            !int.TryParse(MaxPlayersBox.Text, out var maxPlayers) ||
            !int.TryParse(ViewDistanceBox.Text, out var viewDistance) ||
            !int.TryParse(SimulationDistanceBox.Text, out var simulationDistance) ||
            !int.TryParse(SpawnProtectionBox.Text, out var spawnProtection))
        {
            if (!int.TryParse(PortBox.Text, out _))
            {
                SetFieldError(
                    PortBox,
                    PortErrorText,
                    "Enter a whole-number port from 1 to 65535.");
            }

            if (!int.TryParse(MaxPlayersBox.Text, out _))
            {
                SetFieldError(
                    MaxPlayersBox,
                    MaxPlayersErrorText,
                    "Enter a whole-number player limit.");
            }

            SettingsStatusText.Text = "Enter valid numeric Minecraft settings.";
            return false;
        }

        var valid = true;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            SetFieldError(
                NameBox,
                NameErrorText,
                "Enter a server name.");
            valid = false;
        }

        if (port is < 1 or > 65_535)
        {
            SetFieldError(
                PortBox,
                PortErrorText,
                "Use a port from 1 to 65535.");
            valid = false;
        }

        if (maxPlayers is < 1 or > 1_000)
        {
            SetFieldError(
                MaxPlayersBox,
                MaxPlayersErrorText,
                "Use 1 to 1000 players.");
            valid = false;
        }

        if (!string.IsNullOrWhiteSpace(JavaPathBox.Text) &&
            !File.Exists(JavaPathBox.Text))
        {
            SetFieldError(
                JavaPathBox,
                JavaPathErrorText,
                "Choose an existing java.exe file.");
            valid = false;
        }

        if (!valid)
        {
            SettingsStatusText.Text =
                "Correct the highlighted Minecraft settings.";
            return false;
        }

        var request = new ServerSettingsUpdateRequest(
            NameBox.Text,
            port,
            maxPlayers,
            MotdBox.Text,
            SelectedCombo(GameModeBox, "survival"),
            SelectedCombo(DifficultyBox, "normal"),
            WhitelistBox.IsChecked == true,
            viewDistance,
            simulationDistance,
            OnlineModeBox.IsChecked == true,
            HardcoreBox.IsChecked == true,
            PvpBox.IsChecked == true,
            JavaPathBox.Text,
            ApplyAndRestart: restart,
            SpawnProtection: spawnProtection,
            EnableCommandBlocks: CommandBlocksBox.IsChecked == true,
            AllowFlight: AllowFlightBox.IsChecked == true,
            SpawnAnimals: SpawnAnimalsBox.IsChecked == true,
            SpawnMonsters: SpawnMonstersBox.IsChecked == true,
            SpawnNpcs: SpawnNpcsBox.IsChecked == true,
            LevelName: LevelNameBox.Text,
            LevelSeed: LevelSeedBox.Text,
            LevelType: LevelTypeBox.Text,
            GenerateStructures: GenerateStructuresBox.IsChecked == true);
        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/servers/{_server.ServerId}/minecraft/settings",
            request);
        var result = await response.Content.ReadFromJsonAsync<OperationResult>();
        var succeeded = response.IsSuccessStatusCode && result?.Success == true;
        SettingsStatusText.Text = succeeded
            ? restart
                ? "Minecraft settings saved, restarted, and startup verified."
                : "Minecraft settings saved for the next restart."
            : result?.Message ?? await ReadErrorAsync(response);
        PublishSettingsNotification(
            succeeded,
            "Minecraft settings",
            SettingsStatusText.Text);
        if (!succeeded)
        {
            return false;
        }

        _gameSettingsDirty = false;
        _hasPendingSettings = !restart;
        RenderSettingsState();
        await RefreshAsync(true);
        return true;
    }

    private async void SavePalworldSettings_Click(
        object sender,
        RoutedEventArgs e) =>
        _ = await SavePalworldSettingsAsync(
            sender is Button { Tag: "restart" });

    private async Task<bool> SavePalworldSettingsAsync(bool restart)
    {
        ClearPalworldFieldErrors();
        if (_server is null ||
            !int.TryParse(PalworldPortBox.Text, out var port) ||
            !int.TryParse(PalworldMaxPlayersBox.Text, out var maxPlayers) ||
            !int.TryParse(PalworldRconPortBox.Text, out var rconPort))
        {
            if (!int.TryParse(PalworldPortBox.Text, out _))
            {
                SetFieldError(
                    PalworldPortBox,
                    PalworldPortErrorText,
                    "Enter a whole-number port from 1 to 65535.");
            }

            if (!int.TryParse(PalworldMaxPlayersBox.Text, out _))
            {
                SetFieldError(
                    PalworldMaxPlayersBox,
                    PalworldPlayersErrorText,
                    "Enter a whole-number player limit.");
            }

            SettingsStatusText.Text = "Enter valid Palworld port and player values.";
            return false;
        }

        var valid = true;
        if (string.IsNullOrWhiteSpace(PalworldNameBox.Text))
        {
            SetFieldError(
                PalworldNameBox,
                PalworldNameErrorText,
                "Enter a server name.");
            valid = false;
        }

        if (port is < 1 or > 65_535)
        {
            SetFieldError(
                PalworldPortBox,
                PalworldPortErrorText,
                "Use a port from 1 to 65535.");
            valid = false;
        }

        if (maxPlayers is < 1 or > 32)
        {
            SetFieldError(
                PalworldMaxPlayersBox,
                PalworldPlayersErrorText,
                "Official Palworld servers support 1 to 32 players.");
            valid = false;
        }

        if (!valid)
        {
            SettingsStatusText.Text =
                "Correct the highlighted Palworld settings.";
            return false;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/servers/{_server.ServerId}/palworld/settings",
            new PalworldSettingsUpdateRequest(
                PalworldNameBox.Text,
                PalworldDescriptionBox.Text,
                maxPlayers,
                port,
                PalworldCommunityBox.IsChecked == true,
                PalworldRconBox.IsChecked == true,
                rconPort,
                PalworldPasswordBox.Password,
                PalworldAdminPasswordBox.Password,
                ApplyAndRestart: restart));
        var result = await response.Content.ReadFromJsonAsync<OperationResult>();
        var succeeded = response.IsSuccessStatusCode && result?.Success == true;
        SettingsStatusText.Text = succeeded
            ? restart
                ? "Palworld settings saved, the real game process restarted, and startup was verified. Passwords remain protected."
                : "Palworld settings saved for the next restart. Passwords remain protected."
            : result?.Message ?? await ReadErrorAsync(response);
        PublishSettingsNotification(
            succeeded,
            "Palworld settings",
            SettingsStatusText.Text);
        if (!succeeded)
        {
            return false;
        }

        _gameSettingsDirty = false;
        _hasPendingSettings = !restart;
        PalworldPasswordBox.Clear();
        PalworldAdminPasswordBox.Clear();
        RenderSettingsState();
        await RefreshAsync(true);
        return true;
    }

    private async void SaveAutomation_Click(object sender, RoutedEventArgs e) =>
        _ = await SaveAutomationAsync();

    private async Task<bool> SaveAutomationAsync()
    {
        if (_server is null)
        {
            return false;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/servers/{_server.ServerId}/automation",
            new ServerAutomationUpdateRequest(
                AutoStartBox.IsChecked == true,
                AutoRestartBox.IsChecked == true));
        var result = await response.Content.ReadFromJsonAsync<OperationResult>();
        var succeeded = response.IsSuccessStatusCode && result?.Success == true;
        SettingsStatusText.Text = succeeded
            ? "Server automation saved. Dashboard startup remains a separate option."
            : result?.Message ?? await ReadErrorAsync(response);
        PublishSettingsNotification(
            succeeded,
            "Server automation",
            SettingsStatusText.Text);
        if (succeeded)
        {
            _automationSettingsDirty = false;
            RenderSettingsState();
            await RefreshAsync(true);
        }

        return succeeded;
    }

    private static void PublishSettingsNotification(
        bool succeeded,
        string title,
        string message) =>
        NotificationService.Publish(
            succeeded ? NotificationKind.Success : NotificationKind.Error,
            title,
            message,
            persistent: !succeeded);

    private void SetFieldError(
        System.Windows.Controls.Control field,
        TextBlock messageTarget,
        string? message)
    {
        messageTarget.Text = message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            field.ClearValue(
                System.Windows.Controls.Control.BorderBrushProperty);
            field.ToolTip = null;
        }
        else
        {
            field.BorderBrush =
                (System.Windows.Media.Brush)FindResource("DangerBrush");
            field.ToolTip = message;
        }
    }

    private void ClearFieldError(
        System.Windows.Controls.Control field,
        TextBlock messageTarget) =>
        SetFieldError(field, messageTarget, null);

    private void ClearMinecraftFieldErrors()
    {
        ClearFieldError(NameBox, NameErrorText);
        ClearFieldError(PortBox, PortErrorText);
        ClearFieldError(MaxPlayersBox, MaxPlayersErrorText);
        ClearFieldError(JavaPathBox, JavaPathErrorText);
    }

    private void ClearPalworldFieldErrors()
    {
        ClearFieldError(
            PalworldNameBox,
            PalworldNameErrorText);
        ClearFieldError(
            PalworldPortBox,
            PalworldPortErrorText);
        ClearFieldError(
            PalworldMaxPlayersBox,
            PalworldPlayersErrorText);
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsList.SelectedItem is not BackupRecord backup ||
            MessageBox.Show(
                "Restore the selected verified backup? A safety backup is created first.",
                "Confirm Restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        using var response = await _httpClient.PostAsync(
            $"/api/v1/backups/{backup.Id}/restore",
            null);
        ConsoleStatusText.Text = response.IsSuccessStatusCode
            ? "Restore completed."
            : await ReadErrorAsync(response);
        await RefreshAsync(true);
    }

    private async void VerifyBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsList.SelectedItem is not BackupRecord backup)
        {
            return;
        }

        using var response = await _httpClient.PostAsync(
            $"/api/v1/backups/{backup.Id}/verify",
            null);
        var result = await response.Content.ReadFromJsonAsync<BackupVerificationResult>();
        ConsoleStatusText.Text = result?.IsValid == true
            ? "Backup manifest and hashes are valid."
            : $"Backup verification failed: {result?.Error}";
    }

    private async void SaveBackupSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null)
        {
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/v1/backup-schedules",
            new BackupScheduleRequest(
                _server.ServerId,
                BackupScheduleBox.Text,
                true));
        ConsoleStatusText.Text = response.IsSuccessStatusCode
            ? "Backup schedule saved."
            : await ReadErrorAsync(response);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null)
        {
            return;
        }

        var update = await _httpClient.GetFromJsonAsync<UpdateCheckResult>(
            $"/api/v1/servers/{_server.ServerId}/updates");
        UpdateText.Text = update is null
            ? "Update status is unavailable."
            : $"Installed: {update.InstalledVersion ?? "unknown"}\n" +
              $"Latest: {update.LatestVersion ?? "unknown"}\n" +
              (update.IsUpdateAvailable ? "Update available" : "Up to date");
        UpdateGameNowButton.IsEnabled = update?.IsUpdateAvailable == true;
        UpdateGameNowButton.ToolTip = update?.IsUpdateAvailable == true
            ? "Create a safety backup and install the available game update."
            : "No game update is currently available.";
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null ||
            MessageBox.Show(
                "Stop the server, create a safety backup, and install the available update?",
                "Confirm Update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await PostActionAsync("updates");
    }

    private async void ResourceMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode })
        {
            return;
        }

        if (mode == "Balanced")
        {
            var snapshot = _dashboard;
            if (snapshot is null)
            {
                return;
            }

            using var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/resources/profile",
                new ResourceProfileRequest(ResourceMode.Balanced));
            ConsoleStatusText.Text = response.IsSuccessStatusCode
                ? "Balanced resource mode applied."
                : await ReadErrorAsync(response);
        }
        else
        {
            await _httpClient.PostAsync($"/api/v1/resources/prioritize/{mode}", null);
            ConsoleStatusText.Text = $"{mode} priority mode applied.";
        }
    }

    private void OpenResourceGovernor_Click(object sender, RoutedEventArgs e) =>
        new ResourceGovernorWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private async void OpenMinecraftRawSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_server is null)
        {
            return;
        }

        var servers = await _httpClient.GetFromJsonAsync<GameServerDefinition[]>(
            "/api/v1/servers") ?? [];
        var definition = servers.FirstOrDefault(item =>
            item.Id == _server.ServerId);
        var path = definition is null
            ? null
            : Path.Combine(definition.RootPath, "server.properties");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SettingsStatusText.Text = "server.properties is unavailable.";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
        SettingsStatusText.Text =
            "Opened server.properties. Save through this editor for validation, checkpoints, and recovery protection.";
    }

    private void OpenFileManager_Click(object sender, RoutedEventArgs e) =>
        new FileManagerWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var server = _server;
        var definition = server is null
            ? null
            : _dashboard?.Servers.FirstOrDefault(item => item.ServerId == server.ServerId);
        if (definition is null)
        {
            return;
        }

        _ = OpenRegisteredFolderAsync(false);
    }

    private async Task OpenRegisteredFolderAsync(bool backups)
    {
        if (_server is null)
        {
            return;
        }

        var servers = await _httpClient.GetFromJsonAsync<GameServerDefinition[]>(
            "/api/v1/servers") ?? [];
        var server = servers.FirstOrDefault(item => item.Id == _server.ServerId);
        if (server is null)
        {
            return;
        }

        var path = backups ? Path.Combine(server.RootPath, "backups") : server.RootPath;
        Directory.CreateDirectory(path);
        var info = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        info.ArgumentList.Add(path);
        Process.Start(info);
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e) =>
        _ = OpenRegisteredFolderAsync(true);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        ServerTabs.SelectedIndex = 1;

    private void OpenConsoleTab_Click(object sender, RoutedEventArgs e) =>
        ServerTabs.SelectedIndex = 1;

    private void OpenSettingsTab_Click(object sender, RoutedEventArgs e) =>
        ServerTabs.SelectedIndex = 2;

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null)
        {
            return;
        }

        // Game server console output frequently echoes admin/RCON passwords or connection
        // strings on startup, so both the last-error text and the console tail must be
        // redacted before they reach the clipboard, same as every other diagnostics
        // copy/export path in this application.
        var diagnostics = new StringBuilder()
            .AppendLine($"Game: {Game}")
            .AppendLine($"Server: {_server.Name}")
            .AppendLine($"State: {_server.State}")
            .AppendLine($"Address: {_server.LocalAddress}")
            .AppendLine($"PID: {_server.ProcessId}")
            .AppendLine($"Version: {_server.InstalledVersion}")
            .AppendLine($"Last error: {DiagnosticsService.Redact(_server.LastError ?? string.Empty)}")
            .AppendLine("Last console lines:")
            .AppendJoin(
                Environment.NewLine,
                _logs.TakeLast(50).Select(log => DiagnosticsService.Redact(log.Message)))
            .ToString();
        ConsoleStatusText.Text =
            ServerManager.Client.Shell.SafeClipboard.TrySetText(diagnostics)
                ? "Diagnostics copied."
                : "The clipboard is busy. Try again.";
    }

    private void OpenFirewallSettings_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:windowsdefender-firewall",
            UseShellExecute = true
        });

    private static void SelectCombo(System.Windows.Controls.ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Content?.ToString()?.Equals(
                    value,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static string SelectedCombo(System.Windows.Controls.ComboBox box, string fallback) =>
        (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            return error is null
                ? $"Operation failed: {response.ReasonPhrase}"
                : $"{error.WhatFailed} {error.SuggestedFix}";
        }
        catch (System.Text.Json.JsonException)
        {
            return $"Operation failed: {await response.Content.ReadAsStringAsync()}";
        }
    }

    private static string FormatMebibytes(long bytes) =>
        $"{bytes / (double)Mebibyte:F0} MB";

    private static string FormatDuration(TimeSpan? duration) =>
        duration is null
            ? "stopped"
            : duration.Value.TotalDays >= 1
                ? $"{duration.Value.TotalDays:F1} days"
                : duration.Value.ToString(@"hh\:mm\:ss");

    private static string StatusWord(bool value) => value ? "Yes" : "No";
}
