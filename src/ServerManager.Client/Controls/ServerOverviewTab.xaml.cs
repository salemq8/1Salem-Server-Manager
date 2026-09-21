using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Threading;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

/// <summary>One row for a connected player. Detail stays LTR: it holds technical values.</summary>
public sealed record PlayerRow(string Name, string Detail);

/// <summary>
/// Answers, in order: is it running, is anyone on, how long, how heavy, are backups fine, can
/// people reach it. Nothing here names a PID, a port or a process tree — those are one
/// disclosure away.
/// </summary>
public partial class ServerOverviewTab : UserControl
{
    private const string EmDash = "—";
    private readonly ServerDetailContext _context = ServerDetailContext.Shared;
    private readonly HttpClient _httpClient;
    private readonly DispatcherTimer _playerTimer;
    private readonly ObservableCollection<PlayerRow> _players = [];

    public ServerOverviewTab()
    {
        InitializeComponent();
        _httpClient = _context.CreateClient(TimeSpan.FromSeconds(20));
        PlayerList.ItemsSource = _players;
        _context.Changed += (_, _) => Dispatcher.Invoke(Render);
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Render);
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(Render);
        _playerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _playerTimer.Tick += async (_, _) => await LoadPlayersAsync();
        Loaded += async (_, _) =>
        {
            Render();
            _playerTimer.Start();
            await LoadPlayersAsync();
        };
        Unloaded += (_, _) => _playerTimer.Stop();
    }

    private void Render()
    {
        if (AtAGlanceHeading is null)
        {
            return;
        }

        AtAGlanceHeading.Text = LocalizationService.Get("ServerDetail.AtAGlance");
        PlayersLabel.Text = LocalizationService.Get("Metric.Players");
        UptimeLabel.Text = LocalizationService.Get("Metric.Uptime");
        CpuLabel.Text = LocalizationService.Get("Metric.Cpu");
        MemoryLabel.Text = LocalizationService.Get("Metric.Memory");
        VersionLabel.Text = LocalizationService.Get("Metric.Version");
        BackupsHeading.Text = LocalizationService.Get("Home.Backups");
        RemoteHeading.Text = LocalizationService.Get("Home.RemoteAccess");
        PlayersHeading.Text = LocalizationService.Get("ServerDetail.WhoIsOnline");
        AdvancedSection.Header = LocalizationService.Get("Advanced.Title");

        var card = _context.Card;
        var source = _context.Source;
        if (card is null || source is null)
        {
            PlayersValue.Text = EmDash;
            UptimeValue.Text = EmDash;
            CpuValue.Text = EmDash;
            MemoryValue.Text = EmDash;
            VersionValue.Text = EmDash;
            BackupText.Text = LocalizationService.Get("Status.Unavailable");
            RemoteText.Text = LocalizationService.Get("Status.Unavailable");
            RemoteAddress.Visibility = Visibility.Collapsed;
            AdvancedBody.Text = LocalizationService.Get("Status.Unavailable");
            AdvancedManagement.Visibility = Visibility.Collapsed;
            PlayersEmpty.Text = LocalizationService.Get("Status.Unavailable");
            return;
        }

        PlayersValue.Text = card.Players;
        UptimeValue.Text = card.Uptime;
        CpuValue.Text = FormatCpu(source);
        MemoryValue.Text = card.Memory;
        VersionValue.Text = string.IsNullOrWhiteSpace(source.InstalledVersion)
            ? EmDash
            : source.InstalledVersion!;

        BackupText.Text = card.BackupLabel;
        BackupDot.Fill = ToneBrush(card.BackupTone);
        RemoteText.Text = card.RemoteAccessLabel;
        RemoteDot.Fill = ToneBrush(card.RemoteAccessTone);
        RemoteAddress.Text = card.InternetAddress ?? string.Empty;
        RemoteAddress.Visibility = string.IsNullOrWhiteSpace(card.InternetAddress)
            ? Visibility.Collapsed
            : Visibility.Visible;

        RenderAdvanced(source);
        RenderPlayersEmptyState(source);
    }

    /// <summary>
    /// CPU is only meaningful while the process exists; a stopped server reports 0, which
    /// would read as "idle" rather than "not running".
    /// </summary>
    private static string FormatCpu(ServerDashboardCard source) =>
        source.ProcessId is null
            ? EmDash
            : string.Format(CultureInfo.CurrentCulture, "{0:0}%", source.CpuPercent);

    private void RenderPlayersEmptyState(ServerDashboardCard source)
    {
        if (_players.Count > 0)
        {
            PlayersEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        PlayersEmpty.Visibility = Visibility.Visible;
        PlayersEmpty.Text = source.PlayersOnline switch
        {
            null => LocalizationService.Get("ServerDetail.PlayersUnknown"),
            0 => LocalizationService.Get("ServerDetail.NobodyOnline"),
            _ => LocalizationService.Get("ServerDetail.PlayerListUnavailable")
        };
    }

    /// <summary>
    /// Straight from Build 5 telemetry — the game process id, tree size and management state
    /// are reported by the agent and must not be recomputed here.
    /// </summary>
    private void RenderAdvanced(ServerDashboardCard source)
    {
        var lines = new List<string>
        {
            $"{LocalizationService.Get("Advanced.RootProcess")}  {Describe(source.ProcessId)}  {source.RootExecutableName}",
            $"{LocalizationService.Get("Advanced.GameProcess")}  {Describe(source.GameProcessId)}  {source.GameExecutableName}",
            $"{LocalizationService.Get("Advanced.ChildProcesses")}  {source.ChildProcessCount}",
            $"{LocalizationService.Get("Advanced.Threads")}  {source.ThreadCount}",
            $"{LocalizationService.Get("Advanced.Port")}  {source.Port}  ({(source.LocalPortOpen ? LocalizationService.Get("Advanced.PortOpen") : LocalizationService.Get("Advanced.PortClosed"))})"
        };
        AdvancedBody.Text = string.Join(Environment.NewLine, lines);

        if (source.PalworldManagement is { } management)
        {
            AdvancedManagement.Visibility = Visibility.Visible;
            AdvancedManagement.Text = string.Join(
                Environment.NewLine,
                $"{LocalizationService.Get("Advanced.Management")}  {management.State}",
                $"{LocalizationService.Get("Advanced.RestPort")}  {management.RestApiPort}  " +
                $"({(source.RestManagementConnected ? LocalizationService.Get("Advanced.Connected") : LocalizationService.Get("Advanced.Disconnected"))})",
                management.StatusMessage);
        }
        else
        {
            AdvancedManagement.Visibility = Visibility.Collapsed;
        }
    }

    private static string Describe(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? EmDash;

    private async Task LoadPlayersAsync()
    {
        var source = _context.Source;
        if (source is null || source.Game != GameType.Minecraft)
        {
            // Only Minecraft publishes a roster today; Palworld reports a count only.
            if (_players.Count > 0)
            {
                _players.Clear();
            }

            if (source is not null)
            {
                RenderPlayersEmptyState(source);
            }

            return;
        }

        try
        {
            var snapshot = await _httpClient.GetFromJsonAsync<MinecraftPlayersSnapshot>(
                $"/api/v1/servers/{_context.ServerId}/minecraft/players");
            _players.Clear();
            foreach (var name in snapshot?.OnlinePlayers ?? [])
            {
                var role = snapshot!.Operators.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? LocalizationService.Get("ServerDetail.Operator")
                    : string.Empty;
                _players.Add(new PlayerRow(name, role));
            }

            RenderPlayersEmptyState(source);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _players.Clear();
            RenderPlayersEmptyState(source);
        }
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
}
