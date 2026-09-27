using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Transport;

namespace ServerManager.Connect.App.ViewModels;

/// <summary>
/// Connect / Disconnect for one server, and the local address to give the game. While a session
/// is open a monitor checks it every few seconds:
/// <list type="bullet">
/// <item>the ticket is renewed two minutes before it expires, each time with a fresh session key;
/// if renewing keeps failing until it has expired, the session ends as "Access expired" (§16);</item>
/// <item>a 404 from the broker is generic (§14), so the membership is looked up to tell a
/// revocation ("Access revoked") from anything else;</item>
/// <item>the transport's per-session log says whether game connections reached the owner's
/// server ("Connected") or not ("Server offline"); a connection that ended or was refused is
/// checked against the membership too, because revoking a friend ends their live connections
/// from the owner's side ("Access revoked").</item>
/// </list>
/// A session survives leaving the page; it ends on Disconnect, on revocation or expiry, or when
/// the app exits. It only shows as ended once its local address is known to be closed.
/// </summary>
public sealed class ConnectionViewModel : ObservableObject
{
    internal static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(5);

    // Tickets live ten minutes (§9). Starting two minutes early leaves room for a slow or briefly
    // unreachable broker before new game connections would be refused.
    internal static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(2);

    private readonly Membership _membership;
    private readonly SessionService _sessions;
    private readonly IAppClock _clock;
    private readonly IClipboardService _clipboard;
    private readonly DiagnosticsLog _log;
    private ConnectionState _state = ConnectionState.Disconnected;
    private string? _localAddress;
    private string? _message;
    private string? _sessionId;
    private DateTimeOffset _expiresAt;
    private string? _lastSignalLine;
    private bool _diagnosticsFailing;
    private bool _closing;
    private PendingEnd? _unclosedEnd;
    private CancellationTokenSource? _monitor;

    public ConnectionViewModel(
        Membership membership,
        SessionService sessions,
        IAppClock clock,
        IClipboardService clipboard,
        DiagnosticsLog log,
        INavigator navigator)
    {
        _membership = membership ?? throw new ArgumentNullException(nameof(membership));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ArgumentNullException.ThrowIfNull(navigator);
        ConnectCommand = new AsyncCommand(ConnectAsync, () => CanConnect);
        DisconnectCommand = new AsyncCommand(DisconnectAsync, () => CanDisconnect);
        CopyAddressCommand = new RelayCommand(CopyAddress, () => HasAddress);
        BackCommand = new RelayCommand(navigator.ShowServers);
    }

    public string ServerLabel => _membership.ServerLabel;

    public ConnectionState State => _state;

    public string StateText => ConnectionStateText.For(_state);

    public string? LocalAddress
    {
        get => _localAddress;
        private set
        {
            if (Set(ref _localAddress, value))
            {
                OnPropertyChanged(nameof(HasAddress));
                CopyAddressCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasAddress => LocalAddress is not null;

    /// <summary>A secondary line: why the last attempt failed, or "Address copied".</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => Message is not null;

    /// <summary>Not after a revocation: a new ticket would be refused, and the button would only pretend otherwise.</summary>
    public bool CanConnect => _membership.CanConnect &&
        _state is ConnectionState.Disconnected or ConnectionState.AccessExpired;

    public bool CanDisconnect => _state is ConnectionState.Connected or ConnectionState.ServerOffline;

    public AsyncCommand ConnectCommand { get; }

    public AsyncCommand DisconnectCommand { get; }

    public RelayCommand CopyAddressCommand { get; }

    public RelayCommand BackCommand { get; }

    /// <summary>The running monitor, so tests can observe it end.</summary>
    internal Task? MonitorTask { get; private set; }

    public async Task ConnectAsync()
    {
        if (!CanConnect)
        {
            return;
        }

        Message = null;
        SetState(ConnectionState.Connecting);
        OpenedSession session;
        try
        {
            session = await _sessions.OpenAsync(_membership, CancellationToken.None);
        }
        catch (BrokerException exception) when (exception.Failure == BrokerFailure.NotFound)
        {
            _log.Record("connect", exception);
            await SettleNotFoundAsync();
            return;
        }
        catch (Exception exception)
        {
            GiveUpConnecting(exception);
            return;
        }

        _sessionId = session.SessionId;
        _expiresAt = session.ExpiresAt;
        _lastSignalLine = null;
        LocalAddress = session.LocalAddress;
        SetState(ConnectionState.Connected);
        StartMonitor();
    }

    public Task DisconnectAsync() => EndSessionAsync(ConnectionState.Disconnected, null);

    /// <summary>Stops watching without closing: used at exit, where the app closes its sessions itself.</summary>
    public void StopMonitoring() => StopMonitor();

    /// <summary>One monitor step. Internal so tests can drive it with a controlled clock.</summary>
    internal async Task CheckSessionAsync(CancellationToken cancellationToken)
    {
        if (_sessionId is not { } sessionId)
        {
            return;
        }

        if (_unclosedEnd is { } unclosed)
        {
            // An earlier end could not confirm that the address was closed; nothing else about
            // this session matters until it can.
            await EndSessionAsync(unclosed.State, unclosed.Message);
            return;
        }

        var now = _clock.UtcNow;
        if (now >= _expiresAt - RefreshLead && !await TryRefreshAsync(sessionId, cancellationToken))
        {
            if (_sessionId is null || _unclosedEnd is not null)
            {
                return;
            }

            if (now >= _expiresAt)
            {
                await EndSessionAsync(ConnectionState.AccessExpired, null);
                return;
            }
        }

        TransportStatus status;
        try
        {
            status = await _sessions.StatusAsync(cancellationToken);
        }
        catch (TransportException exception) when (exception.Code == TransportErrorCodes.NoAnswer)
        {
            // The transport is there but busy, which says nothing about this session: look again
            // on the next step rather than end a session that may be fine.
            _log.Record("session", exception);
            return;
        }

        if (!status.Sessions.Any(session => session.SessionId == sessionId))
        {
            // The transport restarted or dropped the session; its listener is gone.
            await EndSessionAsync(ConnectionState.Disconnected, Text.ErrorSessionLost);
            return;
        }

        if (await SessionLogAsync(cancellationToken) is not { } log)
        {
            return;
        }

        var signal = TransportLogSignals.Latest(log, sessionId);
        if (signal.Line is null || signal.Line == _lastSignalLine)
        {
            return;
        }

        _lastSignalLine = signal.Line;
        switch (signal.Reachability)
        {
            case SessionReachability.Reached when await IsRevokedAsync(cancellationToken):
                // Revoking a friend ends their live connections from the owner's side, and here
                // that looks like any other connection that ended.
                await EndSessionAsync(ConnectionState.AccessRevoked, null);
                break;
            case SessionReachability.Reached:
                SetState(ConnectionState.Connected);
                break;
            case SessionReachability.Unreachable:
                SetState(ConnectionState.ServerOffline);
                break;
            case SessionReachability.Refused when await IsRevokedAsync(cancellationToken):
                await EndSessionAsync(ConnectionState.AccessRevoked, null);
                break;
            case SessionReachability.Refused:
                // The owner's side answered and said no without a revocation: the server is not
                // running (or not enabled for Connect) right now.
                SetState(ConnectionState.ServerOffline);
                break;
        }
    }

    /// <summary>
    /// The transport's recent log, or null when it cannot give one right now. The log only refines
    /// the state (see <see cref="TransportLogSignals"/>); the session itself was just confirmed by
    /// <c>status</c>, so a failing <c>diag</c> must not end it. Recorded once per run of failures.
    /// </summary>
    private async Task<IReadOnlyList<string>?> SessionLogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var log = (await _sessions.DiagnosticsAsync(cancellationToken)).Log;
            _diagnosticsFailing = false;
            return log;
        }
        catch (TransportException exception) when (exception.Code != TransportErrorCodes.Unavailable)
        {
            if (!_diagnosticsFailing)
            {
                _log.Record("session", exception);
                _diagnosticsFailing = true;
            }

            return null;
        }
    }

    private async Task<bool> TryRefreshAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            _expiresAt = await _sessions.RefreshAsync(_membership, sessionId, cancellationToken);
            return true;
        }
        catch (BrokerException exception) when (exception.Failure == BrokerFailure.NotFound)
        {
            _log.Record("refresh", exception);
            if (await IsRevokedAsync(cancellationToken))
            {
                await EndSessionAsync(ConnectionState.AccessRevoked, null);
            }

            return false;
        }
        catch (BrokerException exception)
        {
            // Offline or rate limited: keep the session and try again on the next step, until the
            // ticket actually runs out.
            _log.Record("refresh", exception);
            return false;
        }
        catch (TransportException exception) when (exception.Code is TransportErrorCodes.TicketRejected
                                                       or TransportErrorCodes.SessionKeyRejected
                                                       or TransportErrorCodes.TicketMismatch
                                                       or TransportErrorCodes.NoAnswer)
        {
            // The transport refused this ticket, or was too busy to say whether it took it; the
            // next step sends another one either way.
            _log.Record("refresh", exception);
            return false;
        }
    }

    /// <summary>
    /// After a 404 to Connect: the membership tells a revocation from an expiry (§14). This runs in
    /// <see cref="ConnectAsync"/>'s catch, where its catch-all cannot see what the lookup throws.
    /// </summary>
    private async Task SettleNotFoundAsync()
    {
        try
        {
            SetState(await IsRevokedAsync(CancellationToken.None) ? ConnectionState.AccessRevoked : ConnectionState.AccessExpired);
        }
        catch (Exception exception)
        {
            GiveUpConnecting(exception);
        }
    }

    /// <summary>
    /// No session came of the attempt, so there is nothing to close. Left "Connecting…", both
    /// buttons would stay off until the app restarts. An unexpected failure reads as
    /// <see cref="Text.ErrorUnexpected"/> (<see cref="UserMessages.For"/>).
    /// </summary>
    private void GiveUpConnecting(Exception exception)
    {
        _log.Record("connect", exception);
        Message = UserMessages.For(exception);
        SetState(ConnectionState.Disconnected);
    }

    private async Task<bool> IsRevokedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await _sessions.MembershipStateAsync(_membership.MembershipId, cancellationToken);

            // The broker lists ended memberships too, so one that is missing is gone for good.
            return state is null or MembershipState.Revoked or MembershipState.Rejected;
        }
        catch (BrokerException exception)
        {
            _log.Record("membership", exception);
            return false;
        }
    }

    /// <summary>
    /// Ends the session as <paramref name="state"/> once its local address is known to be closed
    /// (<see cref="SessionService.CloseAsync"/>). Until then the address stays on screen,
    /// Disconnect stays available and the monitor tries again on every step: the page never says
    /// a session is over while its address may still reach the owner's server.
    /// </summary>
    private async Task EndSessionAsync(ConnectionState state, string? message)
    {
        StopMonitor();
        if (_closing)
        {
            // Another end is closing this session right now and settles the page when it is done.
            return;
        }

        if (_sessionId is not { } sessionId)
        {
            LocalAddress = null;
            Message = message;
            SetState(state);
            return;
        }

        // A revocation or expiry still waiting to close stays what it was, even if the friend
        // presses Disconnect meanwhile.
        var end = _unclosedEnd ?? new PendingEnd(state, message);
        bool closed;
        _closing = true;
        try
        {
            closed = await _sessions.CloseAsync(sessionId, CancellationToken.None);
        }
        finally
        {
            _closing = false;
        }

        if (!closed)
        {
            _unclosedEnd = end;
            Message = Text.ErrorCloseUnconfirmed;
            StartMonitor();
            return;
        }

        _unclosedEnd = null;
        _sessionId = null;
        LocalAddress = null;
        Message = end.Message;
        SetState(end.State);
    }

    private void StartMonitor()
    {
        StopMonitor();
        _monitor = new CancellationTokenSource();
        MonitorTask = MonitorLoopAsync(_monitor.Token);
    }

    private void StopMonitor()
    {
        // Cancelled but not disposed: the loop may still be inside a call holding this token.
        _monitor?.Cancel();
        _monitor = null;
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _clock.Delay(MonitorInterval, cancellationToken);
                await CheckSessionAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (UserMessages.IsExpected(exception))
        {
            _log.Record("session", exception);
            await EndSessionAsync(ConnectionState.Disconnected, Text.ErrorSessionLost);
        }
        catch (Exception exception)
        {
            // A fire-and-forget loop must not fail silently, nor leave "Connected" on screen.
            _log.Record("session", exception);
            await EndSessionAsync(ConnectionState.Disconnected, Text.ErrorUnexpected);
        }
    }

    private void CopyAddress()
    {
        if (LocalAddress is { } address)
        {
            Message = _clipboard.TrySetText(address) ? Text.ConnectionCopied : null;
        }
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
    }

    /// <summary>How a session is to end once its address is confirmed closed.</summary>
    private sealed record PendingEnd(ConnectionState State, string? Message);
}
