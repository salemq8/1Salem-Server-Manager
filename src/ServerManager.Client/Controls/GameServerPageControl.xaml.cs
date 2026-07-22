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
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ServerManager.Client.Controls;

public partial class GameServerPageControl : System.Windows.Controls.UserControl, IDisposable
{
    private const long Mebibyte = 1024L * 1024;
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromMinutes(30)
    };
    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private readonly List<LogEntry> _logs = [];
    private readonly List<string> _commandHistory = [];
    private DashboardSnapshot? _dashboard;
    private ServerDashboardCard? _server;
    private MinecraftConfigurationResponse? _minecraftConfiguration;
    private PalworldConfigurationResponse? _palworldConfiguration;
    private bool _refreshing;
    private bool _configurationLoaded;
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
        MinecraftPlayersPanel.Visibility =
            game == GameType.Minecraft ? Visibility.Visible : Visibility.Collapsed;
        PalworldPlayersPanel.Visibility =
            game == GameType.Palworld ? Visibility.Visible : Visibility.Collapsed;
        PalworldWorldSettingsTab.Visibility =
            game == GameType.Palworld ? Visibility.Visible : Visibility.Collapsed;
        ServerSettingsTab.Visibility =
            game == GameType.Minecraft ? Visibility.Visible : Visibility.Collapsed;
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
                PlayersTab,
                ConsoleTab,
                PalworldWorldSettingsTab,
                PerformanceTab,
                SaveBackupsTab,
                NetworkTab,
                FilesTab,
                UpdatesTab
            ]
            :
            [
                OverviewTab,
                ConsoleTab,
                ServerSettingsTab,
                PlayersTab,
                PerformanceTab,
                SaveBackupsTab,
                NetworkTab,
                FilesTab,
                UpdatesTab
            ];
        ServerTabs.Items.Clear();
        foreach (var tab in ordered)
        {
            ServerTabs.Items.Add(tab);
        }

        ServerTabs.SelectedIndex = 0;
    }

    public async Task RefreshNowAsync() => await RefreshAsync(true);

    public void SelectTab(int index) =>
        ServerTabs.SelectedIndex = Math.Clamp(index, 0, ServerTabs.Items.Count - 1);

    public void Dispose()
    {
        _timer.Stop();
        PalworldWorldSettingsControl.Dispose();
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
            PalworldMemoryPerformanceControl.SetServer(null);
            PalworldSaveBackupControl.SetServer(null);
            return;
        }

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
        var totalMemory = _dashboard?.TotalMemoryBytes ?? 0;
        ServerResourceGraph.AddSample(
            _server.CpuPercent,
            totalMemory <= 0 ? 0 : _server.WorkingSetBytes * 100d / totalMemory);
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
        AutoStartBox.IsChecked = _server.AutoStart;
        AutoRestartBox.IsChecked = _server.AutoRestart;
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
        var query = LogSearchBox.Text.Trim();
        ConsoleList.ItemsSource = _logs
            .Where(entry =>
                string.IsNullOrEmpty(query) ||
                entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Level.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(entry =>
                $"{entry.TimestampUtc.ToLocalTime():HH:mm:ss} [{entry.Level}] {entry.Message}")
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

        NameBox.Text = _minecraftConfiguration.Name;
        PortBox.Text = _minecraftConfiguration.Port.ToString();
        MaxPlayersBox.Text = _minecraftConfiguration.MaxPlayers.ToString();
        JavaPathBox.Text = _minecraftConfiguration.JavaExecutablePath ?? string.Empty;
        MotdBox.Text = _minecraftConfiguration.Motd;
        ViewDistanceBox.Text = _minecraftConfiguration.ViewDistance.ToString();
        SimulationDistanceBox.Text = _minecraftConfiguration.SimulationDistance.ToString();
        SelectCombo(GameModeBox, _minecraftConfiguration.GameMode);
        SelectCombo(DifficultyBox, _minecraftConfiguration.Difficulty);
        OnlineModeBox.IsChecked = _minecraftConfiguration.OnlineMode;
        WhitelistBox.IsChecked = _minecraftConfiguration.WhitelistEnabled;
        HardcoreBox.IsChecked = _minecraftConfiguration.Hardcore;
        PvpBox.IsChecked = _minecraftConfiguration.Pvp;
        XmsBox.Text = _minecraftConfiguration.MinimumMemoryMb.ToString();
        XmxBox.Text = _minecraftConfiguration.MaximumMemoryMb.ToString();
        AutoStartBox.IsChecked = _minecraftConfiguration.AutoStart;
        AutoRestartBox.IsChecked = _minecraftConfiguration.AutoRestart;
        _configurationLoaded = true;
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

        PalworldNameBox.Text = _palworldConfiguration.ServerName;
        PalworldDescriptionBox.Text = _palworldConfiguration.Description;
        PalworldPortBox.Text = _palworldConfiguration.Port.ToString();
        PalworldMaxPlayersBox.Text = _palworldConfiguration.MaxPlayers.ToString();
        PalworldRconPortBox.Text = _palworldConfiguration.RconPort.ToString();
        PalworldCommunityBox.IsChecked = _palworldConfiguration.CommunityServer;
        PalworldRconBox.IsChecked = _palworldConfiguration.RconEnabled;
        AutoStartBox.IsChecked = _palworldConfiguration.AutoStart;
        AutoRestartBox.IsChecked = _palworldConfiguration.AutoRestart;
        PalworldPasswordBox.Clear();
        PalworldAdminPasswordBox.Clear();
        _configurationLoaded = true;
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

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_server?.LocalAddress))
        {
            NetworkStatusText.Text =
                ServerManager.Client.Shell.SafeClipboard.TrySetText(_server.LocalAddress)
                    ? "Address copied."
                    : "The clipboard is busy. Try again.";
        }
    }

    private void CopyInternetAddress_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_server?.InternetAddress))
        {
            NetworkStatusText.Text =
                ServerManager.Client.Shell.SafeClipboard.TrySetText(
                    _server.InternetAddress)
                    ? "Internet address copied."
                    : "The clipboard is busy. Try again.";
        }
        else
        {
            NetworkStatusText.Text =
                "No Internet address is configured for this server.";
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
        _commandHistory.Add(command);
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
        _logs.Clear();
        RenderLogs();
        ConsoleStatusText.Text =
            "The view was cleared. Agent log history was not deleted.";
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

    private async void SaveMemory_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null ||
            !int.TryParse(XmsBox.Text, out var xms) ||
            !int.TryParse(XmxBox.Text, out var xmx))
        {
            SettingsStatusText.Text = "Enter valid Xms and Xmx values in MB.";
            return;
        }

        var restart = sender is Button { Tag: "restart" };
        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/servers/{_server.ServerId}/minecraft/memory",
            new MinecraftMemoryUpdateRequest(xms, xmx, restart));
        SettingsStatusText.Text = response.IsSuccessStatusCode
            ? restart ? "Memory saved and restart requested." : "Memory saved for the next restart."
            : await ReadErrorAsync(response);
        _configurationLoaded = false;
        await RefreshAsync(true);
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

    private async void SaveMinecraftSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null ||
            !int.TryParse(PortBox.Text, out var port) ||
            !int.TryParse(MaxPlayersBox.Text, out var maxPlayers) ||
            !int.TryParse(ViewDistanceBox.Text, out var viewDistance) ||
            !int.TryParse(SimulationDistanceBox.Text, out var simulationDistance))
        {
            SettingsStatusText.Text = "Enter valid numeric Minecraft settings.";
            return;
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
            sender is Button { Tag: "restart" });
        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/servers/{_server.ServerId}/minecraft/settings",
            request);
        SettingsStatusText.Text = response.IsSuccessStatusCode
            ? "Minecraft settings saved."
            : await ReadErrorAsync(response);
        _configurationLoaded = false;
        await RefreshAsync(true);
    }

    private async void SavePalworldSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null ||
            !int.TryParse(PalworldPortBox.Text, out var port) ||
            !int.TryParse(PalworldMaxPlayersBox.Text, out var maxPlayers) ||
            !int.TryParse(PalworldRconPortBox.Text, out var rconPort))
        {
            SettingsStatusText.Text = "Enter valid Palworld port and player values.";
            return;
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
                sender is Button { Tag: "restart" }));
        SettingsStatusText.Text = response.IsSuccessStatusCode
            ? "Palworld settings saved without exposing passwords."
            : await ReadErrorAsync(response);
        _configurationLoaded = false;
        await RefreshAsync(true);
    }

    private async void SaveAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null)
        {
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/servers/{_server.ServerId}/automation",
            new ServerAutomationUpdateRequest(
                AutoStartBox.IsChecked == true,
                AutoRestartBox.IsChecked == true));
        SettingsStatusText.Text = response.IsSuccessStatusCode
            ? "Server automation saved. Dashboard startup remains a separate option."
            : await ReadErrorAsync(response);
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

        var diagnostics = new StringBuilder()
            .AppendLine($"Game: {Game}")
            .AppendLine($"Server: {_server.Name}")
            .AppendLine($"State: {_server.State}")
            .AppendLine($"Address: {_server.LocalAddress}")
            .AppendLine($"PID: {_server.ProcessId}")
            .AppendLine($"Version: {_server.InstalledVersion}")
            .AppendLine($"Last error: {_server.LastError}")
            .AppendLine("Last console lines:")
            .AppendJoin(Environment.NewLine, _logs.TakeLast(50).Select(log => log.Message))
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
