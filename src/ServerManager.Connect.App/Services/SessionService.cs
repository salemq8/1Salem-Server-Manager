using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Transport;

namespace ServerManager.Connect.App.Services;

/// <summary>An open session: the transport's id, the loopback address for the game, and when its ticket ends.</summary>
public sealed record OpenedSession(string SessionId, string LocalAddress, DateTimeOffset ExpiresAt);

/// <summary>
/// Gets tickets from the broker and hands them to the transport. Every ticket, including each
/// refresh, is bound to a brand-new session key (§5, §10): its public half goes into the ticket
/// request, its private half to the transport, and the app keeps neither. It also remembers which
/// sessions this run opened and has not closed, so the app can close them when it exits.
/// </summary>
public sealed class SessionService
{
    /// <summary>0 lets the transport use 18211, or the next free port in 18211-18299.</summary>
    private const int DefaultPort = 0;

    private readonly IBrokerClient _broker;
    private readonly ITransportClient _transport;
    private readonly ITransportProcess _process;
    private readonly DiagnosticsLog _log;

    // Sessions this run opened whose local address is not known to be closed yet. A transport
    // another copy of the app started keeps running after this app exits, and with it every
    // session left open in it (its address and any live game stream), so exit closes these.
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);

    public SessionService(IBrokerClient broker, ITransportClient transport, ITransportProcess process, DiagnosticsLog log)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<OpenedSession> OpenAsync(Membership membership, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(membership);
        if (!membership.CanConnect)
        {
            throw new InvalidOperationException("A session can be opened only for an owner-confirmed node.");
        }

        await _process.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        using var key = SessionKey.Create();
        var ticket = await _broker.CreateSessionAsync(membership.MembershipId, key.PublicKeySpki, cancellationToken).ConfigureAwait(false);
        var opened = await _transport.OpenAsync(
                TransportNodeNames.ForOwner(membership.OwnerId),
                ticket.Ticket,
                key.ExportPrivateKey(),
                DefaultPort,
                cancellationToken)
            .ConfigureAwait(false);
        lock (_open)
        {
            _open.Add(opened.SessionId);
        }

        return new OpenedSession(opened.SessionId, opened.Local, ticket.ExpiresAt);
    }

    /// <summary>Replaces the session's ticket; returns the new expiry. Live game connections are not interrupted.</summary>
    public async Task<DateTimeOffset> RefreshAsync(Membership membership, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(membership);
        using var key = SessionKey.Create();
        var ticket = await _broker.CreateSessionAsync(membership.MembershipId, key.PublicKeySpki, cancellationToken).ConfigureAwait(false);
        await _transport.RefreshAsync(sessionId, ticket.Ticket, key.ExportPrivateKey(), cancellationToken).ConfigureAwait(false);
        return ticket.ExpiresAt;
    }

    /// <summary>
    /// Closes a session and returns whether its local address is known to be closed. A close that
    /// failed may have happened anyway (only its answer was lost) or not at all, so the
    /// transport's status settles it. When the transport cannot be asked for that either (nothing
    /// serves the pipe, or something this app does not trust does), the address is known closed
    /// only if every transport process that served this app has exited (the one it started, or a
    /// running one it reused), because the listener lives in that process. Failures are recorded,
    /// never thrown.
    /// </summary>
    public async Task<bool> CloseAsync(string sessionId, CancellationToken cancellationToken)
    {
        var closed = await TryCloseAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (closed)
        {
            Forget(sessionId);
        }

        return closed;
    }

    /// <summary>
    /// At exit: asks the transport to close every session this run opened and has not closed,
    /// whether this app started that transport or reused one. Best effort within
    /// <paramref name="timeout"/>, then it returns whatever the outcome: a transport that does not
    /// answer must not hold the app's exit. Failures are recorded, never thrown.
    /// </summary>
    public void CloseOpenSessionsAtExit(TimeSpan timeout)
    {
        string[] open;
        lock (_open)
        {
            open = [.. _open];
        }

        if (open.Length == 0)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(timeout);
        var token = deadline.Token;

        // On the thread pool: the caller is the UI thread, blocked here, so nothing the closing
        // awaits may need it. The wait has a bound of its own: a call that did not honour its
        // token must not hold the exit either.
        var closing = Task.Run(() => CloseEachAsync(open, token), CancellationToken.None);
        Task.WaitAny([closing], timeout);
        if (closing.Exception is { } failure)
        {
            _log.Record("exit", failure.GetBaseException());
        }

        int left;
        lock (_open)
        {
            left = open.Count(_open.Contains);
        }

        if (left > 0)
        {
            _log.Record("exit", $"{left} of this run's sessions could not be closed before the app exited.");
        }
    }

    public Task<TransportStatus> StatusAsync(CancellationToken cancellationToken) =>
        _transport.StatusAsync(cancellationToken);

    public Task<TransportDiagnostics> DiagnosticsAsync(CancellationToken cancellationToken) =>
        _transport.DiagnosticsAsync(cancellationToken);

    /// <summary>
    /// The broker's current view of one membership. A 404 from the broker is deliberately
    /// generic (§14), so this is how the app tells "revoked" from "not issuable right now".
    /// </summary>
    public async Task<MembershipState?> MembershipStateAsync(string membershipId, CancellationToken cancellationToken)
    {
        var memberships = await _broker.GetMembershipsAsync(cancellationToken).ConfigureAwait(false);
        return memberships.FirstOrDefault(membership => membership.MembershipId == membershipId)?.State;
    }

    private async Task<bool> TryCloseAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await _transport.CloseAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TransportException exception) when (exception.Code == TransportErrorCodes.NoSession)
        {
            return true;
        }
        catch (TransportException exception)
        {
            _log.Record("disconnect", exception);
        }

        try
        {
            var status = await _transport.StatusAsync(cancellationToken).ConfigureAwait(false);
            return status.Sessions.All(session => session.SessionId != sessionId);
        }
        catch (TransportException exception) when (exception.Code is TransportErrorCodes.Unavailable or TransportErrorCodes.Untrusted &&
                                                   _process.UsedTransportsExited)
        {
            // Untrusted: something else serves the pipe now, say a transport another copy of the
            // app started after this app's own one exited. A session cannot outlive the process
            // it listened in.
            return true;
        }
        catch (TransportException exception)
        {
            _log.Record("disconnect", exception);
            return false;
        }
    }

    private async Task CloseEachAsync(IEnumerable<string> sessionIds, CancellationToken cancellationToken)
    {
        foreach (var sessionId in sessionIds)
        {
            try
            {
                await _transport.CloseAsync(sessionId, cancellationToken).ConfigureAwait(false);
                Forget(sessionId);
            }
            catch (TransportException exception) when (exception.Code == TransportErrorCodes.NoSession)
            {
                Forget(sessionId);
            }
            catch (TransportException exception)
            {
                // One session the transport would not close must not keep the others open.
                _log.Record("exit", exception);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Out of time: the rest stay open, and the caller records how many.
                return;
            }
        }
    }

    private void Forget(string sessionId)
    {
        lock (_open)
        {
            _open.Remove(sessionId);
        }
    }
}
