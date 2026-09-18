using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace ServerManager.Client.Controls;

public partial class RemoteAccessControl : System.Windows.Controls.UserControl, IDisposable
{
    private static readonly Uri DownloadUri = new("https://playit.gg/download/");
    private static readonly Uri TunnelsUri = new("https://playit.gg/account/tunnels");
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromSeconds(30));
    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private PlayitStatusResponse? _status;
    private bool _refreshing;
    private bool _editing;

    public RemoteAccessControl()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync(false);
        Loaded += async (_, _) =>
        {
            await RefreshAsync(true);
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
        MinecraftAddressBox.TextChanged += (_, _) => _editing = true;
        PalworldAddressBox.TextChanged += (_, _) => _editing = true;
        AgentNameBox.TextChanged += (_, _) => _editing = true;
        EnabledBox.Click += (_, _) => _editing = true;
    }

    public void Dispose()
    {
        _timer.Stop();
        _httpClient.Dispose();
    }

    private async Task RefreshAsync(bool force)
    {
        var window = Window.GetWindow(this);
        if (_refreshing ||
            (!force &&
             (!IsVisible || window?.WindowState == WindowState.Minimized)))
        {
            return;
        }

        _refreshing = true;
        try
        {
            _status = await _httpClient.GetFromJsonAsync<PlayitStatusResponse>(
                "/api/v1/playit");
            if (_status is null)
            {
                return;
            }

            InstalledText.Text = _status.IsInstalled
                ? $"Installed {_status.Version ?? string.Empty}".Trim()
                : "Not installed";
            StateText.Text = _status.State.ToString();
            VerifiedText.Text = _status.IsLinked
                ? _status.IsVerified ? "Linked · Verified" : "Linked · Not online"
                : "Unlinked";
            AgentDetailText.Text =
                $"Agent: {_status.AgentName ?? "Not named"} · " +
                $"PID: {_status.ProcessId?.ToString() ?? "—"} · " +
                $"Last connection: {_status.LastConnectionAtUtc?.ToLocalTime().ToString("g") ?? "Never"}" +
                (string.IsNullOrWhiteSpace(_status.LastError)
                    ? string.Empty
                    : $" · Last error: {_status.LastError}");
            LinkButton.IsEnabled = Uri.TryCreate(
                _status.ClaimUrl,
                UriKind.Absolute,
                out _);
            MinecraftTargetText.Text =
                $"{_status.Minecraft.Protocol.ToString().ToUpperInvariant()} → " +
                $"{_status.Minecraft.LocalHost}:{_status.Minecraft.LocalPort}";
            PalworldTargetText.Text =
                $"{_status.Palworld.Protocol.ToString().ToUpperInvariant()} → " +
                $"{_status.Palworld.LocalHost}:{_status.Palworld.LocalPort}";
            MinecraftStatusText.Text = _status.Minecraft.ValidationMessage ?? string.Empty;
            PalworldStatusText.Text = _status.Palworld.ValidationMessage ?? string.Empty;
            LogList.ItemsSource = _status.RecentLog.TakeLast(200).Reverse().ToArray();
            if (!_editing)
            {
                EnabledBox.IsChecked = _status.IsEnabled;
                AgentNameBox.Text = _status.AgentName ?? string.Empty;
                MinecraftAddressBox.Text = _status.Minecraft.PublicAddress ?? string.Empty;
                PalworldAddressBox.Text = _status.Palworld.PublicAddress ?? string.Empty;
                _editing = false;
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = $"Remote Access status unavailable: {exception.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void Detect_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("/api/v1/playit/detect");

    private async void Start_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("/api/v1/playit/start");

    private async void Stop_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("/api/v1/playit/stop");

    private async void Restart_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("/api/v1/playit/restart");

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Disable external access and stop the Server Manager-controlled Playit process? Local game servers will continue.",
                "Disable Remote Access",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunActionAsync("/api/v1/playit/disable");
        _editing = false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/playit/settings",
                new PlayitSettingsRequest(
                    EnabledBox.IsChecked == true,
                    AgentNameBox.Text,
                    MinecraftAddressBox.Text,
                    PalworldAddressBox.Text));
            var result = await response.Content.ReadFromJsonAsync<PlayitActionResponse>();
            StatusText.Text = result?.Message ?? response.ReasonPhrase;
            _editing = false;
            await RefreshAsync(true);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = $"Could not save Remote Access settings: {exception.Message}";
        }
    }

    private async Task RunActionAsync(string path)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(path, new { });
            var result = await response.Content.ReadFromJsonAsync<PlayitActionResponse>();
            StatusText.Text = result?.Message ?? response.ReasonPhrase;
            await RefreshAsync(true);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = $"Playit action failed: {exception.Message}";
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e) => OpenUri(DownloadUri);

    private void OpenSetup_Click(object sender, RoutedEventArgs e) => OpenUri(TunnelsUri);

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_status?.ClaimUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("playit.gg", StringComparison.OrdinalIgnoreCase))
        {
            OpenUri(uri);
        }
    }

    private void MinecraftGuide_Click(object sender, RoutedEventArgs e) =>
        ShowGuide(
            "Minecraft Java",
            "TCP",
            _status?.Minecraft.LocalPort ?? 25565);

    private void PalworldGuide_Click(object sender, RoutedEventArgs e) =>
        ShowGuide(
            "Palworld",
            "UDP",
            _status?.Palworld.LocalPort ?? 8211);

    private static void ShowGuide(string game, string protocol, int port)
    {
        MessageBox.Show(
            $"1. Open the official Playit tunnel page.\n" +
            $"2. Select the existing ALSarabeetMC Agent.\n" +
            $"3. Create a separate {game} tunnel.\n" +
            $"4. Protocol: {protocol}\n" +
            $"5. Local address: 127.0.0.1:{port}\n" +
            "6. Save it in Playit, then paste only its public address into Server Manager.\n\n" +
            "Do not change or delete the existing Minecraft tunnel.",
            $"{game} tunnel guide",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        OpenUri(TunnelsUri);
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        var text = (sender as Button)?.Tag?.ToString() == "Palworld"
            ? PalworldAddressBox.Text
            : MinecraftAddressBox.Text;
        StatusText.Text = SafeClipboard.TrySetText(text)
            ? "Public address copied."
            : "There is no public address to copy.";
    }

    private async void TestPort_Click(object sender, RoutedEventArgs e)
    {
        var palworld = (sender as Button)?.Tag?.ToString() == "Palworld";
        var tunnel = palworld ? _status?.Palworld : _status?.Minecraft;
        if (tunnel is null)
        {
            return;
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/network/test-port",
                new PortTestRequest(
                    tunnel.LocalPort,
                    tunnel.Protocol == PlayitTunnelProtocol.Udp ? "UDP" : "TCP"));
            var result = await response.Content.ReadFromJsonAsync<PortTestResponse>();
            StatusText.Text = result?.Message ?? response.ReasonPhrase;
        }
        catch (HttpRequestException exception)
        {
            StatusText.Text = $"Port test failed: {exception.Message}";
        }
    }

    private static void OpenUri(Uri uri) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
}
