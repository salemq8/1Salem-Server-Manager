using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Client.Shell;

namespace ServerManager.Client.Controls;

/// <summary>
/// The small set of states the UI is allowed to talk about. Internal vocabulary such as
/// "process adopted" or "SCM pending" never reaches a label; it belongs in Advanced details.
/// </summary>
public enum UiStatus
{
    Unknown,
    Running,
    Stopped,
    Starting,
    Stopping,
    Busy,
    NeedsAttention
}

public enum UiStatusTone
{
    Neutral,
    Positive,
    Caution,
    Negative
}

/// <summary>
/// Backup protection, ordered from best to worst so aggregating is a maximum. Unknown is
/// first because "we have no information" must never outrank a real problem.
/// </summary>
public enum BackupHealth
{
    Unknown = 0,
    Healthy = 1,
    DueSoon = 2,
    Overdue = 3,
    Failed = 4
}

/// <summary>
/// Pure mapping from backend state to what a person reads. Kept free of WPF types so the
/// rules can be unit tested directly.
/// </summary>
public static class ServerPresentation
{
    public static UiStatus MapState(ServerState state) => state switch
    {
        ServerState.Running => UiStatus.Running,
        ServerState.Stopped => UiStatus.Stopped,
        ServerState.Starting => UiStatus.Starting,
        ServerState.Stopping => UiStatus.Stopping,
        ServerState.Restarting => UiStatus.Starting,
        ServerState.Updating or ServerState.BackingUp or ServerState.Restoring => UiStatus.Busy,
        ServerState.Crashed or ServerState.Error => UiStatus.NeedsAttention,
        _ => UiStatus.Unknown
    };

    public static UiStatusTone ToneOf(UiStatus status) => status switch
    {
        UiStatus.Running => UiStatusTone.Positive,
        UiStatus.NeedsAttention => UiStatusTone.Negative,
        UiStatus.Starting or UiStatus.Stopping or UiStatus.Busy => UiStatusTone.Caution,
        _ => UiStatusTone.Neutral
    };

    public static string LabelFor(UiStatus status) => status switch
    {
        UiStatus.Running => LocalizationService.Get("Status.Running"),
        UiStatus.Stopped => LocalizationService.Get("Status.Stopped"),
        UiStatus.Starting => LocalizationService.Get("Status.Starting"),
        UiStatus.Stopping => LocalizationService.Get("Status.Stopping"),
        UiStatus.Busy => LocalizationService.Get("Status.Checking"),
        UiStatus.NeedsAttention => LocalizationService.Get("Status.NeedsAttention"),
        _ => LocalizationService.Get("Status.Unavailable")
    };

    /// <summary>
    /// The one obvious action for a card in this state. Everything else belongs in the
    /// overflow menu, so a card never shows a row of equally loud buttons.
    /// </summary>
    public static string PrimaryActionFor(UiStatus status) => status switch
    {
        UiStatus.Running => LocalizationService.Get("Action.Manage"),
        UiStatus.Stopped => LocalizationService.Get("Action.Start"),
        _ => LocalizationService.Get("Action.Manage")
    };

    public static bool PrimaryActionStartsServer(UiStatus status) =>
        status == UiStatus.Stopped;

    /// <summary>
    /// Players as "3 / 32", or a dash when the server is not reporting. Reading order under
    /// RTL is handled by the view (MetricValueStyle pins FlowDirection), not by embedding
    /// invisible control characters here: WPF ignores Unicode isolates, and putting them in
    /// the string would also leak into accessible names and clipboard text.
    /// </summary>
    public static string FormatPlayers(int? online, int? maximum)
    {
        if (online is null)
        {
            return "—";
        }

        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        return maximum is > 0
            ? $"{online} / {maximum}"
            : online.Value.ToString(culture);
    }

    public static string FormatUptime(TimeSpan? uptime)
    {
        if (uptime is not { } value || value <= TimeSpan.Zero)
        {
            return "—";
        }

        return value.TotalDays >= 1
            ? $"{(int)value.TotalDays}d {value.Hours}h"
            : value.TotalHours >= 1
                ? $"{(int)value.TotalHours}h {value.Minutes}m"
                : $"{value.Minutes}m";
    }

    public static string FormatMemory(long bytes)
    {
        if (bytes <= 0)
        {
            return "—";
        }

        var gigabytes = bytes / 1024d / 1024d / 1024d;
        return gigabytes >= 1
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "{0:0.0} GB",
                gigabytes)
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "{0:0} MB",
                bytes / 1024d / 1024d);
    }

    /// <summary>
    /// Backup health in plain words. Anything older than a week is worth surfacing; never
    /// having backed up at all is worth surfacing more loudly.
    /// </summary>
    /// <summary>
    /// How protected a server's data is. Ordered worst-last so an aggregate can simply take
    /// the maximum: a fleet is only as protected as its least-protected server.
    /// </summary>
    public static BackupHealth ClassifyBackup(
        DateTimeOffset? lastBackupUtc,
        bool lastAttemptFailed,
        DateTimeOffset nowUtc)
    {
        if (lastAttemptFailed)
        {
            return BackupHealth.Failed;
        }

        if (lastBackupUtc is not { } last)
        {
            return BackupHealth.Overdue;
        }

        var age = nowUtc - last;
        return age.TotalDays switch
        {
            >= 7 => BackupHealth.Overdue,
            >= 3 => BackupHealth.DueSoon,
            _ => BackupHealth.Healthy
        };
    }

    /// <summary>
    /// The worst state across every server. Reporting the newest backup as if it covered the
    /// fleet is exactly the lie this exists to prevent: one server backed up an hour ago does
    /// not protect another that has not been backed up in months.
    /// </summary>
    public static BackupHealth AggregateBackupHealth(IEnumerable<BackupHealth> perServer)
    {
        ArgumentNullException.ThrowIfNull(perServer);
        var worst = BackupHealth.Unknown;
        var sawAny = false;
        foreach (var health in perServer)
        {
            sawAny = true;
            if (health > worst)
            {
                worst = health;
            }
        }

        return sawAny ? worst : BackupHealth.Unknown;
    }

    public static (string Label, UiStatusTone Tone) DescribeBackupHealth(BackupHealth health) =>
        health switch
        {
            BackupHealth.Healthy => (LocalizationService.Get("BackupHealth.Healthy"), UiStatusTone.Positive),
            BackupHealth.DueSoon => (LocalizationService.Get("BackupHealth.DueSoon"), UiStatusTone.Caution),
            BackupHealth.Overdue => (LocalizationService.Get("BackupHealth.Overdue"), UiStatusTone.Caution),
            BackupHealth.Failed => (LocalizationService.Get("BackupHealth.Failed"), UiStatusTone.Negative),
            _ => (LocalizationService.Get("BackupHealth.Unknown"), UiStatusTone.Neutral)
        };

    public static (string Label, UiStatusTone Tone) DescribeBackup(
        DateTimeOffset? lastBackupUtc,
        DateTimeOffset nowUtc)
    {
        if (lastBackupUtc is not { } last)
        {
            return (LocalizationService.Get("Backup.None"), UiStatusTone.Caution);
        }

        var age = nowUtc - last;
        if (age < TimeSpan.FromHours(24))
        {
            return (LocalizationService.Get("Backup.Today"), UiStatusTone.Positive);
        }

        var days = (int)Math.Floor(age.TotalDays);
        return (
            LocalizationService.Format("Backup.DaysAgo", days),
            days >= 7 ? UiStatusTone.Caution : UiStatusTone.Positive);
    }

    /// <summary>
    /// Remote access in one phrase. A server with no internet address was never set up to be
    /// reachable from outside, which is not the same as one whose tunnel is down, and saying
    /// "Offline" for it would invent a problem.
    /// </summary>
    public static (string Label, UiStatusTone Tone) DescribeRemoteAccess(
        bool tunnelOnline,
        string? internetAddress)
    {
        if (string.IsNullOrWhiteSpace(internetAddress))
        {
            return (LocalizationService.Get("RemoteAccess.NotSetUp"), UiStatusTone.Neutral);
        }

        return tunnelOnline
            ? (LocalizationService.Get("RemoteAccess.Reachable"), UiStatusTone.Positive)
            : (LocalizationService.Get("RemoteAccess.Unreachable"), UiStatusTone.Caution);
    }

    public static string DescribeGame(GameType game) => game switch
    {
        GameType.Minecraft => LocalizationService.Get("Minecraft"),
        GameType.Palworld => LocalizationService.Get("Palworld"),
        _ => game.ToString()
    };

    /// <summary>
    /// The single sentence Home leads with. A person should be able to read only this line
    /// and know whether they need to do anything.
    /// </summary>
    public static (string Headline, UiStatusTone Tone) SummarizeHealth(
        bool agentConnected,
        IReadOnlyList<UiStatus> serverStatuses,
        bool remoteAccessOnline,
        bool anyServerRunning,
        bool connectionAttempted = true,
        bool remoteAccessConfigured = false,
        bool anyBackupNeedsAttention = false)
    {
        if (!agentConnected)
        {
            // "Connecting" is only honest before a poll has come back. Once one has failed,
            // saying it again would leave the person waiting on something that is not coming.
            return connectionAttempted
                ? (LocalizationService.Get("Home.Unreachable"), UiStatusTone.Negative)
                : (LocalizationService.Get("Home.Connecting"), UiStatusTone.Caution);
        }

        if (serverStatuses.Any(status => status == UiStatus.NeedsAttention))
        {
            return (LocalizationService.Get("Home.NeedsAttention"), UiStatusTone.Negative);
        }

        // Remote access being off is only a problem for a server that was set up to be
        // reachable from the internet. A LAN-only server has no tunnel by design, and
        // warning about it forever would train the person to ignore this line.
        if (anyServerRunning && remoteAccessConfigured && !remoteAccessOnline)
        {
            return (LocalizationService.Get("Home.NeedsAttention"), UiStatusTone.Caution);
        }

        // A backup that is months old is exactly the kind of thing this one line exists to
        // surface. Saying "Everything looks good" above a card reading "Last backup 63 days
        // ago" makes the summary worthless.
        if (anyBackupNeedsAttention)
        {
            return (LocalizationService.Get("Home.BackupOverdue"), UiStatusTone.Caution);
        }

        return (LocalizationService.Get("Home.AllGood"), UiStatusTone.Positive);
    }
}
