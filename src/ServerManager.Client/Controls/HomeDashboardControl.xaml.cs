using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ServerManager.Contracts;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;

namespace ServerManager.Client.Controls;

public sealed record ServerNavigationRequest(GameType Game, int TabIndex);

public partial class HomeDashboardControl : System.Windows.Controls.UserControl, IDisposable
{
    private const long Gibibyte = 1024L * 1024 * 1024;
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private DashboardSnapshot? _snapshot;
    private bool _refreshing;

    public HomeDashboardControl()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    public event EventHandler? CreateMinecraftRequested;

    public event EventHandler? CreatePalworldRequested;

    public event EventHandler<ServerNavigationRequest>? ManageRequested;

    public async Task RefreshNowAsync() => await RefreshAsync();

    public void Dispose()
    {
        _timer.Stop();
        _httpClient.Dispose();
    }

    private async Task RefreshAsync()
    {
        var window = Window.GetWindow(this);
        if (_refreshing ||
            !IsVisible ||
            window is null ||
            !window.IsVisible ||
            window.WindowState == WindowState.Minimized)
        {
            return;
        }

        _refreshing = true;
        try
        {
            _snapshot = await _httpClient.GetFromJsonAsync<DashboardSnapshot>(
                "/api/v1/dashboard");
            if (_snapshot is null)
            {
                return;
            }

            Render(_snapshot);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            AgentText.Text = "Offline";
            WarningText.Text =
                "The local Agent is unavailable. Start or repair the Agent service, then refresh.";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Render(DashboardSnapshot snapshot)
    {
        AgentText.Text = "Online";
        PcText.Text = $"{snapshot.Agent.MachineName} · v{snapshot.Agent.Version}";
        Ipv4Text.Text = snapshot.LocalIpv4 ?? "No LAN IPv4";
        CpuText.Text = $"{snapshot.CpuPercent:F1}%";
        AgentMemoryText.Text = $"Agent {FormatBytes(snapshot.AgentMemoryBytes)}";
        MemoryText.Text =
            $"{FormatBytes(snapshot.UsedMemoryBytes)} / {FormatBytes(snapshot.TotalMemoryBytes)}";
        AvailableMemoryText.Text =
            $"{FormatBytes(snapshot.AvailableMemoryBytes)} available";
        DiskText.Text = FormatBytes(snapshot.SystemDriveFreeBytes);
        CountsText.Text =
            $"{snapshot.ActiveServerCount} active · {snapshot.WarningCount} warnings";
        ResourceProfileText.Text = snapshot.ResourceProfileSummary;
        WarningText.Text = snapshot.Warnings.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, snapshot.Warnings.Take(3));
        HistoryGraph.AddSample(
            snapshot.CpuPercent,
            snapshot.TotalMemoryBytes <= 0
                ? 0
                : snapshot.UsedMemoryBytes * 100d / snapshot.TotalMemoryBytes);

        RenderServer(
            snapshot.Servers.FirstOrDefault(server => server.Game == GameType.Minecraft),
            GameType.Minecraft);
        RenderServer(
            snapshot.Servers.FirstOrDefault(server => server.Game == GameType.Palworld),
            GameType.Palworld);
    }

    private void RenderServer(ServerDashboardCard? server, GameType game)
    {
        var minecraft = game == GameType.Minecraft;
        var create = minecraft ? CreateMinecraftButton : CreatePalworldButton;
        var details = minecraft ? MinecraftDetails : PalworldDetails;
        var state = minecraft ? MinecraftStateText : PalworldStateText;
        var name = minecraft ? MinecraftNameText : PalworldNameText;
        create.Visibility = server is null ? Visibility.Visible : Visibility.Collapsed;
        details.Visibility = server is null ? Visibility.Collapsed : Visibility.Visible;
        state.Text = server?.State.ToString() ?? "Not installed";
        name.Text = server?.Name ?? "No server is registered.";
        if (server is null)
        {
            return;
        }

        var address = minecraft ? MinecraftAddressText : PalworldAddressText;
        var internetAddress = minecraft
            ? MinecraftInternetAddressText
            : PalworldInternetAddressText;
        var tunnel = minecraft ? MinecraftTunnelText : PalworldTunnelText;
        var version = minecraft ? MinecraftVersionText : PalworldVersionText;
        var players = minecraft ? MinecraftPlayersText : PalworldPlayersText;
        var resources = minecraft ? MinecraftResourcesText : PalworldResourcesText;
        var start = minecraft ? MinecraftStartButton : PalworldStartButton;
        var stop = minecraft ? MinecraftStopButton : PalworldStopButton;
        var restart = minecraft ? MinecraftRestartButton : PalworldRestartButton;
        var backup = minecraft ? MinecraftBackupButton : PalworldBackupButton;
        address.Text = server.LocalAddress ?? $"Port {server.Port}";
        internetAddress.Text = server.InternetAddress ?? "Not configured";
        tunnel.Text =
            $"Playit {server.PlayitState ?? "unavailable"} · " +
            (server.PublicTunnelOnline
                ? "online and verified"
                : "reachability not verified");
        version.Text = server.Game == GameType.Minecraft
            ? $"{server.InstalledVersion ?? "unknown"} · {server.RuntimeVersion ?? "Java unknown"}"
            : server.InstalledVersion ?? "unknown";
        players.Text =
            $"{(server.PlayersOnline?.ToString() ?? "—")}/{(server.MaximumPlayers?.ToString() ?? "—")} · " +
            FormatDuration(server.PalworldManagement?.UptimeSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : server.Uptime);
        resources.Text =
            $"Game PID {(server.GameProcessId?.ToString() ?? "—")} · " +
            $"{server.CpuPercent:F1}% · " +
            FormatBytes(server.WorkingSetBytes);
        start.IsEnabled = server.Actions.CanStart;
        stop.IsEnabled = server.Actions.CanStop;
        restart.IsEnabled = server.Actions.CanRestart;
        backup.IsEnabled = server.Actions.CanBackup;
    }

    private void CreateMinecraft_Click(object sender, RoutedEventArgs e) =>
        CreateMinecraftRequested?.Invoke(this, EventArgs.Empty);

    private void CreatePalworld_Click(object sender, RoutedEventArgs e) =>
        CreatePalworldRequested?.Invoke(this, EventArgs.Empty);

    private void Manage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            var parts = tag.Split(':');
            if (parts.Length == 2 &&
                Enum.TryParse<GameType>(parts[0], out var game) &&
                int.TryParse(parts[1], out var tabIndex))
            {
                ManageRequested?.Invoke(
                    this,
                    new ServerNavigationRequest(game, tabIndex));
            }
        }
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split(':', 2);
        if (!Enum.TryParse<GameType>(parts[0], out var parsed))
        {
            return;
        }

        var server = _snapshot?.Servers
            .FirstOrDefault(item => item.Game == parsed);
        var address = parts.Length == 2 &&
                      parts[1].Equals("internet", StringComparison.OrdinalIgnoreCase)
            ? server?.InternetAddress
            : server?.LocalAddress;
        if (!string.IsNullOrWhiteSpace(address))
        {
            WarningText.Text =
                ServerManager.Client.Shell.SafeClipboard.TrySetText(address)
                    ? $"Copied {address}"
                    : "The clipboard is busy. Try Copy Address again.";
        }
    }

    private async void ServerAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split(':');
        if (parts.Length != 2 ||
            !Enum.TryParse<GameType>(parts[0], out var game))
        {
            return;
        }

        var server = _snapshot?.Servers.FirstOrDefault(item => item.Game == game);
        if (server is null)
        {
            return;
        }

        try
        {
            using var response = parts[1] switch
            {
                "stop" => await _httpClient.PostAsJsonAsync(
                    $"/api/v1/servers/{server.ServerId}/stop",
                    new ServerStopRequest()),
                "backup" => await _httpClient.PostAsync(
                    $"/api/v1/servers/{server.ServerId}/backups",
                    null),
                _ => await _httpClient.PostAsync(
                    $"/api/v1/servers/{server.ServerId}/{parts[1]}",
                    null)
            };
            if (!response.IsSuccessStatusCode)
            {
                WarningText.Text = await ReadErrorAsync(response);
            }

            await RefreshAsync();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            WarningText.Text = $"Action failed: {exception.Message}";
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        return error is null
            ? $"Action failed: {response.ReasonPhrase}"
            : $"{error.WhatFailed} {error.SuggestedFix}";
    }

    private static string FormatBytes(long bytes) =>
        bytes <= 0 ? "0 GB" : $"{bytes / (double)Gibibyte:F1} GB";

    private static string FormatDuration(TimeSpan? duration) =>
        duration is null
            ? "stopped"
            : duration.Value.TotalDays >= 1
                ? $"{duration.Value.TotalDays:F1}d"
                : duration.Value.ToString(@"hh\:mm\:ss");
}
