using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

/// <summary>One backup, shaped for display. Technical paths and hashes stay out of the list.</summary>
public sealed record BackupRow(
    Guid Id,
    string Title,
    string Detail,
    string StatusLabel,
    UiStatusTone Tone,
    bool CanRestore,
    string RestoreLabel,
    string VerifyLabel,
    string ProtectedLabel,
    Visibility ProtectedVisibility);

/// <summary>
/// This server's backups. Restore is confirmed before it runs and is only offered for a
/// backup the agent reports as complete — restoring a failed archive would overwrite a good
/// world with a broken one.
/// </summary>
public partial class ServerBackupsTab : UserControl
{
    private readonly ServerDetailContext _context = ServerDetailContext.Shared;
    private readonly HttpClient _httpClient;
    private readonly ObservableCollection<BackupRow> _rows = [];
    private bool _active;
    private bool _busy;

    public ServerBackupsTab()
    {
        InitializeComponent();
        _httpClient = _context.CreateClient(TimeSpan.FromMinutes(10));
        BackupList.ItemsSource = _rows;
        _context.Changed += (_, _) => Dispatcher.Invoke(Localize);
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            Localize();
            _ = LoadAsync();
        });
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(Localize);
        Loaded += (_, _) => Localize();
    }

    public void SetActive(bool active)
    {
        _active = active;

        // Bind the embedded editors only while this tab is showing, so hidden controls do
        // not keep polling the agent.
        var id = active && _context.Card is not null ? _context.ServerId : (Guid?)null;
        var isPalworld = _context.Source?.Game == GameType.Palworld;
        SaveToolsCard.Visibility = active && isPalworld
            ? Visibility.Visible
            : Visibility.Collapsed;
        SaveTools.SetServer(isPalworld ? id : null);
        RestorePoints.SetServer(id);

        if (active)
        {
            Localize();
            _ = LoadAsync();
        }
    }

    private void Localize()
    {
        if (SummaryHeading is null)
        {
            return;
        }

        SummaryHeading.Text = LocalizationService.Get("Home.Backups");
        HistoryHeading.Text = LocalizationService.Get("Backups.History");
        CreateBackupButton.Content = LocalizationService.Get("Action.CreateBackup");
        OpenLocationButton.Content = LocalizationService.Get("Action.OpenLocation");
        EmptyText.Text = LocalizationService.Get("Backups.None");
        ToolsExpander.Header = LocalizationService.Get("Backups.Tools");

        var card = _context.Card;
        SummaryText.Text = card?.BackupLabel ?? LocalizationService.Get("Status.Unavailable");
        SummaryDot.Fill = ToneBrush(card?.BackupTone ?? UiStatusTone.Neutral);
        CreateBackupButton.IsEnabled = _context.Actions.CanBackup && !_busy;
    }

    private async Task LoadAsync()
    {
        if (!_active || _context.Card is null)
        {
            return;
        }

        try
        {
            var records = await _httpClient.GetFromJsonAsync<BackupRecord[]>(
                $"/api/v1/servers/{_context.ServerId}/backups") ?? [];
            _rows.Clear();
            foreach (var record in records.OrderByDescending(item => item.CreatedAtUtc))
            {
                _rows.Add(ToRow(record));
            }

            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _rows.Clear();
            EmptyText.Visibility = Visibility.Visible;
            EmptyText.Text = LocalizationService.Get("Error.ServiceUnavailable");
        }

        Localize();
    }

    private BackupRow ToRow(BackupRecord record)
    {
        var complete = record.Status == BackupStatus.Completed;
        var taken = record.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var named = !string.IsNullOrWhiteSpace(record.DisplayName);

        // An unnamed backup is titled by when it was taken, so repeating the timestamp in the
        // detail line below would say the same thing twice.
        var parts = new List<string>();
        if (named)
        {
            parts.Add(taken);
        }

        parts.Add(ServerPresentation.FormatMemory(record.SizeBytes));
        parts.Add(LocalizationService.Format("Backups.Files", record.FileCount));

        return new BackupRow(
            record.Id,
            named ? record.DisplayName! : taken,
            string.Join("   ·   ", parts),
            LocalizationService.Get($"Backups.Status.{record.Status}"),
            complete ? UiStatusTone.Positive : UiStatusTone.Caution,
            complete && _context.Actions.CanRestore && !_busy,
            LocalizationService.Get("Action.Restore"),
            LocalizationService.Get("Action.Verify"),
            LocalizationService.Get("Backups.Protected"),
            record.IsProtected ? Visibility.Visible : Visibility.Collapsed);
    }

    private static Brush ToneBrush(UiStatusTone tone)
    {
        var key = tone switch
        {
            UiStatusTone.Positive => "SuccessBrush",
            UiStatusTone.Caution => "WarningBrush",
            UiStatusTone.Negative => "DangerBrush",
            _ => "TextSecondaryBrush"
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!_context.Actions.CanBackup || _busy)
        {
            return;
        }

        await PostAsync($"/api/v1/servers/{_context.ServerId}/backups", "Action.CreateBackup");
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id } || _busy)
        {
            return;
        }

        if (!_context.Actions.CanRestore)
        {
            return;
        }

        // Restoring replaces the live world. It always asks first.
        var confirmed = MessageBox.Show(
            LocalizationService.Get("Confirm.Restore"),
            LocalizationService.Get("Action.Restore"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (confirmed)
        {
            await PostAsync($"/api/v1/backups/{id}/restore", "Action.Restore");
        }
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id } && !_busy)
        {
            await PostAsync($"/api/v1/backups/{id}/verify", "Action.Verify");
        }
    }

    private async Task PostAsync(string route, string titleKey)
    {
        _busy = true;
        Localize();
        try
        {
            var response = await _httpClient.PostAsJsonAsync(route, new { });
            NotificationService.Publish(
                response.IsSuccessStatusCode ? NotificationKind.Success : NotificationKind.Error,
                LocalizationService.Get(titleKey),
                response.IsSuccessStatusCode
                    ? LocalizationService.Get("Action.Done")
                    : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get(titleKey),
                exception.Message);
        }
        finally
        {
            _busy = false;
            await DashboardFeed.Shared.RefreshAsync();
            await LoadAsync();
        }
    }

    private async void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var servers = await _httpClient.GetFromJsonAsync<GameServerDefinition[]>(
                "/api/v1/servers") ?? [];
            var definition = servers.FirstOrDefault(item => item.Id == _context.ServerId);
            if (definition is null)
            {
                return;
            }

            var path = Path.Combine(definition.RootPath, "backups");
            Directory.CreateDirectory(path);
            var info = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
            info.ArgumentList.Add(path);
            Process.Start(info);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get("Action.OpenLocation"),
                exception.Message);
        }
    }
}
