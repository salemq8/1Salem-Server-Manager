using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Client.Shell;
using MessageBox = System.Windows.MessageBox;

namespace ServerManager.Client.Controls;

public partial class ConfigurationRestorePointsControl :
    System.Windows.Controls.UserControl,
    IDisposable
{
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromMinutes(5));
    private Guid? _serverId;
    private bool _loading;

    public ConfigurationRestorePointsControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public async void SetServer(Guid? serverId)
    {
        var changed = _serverId != serverId;
        _serverId = serverId;
        if (serverId is null)
        {
            RestorePointList.ItemsSource = null;
            RenderSelection(null);
            return;
        }

        if (IsLoaded && (changed || RestorePointList.Items.Count == 0))
        {
            await LoadAsync();
        }
    }

    public void Dispose()
    {
        Loaded -= OnLoaded;
        _httpClient.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_serverId is not null && RestorePointList.Items.Count == 0)
        {
            await LoadAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await LoadAsync();

    private async Task LoadAsync()
    {
        if (_serverId is null || _loading)
        {
            return;
        }

        _loading = true;
        StatusText.Text = "Loading protected configuration history...";
        try
        {
            var items =
                await _httpClient.GetFromJsonAsync<ConfigurationRestorePointItem[]>(
                    $"/api/v1/servers/{_serverId}/configuration-restore-points")
                ?? [];
            RestorePointList.ItemsSource = items.Select(item =>
                new RestorePointRow(item)).ToArray();
            StatusText.Text = items.Length == 0
                ? "No restore points yet. One is created automatically before the next settings change."
                : $"{items.Length} restore point(s). Protected configuration contents are never shown.";
            RenderSelection(null);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or
            JsonException)
        {
            StatusText.Text =
                $"Configuration history is unavailable: {exception.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    private void RestorePointList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        RenderSelection(RestorePointList.SelectedItem as RestorePointRow);

    private void RenderSelection(RestorePointRow? row)
    {
        var selected = row is not null;
        CompareButton.IsEnabled = selected;
        LabelButton.IsEnabled = selected;
        DeleteButton.IsEnabled = selected;
        RestoreButton.IsEnabled = selected;
        LabelBox.IsEnabled = selected;
        LabelBox.Text = row?.Item.Label ?? string.Empty;
        SummaryText.Text = row?.Item.Summary ??
            "Select a restore point to review its redacted change summary.";
        HashText.Text = row is null
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                row.Item.OriginalFileHashes.Select(item =>
                    $"{item.Key}: {item.Value}"));
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (RestorePointList.SelectedItem is not RestorePointRow row)
        {
            return;
        }

        var hashes = string.Join(
            Environment.NewLine,
            row.Item.OriginalFileHashes.Select(item =>
            {
                var applied = row.Item.AppliedFileHashes.GetValueOrDefault(
                    item.Key,
                    "not recorded");
                return $"{item.Key}{Environment.NewLine}Before: {item.Value}{Environment.NewLine}After:  {applied}";
            }));
        MessageBox.Show(
            $"{row.Item.Summary}{Environment.NewLine}{Environment.NewLine}{hashes}",
            "Configuration Comparison",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void SaveLabel_Click(object sender, RoutedEventArgs e)
    {
        if (_serverId is null ||
            RestorePointList.SelectedItem is not RestorePointRow row)
        {
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/servers/{_serverId}/configuration-restore-points/{row.Item.Id}/label",
            new ConfigurationRestorePointLabelRequest(LabelBox.Text));
        await RenderActionResponseAsync(response);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_serverId is null ||
            RestorePointList.SelectedItem is not RestorePointRow row ||
            MessageBox.Show(
                "Delete this configuration restore point? Game worlds and saves are not affected.",
                "Delete Configuration Restore Point",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        using var response = await _httpClient.DeleteAsync(
            $"/api/v1/servers/{_serverId}/configuration-restore-points/{row.Item.Id}");
        await RenderActionResponseAsync(response);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_serverId is null ||
            RestorePointList.SelectedItem is not RestorePointRow row ||
            MessageBox.Show(
                "The current configuration will be protected first. If the server is running, it will restart and startup will be verified. Continue?",
                "Restore Configuration",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SetActionEnabled(false);
        StatusText.Text = "Protecting the current configuration, restoring, and verifying...";
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/servers/{_serverId}/configuration-restore-points/restore",
                new ConfigurationRestorePointRestoreRequest(
                    row.Item.Id,
                    ConfirmRestart: true));
            await RenderActionResponseAsync(response);
        }
        finally
        {
            SetActionEnabled(true);
        }
    }

    private async Task RenderActionResponseAsync(HttpResponseMessage response)
    {
        var result =
            await response.Content
                .ReadFromJsonAsync<ConfigurationRestorePointActionResponse>();
        StatusText.Text = result?.Message ??
            await response.Content.ReadAsStringAsync();
        NotificationService.Publish(
            result?.Success == true
                ? NotificationKind.Success
                : NotificationKind.Error,
            "Configuration recovery",
            StatusText.Text,
            persistent: result?.Success != true);
        if (response.IsSuccessStatusCode && result?.Success == true)
        {
            await LoadAsync();
        }
    }

    private void SetActionEnabled(bool enabled)
    {
        var selected = enabled &&
            RestorePointList.SelectedItem is RestorePointRow;
        CompareButton.IsEnabled = selected;
        LabelButton.IsEnabled = selected;
        DeleteButton.IsEnabled = selected;
        RestoreButton.IsEnabled = selected;
    }

    private sealed class RestorePointRow
    {
        public RestorePointRow(ConfigurationRestorePointItem item) =>
            Item = item;

        public ConfigurationRestorePointItem Item { get; }
        public string Created => Item.CreatedAtUtc
            .ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture);
        public string Reason => Item.Reason.Replace('-', ' ');
        public string Label => Item.Label ?? "—";
        public string State => Item.KnownWorking
            ? "Known working"
            : "Protected";
        public string Size => FormatBytes(Item.ProtectedSizeBytes);

        private static string FormatBytes(long bytes) =>
            bytes >= 1024 * 1024
                ? $"{bytes / (1024d * 1024d):0.0} MB"
                : $"{Math.Max(1, bytes / 1024d):0.0} KB";
    }
}
