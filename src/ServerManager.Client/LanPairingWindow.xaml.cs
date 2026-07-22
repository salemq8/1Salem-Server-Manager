using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Windows;
using ServerManager.Client.Transport;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client;

public partial class LanPairingWindow : Window
{
    public LanPairingWindow()
    {
        InitializeComponent();
        ClientNameBox.Text = Environment.MachineName;
    }

    private async void GenerateCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
                Timeout = TimeSpan.FromSeconds(10)
            };
            var challenge = await client.PostAsync("/api/v1/pairing/challenge", null);
            challenge.EnsureSuccessStatusCode();
            var value = await challenge.Content.ReadFromJsonAsync<PairingChallenge>();
            GeneratedCodeText.Text = value?.Code ?? string.Empty;
            CodeBox.Text = value?.Code ?? string.Empty;
            ExpiryText.Text = value is null
                ? string.Empty
                : $"Expires at {value.ExpiresAtUtc.ToLocalTime():T}";
            StatusText.Text = "Enter this code on the LAN client.";
        }
        catch (HttpRequestException exception)
        {
            StatusText.Text = $"Could not generate a code: {exception.Message}";
        }
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port))
        {
            StatusText.Text = "Enter a valid HTTPS port.";
            return;
        }

        try
        {
            StatusText.Text = "Pairing over HTTPS and pinning the Agent certificate...";
            var client = new LanAgentClient();
            var result = await client.PairAsync(
                HostBox.Text.Trim(),
                port,
                CodeBox.Text.Trim(),
                ClientNameBox.Text.Trim());
            new LanClientProfileStore().Save(
                HostBox.Text.Trim(),
                port,
                result);
            StatusText.Text =
                $"Paired securely. Certificate SHA-256: {result.CertificateFingerprint}";
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            AuthenticationException or
            ArgumentException)
        {
            StatusText.Text = $"Pairing failed: {exception.Message}";
        }
    }
}
