using System.ComponentModel;
using System.Windows;
using Application = System.Windows.Application;
using ServerManager.Client.Shell;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

public partial class HomePageControl : UserControl, INotifyPropertyChanged
{
    private const string EmDash = "—";
    private readonly DashboardFeed _feed = DashboardFeed.Shared;
    private UiStatusTone _healthTone = UiStatusTone.Neutral;

    public HomePageControl()
    {
        InitializeComponent();
        DataContext = this;
        ServerCards.ItemsSource = _feed.Servers;
        _feed.PropertyChanged += (_, _) => Dispatcher.Invoke(Render);
        _feed.Servers.CollectionChanged += (_, _) => Dispatcher.Invoke(Render);
        LocalizationService.LanguageChanged += OnLanguageChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        Loaded += OnLoaded;
    }

    private void OnThemeChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            foreach (var card in _feed.Servers)
            {
                card.RefreshThemeBindings();
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HealthTone)));
            Render();
        });

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a card asks to open its server; the shell handles navigation.</summary>
    public event EventHandler<Guid>? ServerOpenRequested;

    public event EventHandler<Guid>? ServerStartRequested;

    public string ServersHeading => LocalizationService.Get("Home.Servers");

    public string RemoteAccessHeading => LocalizationService.Get("Home.RemoteAccess");

    public string BackupsHeading => LocalizationService.Get("Home.Backups");

    public string PlayersLabel => LocalizationService.Get("Metric.Players");

    public string UptimeLabel => LocalizationService.Get("Metric.Uptime");

    public string MemoryLabel => LocalizationService.Get("Metric.Memory");

    public string MoreActionsLabel => LocalizationService.Get("Action.MoreActions");

    public UiStatusTone HealthTone
    {
        get => _healthTone;
        private set
        {
            if (_healthTone == value)
            {
                return;
            }

            _healthTone = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HealthTone)));
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            // Every label on this page is a get-only property read once by its binding, so a
            // language switch needs an explicit nudge or the page stays in the old language.
            foreach (var name in new[]
                     {
                         nameof(ServersHeading),
                         nameof(RemoteAccessHeading),
                         nameof(BackupsHeading),
                         nameof(PlayersLabel),
                         nameof(UptimeLabel),
                         nameof(MemoryLabel),
                         nameof(MoreActionsLabel)
                     })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }

            foreach (var card in _feed.Servers)
            {
                card.RefreshLocalizedText();
            }

            Render();
        });

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _feed.Start();
        await _feed.RefreshAsync();
        Render();
    }

    private void Render()
    {
        // When nothing is reachable the banner carries the whole message, so everything below
        // it goes away: repeating the failure per-section, or showing "Offline" and dashes in
        // summary cards, reads as several problems instead of the one that actually exists.
        var unreachable = _feed.ShowErrorState;
        var belowBanner = unreachable ? Visibility.Collapsed : Visibility.Visible;
        ServersHeadingText.Visibility = belowBanner;
        SecondarySummaries.Visibility = belowBanner;
        RetryButton.Visibility = unreachable ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Content = LocalizationService.Get(
            _feed.NeedsElevation ? "Error.RestartAsAdministrator" : "Error.Retry");

        if (unreachable)
        {
            // The shared component would render the same failure a second time here.
            StateView.Visibility = Visibility.Collapsed;
            ServerCards.Visibility = Visibility.Collapsed;
        }
        else
        {
            var stateShown = StateView.Apply(
                _feed,
                "Empty.NoServers",
                "Empty.NoServersMessage");
            ServerCards.Visibility = stateShown ? Visibility.Collapsed : Visibility.Visible;
        }

        var statuses = _feed.Servers.Select(server => server.Status).ToArray();
        var anyRunning = statuses.Any(status => status == UiStatus.Running);
        var remoteOnline = _feed.Servers.Any(server => server.RemoteAccessOnline);

        var remoteConfigured = _feed.Servers.Any(
            server => !string.IsNullOrWhiteSpace(server.InternetAddress));

        var backupNeedsAttention = _feed.Servers.Any(
            server => server.BackupTone == UiStatusTone.Caution);

        // One dropped poll keeps the last reading (the two-poll grace); before anything has
        // loaded the honest headline is "Connecting", not "unreachable".
        var health = ServerPresentation.SummarizeHealth(
            _feed.HasCurrentData,
            statuses,
            remoteOnline,
            anyRunning,
            _feed.ShowErrorState,
            remoteConfigured,
            backupNeedsAttention);
        HealthHeadline.Text = _feed.NeedsElevation
            ? LocalizationService.Get("Error.NeedsAdministrator")
            : health.Headline;
        HealthTone = _feed.NeedsElevation ? UiStatusTone.Caution : health.Tone;
        HealthGlyph.Text = char.ConvertFromUtf32(health.Tone switch
        {
            UiStatusTone.Positive => 0xE73E,
            UiStatusTone.Negative => 0xE783,
            _ => 0xE7BA
        });
        HealthDetail.Text = BuildHealthDetail(anyRunning, statuses.Length);

        RenderRemoteAccess(remoteOnline);
        RenderBackups();
    }

    private string BuildHealthDetail(bool anyRunning, int serverCount)
    {
        if (!_feed.HasCurrentData)
        {
            // Reassurance first: a client that cannot see the servers has not stopped them.
            // While still connecting nothing has failed, so there is no failure to announce.
            return !_feed.ShowErrorState
                ? string.Empty
                : LocalizationService.Get(
                    _feed.NeedsElevation ? "Error.NeedsAdministratorHint" : "Error.ServiceHint");
        }

        if (serverCount == 0)
        {
            return LocalizationService.Get("Empty.NoServersMessage");
        }

        var running = _feed.Servers.Count(server => server.Status == UiStatus.Running);
        if (!anyRunning)
        {
            return LocalizationService.Format("Home.Detail.NoneRunning", serverCount);
        }

        // A running server that is not reporting a player count is reported as unknown, never
        // folded into a total: "0 players online" would read as an empty server and invite
        // someone to stop it while people are connected.
        var summary = TotalPlayers() is { } players
            ? LocalizationService.Format(
                "Home.Detail.Players",
                running,
                serverCount,
                players)
            : LocalizationService.Format(
                "Home.Detail.PlayersUnknown",
                running,
                serverCount);
        return _feed.Snapshot?.Servers.Any(server => server.State == ServerManager.Contracts.ServerState.Running && server.PlayersStale) == true
            ? summary + " · " + MinecraftPlayersPresentation.Text("Player counts are stale", "أعداد اللاعبين غير محدثة")
            : summary;
    }

    /// <summary>Total players, or null when any running server is not reporting a count.</summary>
    private int? TotalPlayers()
    {
        var snapshot = _feed.Snapshot;
        if (snapshot is null)
        {
            return null;
        }

        var total = 0;
        foreach (var server in snapshot.Servers)
        {
            if (ServerPresentation.MapState(server.State) != UiStatus.Running)
            {
                continue;
            }

            if (server.PlayersOnline is not { } online)
            {
                return null;
            }

            total += online;
        }

        return total;
    }

    private void RenderRemoteAccess(bool online)
    {
        // The same server the Network page describes, so the two pages cannot disagree.
        var address = ServerPresentation.SelectRemoteAccessServer(
            _feed.Servers,
            server => server.RemoteAccessOnline,
            server => server.InternetAddress)?.InternetAddress;
        var configured = !string.IsNullOrWhiteSpace(address);

        // With no internet address configured there is no remote access to be online or
        // offline; claiming "Offline" would invent a problem that does not exist.
        var tone = !configured
            ? UiStatusTone.Neutral
            : online
                ? UiStatusTone.Positive
                : UiStatusTone.Neutral;
        RemoteAccessDot.Fill = ResolveToneBrush(tone);
        RemoteAccessStatus.Text = configured
            ? LocalizationService.Get(online ? "Status.Online" : "Status.Offline")
            : EmDash;
        RemoteAccessAddress.Text = address ?? string.Empty;
        RemoteAccessAddress.Visibility = configured
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RenderBackups()
    {
        if (_feed.Servers.Count == 0)
        {
            BackupsDot.Fill = ResolveToneBrush(UiStatusTone.Neutral);
            BackupsStatus.Text = EmDash;
            BackupsDetail.Text = string.Empty;
            return;
        }

        var worst = _feed.Servers
            .OrderByDescending(server => server.BackupTone == UiStatusTone.Caution)
            .First();
        BackupsDot.Fill = ResolveToneBrush(worst.BackupTone);
        BackupsStatus.Text = worst.BackupLabel;

        // The headline above is one server's backup age. Naming that server is honest;
        // "Across N servers" would read as if every server were backed up that recently.
        BackupsDetail.Text = worst.Name;
    }

    private static Brush ResolveToneBrush(UiStatusTone tone)
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

    private void ServerCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ServerCardViewModel card })
        {
            ServerOpenRequested?.Invoke(this, card.ServerId);
        }
    }

    private void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        // The card itself is clickable; without this the click also opens it a second time.
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: ServerCardViewModel card })
        {
            return;
        }

        if (card.PrimaryActionStartsServer)
        {
            ServerStartRequested?.Invoke(this, card.ServerId);
        }
        else
        {
            ServerOpenRequested?.Invoke(this, card.ServerId);
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_feed.NeedsElevation)
        {
            (Application.Current as App)?.RestartAsAdministrator();
            return;
        }

        // Kept enabled: disabling the focused button threw keyboard focus away, and the feed
        // already ignores a refresh that is still in flight.
        await _feed.RefreshAsync();
        Render();
    }

    private void MoreActions_Click(object sender, RoutedEventArgs e)
    {
        // The card itself is clickable; without this the click also opens it a second time.
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: ServerCardViewModel card })
        {
            ServerOpenRequested?.Invoke(this, card.ServerId);
        }
    }
}
