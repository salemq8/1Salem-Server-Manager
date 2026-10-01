using System.Globalization;
using System.Windows;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Updates;

namespace ServerManager.Connect.App.ViewModels;

/// <summary>
/// The window's view model and the page flow. It decides the first page (not configured, blocked,
/// Welcome, or Servers for a returning friend) and keeps one connection page per server so a
/// session keeps running while the friend looks at other pages.
/// </summary>
public sealed class MainViewModel : ObservableObject, INavigator
{
    /// <summary>
    /// How long exit gives the transport to close this run's sessions. A healthy transport closes
    /// them in milliseconds; the bound covers one connection attempt to a pipe nobody serves.
    /// </summary>
    internal static readonly TimeSpan SessionCloseAtExitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The first automatic update check waits until the app has settled after starting.</summary>
    internal static readonly TimeSpan AutomaticCheckDelay = TimeSpan.FromSeconds(20);

    /// <summary>How often a long-running app asks whether a daily check is due; nothing is fetched unless it is.</summary>
    internal static readonly TimeSpan AutomaticCheckWake = TimeSpan.FromHours(1);

    private readonly ConnectAppContext _context;
    private readonly DeviceRegistration? _registration;
    private readonly EnrollmentCoordinator? _enrollment;
    private readonly SessionService? _sessions;
    private readonly Dictionary<string, ConnectionViewModel> _connections = new(StringComparer.Ordinal);
    private object? _currentPage;
    private bool _hasServers;
    private CancellationTokenSource? _updateChecks;
    private int? _dismissedOfferBuild;
    private string? _noticeText;
    private string? _noticeActionText;
    private bool _noticeIsWarning;
    private Action? _noticeAction;

    public MainViewModel(ConnectAppContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (context.Services is { } services)
        {
            _registration = new DeviceRegistration(services.Broker);
            _enrollment = new EnrollmentCoordinator(
                services.Broker,
                context.Transport,
                services.TransportProcess,
                services.Identity,
                new ConsumedEnrollments(context.ConsumedEnrollmentsPath, context.Log),
                context.Log);
            _sessions = new SessionService(services.Broker, context.Transport, services.TransportProcess, context.Log);
        }

        var culture = CultureInfo.CurrentUICulture;
        FlowDirection = culture.TextInfo.IsRightToLeft && Text.HasOwnTable(culture) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ShowServersCommand = new RelayCommand(ShowServers, () => CanUseServers);
        ShowDiagnosticsCommand = new RelayCommand(ShowDiagnostics);
        ShowSettingsCommand = new RelayCommand(ShowSettings);
        NoticeActionCommand = new RelayCommand(() => _noticeAction?.Invoke(), () => _noticeAction is not null);
        DismissNoticeCommand = new RelayCommand(DismissNotice);
    }

    public string Title => Text.AppTitle;

    /// <summary>Right to left for Arabic and other RTL UI cultures.</summary>
    public FlowDirection FlowDirection { get; }

    public bool CanUseServers => _context.Services is not null;

    public object? CurrentPage
    {
        get => _currentPage;
        private set
        {
            var previous = _currentPage;
            if (Set(ref _currentPage, value))
            {
                (previous as IPageLifetime)?.OnHidden();
                (value as IPageLifetime)?.OnShown();
            }
        }
    }

    public RelayCommand ShowServersCommand { get; }

    public RelayCommand ShowDiagnosticsCommand { get; }

    public RelayCommand ShowSettingsCommand { get; }

    /// <summary>A one-line, non-blocking notice above the page (an update, or how the last one ended).</summary>
    public string? NoticeText
    {
        get => _noticeText;
        private set
        {
            if (Set(ref _noticeText, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => NoticeText is not null;

    public string? NoticeActionText
    {
        get => _noticeActionText;
        private set
        {
            if (Set(ref _noticeActionText, value))
            {
                OnPropertyChanged(nameof(HasNoticeAction));
            }
        }
    }

    public bool HasNoticeAction => NoticeActionText is not null;

    public bool NoticeIsWarning
    {
        get => _noticeIsWarning;
        private set => Set(ref _noticeIsWarning, value);
    }

    public RelayCommand NoticeActionCommand { get; }

    public RelayCommand DismissNoticeCommand { get; }

    public async Task InitializeAsync()
    {
        ReviewLastUpdate();
        StartAutomaticUpdateChecks();
        if (_context.Services is not { } services)
        {
            CurrentPage = UnavailablePage();
            return;
        }

        try
        {
            await _registration!.EnsureAsync(CancellationToken.None);
            var memberships = await services.Broker.GetMembershipsAsync(CancellationToken.None);
            _hasServers = memberships.Count > 0;
            if (_hasServers)
            {
                ShowServers();
            }
            else
            {
                ShowWelcome();
            }
        }
        catch (Exception exception) when (UserMessages.IsExpected(exception))
        {
            _context.Log.Record("startup", exception);
            CurrentPage = WelcomeViewModel.Failed(UserMessages.For(exception), InitializeAsync);
        }
    }

    public void ShowWelcome() =>
        CurrentPage = _context.Services is null ? UnavailablePage() : WelcomeViewModel.Ready(this);

    public void ShowInvite()
    {
        if (_context.Services is not { } services)
        {
            ShowWelcome();
            return;
        }

        CurrentPage = new InviteViewModel(services.Broker, _registration!, _context.Log, this, _hasServers ? ShowServers : ShowWelcome);
    }

    public void ShowWaiting(InviteRedemption redemption)
    {
        if (_context.Services is not { } services)
        {
            ShowWelcome();
            return;
        }

        _hasServers = true;
        CurrentPage = new WaitingViewModel(redemption, services.Broker, _context.Clock, _context.Log, this);
    }

    public void ShowServers()
    {
        if (_context.Services is not { } services)
        {
            ShowWelcome();
            return;
        }

        _hasServers = true;
        CurrentPage = new ServersViewModel(services.Broker, _registration!, _enrollment!, _context.Clock, _context.Log, this);
    }

    public void ShowConnection(Membership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        if (_sessions is null)
        {
            ShowWelcome();
            return;
        }

        if (!_connections.TryGetValue(membership.MembershipId, out var connection))
        {
            connection = new ConnectionViewModel(membership, _sessions, _context.Clock, _context.Clipboard, _context.Log, this);
            _connections.Add(membership.MembershipId, connection);
        }

        CurrentPage = connection;
    }

    public void ShowDiagnostics() => CurrentPage = new DiagnosticsViewModel(_context);

    public void ShowSettings() =>
        CurrentPage = new SettingsViewModel(_context.Updater, _context.Preferences, _context.Shell, ActiveConnections);

    /// <summary>Servers with a session this run opened that is not known to be closed.</summary>
    public IReadOnlyList<ConnectionViewModel> ActiveConnections() =>
        _connections.Values
            .Where(connection => connection.HasAddress ||
                connection.State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.ServerOffline)
            .ToList();

    /// <summary>
    /// After an update this app handed to Setup: the new build confirms it started (Setup keeps the
    /// previous build until then) and says so; an update that did not take effect says that instead.
    /// </summary>
    internal void ReviewLastUpdate()
    {
        if (_context.Updater is not { } updater)
        {
            return;
        }

        var report = updater.ReviewAtStartup();
        var build = report.ToBuild.ToString(CultureInfo.CurrentCulture);
        switch (report.Outcome)
        {
            case PostUpdateOutcome.Updated:
                ShowNotice(Text.Format(Text.NoticeUpdated, build), warning: false, null, null);
                break;
            case PostUpdateOutcome.InProgress:
                ShowNotice(Text.NoticeUpdateInProgress, warning: false, null, null);
                break;
            case PostUpdateOutcome.NotUpdated:
                ShowNotice(
                    Text.Format(report.Result?.Outcome == InstallerResult.Blocked ? Text.NoticeUpdateBlocked : Text.NoticeUpdateFailed, build),
                    warning: true,
                    Text.CommonRetry,
                    () =>
                    {
                        DismissNotice();
                        ShowSettings();
                    });
                break;
        }
    }

    /// <summary>
    /// One check shortly after start, then at most one a day for an app left open: never more
    /// often, never a download, never an install. A newer build only shows the notice.
    /// </summary>
    internal async Task RunAutomaticUpdateChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.Clock.Delay(AutomaticCheckDelay, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckForUpdateAutomaticallyAsync(cancellationToken);
                await _context.Clock.Delay(AutomaticCheckWake, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task CheckForUpdateAutomaticallyAsync(CancellationToken cancellationToken)
    {
        if (_context.Updater is not { } updater || updater.IsBusy || !updater.IsAutomaticCheckDue)
        {
            return;
        }

        await updater.CheckAsync(cancellationToken);
        if (updater.Status == UpdateStatus.Available && updater.Offer is { } offer &&
            _dismissedOfferBuild != offer.Build.BuildRevision && CurrentPage is not SettingsViewModel)
        {
            ShowNotice(
                Text.Format(Text.NoticeUpdateAvailable, offer.Build.BuildRevision.ToString(CultureInfo.CurrentCulture)),
                warning: false,
                Text.NoticeUpdate,
                () =>
                {
                    DismissNotice();
                    ShowSettings();
                });
        }
    }

    private void StartAutomaticUpdateChecks()
    {
        // An unconfigured copy writes nothing to the friend's profile, so it only checks when asked.
        if (_context.Updater is null || !_context.Settings.IsConfigured || _updateChecks is not null)
        {
            return;
        }

        _updateChecks = new CancellationTokenSource();
        _ = RunAutomaticUpdateChecksAsync(_updateChecks.Token);
    }

    private void ShowNotice(string text, bool warning, string? actionText, Action? action)
    {
        NoticeIsWarning = warning;
        _noticeAction = action;
        NoticeActionText = actionText;
        NoticeText = text;
        NoticeActionCommand.RaiseCanExecuteChanged();
    }

    private void DismissNotice()
    {
        if (_context.Updater?.Offer is { } offer)
        {
            _dismissedOfferBuild = offer.Build.BuildRevision;
        }

        _noticeAction = null;
        NoticeActionText = null;
        NoticeText = null;
        NoticeActionCommand.RaiseCanExecuteChanged();
    }

    public ConnectionViewModel? FindConnection(string membershipId) =>
        _connections.GetValueOrDefault(membershipId);

    /// <summary>Why nothing can work: no broker configured, or a device identity this user cannot open.</summary>
    private WelcomeViewModel UnavailablePage() =>
        _context.Settings.IsConfigured
            ? WelcomeViewModel.Blocked(_context.StartupProblem ?? Text.ErrorUnexpected)
            : WelcomeViewModel.NotConfigured();

    /// <summary>
    /// At exit: stop polling and monitoring, close every session this run opened, then stop the
    /// transport this app started. Stopping only ends a transport this app started: one it reused
    /// (started by another copy of the app, or left by an earlier run) keeps running, and would
    /// keep this run's sessions open in it, addresses and live game streams included, although
    /// the friend has closed the app. Closing is bounded (<see cref="SessionCloseAtExitTimeout"/>),
    /// so a transport that does not answer cannot hold the exit.
    /// </summary>
    public void Shutdown()
    {
        _updateChecks?.Cancel();
        (_currentPage as IPageLifetime)?.OnHidden();
        foreach (var connection in _connections.Values)
        {
            connection.StopMonitoring();
        }

        _sessions?.CloseOpenSessionsAtExit(SessionCloseAtExitTimeout);
        _context.Services?.TransportProcess.Stop();
    }
}
