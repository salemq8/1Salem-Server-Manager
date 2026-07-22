using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ServerManager.Contracts;
using ServerManager.Client.Shell;
using MessageBox = System.Windows.MessageBox;

namespace ServerManager.Client.Controls;

public partial class ApplicationUpdateControl : System.Windows.Controls.UserControl, IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromMinutes(30)
    };
    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private ApplicationUpdateStatusResponse? _status;
    private bool _refreshing;
    private bool _editing;

    public ApplicationUpdateControl()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync(false);
        Loaded += async (_, _) =>
        {
            await RefreshAsync(true);
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
        ChannelBox.SelectionChanged += (_, _) => _editing = true;
        AutomaticChecksBox.Click += (_, _) => _editing = true;
    }

    public event EventHandler? ExitForUpdateRequested;

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
             (!IsVisible || window?.WindowState == WindowState.Minimized)))
        {
            return;
        }

        _refreshing = true;
        try
        {
            _status = await _httpClient.GetFromJsonAsync<ApplicationUpdateStatusResponse>(
                "/api/v1/application-updates");
            if (_status is null)
            {
                return;
            }

            var installed = InstalledVersionDetector.Detect();
            CurrentVersionText.Text = installed.Client.Version ?? ProductInfo.Version;
            ClientVersionText.Text = installed.Client.Version ?? "Unavailable";
            AgentVersionText.Text = installed.Agent.Version ?? "Unavailable";
            UpdaterVersionText.Text = installed.Updater.Version ?? "Unavailable";
            ComponentStateText.Text = $"{installed.ReleaseChannel} · {installed.OverallState}";
            InstalledHistoryText.Text =
                $"Last successful update: {installed.LastSuccessfulUpdateUtc?.ToLocalTime().ToString("g") ?? "Not recorded"} · " +
                $"Previous: {installed.PreviousVersion ?? "None"} · " +
                $"Rollback: {installed.RollbackVersion ?? "None"}";
            LatestVersionText.Text = _status.LatestVersion ?? "Not checked";
            StageText.Text = _status.Stage.ToString();
            DownloadProgress.Value = _status.DownloadPercent;
            ProgressText.Text =
                $"{_status.DownloadPercent}% · {FormatBytes(_status.DownloadedBytes)}" +
                (_status.PackageSize is { } size ? $" / {FormatBytes(size)}" : string.Empty);
            SignatureText.Text = _status.CurrentBuildSigned
                ? "Current build: Authenticode signature detected."
                : "Current local build is unsigned. Update packages are still protected by HTTPS, exact size, and SHA-256 verification.";
            RollbackText.Text = _status.RollbackStatus ?? "No rollback has been needed.";
            ReleaseNotesText.Text = _status.ReleaseNotes ?? string.Empty;
            HistoryList.ItemsSource = _status.History.Select(item => new HistoryItem(
                $"{item.StartedAtUtc.ToLocalTime():g} · {item.Version} · {item.Result}" +
                (string.IsNullOrWhiteSpace(item.Message) ? string.Empty : $" · {item.Message}")))
                .ToArray();
            if (!_editing)
            {
                ChannelBox.SelectedIndex = _status.Channel switch
                {
                    ApplicationUpdateChannel.Preview => 1,
                    ApplicationUpdateChannel.Development => 2,
                    _ => 0
                };
                AutomaticChecksBox.IsChecked = _status.AutomaticChecksEnabled;
                _editing = false;
            }

            StatusText.Text = _status.LastError ??
                (_status.GameServerBusy
                    ? "A game server is active. The dashboard and Updater can update now; Agent files will be staged until a safe restart."
                    : $"Last checked: {_status.LastCheckedAtUtc?.ToLocalTime().ToString("g") ?? "Never"}");
            RenderActionAvailability(_status);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = $"Application update status unavailable: {exception.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RenderActionAvailability(
        ApplicationUpdateStatusResponse status)
    {
        var actionRunning = status.Stage is
            ApplicationUpdateStage.Checking or
            ApplicationUpdateStage.Downloading or
            ApplicationUpdateStage.Installing;
        CheckButton.IsEnabled = !actionRunning;
        DownloadButton.IsEnabled =
            !actionRunning &&
            status.IsUpdateAvailable &&
            (status.Stage is
                ApplicationUpdateStage.Available or
                ApplicationUpdateStage.Failed) &&
            string.IsNullOrWhiteSpace(status.StagedPackagePath);
        UpdateNowButton.IsEnabled =
            !actionRunning &&
            !string.IsNullOrWhiteSpace(status.StagedPackagePath) &&
            (status.Stage is
                ApplicationUpdateStage.Verified or
                ApplicationUpdateStage.ReadyToInstall);
        DownloadButton.ToolTip = DownloadButton.IsEnabled
            ? "Download and verify the available package."
            : "Check for an available update first.";
        UpdateNowButton.ToolTip = UpdateNowButton.IsEnabled
            ? "Install the verified package with rollback protection."
            : "Download and verify an update before installing.";
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var channel = ChannelBox.SelectedIndex switch
        {
            1 => ApplicationUpdateChannel.Preview,
            2 => ApplicationUpdateChannel.Development,
            _ => ApplicationUpdateChannel.Stable
        };
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/v1/application-updates/settings",
            new ApplicationUpdateSettingsRequest(
                channel,
                AutomaticChecksBox.IsChecked == true));
        StatusText.Text = response.IsSuccessStatusCode
            ? "Application update preferences saved."
            : $"Could not save update preferences: {response.ReasonPhrase}";
        _editing = false;
        await RefreshAsync(true);
    }

    private async void Check_Click(object sender, RoutedEventArgs e) =>
        await PostAndRefreshAsync("/api/v1/application-updates/check", new { });

    private async void Download_Click(object sender, RoutedEventArgs e) =>
        await PostAndRefreshAsync(
            "/api/v1/application-updates/download",
            new ApplicationUpdateActionRequest(false));

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/application-updates/prepare",
                new ApplicationUpdateActionRequest(false));
            var launch = await response.Content.ReadFromJsonAsync<ApplicationUpdateLaunchResponse>();
            if (launch is not { Success: true, UpdaterPath: not null })
            {
                StatusText.Text = launch?.Message ?? response.ReasonPhrase;
                return;
            }

            var start = new ProcessStartInfo
            {
                FileName = launch.UpdaterPath,
                WorkingDirectory = Path.GetDirectoryName(launch.UpdaterPath)!,
                UseShellExecute = true
            };
            foreach (var argument in launch.Arguments)
            {
                if (argument.Equals(
                        "--requires-elevation",
                        StringComparison.OrdinalIgnoreCase))
                {
                    start.Verb = "runas";
                    continue;
                }

                start.ArgumentList.Add(argument);
            }

            start.ArgumentList.Add("--wait-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            Process.Start(start);
            ExitForUpdateRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            System.ComponentModel.Win32Exception or
            InvalidOperationException)
        {
            StatusText.Text = $"Updater was not launched: {exception.Message}";
        }
    }

    private async Task PostAndRefreshAsync<T>(string path, T body)
    {
        try
        {
            StatusText.Text = "Working…";
            using var response = await _httpClient.PostAsJsonAsync(path, body);
            if (!response.IsSuccessStatusCode)
            {
                StatusText.Text = $"Update action failed: {response.ReasonPhrase}";
            }

            await RefreshAsync(true);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = $"Update action failed: {exception.Message}";
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024L * 1024L
            ? $"{bytes / (1024d * 1024d * 1024d):0.0} GB"
            : bytes >= 1024L * 1024L
                ? $"{bytes / (1024d * 1024d):0.0} MB"
                : bytes >= 1024L
                    ? $"{bytes / 1024d:0.0} KB"
                    : $"{bytes} B";

    private sealed record HistoryItem(string Display);
}
