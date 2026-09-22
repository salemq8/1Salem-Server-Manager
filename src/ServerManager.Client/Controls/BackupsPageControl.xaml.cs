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
/// <remarks>
/// The *Name members are what a screen reader announces for each row's buttons: every row
/// has an "Open", so the visible label alone cannot say which server it acts on.
/// </remarks>
public sealed record ServerBackupHealthRow(
    Guid ServerId,
    string ServerName,
    string HealthLabel,
    UiStatusTone Tone,
    string Detail,
    string OpenLabel,
    string OpenName);

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
    DateTimeOffset CreatedAtUtc,
    string RestoreName,
    string VerifyName,
    string DeleteName);

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
    private bool _listFailed;
    private int _loading;

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
        // Backups taken elsewhere (Server Detail, a schedule) must be listed when the person
        // comes back here, not only after an action on this page.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _ = LoadAsync();
            }
        };
        Loaded += OnLoaded;
    }

    /// <summary>Raised when the person wants to open a server's own Backups tab.</summary>
    public event EventHandler<Guid>? BackupRequested;

    /// <summary>Re-reads the backup list, e.g. from the top-bar Refresh.</summary>
    public void Reload() => _ = LoadAsync();

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
        RecentEmpty.Text = LocalizationService.Get(
            _listFailed ? "Backups.ListUnavailable" : "Backups.NoneAnywhere");
        // While something runs every row action is unavailable, and should look it.
        RecentList.IsEnabled = !_busy;
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

        var healths = new List<BackupHealth>();
        var rows = new List<ServerBackupHealthRow>();
        foreach (var server in _feed.Servers)
        {
            var last = server.Source?.LastBackupAtUtc;
            var health = ServerPresentation.ClassifyBackup(last, false, now);
            healths.Add(health);
            var described = ServerPresentation.DescribeBackupHealth(health);
            rows.Add(new ServerBackupHealthRow(
                server.ServerId,
                server.Name,
                described.Label,
                described.Tone,
                server.BackupLabel,
                LocalizationService.Get("Action.Open"),
                LocalizationService.Format("Backups.OpenServerName", server.Name)));
        }

        // Render runs on every feed notification — about a dozen per poll. Rebuilding the
        // rows each time threw away their buttons, so keyboard and screen-reader focus was
        // lost every two seconds. Rows are records, so unchanged ones are left alone.
        SyncRows(_serverRows, rows);

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

        if (Interlocked.Exchange(ref _loading, 1) == 1)
        {
            return;
        }

        try
        {
            var rows = new List<GlobalBackupRow>();
            var failed = false;
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
                    // Listing failed, which is not the same as having no backups. The rows
                    // that did load are still shown; the empty text must not claim "none".
                    failed = true;
                }
            }

            _listFailed = failed;
            SyncRows(_backupRows, [.. rows.OrderByDescending(item => item.CreatedAtUtc)]);
            RecentEmpty.Text = LocalizationService.Get(
                _listFailed ? "Backups.ListUnavailable" : "Backups.NoneAnywhere");
            RecentEmpty.Visibility = _backupRows.Count == 0 || _listFailed
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    /// <summary>
    /// Brings a bound list in line with <paramref name="wanted"/> while touching only the rows
    /// that changed, so containers — and whatever has focus inside them — survive a refresh.
    /// </summary>
    private static void SyncRows<T>(ObservableCollection<T> current, IReadOnlyList<T> wanted)
    {
        if (current.SequenceEqual(wanted))
        {
            return;
        }

        for (var index = 0; index < wanted.Count; index++)
        {
            if (index >= current.Count)
            {
                current.Add(wanted[index]);
            }
            else if (!EqualityComparer<T>.Default.Equals(current[index], wanted[index]))
            {
                current[index] = wanted[index];
            }
        }

        while (current.Count > wanted.Count)
        {
            current.RemoveAt(current.Count - 1);
        }
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

        var title = string.IsNullOrWhiteSpace(record.DisplayName) ? taken : record.DisplayName!;
        return new GlobalBackupRow(
            record.Id,
            server.ServerId,
            server.Name,
            title,
            detail,
            LocalizationService.Get($"Backups.Status.{record.Status}"),
            complete ? UiStatusTone.Positive : UiStatusTone.Caution,
            complete && canBackupActions?.CanRestore == true,
            !record.IsProtected,
            LocalizationService.Get("Action.Restore"),
            LocalizationService.Get("Action.Verify"),
            LocalizationService.Get("Action.Delete"),
            record.CreatedAtUtc,
            LocalizationService.Format("Backups.RestoreName", title, server.Name),
            LocalizationService.Format("Backups.VerifyName", title, server.Name),
            LocalizationService.Format("Backups.DeleteName", title, server.Name));
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

        // Names the server and the exact backup: this replaces the live world.
        var confirmed = MessageBox.Show(
            LocalizationService.Format("Confirm.RestoreBackup", row.ServerName, row.Title),
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
                LocalizationService.Get("Error.ServiceUnavailable"));
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
                LocalizationService.Get("Error.FolderUnavailable"));
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
                LocalizationService.Get("Error.ServiceUnavailable"));
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
            ok ? LocalizationService.Get("Action.Done") : DescribeFailure(response.StatusCode));

    /// <summary>
    /// A person reads why it did not work, in their language — not a bare "409". The agent's
    /// own detail stays in its log.
    /// </summary>
    public static string DescribeFailure(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Conflict => LocalizationService.Get("Backups.Error.Busy"),
        System.Net.HttpStatusCode.NotFound => LocalizationService.Get("Backups.Error.NotFound"),
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            LocalizationService.Get("Error.ServiceUnavailable"),
        _ => LocalizationService.Get("Backups.Error.Failed")
    };

    private async void State_RetryRequested(object? sender, EventArgs e)
    {
        await _feed.RefreshAsync();
        Render();
        await LoadAsync();
    }
}
