using System.Diagnostics;
using System.Globalization;
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

/// <summary>
/// The proven download / verify / install flow. Build 6 hosts it inside Settings > Updates,
/// so only its presentation changed: every endpoint, availability rule and the Update Now
/// launch-then-exit sequence are exactly as they were in Build 5.
/// </summary>
public partial class ApplicationUpdateControl : System.Windows.Controls.UserControl, IDisposable
{
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromMinutes(30));
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
            Localize();
            await RefreshAsync(true);
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
        ChannelBox.SelectionChanged += (_, _) => _editing = true;
        AutomaticChecksBox.Click += (_, _) => _editing = true;
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            Localize();
            if (_status is not null)
            {
                Render(_status);
            }
        });
    }

    public event EventHandler? ExitForUpdateRequested;

    /// <summary>Raised after every refresh attempt, whether or not it reached the agent.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>The last status the agent reported, or null if it has never answered.</summary>
    public ApplicationUpdateStatusResponse? CurrentStatus => _status;

    /// <summary>True when the most recent refresh could not reach the agent.</summary>
    public bool LastRefreshFailed { get; private set; }

    /// <summary>Refreshes now, regardless of visibility, e.g. after an external Check.</summary>
    public Task RefreshNowAsync() => RefreshAsync(true);

    public void Dispose()
    {
        _timer.Stop();
        _httpClient.Dispose();
    }

    /// <summary>
    /// The one-line summary for the simple Updates card. It never claims "up to date" unless
    /// a check actually happened and did not fail, and it never puts a same-looking version
    /// under the installed one: the visible version stays 1.5 across builds, so "Version 1.5
    /// is available" under "Version 1.5" would read as nonsense.
    /// </summary>
    public static string DescribeSimpleStatus(
        ApplicationUpdateStatusResponse? status,
        bool refreshFailed)
    {
        if (status is null)
        {
            return LocalizationService.Get(
                refreshFailed ? "Updates.Unavailable" : "Updates.Loading");
        }

        switch (status.Stage)
        {
            case ApplicationUpdateStage.Checking:
                return LocalizationService.Get("Updates.Stage.Checking");
            case ApplicationUpdateStage.Downloading:
                return LocalizationService.Get("Updates.Stage.Downloading");
            case ApplicationUpdateStage.Verified:
            case ApplicationUpdateStage.ReadyToInstall:
                return LocalizationService.Get("Updates.ReadyToInstall");
            case ApplicationUpdateStage.Installing:
                return LocalizationService.Get("Updates.Stage.Installing");
            case ApplicationUpdateStage.Failed:
                return LocalizationService.Get("Updates.LastStepFailed");
        }

        if (status.IsUpdateAvailable)
        {
            return LocalizationService.Get("Updates.Available");
        }

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            return LocalizationService.Get("Updates.LastStepFailed");
        }

        return status.LastCheckedAtUtc is { } checkedAt
            ? LocalizationService.Format(
                "Updates.UpToDate",
                checkedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : LocalizationService.Get("Updates.NotCheckedYet");
    }

    public static string DescribeStage(ApplicationUpdateStage stage)
    {
        var key = $"Updates.Stage.{stage}";
        return LocalizationService.HasKey(key) ? LocalizationService.Get(key) : stage.ToString();
    }

    private void Localize()
    {
        CurrentVersionLabel.Text = LocalizationService.Get("Updates.CurrentVersion");
        LatestVersionLabel.Text = LocalizationService.Get("Updates.LatestVersion");
        StageLabel.Text = LocalizationService.Get("Updates.Status");
        CheckButton.Content = LocalizationService.Get("Updates.CheckNow");
        DownloadButton.Content = LocalizationService.Get("Updates.Download");
        UpdateNowButton.Content = LocalizationService.Get("Updates.Install");
        ChannelLabel.Text = LocalizationService.Get("Updates.Channel");
        ChannelStable.Content = LocalizationService.Get("Updates.Channel.Stable");
        ChannelPreview.Content = LocalizationService.Get("Updates.Channel.Preview");
        ChannelDevelopment.Content = LocalizationService.Get("Updates.Channel.Development");
        AutomaticChecksBox.Content = LocalizationService.Get("Updates.AutomaticChecks");
        SavePreferencesButton.Content = LocalizationService.Get("Updates.SavePreferences");
        ComponentsLabel.Text = LocalizationService.Get("Updates.Components");
        ClientLabel.Text = LocalizationService.Get("Updates.Component.Client");
        AgentLabel.Text = LocalizationService.Get("Updates.Component.Agent");
        UpdaterLabel.Text = LocalizationService.Get("Updates.Component.Updater");
        ChannelStateLabel.Text = LocalizationService.Get("Updates.Component.Channel");
        ReleaseNotesLabel.Text = LocalizationService.Get("Updates.ReleaseNotes");
        HistoryLabel.Text = LocalizationService.Get("Updates.History");
        if (_status is null && string.IsNullOrEmpty(LatestVersionText.Text))
        {
            LatestVersionText.Text = LocalizationService.Get("Updates.NotChecked");
        }
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

            LastRefreshFailed = false;
            Render(_status);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            LastRefreshFailed = true;
            ShowStatus(LocalizationService.Get("Updates.Unavailable"), exception.Message);
        }
        finally
        {
            _refreshing = false;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Render(ApplicationUpdateStatusResponse status)
    {
        var installed = InstalledVersionDetector.Detect();
        CurrentVersionText.Text = installed.Client.Version ?? ProductInfo.Version;
        ClientVersionText.Text = FormatComponent(installed.Client);
        AgentVersionText.Text = FormatComponent(installed.Agent);
        UpdaterVersionText.Text = FormatComponent(installed.Updater);
        ComponentStateText.Text = $"{DescribeChannel(installed.ReleaseChannel)} · {installed.OverallState}";
        InstalledHistoryText.Text = LocalizationService.Format(
            "Updates.InstalledHistory",
            installed.LastSuccessfulUpdateUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                ?? LocalizationService.Get("Updates.NotRecorded"),
            FormatBuild(installed.PreviousVersion, installed.PreviousBuildRevision),
            FormatBuild(installed.RollbackVersion, installed.RollbackBuildRevision));
        LatestVersionText.Text = status.LatestVersion is null
            ? LocalizationService.Get("Updates.NotChecked")
            : FormatBuild(status.LatestVersion, status.LatestBuildRevision);
        StageText.Text = DescribeStage(status.Stage);
        DownloadProgress.Value = status.DownloadPercent;
        ProgressText.Text =
            $"{status.DownloadPercent}% · {FormatBytes(status.DownloadedBytes)}" +
            (status.PackageSize is { } size ? $" / {FormatBytes(size)}" : string.Empty);
        SignatureText.Text = LocalizationService.Get(
            status.CurrentBuildSigned ? "Updates.Signed" : "Updates.Unsigned");
        RollbackText.Text = status.RollbackStatus ?? LocalizationService.Get("Updates.NoRollback");
        ReleaseNotesText.Text = status.ReleaseNotes ?? string.Empty;
        HistoryList.ItemsSource = status.History.Select(item => new HistoryItem(
            $"{item.StartedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · " +
            $"{FormatBuild(item.Version, item.BuildRevision)} · {DescribeStage(item.Result)}" +
            (string.IsNullOrWhiteSpace(item.Message) ? string.Empty : $" · {item.Message}")))
            .ToArray();
        if (!_editing)
        {
            ChannelBox.SelectedIndex = status.Channel switch
            {
                ApplicationUpdateChannel.Preview => 1,
                ApplicationUpdateChannel.Development => 2,
                _ => 0
            };
            AutomaticChecksBox.IsChecked = status.AutomaticChecksEnabled;
            _editing = false;
        }

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            // The agent's own wording is kept for troubleshooting, not shown as the message.
            ShowStatus(LocalizationService.Get("Updates.LastStepFailed"), status.LastError);
        }
        else if (status.GameServerBusy)
        {
            ShowStatus(LocalizationService.Get("Updates.GameServerBusy"), null);
        }
        else
        {
            ShowStatus(
                LocalizationService.Format(
                    "Updates.LastChecked",
                    status.LastCheckedAtUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                        ?? LocalizationService.Get("Updates.Never")),
                null);
        }

        RenderActionAvailability(status);
    }

    private void ShowStatus(string message, string? detail)
    {
        StatusText.Text = message;
        StatusText.ToolTip = string.IsNullOrWhiteSpace(detail) ? null : detail;
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
        DownloadButton.ToolTip = LocalizationService.Get(
            DownloadButton.IsEnabled ? "Updates.DownloadHint" : "Updates.DownloadDisabledHint");
        UpdateNowButton.ToolTip = LocalizationService.Get(
            UpdateNowButton.IsEnabled ? "Updates.InstallHint" : "Updates.InstallDisabledHint");
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var channel = ChannelBox.SelectedIndex switch
        {
            1 => ApplicationUpdateChannel.Preview,
            2 => ApplicationUpdateChannel.Development,
            _ => ApplicationUpdateChannel.Stable
        };
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/application-updates/settings",
                new ApplicationUpdateSettingsRequest(
                    channel,
                    AutomaticChecksBox.IsChecked == true));
            if (response.IsSuccessStatusCode)
            {
                ShowStatus(LocalizationService.Get("Updates.PreferencesSaved"), null);
            }
            else
            {
                ShowStatus(
                    LocalizationService.Get("Updates.PreferencesFailed"),
                    $"{(int)response.StatusCode} {response.ReasonPhrase}");
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            ShowStatus(LocalizationService.Get("Updates.PreferencesFailed"), exception.Message);
        }

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
                ShowStatus(
                    LocalizationService.Get("Updates.PrepareFailed"),
                    launch?.Message ?? response.ReasonPhrase);
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
            ShowStatus(LocalizationService.Get("Updates.LaunchFailed"), exception.Message);
        }
    }

    private async Task PostAndRefreshAsync<T>(string path, T body)
    {
        try
        {
            ShowStatus(LocalizationService.Get("Updates.Working"), null);
            using var response = await _httpClient.PostAsJsonAsync(path, body);
            if (!response.IsSuccessStatusCode)
            {
                ShowStatus(
                    LocalizationService.Get("Updates.ActionFailed"),
                    $"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await RefreshAsync(true);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            ShowStatus(LocalizationService.Get("Updates.ActionFailed"), exception.Message);
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

    private static string FormatComponent(InstalledComponentVersion component) =>
        component.Version is null
            ? LocalizationService.Get("Status.Unavailable")
            : FormatBuild(component.Version, component.BuildRevision);

    private static string FormatBuild(string? version, int? buildRevision) =>
        version is null
            ? LocalizationService.Get("Updates.None")
            : buildRevision is > 0
                ? $"{version} · {LocalizationService.Format("Settings.BuildValue", buildRevision)}"
                : version;

    private static string DescribeChannel(string channel)
    {
        var key = $"Updates.Channel.{channel}";
        return LocalizationService.HasKey(key) ? LocalizationService.Get(key) : channel;
    }

    /// <remarks>
    /// A ListBoxItem's automation name falls back to the item's ToString(); a record's would
    /// read "HistoryItem { Display = … }" to a screen reader.
    /// </remarks>
    private sealed record HistoryItem(string Display)
    {
        public override string ToString() => Display;
    }
}
