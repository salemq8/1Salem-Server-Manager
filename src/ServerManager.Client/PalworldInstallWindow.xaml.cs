using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ServerManager.Contracts;
using Forms = System.Windows.Forms;

namespace ServerManager.Client;

public partial class PalworldInstallWindow : Window
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromMinutes(40)
    };

    public PalworldInstallWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _httpClient.Dispose();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the parent folder for the new Palworld server",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            FolderBox.Text = Path.Combine(dialog.SelectedPath, "Palworld");
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port) ||
            !int.TryParse(MaxPlayersBox.Text, out var maxPlayers))
        {
            StatusText.Text = "Enter valid numeric port and player values.";
            return;
        }

        var request = new PalworldInstallRequest(
            FolderBox.Text,
            new PalworldServerSettings(
                ServerNameBox.Text,
                DescriptionBox.Text,
                ServerPasswordBox.Password,
                AdminPasswordBox.Password,
                maxPlayers,
                port,
                CommunityBox.IsChecked == true));
        InstallButton.IsEnabled = false;
        try
        {
            StatusText.Text =
                "Installing isolated SteamCMD, downloading Palworld, validating, and finalizing…";
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/palworld/install",
                request);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                StatusText.Text = $"Installation failed: {responseText}";
                return;
            }

            var result = await response.Content.ReadFromJsonAsync<PalworldInstallResult>();
            var dashboard = await _httpClient.GetFromJsonAsync<DashboardSnapshot>(
                "/api/v1/dashboard");
            var server = dashboard?.Servers.FirstOrDefault(
                item => item.ServerId == result?.ServerId);
            StatusText.Text =
                $"Installed Palworld build {result?.BuildId ?? "unknown"} safely at {result?.RootPath}.\n" +
                $"Local address: {server?.LocalAddress ?? $"port {port}"}\n" +
                $"Internet address: {server?.InternetAddress ?? "not configured"}\n" +
                $"Playit: {server?.PlayitState ?? "unavailable"}; public reachability " +
                $"{(server?.PublicTunnelOnline == true ? "verified" : "not verified")}.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = $"Installation failed: {exception.Message}";
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }
}
