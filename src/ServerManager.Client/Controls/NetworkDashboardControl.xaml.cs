using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ServerManager.Contracts;
using ServerManager.Core;
using Clipboard = System.Windows.Clipboard;

namespace ServerManager.Client.Controls;

public partial class NetworkDashboardControl : System.Windows.Controls.UserControl, IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private NetworkSnapshot? _network;
    private bool _refreshing;

    public NetworkDashboardControl()
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
             (!IsVisible ||
              window?.WindowState == WindowState.Minimized)))
        {
            return;
        }

        _refreshing = true;
        try
        {
            _network = await _httpClient.GetFromJsonAsync<NetworkSnapshot>(
                "/api/v1/network");
            var dashboard = await _httpClient.GetFromJsonAsync<DashboardSnapshot>(
                "/api/v1/dashboard");
            var servers = dashboard?.Servers ?? [];
            Ipv4Text.Text = _network?.LocalIpv4 ?? "No active LAN IPv4";
            var selectedId = _network?.PreferredAdapterId;
            var adapters = (_network?.Adapters ?? [])
                .Select(adapter => new AdapterItem(
                    adapter,
                    $"{adapter.Name} — {adapter.Ipv4}" +
                    (adapter.HasDefaultGateway ? " (default route)" : string.Empty)))
                .ToArray();
            AdapterBox.ItemsSource = adapters;
            AdapterBox.SelectedItem = adapters.FirstOrDefault(item =>
                item.Adapter.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
            AddressList.ItemsSource = servers
                .SelectMany(server =>
                {
                    var protocol =
                        server.Game == GameType.Palworld ? "UDP" : "TCP";
                    var items = new List<AddressItem>();
                    if (!string.IsNullOrWhiteSpace(server.LocalAddress))
                    {
                        items.Add(new AddressItem(
                            server.Game,
                            server.Port,
                            protocol,
                            $"{server.Game} local (same LAN): {server.LocalAddress}",
                            server.LocalAddress,
                            true));
                    }

                    if (!string.IsNullOrWhiteSpace(server.InternetAddress))
                    {
                        items.Add(new AddressItem(
                            server.Game,
                            server.Port,
                            protocol,
                            $"{server.Game} Internet: {server.InternetAddress} · " +
                            $"Playit {server.PlayitState ?? "unavailable"} · " +
                            (server.PublicTunnelOnline
                                ? "verified online"
                                : "reachability not verified"),
                            server.InternetAddress,
                            false));
                    }

                    return items;
                })
                .ToArray();
            StatusText.Text = _network?.IsPublicProfile == true
                ? "Windows reports a Public network profile. Private-network firewall rules will not apply until the profile is changed to Private."
                : "Active LAN adapter detected. Server cards refresh automatically when this IP changes.";
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = $"Network status unavailable: {exception.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void SaveAdapter_Click(object sender, RoutedEventArgs e)
    {
        var id = (AdapterBox.SelectedItem as AdapterItem)?.Adapter.Id;
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/v1/network/preference",
            new NetworkPreferenceRequest(id));
        StatusText.Text = response.IsSuccessStatusCode
            ? "Preferred adapter saved."
            : $"Could not save preferred adapter: {response.ReasonPhrase}";
        await RefreshAsync(true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(true);

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (AddressList.SelectedItem is AddressItem item)
        {
            ServerManager.Client.Shell.SafeClipboard.TrySetText(item.Address);
            StatusText.Text = "Address copied.";
        }
    }

    private void CopyIp_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_network?.LocalIpv4))
        {
            ServerManager.Client.Shell.SafeClipboard.TrySetText(_network.LocalIpv4);
            StatusText.Text = "IP address copied.";
        }
    }

    private void CopyPort_Click(object sender, RoutedEventArgs e)
    {
        if (AddressList.SelectedItem is AddressItem item)
        {
            ServerManager.Client.Shell.SafeClipboard.TrySetText(item.Port.ToString());
            StatusText.Text = "Port copied.";
        }
    }

    private async void TestPort_Click(object sender, RoutedEventArgs e)
    {
        if (AddressList.SelectedItem is not AddressItem item)
        {
            StatusText.Text = "Select a server address first.";
            return;
        }

        if (!item.CanTestLocally)
        {
            StatusText.Text =
                "The Internet address is provided by Playit. Test the local game port here; tunnel status alone cannot prove end-to-end game reachability.";
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/v1/network/test-port",
            new PortTestRequest(item.Port, item.Protocol));
        var result = await response.Content.ReadFromJsonAsync<PortTestResponse>();
        StatusText.Text = result?.Message ?? response.ReasonPhrase;
    }

    private void OpenFirewall_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:windowsdefender-firewall",
            UseShellExecute = true
        });

    private sealed record AdapterItem(
        NetworkAdapterSnapshot Adapter,
        string Display);

    private sealed record AddressItem(
        GameType Game,
        int Port,
        string Protocol,
        string Display,
        string Address,
        bool CanTestLocally);
}
