using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Controls;

public enum DashboardMetricAvailability
{
    Loading = 0,
    Available = 1,
    Unavailable = 2,
    Stale = 3
}

public sealed record DashboardMetricValue(
    string Primary,
    string Supporting,
    DashboardMetricAvailability Availability);

public sealed record DashboardStatusBadge(
    bool IsHealthy,
    DashboardMetricAvailability Availability);

public sealed record PalworldQuickActionState(
    bool CanRefresh,
    bool CanStart,
    bool CanSaveWorld,
    bool CanAnnounce,
    bool CanGracefullyStop,
    bool CanRestart,
    bool CanBackup,
    bool CanCopyInternetAddress);

public sealed record DashboardResourceValue(
    string Primary,
    string Supporting,
    DashboardMetricAvailability Availability);

public sealed record PalworldConnectionSummary(
    string LocalAddress,
    string InternetAddress,
    string ProtocolTarget,
    bool CanCopyLocal,
    bool CanCopyInternet);

public sealed record PalworldOverviewViewState(
    string ServerName,
    string VersionSummary,
    DashboardStatusBadge Process,
    DashboardStatusBadge Port,
    DashboardStatusBadge GameReady,
    DashboardStatusBadge Rest,
    DashboardStatusBadge Playit,
    DashboardStatusBadge PublicMapping,
    DashboardMetricValue Players,
    DashboardMetricValue ServerFps,
    DashboardMetricValue Uptime,
    DashboardMetricValue Cpu,
    DashboardMetricValue Ram,
    DashboardMetricValue LastBackup,
    DashboardResourceValue CpuSummary,
    DashboardResourceValue RamSummary,
    DashboardResourceValue DiskSummary,
    PalworldConnectionSummary Connection,
    PalworldQuickActionState Actions,
    int? PlayersValue,
    double? CpuPercentValue,
    double? RamPercentValue,
    DateTimeOffset CapturedAtUtc,
    bool IsStale);

public sealed record PalworldMetricSample(
    DateTimeOffset TimestampUtc,
    int? Players,
    double? CpuPercent,
    double? RamPercent);

public sealed class BoundedPalworldMetricHistory
{
    public const int MaximumSamples = 1_800;
    private readonly Queue<PalworldMetricSample> _items = [];

    public int Count => _items.Count;

    public void Clear() => _items.Clear();

    public void Add(PalworldMetricSample sample)
    {
        _items.Enqueue(sample);
        while (_items.Count > MaximumSamples)
        {
            _items.Dequeue();
        }
    }

    public IReadOnlyList<PalworldMetricSample> GetRange(
        TimeSpan range,
        DateTimeOffset now) =>
        _items
            .Where(item => item.TimestampUtc >= now - range)
            .ToArray();
}

public sealed class BoundedServerActivityFeed
{
    public const int MaximumItems = 40;
    private IReadOnlyList<ServerActivityItem> _items = [];

    public IReadOnlyList<ServerActivityItem> Items => _items;

    public void Replace(IEnumerable<ServerActivityItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items
            .OrderByDescending(item => item.TimestampUtc)
            .Take(MaximumItems)
            .ToArray();
    }
}

public static class PalworldOverviewStateFactory
{
    public const double MinimumSupportedContentWidth = 620;
    public static TimeSpan StaleAfter { get; } = TimeSpan.FromSeconds(10);

    public static PalworldOverviewViewState Loading(DateTimeOffset now)
    {
        var metric = new DashboardMetricValue(
            "—",
            LocalizationService.Get("Palworld.Overview.Loading"),
            DashboardMetricAvailability.Loading);
        var badge = new DashboardStatusBadge(
            false,
            DashboardMetricAvailability.Loading);
        var resource = new DashboardResourceValue(
            "—",
            LocalizationService.Get("Palworld.Overview.Loading"),
            DashboardMetricAvailability.Loading);
        return new PalworldOverviewViewState(
            LocalizationService.Get("Palworld.Overview.Loading"),
            LocalizationService.Get("Palworld.Overview.LoadingLiveData"),
            badge,
            badge,
            badge,
            badge,
            badge,
            badge,
            metric,
            metric,
            metric,
            metric,
            metric,
            metric,
            resource,
            resource,
            resource,
            new PalworldConnectionSummary("—", "—", "—", false, false),
            new PalworldQuickActionState(true, false, false, false, false, false, false, false),
            null,
            null,
            null,
            now,
            false);
    }

    public static PalworldOverviewViewState Unavailable(
        string serverName,
        string reason,
        DateTimeOffset now)
    {
        var metric = new DashboardMetricValue(
            "—",
            reason,
            DashboardMetricAvailability.Unavailable);
        var badge = new DashboardStatusBadge(
            false,
            DashboardMetricAvailability.Unavailable);
        var resource = new DashboardResourceValue(
            "—",
            reason,
            DashboardMetricAvailability.Unavailable);
        return new PalworldOverviewViewState(
            serverName,
            reason,
            badge,
            badge,
            badge,
            badge,
            badge,
            badge,
            metric,
            metric,
            metric,
            metric,
            metric,
            metric,
            resource,
            resource,
            resource,
            new PalworldConnectionSummary("—", "—", "—", false, false),
            new PalworldQuickActionState(true, false, false, false, false, false, false, false),
            null,
            null,
            null,
            now,
            false);
    }

    public static PalworldOverviewViewState Create(
        ServerDashboardCard server,
        DashboardSnapshot dashboard,
        MemoryPerformancePolicySnapshot? memoryPolicy,
        long systemDriveTotalBytes,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(dashboard);
        var dashboardStale = now - dashboard.CapturedAtUtc > StaleAfter;
        var management = server.PalworldManagement;
        var managementStale = management?.IsStale == true ||
                              management is not null &&
                              now - management.CapturedAtUtc > StaleAfter;
        var running = server.State == ServerState.Running;
        var restConnected = server.RestManagementConnected &&
                            management?.State == PalworldManagementState.Online;
        var players = management?.PlayersOnline ?? server.PlayersOnline;
        var maximumPlayers = management?.MaximumPlayers ?? server.MaximumPlayers;
        var playerMetric = players is { } current && maximumPlayers is { } maximum
            ? Metric(
                $"{current}/{maximum}",
                LocalizationService.Get("Palworld.Overview.LivePlayers"),
                managementStale || dashboardStale)
            : Unavailable();
        var fpsMetric = management?.ServerFps is { } fps
            ? Metric(
                $"{fps:0.0}",
                management.ServerFrameTimeMilliseconds is { } frameTime
                    ? $"{frameTime:0.00} ms"
                    : LocalizationService.Get("Palworld.Overview.LiveRestMetric"),
                managementStale)
            : Unavailable();
        var uptime = management?.UptimeSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : server.Uptime;
        var uptimeMetric = uptime is { } value
            ? Metric(
                FormatDuration(value),
                LocalizationService.Get("Palworld.Overview.ProcessUptime"),
                management is not null ? managementStale : dashboardStale)
            : Unavailable();
        var cpuMetric = running
            ? Metric(
                $"{server.CpuPercent:0.0}%",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    LocalizationService.Get("Palworld.Overview.LogicalProcessors"),
                    Environment.ProcessorCount),
                dashboardStale)
            : Unavailable(LocalizationService.Get("Palworld.Overview.ServerStopped"));
        var ramMetric = running
            ? Metric(
                FormatBytes(server.WorkingSetBytes),
                memoryPolicy?.WarningThresholdBytes is { } warning
                    ? string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        LocalizationService.Get("Palworld.Overview.WarningAt"),
                        FormatBytes(warning))
                    : LocalizationService.Get("Palworld.Overview.WorkingSet"),
                dashboardStale)
            : Unavailable(LocalizationService.Get("Palworld.Overview.ServerStopped"));
        var backupMetric = server.LastBackupAtUtc is { } backup
            ? Metric(
                backup.ToLocalTime().ToString("g"),
                FormatAge(now, backup),
                dashboardStale)
            : Unavailable(LocalizationService.Get("Palworld.Overview.NoBackup"));
        var warningThreshold = memoryPolicy?.WarningThresholdBytes;
        var ramPercent = warningThreshold is > 0
            ? server.WorkingSetBytes * 100d / warningThreshold.Value
            : 0;
        var diskUsed = systemDriveTotalBytes > 0
            ? Math.Max(0, systemDriveTotalBytes - dashboard.SystemDriveFreeBytes)
            : 0;

        return new PalworldOverviewViewState(
            server.Name,
            $"{management?.ServerVersion ?? server.InstalledVersion ?? "—"}  ·  " +
            $"{server.RuntimeVersion ?? LocalizationService.Get("Palworld.Overview.RuntimeUnavailable")}",
            Badge(running, dashboardStale),
            Badge(server.LocalPortOpen, dashboardStale),
            Badge(running && server.LocalPortOpen, dashboardStale),
            management is null && !server.RestManagementConnected
                ? new DashboardStatusBadge(false, DashboardMetricAvailability.Unavailable)
                : Badge(restConnected, managementStale || dashboardStale),
            string.IsNullOrWhiteSpace(server.PlayitState) && !server.PlayitOnline
                ? new DashboardStatusBadge(false, DashboardMetricAvailability.Unavailable)
                : Badge(server.PlayitOnline, dashboardStale),
            string.IsNullOrWhiteSpace(server.InternetAddress)
                ? new DashboardStatusBadge(false, DashboardMetricAvailability.Unavailable)
                : Badge(server.PublicTunnelVerified, dashboardStale),
            playerMetric,
            fpsMetric,
            uptimeMetric,
            cpuMetric,
            ramMetric,
            backupMetric,
            new DashboardResourceValue(
                running ? $"{server.CpuPercent:0.0}%" : "—",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    LocalizationService.Get("Palworld.Overview.LogicalProcessors"),
                    Environment.ProcessorCount),
                running ? Availability(dashboardStale) : DashboardMetricAvailability.Unavailable),
            new DashboardResourceValue(
                running ? FormatBytes(server.WorkingSetBytes) : "—",
                warningThreshold is { } threshold
                    ? string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        LocalizationService.Get("Palworld.Overview.PercentOfWarning"),
                        ramPercent,
                        FormatBytes(threshold))
                    : LocalizationService.Get("Palworld.Overview.WarningUnavailable"),
                running ? Availability(dashboardStale) : DashboardMetricAvailability.Unavailable),
            new DashboardResourceValue(
                systemDriveTotalBytes > 0 ? FormatBytes(diskUsed) : "—",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    LocalizationService.Get("Palworld.Overview.DiskFree"),
                    FormatBytes(dashboard.SystemDriveFreeBytes)),
                systemDriveTotalBytes > 0
                    ? Availability(dashboardStale)
                    : DashboardMetricAvailability.Unavailable),
            new PalworldConnectionSummary(
                server.LocalAddress ?? "—",
                server.InternetAddress ?? "—",
                $"UDP → 127.0.0.1:{server.Port}",
                !string.IsNullOrWhiteSpace(server.LocalAddress),
                !string.IsNullOrWhiteSpace(server.InternetAddress)),
            new PalworldQuickActionState(
                true,
                server.Actions.CanStart,
                running && restConnected,
                running && restConnected,
                server.Actions.CanStop,
                server.Actions.CanRestart,
                server.Actions.CanBackup,
                !string.IsNullOrWhiteSpace(server.InternetAddress)),
            players,
            running ? server.CpuPercent : null,
            running && warningThreshold is > 0 ? ramPercent : null,
            dashboard.CapturedAtUtc,
            dashboardStale || managementStale);
    }

    public static int GetMetricColumns(double width) => width switch
    {
        >= 960 => 6,
        >= 760 => 3,
        _ => 2
    };

    public static bool UseStackedContent(double width) => width < 1_060;

    public static IReadOnlyList<string> GetQuickActionOrder(bool rightToLeft)
    {
        string[] actions =
        [
            "Refresh",
            "SaveWorld",
            "Announcement",
            "GracefulStop",
            "Restart",
            "BackupNow",
            "CopyInternetAddress"
        ];
        return rightToLeft ? actions.Reverse().ToArray() : actions;
    }

    private static DashboardStatusBadge Badge(bool healthy, bool stale) =>
        new(healthy, Availability(stale));

    private static DashboardMetricValue Metric(
        string primary,
        string supporting,
        bool stale) =>
        new(
            primary,
            stale
                ? $"{supporting} · {LocalizationService.Get("Palworld.Overview.Stale")}"
                : supporting,
            Availability(stale));

    private static DashboardMetricValue Unavailable(string? message = null) =>
        new(
            "—",
            message ?? LocalizationService.Get("Palworld.Overview.Unavailable"),
            DashboardMetricAvailability.Unavailable);

    private static DashboardMetricAvailability Availability(bool stale) =>
        stale
            ? DashboardMetricAvailability.Stale
            : DashboardMetricAvailability.Available;

    private static string FormatDuration(TimeSpan duration) => duration.TotalDays >= 1
        ? $"{(int)duration.TotalDays}d {duration.Hours}h"
        : duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{duration.Minutes}m {duration.Seconds}s";

    private static string FormatAge(DateTimeOffset now, DateTimeOffset value)
    {
        var age = now - value;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalDays >= 1
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.Get("Palworld.Overview.DaysAgo"),
                (int)age.TotalDays)
            : age.TotalHours >= 1
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    LocalizationService.Get("Palworld.Overview.HoursAgo"),
                    (int)age.TotalHours)
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    LocalizationService.Get("Palworld.Overview.MinutesAgo"),
                    Math.Max(0, (int)age.TotalMinutes));
    }

    internal static string FormatBytes(long value)
    {
        const double gibibyte = 1024d * 1024 * 1024;
        const double mebibyte = 1024d * 1024;
        return value >= gibibyte
            ? $"{value / gibibyte:0.0} GiB"
            : $"{value / mebibyte:0} MiB";
    }
}
