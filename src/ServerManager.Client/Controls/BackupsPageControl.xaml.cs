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
using AutomationProperties = System.Windows.Automation.AutomationProperties;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

/// <summary>Protection state for one server, shown on the global Backups page.</summary>
public sealed record ServerBackupHealthRow(
    Guid ServerId,
    string ServerName,
    string HealthLabel,
    UiStatusTone Tone,
    string Detail,
    string OpenLabel);

/// <summary>One backup from any server, for the cross-server list.</summary>
public sealed record GlobalBackupRow(
    Guid Id,
    Guid ServerId,
    string ServerName,
    string Title,
    string Detail,
    string StatusLabel,
    UiStatusTone Tone,
    bool CanRestore,
    bool CanDelete,
    string RestoreLabel,
    string VerifyLabel,
    string DeleteLabel,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Backups across every server. Deliberately different from the per-server tab: this answers
/// "is anything unprotected?", which a single server's page cannot. The headline is the worst
/// server's state, never the newest backup.
/// </summary>
public partial class BackupsPageControl : UserControl
{
    private readonly DashboardFeed _feed = DashboardFeed.Shared;
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromMinutes(10));
    private readonly ObservableCollection<ServerBackupHealthRow> _serverRows = [];
    private readonly ObservableCollection<GlobalBackupRow> _backupRows = [];
    private bool _busy;

    public BackupsPageControl()
    {
        InitializeComponent();
        DataContext = this;
        ServerHealthList.ItemsSource = _serverRows;
        RecentList.ItemsSource = _backupRows;
        _feed.PropertyChanged += (_, _) => Dispatcher.Invoke(Render);
        _feed.Servers.CollectionChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            Render();
            _ = LoadAsync();
        });
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            Render();
            _ = LoadAsync();
        });
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(Render);
        Loaded += OnLoaded;
    }

    /// <summary>Raised when the person wants to open a server's own Backups tab.</summary>
    public event EventHandler<Guid>? BackupRequested;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _feed.Start();
        await _feed.RefreshAsync();
        Render();
        await LoadAsync();
    }

    private void Render()
    {
        if (CreateBackupButton is null)
        {
            return;
        }

        CreateBackupButton.Content = LocalizationService.Get("Action.CreateBackup");
        ByServerHeading.Text = LocalizationService.Get("Backups.ByServer");
        RecentHeading.Text = LocalizationService.Get("Backups.Recent");
        RecentEmpty.Text = LocalizationService.Get("Backups.None");
        MenuOpenLocation.Header = LocalizationService.Get("Action.OpenLocation");
        MenuVerifyAll.Header = LocalizationService.Get("Backups.VerifyNewest");
        AutomationProperties.SetName(
            MoreButton,
            LocalizationService.Get("Action.MoreActions"));
        MoreButton.ToolTip = LocalizationService.Get("Action.MoreActions");

        var stateShown = StateView.Apply(_feed, "Empty.NoServers", "Empty.NoServersMessage");
        ContentRoot.Visibility = stateShown ? Visibility.Collapsed : Visibility.Visible;
        if (stateShown)
        {
            return;
        }

        RenderServerHealth();
    }

    private void RenderServerHealth()
    {
        var now = _feed.Snapshot?.CapturedAtUtc ?? DateTimeOffset.UtcNow;
        _serverRows.Clear();

        var healths = new List<BackupHealth>();
        foreach (var server in _feed.Servers)
        {
            var last = server.Source?.LastBackupAtUtc;
            var health = ServerPresentation.ClassifyBackup(last, false, now);
            healths.Add(health);
            var described = ServerPresentation.DescribeBackupHealth(health);
            _serverRows.Add(new ServerBackupHealthRow(
                server.ServerId,
                server.Name,
                described.Label,
                described.Tone,
                server.BackupLabel,
                LocalizationService.Get("Action.Open")));
        }

        var overall = ServerPresentation.AggregateBackupHealth(healths);
        var summary = ServerPresentation.DescribeBackupHealth(overall);
        HealthText.Text = summary.Label;
        HealthDot.Fill = ToneBrush(summary.Tone);
        HealthDetail.Text = BuildHealthDetail(healths);

        CreateBackupButton.IsEnabled = !_busy && _feed.Servers.Any(
            server => server.Source?.Actions.CanBackup == true);
    }

    /// <summary>
    /// Names how many servers are in which state rather than summarising one of them. With a
    /// single server it reads as that server's own state.
    /// </summary>
    private string BuildHealthDetail(IReadOnlyList<BackupHealth> healths)
    {
        if (healths.Count == 0)
        {
            return LocalizationService.Get("Empty.NoServersMessage");
        }

        var protectedCount = healths.Count(health => health == BackupHealth.Healthy);
        var attention = healths.Count(health =>
            health is BackupHealth.Overdue or BackupHealth.DueSoon or BackupHealth.Failed);

        if (attention == 0)
        {
            return LocalizationService.Format("Backups.AllProtected", healths.Count);
        }

        return LocalizationService.Format(
            "Backups.NeedAttention",
            attention,
            healths.Count,
            protectedCount);
    }

    private async Task LoadAsync()
    {
        if (_feed.Servers.Count == 0)
        {
            _backupRows.Clear();
            return;
        }

        var rows = new List<GlobalBackupRow>();
        foreach (var server in _feed.Servers.ToArray())
        {
            try
            {
                var records = await _httpClient.GetFromJsonAsync<BackupRecord[]>(
                    $"/api/v1/servers/{server.ServerId}/backups") ?? [];
                foreach (var record in records)
                {
                    rows.Add(ToRow(server, record));
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A server whose backups cannot be listed simply contributes none; the
                // per-server health card above still reports what the dashboard knows.
            }
        }

        _backupRows.Clear();
        foreach (var row in rows.OrderByDescending(item => item.CreatedAtUtc))
        {
            _backupRows.Add(row);
        }

        RecentEmpty.Visibility = _backupRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private GlobalBackupRow ToRow(ServerCardViewModel server, BackupRecord record)
    {
        var complete = record.Status == BackupStatus.Completed;
        var canBackupActions = server.Source?.Actions;
        var taken = record.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var detail = string.Join(
            "   ·   ",
            taken,
            ServerPresentation.FormatMemory(record.SizeBytes),
            LocalizationService.Format("Backups.Files", record.FileCount));

        return new GlobalBackupRow(
            record.Id,
            server.ServerId,
            server.Name,
            string.IsNullOrWhiteSpace(record.DisplayName) ? taken : record.DisplayName!,
            detail,
            LocalizationService.Get($"Backups.Status.{record.Status}"),
            complete ? UiStatusTone.Positive : UiStatusTone.Caution,
            complete && canBackupActions?.CanRestore == true && !_busy,
            !record.IsProtected && !_busy,
            LocalizationService.Get("Action.Restore"),
            LocalizationService.Get("Action.Verify"),
            LocalizationService.Get("Action.Delete"),
            record.CreatedAtUtc);
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

    // --- actions ----------------------------------------------------------------

    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.IsOpen = true;
    }

    private void OpenServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid serverId })
        {
            BackupRequested?.Invoke(this, serverId);
        }
    }

    /// <summary>
    /// With one server this backs it up directly. With several, the person chooses which —
    /// backing up "all" silently would be a different, much heavier operation than the
    /// button says.
    /// </summary>
    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var candidates = _feed.Servers
            .Where(server => server.Source?.Actions.CanBackup == true)
            .ToArray();
        if (candidates.Length == 0 || _busy)
        {
            return;
        }

        if (candidates.Length > 1)
        {
            // Send them to the server that most needs it rather than guessing.
            var now = _feed.Snapshot?.CapturedAtUtc ?? DateTimeOffset.UtcNow;
            var worst = candidates
                .OrderByDescending(server => ServerPresentation.ClassifyBackup(
                    server.Source?.LastBackupAtUtc, false, now))
                .First();
            BackupRequested?.Invoke(this, worst.ServerId);
            return;
        }

        await PostAsync(
            $"/api/v1/servers/{candidates[0].ServerId}/backups",
            "Action.CreateBackup");
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id } || _busy)
        {
            return;
        }

        var row = _backupRows.FirstOrDefault(item => item.Id == id);
        if (row is null || !row.CanRestore)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            LocalizationService.Format("Confirm.RestoreServer", row.ServerName),
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

    private async void VerifyNewest_Click(object sender, RoutedEventArgs e)
    {
        var newest = _backupRows.FirstOrDefault();
        if (newest is not null && !_busy)
        {
            await PostAsync($"/api/v1/backups/{newest.Id}/verify", "Action.Verify");
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id } || _busy)
        {
            return;
        }

        var row = _backupRows.FirstOrDefault(item => item.Id == id);
        if (row is null || !row.CanDelete)
        {
            return;
        }

        // Deleting a backup cannot be undone, so it names exactly what is going.
        var confirmed = MessageBox.Show(
            LocalizationService.Format("Confirm.DeleteBackup", row.ServerName, row.Title),
            LocalizationService.Get("Action.Delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        _busy = true;
        Render();
        try
        {
            var response = await _httpClient.DeleteAsync($"/api/v1/backups/{id}");
            Notify(response.IsSuccessStatusCode, "Action.Delete", response);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get("Action.Delete"),
                exception.Message);
        }
        finally
        {
            _busy = false;
            await _feed.RefreshAsync();
            Render();
            await LoadAsync();
        }
    }

    private async void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var server = _feed.Servers.FirstOrDefault();
        if (server is null)
        {
            return;
        }

        try
        {
            var servers = await _httpClient.GetFromJsonAsync<GameServerDefinition[]>(
                "/api/v1/servers") ?? [];
            var definition = servers.FirstOrDefault(item => item.Id == server.ServerId);
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

    private async Task PostAsync(string route, string titleKey)
    {
        _busy = true;
        Render();
        try
        {
            var response = await _httpClient.PostAsJsonAsync(route, new { });
            Notify(response.IsSuccessStatusCode, titleKey, response);
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
            await _feed.RefreshAsync();
            Render();
            await LoadAsync();
        }
    }

    private static void Notify(bool ok, string titleKey, HttpResponseMessage response) =>
        NotificationService.Publish(
            ok ? NotificationKind.Success : NotificationKind.Error,
            LocalizationService.Get(titleKey),
            ok
                ? LocalizationService.Get("Action.Done")
                : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));

    private async void State_RetryRequested(object? sender, EventArgs e)
    {
        await _feed.RefreshAsync();
        Render();
        await LoadAsync();
    }
}
