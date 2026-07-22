using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ServerManager.Contracts;
using ServerManager.Core;
using MessageBox = System.Windows.MessageBox;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ServerManager.Client.Controls;

public partial class PalworldSaveBackupControl : System.Windows.Controls.UserControl, IDisposable
{
    private const long Gibibyte = 1024L * 1024 * 1024;
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromMinutes(30)
    };
    private Guid? _serverId;
    private BackupCenterSettings? _settings;

    public PalworldSaveBackupControl()
    {
        InitializeComponent();
        SchedulePresetBox.SelectedIndex = 6;
    }

    public async void SetServer(Guid? serverId)
    {
        if (_serverId == serverId)
        {
            return;
        }

        _serverId = serverId;
        if (serverId is not null && IsLoaded)
        {
            await RefreshAsync();
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task RefreshAsync()
    {
        if (_serverId is null)
        {
            return;
        }

        try
        {
            _settings =
                await _httpClient.GetFromJsonAsync<BackupCenterSettings>(
                    $"/api/v1/servers/{_serverId}/backup-center");
            if (_settings is not null)
            {
                DestinationPathText.Text = _settings.DestinationRoot;
                ScheduleEnabledBox.IsChecked = _settings.ScheduleEnabled;
                ScheduleExpressionBox.Text = _settings.ScheduleExpression;
                MaximumCountBox.Text =
                    _settings.MaximumCount.ToString(CultureInfo.InvariantCulture);
                MaximumAgeBox.Text =
                    _settings.MaximumAgeDays.ToString(CultureInfo.InvariantCulture);
                MaximumStorageBox.Text =
                    (_settings.MaximumTotalBytes / (double)Gibibyte)
                    .ToString("0.##", CultureInfo.InvariantCulture);
                KeepDailyBox.IsChecked = _settings.KeepDaily;
                KeepWeeklyBox.IsChecked = _settings.KeepWeekly;
                LastWorldSaveText.Text =
                    _settings.LastSuccessfulWorldSaveAtUtc is { } saved
                        ? $"Last successful world save: {saved.ToLocalTime():g}"
                        : "No successful REST world save is recorded yet.";
                ScheduleStatusText.Text =
                    $"Next: {_settings.NextRunAtUtc?.ToLocalTime().ToString("g") ?? "not scheduled"} · " +
                    $"Last: {_settings.LastRunAtUtc?.ToLocalTime().ToString("g") ?? "never"}";
            }

            BackupsGrid.ItemsSource =
                await _httpClient.GetFromJsonAsync<BackupRecord[]>(
                    $"/api/v1/servers/{_serverId}/backups") ?? [];
            var world = await _httpClient
                .GetFromJsonAsync<PalworldWorldSettingsResponse>(
                    $"/api/v1/servers/{_serverId}/palworld/world-settings");
            var builtIn = world?.Settings.FirstOrDefault(setting =>
                setting.Name == "bIsUseBackupSaveData");
            BuiltInBackupBox.IsChecked =
                builtIn is not null &&
                bool.TryParse(builtIn.CurrentValue, out var enabled) &&
                enabled;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            BackupStatusText.Text =
                $"Backup Center unavailable: {exception.Message}";
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async void SaveWorld_Click(object sender, RoutedEventArgs e)
    {
        if (_serverId is null)
        {
            return;
        }

        SaveWorldButton.IsEnabled = false;
        LastWorldSaveText.Text = "Saving…";
        try
        {
            using var response = await _httpClient.PostAsync(
                $"/api/v1/servers/{_serverId}/palworld/save",
                null);
            var result =
                await response.Content.ReadFromJsonAsync<PalworldRestOperationResult>();
            LastWorldSaveText.Text = result?.Success == true
                ? $"Saved successfully at {result.CompletedAtUtc.ToLocalTime():g}"
                : $"{result?.Code ?? "Failed"}: {result?.Message ?? await response.Content.ReadAsStringAsync()}";
        }
        finally
        {
            SaveWorldButton.IsEnabled = true;
        }
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (_serverId is null)
        {
            return;
        }

        BackupNowButton.IsEnabled = false;
        BackupStatusText.Text =
            "Saving world, waiting for files, creating ZIP, calculating SHA-256, and verifying…";
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/servers/{_serverId}/backups/create",
                new PalworldBackupCreateRequest(
                    DestinationPathText.Text,
                    false,
                    false,
                    null,
                    null));
            if (!response.IsSuccessStatusCode)
            {
                BackupStatusText.Text = await response.Content.ReadAsStringAsync();
                return;
            }

            var result = await response.Content.ReadFromJsonAsync<BackupResult>();
            BackupStatusText.Text = result is null
                ? "Backup failed: no result was returned."
                : $"Verified backup created: {result.ArchivePath}\nSHA-256: {result.Sha256}";
            await RefreshAsync();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            BackupStatusText.Text = $"Backup failed: {exception.Message}";
        }
        finally
        {
            BackupNowButton.IsEnabled = true;
        }
    }

    private void SchedulePreset_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (SchedulePresetBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            tag != "custom")
        {
            ScheduleExpressionBox.Text = tag;
        }
    }

    private async void SaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_serverId is null ||
            !int.TryParse(MaximumCountBox.Text, out var count) ||
            !int.TryParse(MaximumAgeBox.Text, out var age) ||
            !double.TryParse(
                MaximumStorageBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var storageGiB))
        {
            ScheduleStatusText.Text =
                "Enter valid schedule and retention values.";
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/v1/backup-schedules",
            new BackupScheduleRequest(
                _serverId.Value,
                ScheduleExpressionBox.Text,
                ScheduleEnabledBox.IsChecked == true,
                DestinationPathText.Text,
                count,
                age,
                checked((long)(storageGiB * Gibibyte)),
                KeepDailyBox.IsChecked == true,
                KeepWeeklyBox.IsChecked == true));
        ScheduleStatusText.Text = response.IsSuccessStatusCode
            ? "Backup schedule and retention policy saved."
            : await response.Content.ReadAsStringAsync();
        await RefreshAsync();
    }

    private async void ChooseDestination_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Palworld backup destination",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            DestinationPathText.Text = dialog.FolderName;
            await ValidateDestinationAsync();
        }
    }

    private async void ValidateDestination_Click(
        object sender,
        RoutedEventArgs e) =>
        await ValidateDestinationAsync();

    private async Task ValidateDestinationAsync()
    {
        if (_serverId is null)
        {
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/servers/{_serverId}/backups/destination/validate",
            new PalworldBackupCreateRequest(
                DestinationPathText.Text));
        var result =
            await response.Content.ReadFromJsonAsync<BackupDestinationValidationResult>();
        DestinationStatusText.Text = result is null
            ? await response.Content.ReadAsStringAsync()
            : $"{result.Code}: {result.Message} Available: {result.AvailableBytes / (double)Gibibyte:F1} GiB";
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(DestinationPathText.Text))
        {
            Process.Start(new ProcessStartInfo(
                "explorer.exe",
                DestinationPathText.Text)
            {
                UseShellExecute = true
            });
        }
        else
        {
            DestinationStatusText.Text =
                "The selected backup destination is not currently available.";
        }
    }

    private async void SaveBuiltInBackup_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_serverId is null)
        {
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/servers/{_serverId}/palworld/world-settings",
            new PalworldWorldSettingsUpdateRequest(
                new Dictionary<string, string>
                {
                    ["bIsUseBackupSaveData"] =
                        (BuiltInBackupBox.IsChecked == true)
                        .ToString()
                        .ToLowerInvariant()
                },
                false,
                false));
        var result =
            await response.Content.ReadFromJsonAsync<PalworldWorldSettingsUpdateResponse>();
        BackupStatusText.Text = result?.Message ??
                                await response.Content.ReadAsStringAsync();
    }

    private void BackupsGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is BackupRecord backup)
        {
            BackupNameBox.Text = backup.DisplayName ?? string.Empty;
            BackupNotesBox.Text = backup.Notes ?? string.Empty;
            ProtectBackupBox.IsChecked = backup.IsProtected;
            BackupStatusText.Text =
                $"{backup.ArchivePath}\nSHA-256: {backup.Sha256}";
        }
    }

    private async void SaveBackupDetails_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupRecord backup)
        {
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/backups/{backup.Id}/metadata",
            new BackupMetadataUpdateRequest(
                BackupNameBox.Text,
                BackupNotesBox.Text,
                ProtectBackupBox.IsChecked == true));
        BackupStatusText.Text = response.IsSuccessStatusCode
            ? "Backup name, notes, and protection state saved."
            : await response.Content.ReadAsStringAsync();
        await RefreshAsync();
    }

    private async void VerifyBackup_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupRecord backup)
        {
            return;
        }

        BackupStatusText.Text = "Verifying archive and every manifest hash…";
        using var response = await _httpClient.PostAsync(
            $"/api/v1/backups/{backup.Id}/verify",
            null);
        var result =
            await response.Content.ReadFromJsonAsync<BackupVerificationResult>();
        BackupStatusText.Text = result?.IsValid == true
            ? "Backup ZIP, manifest, file sizes, and SHA-256 hashes are valid."
            : $"Verification failed: {result?.Error}";
    }

    private async void RestoreBackup_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupRecord backup ||
            MessageBox.Show(
                "Palworld will stop. The Agent will verify this archive, create a safety backup of the current world, restore through staging, restart, and roll back if health verification fails. Continue?",
                "Restore Palworld Backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        BackupStatusText.Text = "Verifying and restoring with rollback protection…";
        using var response = await _httpClient.PostAsync(
            $"/api/v1/backups/{backup.Id}/restore",
            null);
        var result = await response.Content.ReadFromJsonAsync<OperationResult>();
        BackupStatusText.Text = result?.Success == true
            ? "Restore completed and server health verification passed."
            : $"Restore failed: {result?.Message}";
    }

    private async void DeleteBackup_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupRecord backup ||
            MessageBox.Show(
                $"Delete backup '{backup.DisplayName ?? backup.ArchivePath}'? Protected backups cannot be deleted.",
                "Delete Backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        using var response = await _httpClient.DeleteAsync(
            $"/api/v1/backups/{backup.Id}");
        var result = await response.Content.ReadFromJsonAsync<OperationResult>();
        BackupStatusText.Text = result?.Success == true
            ? "Backup deleted."
            : $"{result?.ErrorCode}: {result?.Message}";
        await RefreshAsync();
    }

    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupRecord backup ||
            !File.Exists(backup.ArchivePath))
        {
            BackupStatusText.Text = "Select an available backup to export.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "ZIP backup (*.zip)|*.zip",
            FileName = Path.GetFileName(backup.ArchivePath)
        };
        if (dialog.ShowDialog() == true)
        {
            File.Copy(backup.ArchivePath, dialog.FileName, true);
            File.WriteAllText(
                $"{dialog.FileName}.sha256",
                backup.Sha256);
            BackupStatusText.Text =
                $"Backup and SHA-256 exported to {dialog.FileName}.";
        }
    }
}
