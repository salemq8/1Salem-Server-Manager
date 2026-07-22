using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client;

public partial class BackupCenterWindow : Window
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromMinutes(30)
    };

    public BackupCenterWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => _httpClient.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ServerBox.ItemsSource =
                await _httpClient.GetFromJsonAsync<GameServerDefinition[]>("/api/v1/servers") ?? [];
            ServerBox.SelectedIndex = ServerBox.Items.Count > 0 ? 0 : -1;
        }
        catch (HttpRequestException exception)
        {
            StatusText.Text = $"Could not load servers: {exception.Message}";
        }
    }

    private async void ServerBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        await RefreshBackupsAsync();

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (ServerBox.SelectedItem is not GameServerDefinition server)
        {
            return;
        }

        StatusText.Text = "Stopping safely if needed, creating and verifying backup...";
        using var response = await _httpClient.PostAsync(
            $"/api/v1/servers/{server.Id}/backups",
            null);
        StatusText.Text = response.IsSuccessStatusCode
            ? "Backup completed."
            : $"Backup failed: {await response.Content.ReadAsStringAsync()}";
        await RefreshBackupsAsync();
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupRecord backup)
        {
            return;
        }

        using var response = await _httpClient.PostAsync(
            $"/api/v1/backups/{backup.Id}/verify",
            null);
        var result = await response.Content.ReadFromJsonAsync<BackupVerificationResult>();
        StatusText.Text = result?.IsValid == true
            ? "Backup manifest, file sizes, and hashes are valid."
            : $"Backup verification failed: {result?.Error}";
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupRecord backup ||
            System.Windows.MessageBox.Show(
                "The server will stop and its current state will be backed up before restore. Continue?",
                "Confirm restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        StatusText.Text = "Creating safety backup, restoring through staging, and verifying...";
        using var response = await _httpClient.PostAsync(
            $"/api/v1/backups/{backup.Id}/restore",
            null);
        var result = await response.Content.ReadFromJsonAsync<OperationResult>();
        StatusText.Text = result?.Success == true
            ? "Restore completed and verified."
            : $"Restore failed and rollback was attempted: {result?.Message}";
    }

    private async Task RefreshBackupsAsync()
    {
        if (ServerBox.SelectedItem is not GameServerDefinition server)
        {
            BackupList.ItemsSource = null;
            return;
        }

        BackupList.ItemsSource = await _httpClient.GetFromJsonAsync<BackupRecord[]>(
            $"/api/v1/servers/{server.Id}/backups") ?? [];
    }
}
