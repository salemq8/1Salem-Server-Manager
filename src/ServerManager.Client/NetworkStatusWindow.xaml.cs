using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client;

public partial class NetworkStatusWindow : Window
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromSeconds(15)
    };

    public NetworkStatusWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _httpClient.Dispose();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (AddressList.SelectedItem is AddressItem item)
        {
            Shell.SafeClipboard.TrySetText(item.Address);
            WarningText.Text = "Address copied.";
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var network = await _httpClient.GetFromJsonAsync<NetworkSnapshot>(
                "/api/v1/network");
            var servers = await _httpClient.GetFromJsonAsync<GameServerDefinition[]>(
                "/api/v1/servers") ?? [];
            MachineText.Text = network?.MachineName ?? "Unavailable";
            AddressText.Text = network?.LocalIpv4 ?? "No connected IPv4 adapter";
            AddressList.ItemsSource = network?.LocalIpv4 is null
                ? []
                : servers.Select(server => new AddressItem(
                    $"{server.Game}: {network.LocalIpv4}:{server.Port}",
                    $"{network.LocalIpv4}:{server.Port}")).ToArray();
            WarningText.Text = network?.IsPublicProfile == true
                ? "Warning: Windows reports a Public network profile. Use Private profile before allowing LAN firewall rules."
                : "Private-network firewall rules can be managed from Administrator Tools.";
        }
        catch (HttpRequestException exception)
        {
            WarningText.Text = $"Network status unavailable: {exception.Message}";
        }
    }

    private sealed record AddressItem(string Display, string Address);
}
