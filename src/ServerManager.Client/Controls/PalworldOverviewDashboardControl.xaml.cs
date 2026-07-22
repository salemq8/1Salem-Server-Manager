using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using Brush = System.Windows.Media.Brush;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

public partial class PalworldOverviewDashboardControl : UserControl
{
    private readonly BoundedPalworldMetricHistory _history = new();
    private readonly BoundedServerActivityFeed _activity = new();
    private TimeSpan _selectedRange = TimeSpan.FromMinutes(5);
    private PalworldOverviewViewState _state;

    public PalworldOverviewDashboardControl()
    {
        InitializeComponent();
        _state = PalworldOverviewStateFactory.Loading(DateTimeOffset.UtcNow);
        SetState(_state, addHistorySample: false);
        SizeChanged += (_, _) => ApplyResponsiveLayout(ActualWidth);
    }

    public event EventHandler? RefreshRequested;
    public event EventHandler? StartRequested;
    public event EventHandler? SaveWorldRequested;
    public event EventHandler<string>? AnnouncementRequested;
    public event EventHandler? GracefulStopRequested;
    public event EventHandler? RestartRequested;
    public event EventHandler? BackupRequested;
    public event EventHandler? CopyLocalAddressRequested;
    public event EventHandler? CopyInternetAddressRequested;

    public void SetState(
        PalworldOverviewViewState state,
        bool addHistorySample = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        ServerNameText.Text = state.ServerName;
        VersionSummaryText.Text = state.VersionSummary;

        ApplyBadge(ProcessBadge, ProcessBadgeText, state.Process, "Palworld.Overview.Badge.ProcessRunning");
        ApplyBadge(PortBadge, PortBadgeText, state.Port, "Palworld.Overview.Badge.PortOpen");
        ApplyBadge(GameReadyBadge, GameReadyBadgeText, state.GameReady, "Palworld.Overview.Badge.GameReady");
        ApplyBadge(RestBadge, RestBadgeText, state.Rest, "Palworld.Overview.Badge.RestConnected");
        ApplyBadge(PlayitBadge, PlayitBadgeText, state.Playit, "Palworld.Overview.Badge.PlayitOnline");
        ApplyBadge(PublicMappingBadge, PublicMappingBadgeText, state.PublicMapping, "Palworld.Overview.Badge.PublicMappingValid");

        ApplyMetric(PlayersValueText, PlayersSupportText, state.Players);
        ApplyMetric(FpsValueText, FpsSupportText, state.ServerFps);
        ApplyMetric(UptimeValueText, UptimeSupportText, state.Uptime);
        ApplyMetric(CpuValueText, CpuSupportText, state.Cpu);
        ApplyMetric(RamValueText, RamSupportText, state.Ram);
        ApplyMetric(BackupValueText, BackupSupportText, state.LastBackup);
        ApplyResource(CpuSummaryValueText, CpuSummarySupportText, state.CpuSummary);
        ApplyResource(RamSummaryValueText, RamSummarySupportText, state.RamSummary);
        ApplyResource(DiskSummaryValueText, DiskSummarySupportText, state.DiskSummary);

        LocalAddressText.Text = state.Connection.LocalAddress;
        InternetAddressText.Text = state.Connection.InternetAddress;
        ProtocolTargetText.Text = state.Connection.ProtocolTarget;
        CopyLocalButton.IsEnabled = state.Connection.CanCopyLocal;
        CopyPublicButton.IsEnabled = state.Connection.CanCopyInternet;

        StartServerButton.Visibility = state.Actions.CanStart ? Visibility.Visible : Visibility.Collapsed;
        StartServerButton.IsEnabled = state.Actions.CanStart;
        RefreshButton.IsEnabled = state.Actions.CanRefresh;
        SaveWorldButton.IsEnabled = state.Actions.CanSaveWorld;
        AnnouncementButton.IsEnabled = state.Actions.CanAnnounce;
        GracefulStopButton.IsEnabled = state.Actions.CanGracefullyStop;
        RestartButton.IsEnabled = state.Actions.CanRestart;
        BackupNowButton.IsEnabled = state.Actions.CanBackup;
        CopyInternetButton.IsEnabled = state.Actions.CanCopyInternetAddress;

        if (addHistorySample &&
            (state.PlayersValue is not null || state.CpuPercentValue is not null || state.RamPercentValue is not null))
        {
            _history.Add(new PalworldMetricSample(
                state.CapturedAtUtc,
                state.PlayersValue,
                state.CpuPercentValue,
                state.RamPercentValue));
        }

        RefreshChart();
        ApplyResponsiveLayout(ActualWidth);
    }

    public void SetActivity(IEnumerable<ServerActivityItem> items)
    {
        _activity.Replace(items);
        var rows = _activity.Items
            .Select(item => new ActivityRow(
                item.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                item.DisplayName,
                string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizationService.Get("Palworld.Overview.Activity.Source"),
                    item.Actor),
                BuildActivityDetail(item)))
            .ToArray();
        RecentActivityList.ItemsSource = rows;
        RecentActivityList.Visibility = rows.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyActivityText.Visibility = rows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void ResetHistory()
    {
        _history.Clear();
        RefreshChart();
    }

    public void SetOperationStatus(string? message, bool isError = false)
    {
        OperationStatusText.Text = message ?? string.Empty;
        OperationStatusText.Foreground = ResolveBrush(
            isError ? "DangerBrush" : "MutedTextBrush");
    }

    public void SetBusy(bool busy)
    {
        DashboardHeaderGrid.IsEnabled = !busy;
        if (busy)
        {
            SetOperationStatus(LocalizationService.Get("Palworld.Overview.Loading"));
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (double.IsNaN(width) || width <= 0)
        {
            return;
        }

        MetricsGrid.Columns = PalworldOverviewStateFactory.GetMetricColumns(width);
        ResourceSummaryGrid.Columns = width < 760 ? 1 : 3;

        var stackContent = PalworldOverviewStateFactory.UseStackedContent(width);
        Grid.SetColumn(RecentActivityPanel, stackContent ? 0 : 1);
        Grid.SetRow(RecentActivityPanel, stackContent ? 1 : 0);
        Grid.SetColumnSpan(RecentActivityPanel, stackContent ? 2 : 1);

        var stackHeader = width < 920;
        Grid.SetColumn(QuickActionsPanel, stackHeader ? 0 : 1);
        Grid.SetRow(QuickActionsPanel, stackHeader ? 1 : 0);
        Grid.SetColumnSpan(QuickActionsPanel, stackHeader ? 2 : 1);

        var stackConnections = width < 760;
        ConnectionsGrid.RowDefinitions.Clear();
        if (stackConnections)
        {
            ConnectionsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ConnectionsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (ConnectionsGrid.Children.Count > 1)
            {
                var publicCard = ConnectionsGrid.Children[1];
                Grid.SetColumn(publicCard, 0);
                Grid.SetRow(publicCard, 1);
                publicCard.SetValue(MarginProperty, new Thickness(0, 8, 0, 0));
            }
        }
        else if (ConnectionsGrid.Children.Count > 1)
        {
            var publicCard = ConnectionsGrid.Children[1];
            Grid.SetColumn(publicCard, 1);
            Grid.SetRow(publicCard, 0);
            publicCard.SetValue(MarginProperty, new Thickness(8, 0, 0, 0));
        }
    }

    private void RefreshChart()
    {
        var now = _history.Count > 0
            ? _state.CapturedAtUtc
            : DateTimeOffset.UtcNow;
        ActivityGraph.SetSamples(
            _history.GetRange(_selectedRange, now),
            LocalizationService.Get("Palworld.Overview.NoHistory"));
    }

    private void ApplyBadge(
        Border border,
        TextBlock text,
        DashboardStatusBadge badge,
        string labelKey)
    {
        var label = LocalizationService.Get(labelKey);
        text.Text = $"● {label}";
        var brushKey = badge.Availability switch
        {
            DashboardMetricAvailability.Loading => "MutedTextBrush",
            DashboardMetricAvailability.Stale => "WarningBrush",
            DashboardMetricAvailability.Unavailable => "DisabledBrush",
            _ when badge.IsHealthy => "SuccessBrush",
            _ => "DangerBrush"
        };
        var brush = ResolveBrush(brushKey);
        text.Foreground = brush;
        border.BorderBrush = brush;
        border.ToolTip = badge.Availability switch
        {
            DashboardMetricAvailability.Loading => LocalizationService.Get("Palworld.Overview.Loading"),
            DashboardMetricAvailability.Stale => LocalizationService.Get("Palworld.Overview.Stale"),
            DashboardMetricAvailability.Unavailable => LocalizationService.Get("Palworld.Overview.Unavailable"),
            _ => label
        };
    }

    private void ApplyMetric(
        TextBlock primary,
        TextBlock supporting,
        DashboardMetricValue value)
    {
        primary.Text = value.Primary;
        supporting.Text = value.Supporting;
        primary.Foreground = AvailabilityBrush(value.Availability);
        supporting.Foreground = value.Availability == DashboardMetricAvailability.Available
            ? ResolveBrush("MutedTextBrush")
            : AvailabilityBrush(value.Availability);
    }

    private void ApplyResource(
        TextBlock primary,
        TextBlock supporting,
        DashboardResourceValue value)
    {
        primary.Text = value.Primary;
        supporting.Text = value.Supporting;
        primary.Foreground = AvailabilityBrush(value.Availability);
        supporting.Foreground = value.Availability == DashboardMetricAvailability.Available
            ? ResolveBrush("MutedTextBrush")
            : AvailabilityBrush(value.Availability);
    }

    private Brush AvailabilityBrush(DashboardMetricAvailability availability) =>
        ResolveBrush(availability switch
        {
            DashboardMetricAvailability.Loading => "MutedTextBrush",
            DashboardMetricAvailability.Available => "TextBrush",
            DashboardMetricAvailability.Stale => "WarningBrush",
            _ => "DisabledBrush"
        });

    private Brush ResolveBrush(string key) =>
        TryFindResource(key) as Brush ?? System.Windows.SystemColors.ControlTextBrush;

    private static string BuildActivityDetail(ServerActivityItem item)
    {
        var outcome = LocalizationService.Get(item.Succeeded
            ? "Palworld.Overview.Activity.Succeeded"
            : "Palworld.Overview.Activity.Failed");
        return string.IsNullOrWhiteSpace(item.Detail)
            ? outcome
            : $"{outcome} · {item.Detail}";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void StartServer_Click(object sender, RoutedEventArgs e) =>
        StartRequested?.Invoke(this, EventArgs.Empty);

    private void SaveWorld_Click(object sender, RoutedEventArgs e) =>
        SaveWorldRequested?.Invoke(this, EventArgs.Empty);

    private void Announcement_Click(object sender, RoutedEventArgs e)
    {
        AnnouncementPanel.Visibility = Visibility.Visible;
        AnnouncementTextBox.Focus();
    }

    private void SendAnnouncement_Click(object sender, RoutedEventArgs e)
    {
        var message = AnnouncementTextBox.Text.Trim();
        if (message.Length is < 1 or > 512)
        {
            return;
        }

        AnnouncementRequested?.Invoke(this, message);
        AnnouncementTextBox.Clear();
        AnnouncementPanel.Visibility = Visibility.Collapsed;
    }

    private void CancelAnnouncement_Click(object sender, RoutedEventArgs e)
    {
        AnnouncementTextBox.Clear();
        AnnouncementPanel.Visibility = Visibility.Collapsed;
    }

    private void GracefulStop_Click(object sender, RoutedEventArgs e) =>
        GracefulStopRequested?.Invoke(this, EventArgs.Empty);

    private void Restart_Click(object sender, RoutedEventArgs e) =>
        RestartRequested?.Invoke(this, EventArgs.Empty);

    private void BackupNow_Click(object sender, RoutedEventArgs e) =>
        BackupRequested?.Invoke(this, EventArgs.Empty);

    private void CopyLocal_Click(object sender, RoutedEventArgs e) =>
        CopyLocalAddressRequested?.Invoke(this, EventArgs.Empty);

    private void CopyInternet_Click(object sender, RoutedEventArgs e) =>
        CopyInternetAddressRequested?.Invoke(this, EventArgs.Empty);

    private void TimeRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimeRangeComboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string value &&
            TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var range))
        {
            _selectedRange = range;
        }

        if (ActivityGraph is not null)
        {
            RefreshChart();
        }
    }

    private sealed record ActivityRow(
        string Timestamp,
        string DisplayName,
        string Source,
        string Detail);
}
