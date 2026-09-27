using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Diagnostics;
using ServerManager.Connect.Core.Pipes;
using ServerManager.Connect.Core.Tickets;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// What the Agent needs to decide on bridged connections: its own owner id (the only ticket
/// audience it accepts), the pinned broker keys, and the pipe name.
/// </summary>
public sealed class ConnectHostAuthorizationOptions
{
    private readonly TimeSpan _closeRepeatInterval = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _serverCheckInterval = TimeSpan.FromSeconds(10);

    public ConnectHostAuthorizationOptions(
        string ownerId,
        TicketKeySet ticketKeys,
        string pipeName = ConnectPipeNames.HostAuthorization,
        ConnectRevocationSeed? initialRevocations = null)
    {
        if (!ConnectKeyIds.IsOwnerId(ownerId))
        {
            throw new ArgumentException("The Agent's own owner id (own_…) is required.", nameof(ownerId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        OwnerId = ownerId;
        TicketKeys = ticketKeys ?? throw new ArgumentNullException(nameof(ticketKeys));
        PipeName = pipeName;
        InitialRevocations = initialRevocations ?? ConnectRevocationSeed.Empty;
        ArgumentNullException.ThrowIfNull(InitialRevocations.Devices);
        ArgumentNullException.ThrowIfNull(InitialRevocations.Memberships);
        ArgumentNullException.ThrowIfNull(InitialRevocations.Tickets);
        ArgumentNullException.ThrowIfNull(InitialRevocations.AuthorizationFloors);
    }

    public string OwnerId { get; }

    public TicketKeySet TicketKeys { get; }

    /// <summary>The bare pipe name, without <c>\\.\pipe\</c>.</summary>
    public string PipeName { get; }

    /// <summary>Persisted revocations applied before the first pipe instance is served.</summary>
    public ConnectRevocationSeed InitialRevocations { get; }

    /// <summary>
    /// How often a close the host transport has not confirmed is sent again. The
    /// <see cref="ConnectLiveConnections.CloseRepeats"/> repeats must span more than the host
    /// transport's 8 s wait for an allow, which the 2 s default does; shorter values are for tests.
    /// </summary>
    public TimeSpan CloseRepeatInterval
    {
        get => _closeRepeatInterval;
        init => _closeRepeatInterval = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "The close repeat interval must be positive.");
    }

    /// <summary>
    /// How often the registered servers are re-read while connections are live, so a server that
    /// is deleted, moved to another port or switched off loses its connections even when no
    /// friend connects to anything.
    /// </summary>
    public TimeSpan ServerCheckInterval
    {
        get => _serverCheckInterval;
        init => _serverCheckInterval = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "The server check interval must be positive.");
    }
}

public sealed record ConnectRevocationSeed(
    IReadOnlyList<string> Devices,
    IReadOnlyList<string> Memberships,
    IReadOnlyList<string> Tickets,
    IReadOnlyList<ConnectAuthorizationFloor> AuthorizationFloors)
{
    public static ConnectRevocationSeed Empty { get; } = new([], [], [], []);

    public static ConnectRevocationSeed FromState(ConnectRevocationState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new ConnectRevocationSeed(
            state.Devices,
            state.Memberships,
            state.Tickets.Where(ticket => ticket.ExpiresAt > now).Select(ticket => ticket.TicketId).ToArray(),
            state.AuthorizationFloors);
    }
}

public sealed record ConnectHostAuthorizationStatus(bool SubscriberPresent, int LiveConnections);

/// <summary>
/// The Agent's side of <c>\\.\pipe\1Salem.Connect.HostAuthz.v1</c> (contract §11). The host
/// transport asks here about every friend connection, reports the ones that ended, and keeps a
/// <c>subscribe</c> connection open so the Agent can end live connections when a friend is
/// revoked (§12).
/// <list type="bullet">
/// <item>The pipe is created with <see cref="ConnectPipeSecurity"/>: first instance only, owned by
/// the Agent's account, reachable by that account and SYSTEM, never by NETWORK. A further
/// instance is created before each accepted client is served, so the name is never released
/// while the Agent runs and the transport's request and subscription connections are served
/// side by side.</item>
/// <item>A client must complete <c>hello</c> (<c>{v:1}</c>) before anything else is interpreted.</item>
/// <item>Every <c>authorize</c> re-reads the server catalog and then runs
/// <see cref="HostAuthorizer"/>. The endpoint in an allow is always 127.0.0.1 and the
/// registered port; nothing in the request can choose it.</item>
/// <item>A connection is allowed only while a subscriber is connected, because without one the
/// Agent could not end it on revocation. The host transport refuses on its side too; this is
/// the same rule enforced by the party that owns it.</item>
/// <item>A decision not ready within <see cref="DecisionBudget"/> of reading the request is a
/// deny, and is not tracked: the host transport stops waiting after 8 s and would never learn,
/// or report closed, a connection allowed later.</item>
/// <item>A denial is answered with <c>{"decision":"deny"}</c> only. The reason goes to the host
/// log.</item>
/// <item>Revocations take effect in the revocation set first, so the next connection is refused
/// even if no transport is listening for close events. The revoked friend's replay-cache
/// entries are then released: its tickets are refused before the replay check anyway.</item>
/// <item>Live connections to a server end when Connect is switched off for it
/// (<see cref="DisableConnectAsync"/>), on request (<see cref="CloseServerConnectionsAsync"/>),
/// and when a refresh of the server list finds the server gone, switched off, no longer
/// bridgeable or on another port. The list is refreshed by every <c>authorize</c> and, while
/// connections are live, every <see cref="ConnectHostAuthorizationOptions.ServerCheckInterval"/>.</item>
/// </list>
/// </summary>
public interface IConnectHostAuthorizationServer : IAsyncDisposable
{
    ConnectHostAuthorizationStatus Status { get; }
    void Start();
    Task<IReadOnlyList<string>> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> RevokeMembershipAsync(string membershipId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> RevokeTicketAsync(string ticketId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> DisableConnectAsync(Guid serverId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> CloseServerConnectionsAsync(Guid serverId, CancellationToken cancellationToken);
}

[SupportedOSPlatform("windows")]
public sealed class ConnectHostAuthorizationPipeServer : IConnectHostAuthorizationServer
{
    /// <summary>
    /// The host transport needs two connections (requests and the subscription) and a few more
    /// while it reconnects. Anything beyond this is refused so a misbehaving local client cannot
    /// pin unbounded pipe instances.
    /// </summary>
    public const int MaxConcurrentClients = 16;

    /// <summary>A subscriber that does not take a close event in this time is dropped.</summary>
    public static readonly TimeSpan SubscriberWriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest an <c>authorize</c> may take from reading the request to the decision. The
    /// host transport waits 8 s from sending it; the 2 s margin covers the reply's way back.
    /// </summary>
    public static readonly TimeSpan DecisionBudget = TimeSpan.FromSeconds(6);

    private readonly ConnectHostAuthorizationOptions _options;
    private readonly ConnectServerCatalog _catalog;
    private readonly TimeProvider _clock;
    private readonly RevocationSet _revocations;
    private readonly ReplayCache _replayCache;
    private readonly HostAuthorizer _authorizer;
    private readonly ILogger<ConnectHostAuthorizationPipeServer> _logger;

    // Guards the revocation set together with the live-connection table and the subscriber
    // set, so a connection can never be allowed by a check that ran before a revocation and
    // then be recorded after the revocation looked for it.
    private readonly object _gate = new();
    private readonly ConnectLiveConnections _liveConnections;
    private readonly HashSet<PipeSession> _sessions = [];
    private readonly HashSet<PipeSession> _subscribers = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;
    private Task? _closeRepeater;
    private Task? _serverChecker;
    private int _disposed;

    public ConnectHostAuthorizationPipeServer(
        ConnectHostAuthorizationOptions options,
        ConnectServerCatalog catalog,
        TimeProvider clock,
        ILogger<ConnectHostAuthorizationPipeServer> logger)
        : this(options, catalog, clock, logger, ConnectLiveConnections.DefaultCapacity, ReplayCache.DefaultCapacity)
    {
    }

    internal ConnectHostAuthorizationPipeServer(
        ConnectHostAuthorizationOptions options,
        ConnectServerCatalog catalog,
        TimeProvider clock,
        ILogger<ConnectHostAuthorizationPipeServer> logger,
        int liveConnectionCapacity,
        int replayCacheCapacity)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _revocations = new RevocationSet(clock);
        foreach (var deviceId in options.InitialRevocations.Devices)
        {
            _revocations.RevokeDevice(deviceId);
        }

        foreach (var membershipId in options.InitialRevocations.Memberships)
        {
            _revocations.RevokeMembership(membershipId);
        }

        foreach (var ticketId in options.InitialRevocations.Tickets)
        {
            _revocations.RevokeTicket(ticketId);
        }

        foreach (var floor in options.InitialRevocations.AuthorizationFloors)
        {
            _revocations.RaiseAuthorizationFloor(floor.MembershipId, floor.MinimumVersion);
        }

        _replayCache = new ReplayCache(clock, replayCacheCapacity);
        _authorizer = new HostAuthorizer(
            new TicketVerifier(options.TicketKeys, options.OwnerId, catalog, _revocations, clock),
            _replayCache);
        _liveConnections = new ConnectLiveConnections(liveConnectionCapacity);
    }

    /// <summary>A lock-consistent, read-only health snapshot for the Agent and UI.</summary>
    public ConnectHostAuthorizationStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new ConnectHostAuthorizationStatus(_subscribers.Count > 0, _liveConnections.Count);
            }
        }
    }

    /// <summary>
    /// Creates the pipe and starts accepting clients. Throws
    /// <see cref="ConnectPipeNameInUseException"/> if any process already holds the name; the
    /// Agent must then not offer Connect, because that process would receive the tickets.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The host authorization pipe is already running.");
        }

        var firstInstance = ConnectPipeSecurity.CreateFirstInstance(_options.PipeName);
        _acceptLoop = AcceptLoopAsync(firstInstance, _stopping.Token);
        _closeRepeater = RepeatClosesAsync(_stopping.Token);
        _serverChecker = CheckServersAsync(_stopping.Token);
        _logger.LogInformation("1Salem Connect host authorization pipe {PipeName} is listening.", _options.PipeName);
    }

    /// <summary>Refuses the device's future connections and ends its live ones.</summary>
    public Task<IReadOnlyList<string>> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        return EndConnectionsAsync(
            () =>
            {
                _revocations.RevokeDevice(deviceId);
                _replayCache.ForgetDevice(deviceId);
            },
            connection => string.Equals(connection.DeviceId, deviceId, StringComparison.Ordinal),
            "revoked device",
            deviceId,
            cancellationToken);
    }

    /// <summary>Refuses the membership's future connections and ends its live ones.</summary>
    public Task<IReadOnlyList<string>> RevokeMembershipAsync(string membershipId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(membershipId);
        return EndConnectionsAsync(
            () =>
            {
                _revocations.RevokeMembership(membershipId);
                _replayCache.ForgetMembership(membershipId);
            },
            connection => string.Equals(connection.MembershipId, membershipId, StringComparison.Ordinal),
            "revoked membership",
            membershipId,
            cancellationToken);
    }

    /// <summary>
    /// Refuses the ticket's future connections and ends the ones it opened: a session
    /// revocation from the broker's feed (§13), which names one <c>jti</c>.
    /// </summary>
    public Task<IReadOnlyList<string>> RevokeTicketAsync(string ticketId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(ticketId);
        return EndConnectionsAsync(
            () =>
            {
                _revocations.RevokeTicket(ticketId);
                _replayCache.ForgetTicket(ticketId);
            },
            connection => string.Equals(connection.TicketId, ticketId, StringComparison.Ordinal),
            "revoked ticket",
            ticketId,
            cancellationToken);
    }

    /// <summary>
    /// Switches 1Salem Connect off for the server, which refuses its next connections, and ends
    /// its live ones. Both happen under the lock the decisions take, so no connection can be
    /// allowed by a check made before the switch and escape the close.
    /// </summary>
    public Task<IReadOnlyList<string>> DisableConnectAsync(Guid serverId, CancellationToken cancellationToken) =>
        EndConnectionsAsync(
            () => _catalog.EnabledServers.Disable(serverId),
            connection => connection.ServerId == serverId,
            "switched Connect off for server",
            serverId.ToString("D"),
            cancellationToken);

    /// <summary>
    /// Ends the live connections to one server, for example right after the Agent deleted it
    /// or changed its port. Whether new ones are allowed is up to the catalog.
    /// </summary>
    public Task<IReadOnlyList<string>> CloseServerConnectionsAsync(Guid serverId, CancellationToken cancellationToken) =>
        EndConnectionsAsync(
            takeEffect: null,
            connection => connection.ServerId == serverId,
            "is ending the connections to server",
            serverId.ToString("D"),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        if (_closeRepeater is not null)
        {
            await _closeRepeater.ConfigureAwait(false);
        }

        if (_serverChecker is not null)
        {
            await _serverChecker.ConfigureAwait(false);
        }

        PipeSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions];
        }

        foreach (var session in sessions)
        {
            session.Close();
        }

        await Task.WhenAll(sessions.Select(session => session.Completion)).ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>
    /// Applies <paramref name="takeEffect"/> (a revocation or a switch) and marks the matching
    /// live connections as closing under one lock, then pushes the close to every subscriber.
    /// </summary>
    private async Task<IReadOnlyList<string>> EndConnectionsAsync(
        Action? takeEffect,
        Func<ConnectLiveConnections.LiveConnection, bool> matches,
        string action,
        string subject,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> closing;
        PipeSession[] subscribers;
        lock (_gate)
        {
            takeEffect?.Invoke();
            closing = _liveConnections.RequestClose(matches);
            subscribers = [.. _subscribers];
        }

        _logger.LogInformation(
            "1Salem Connect {Action} {Subject}; asking the host transport to close {Count} live connection(s) through {Subscribers} subscriber(s).",
            action,
            subject,
            closing.Count,
            subscribers.Length);
        if (closing.Count > 0)
        {
            await PushClosesAsync(subscribers, closing, cancellationToken).ConfigureAwait(false);
        }

        return closing;
    }

    /// <summary>
    /// Ends the live connections whose server the latest refresh no longer offers on the port
    /// they were allowed to: deleted, switched off, left out of the catalog, or moved. What
    /// listens on the old port may no longer be the server the friend was approved for.
    /// Connections already closing are left to their repeats.
    /// </summary>
    private async Task CloseStaleConnectionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> closing;
        PipeSession[] subscribers;
        lock (_gate)
        {
            closing = _liveConnections.RequestClose(connection => !connection.CloseRequested && IsStale(connection));
            subscribers = [.. _subscribers];
        }

        if (closing.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "1Salem Connect is closing {Count} live connection(s) to servers that were removed, moved to another port or switched off.",
            closing.Count);
        await PushClosesAsync(subscribers, closing, cancellationToken).ConfigureAwait(false);
    }

    private bool IsStale(ConnectLiveConnections.LiveConnection connection) =>
        _catalog.Find(connection.ServerId) is not { ConnectEnabled: true } server ||
        server.LocalPort != connection.LocalPort;

    /// <summary>
    /// Re-reads the server list every <see cref="ConnectHostAuthorizationOptions.ServerCheckInterval"/>
    /// while any connection is live, so a deleted, moved or switched-off server loses its
    /// connections even when no <c>authorize</c> would refresh the list. A failed read keeps the
    /// connections: a locked database is no reason to end every game.
    /// </summary>
    private async Task CheckServersAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(_options.ServerCheckInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_liveConnections.Count == 0)
                    {
                        continue;
                    }
                }

                try
                {
                    await _catalog.RefreshAsync(stopping).ConfigureAwait(false);
                }
                catch (Exception) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        "1Salem Connect could not re-read the server list for its live connections: {Error}",
                        SecretRedactor.Redact(exception.Message));
                    continue;
                }

                await CloseStaleConnectionsAsync(stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Sends the closes the host transport has not confirmed again, a bounded number of times
    /// (see <see cref="ConnectLiveConnections"/> for why). Nothing is used up while no
    /// subscriber is connected: the next subscriber receives every pending close anyway.
    /// </summary>
    private async Task RepeatClosesAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(_options.CloseRepeatInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                IReadOnlyList<string> repeating;
                PipeSession[] subscribers;
                lock (_gate)
                {
                    subscribers = [.. _subscribers];
                    repeating = subscribers.Length == 0 ? [] : _liveConnections.TakeCloseRepeats();
                }

                if (repeating.Count > 0)
                {
                    await PushClosesAsync(subscribers, repeating, stopping).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private Task PushClosesAsync(PipeSession[] subscribers, IReadOnlyList<string> connectionIds, CancellationToken cancellationToken)
    {
        var events = HostAuthorizationMessages.CloseEvents(connectionIds);
        return Task.WhenAll(subscribers.Select(subscriber => PushAsync(subscriber, events, cancellationToken)));
    }

    private async Task PushAsync(PipeSession subscriber, IReadOnlyList<byte[]> events, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SubscriberWriteTimeout);
        try
        {
            await subscriber.WriteAsync(() => events, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException ||
            (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Dropping the subscriber makes the transport end every live connection and
            // reconnect, and its next subscribe receives every close that is still pending.
            _logger.LogWarning("1Salem Connect dropped a subscriber that did not accept a close event.");
            subscriber.Close();
        }
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream listener, CancellationToken stopping)
    {
        try
        {
            while (true)
            {
                try
                {
                    await listener.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The client left before it was accepted. The replacement is created
                    // before the broken instance is closed, so the name stays held.
                    var replacement = ConnectPipeSecurity.CreateNextInstance(_options.PipeName);
                    await listener.DisposeAsync().ConfigureAwait(false);
                    listener = replacement;
                    continue;
                }

                var connected = listener;
                listener = ConnectPipeSecurity.CreateNextInstance(_options.PipeName);
                Accept(connected, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "1Salem Connect host authorization pipe stopped accepting clients: {Error}",
                SecretRedactor.Redact(exception.Message));
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Accept(NamedPipeServerStream stream, CancellationToken stopping)
    {
        var session = new PipeSession(stream);
        lock (_gate)
        {
            if (_sessions.Count >= MaxConcurrentClients)
            {
                session.Close();
                _logger.LogWarning("1Salem Connect refused a pipe client: {Max} clients are already connected.", MaxConcurrentClients);
                return;
            }

            _sessions.Add(session);
        }

        // Served off the accept loop, so a client with a request already waiting cannot delay
        // the next accept.
        session.Completion = Task.Run(() => ServeAsync(session, stopping), CancellationToken.None);
    }

    private async Task ServeAsync(PipeSession session, CancellationToken stopping)
    {
        var reader = new JsonLineReader(session.Stream);
        try
        {
            while (await reader.ReadAsync(stopping).ConfigureAwait(false) is { } request)
            {
                var receivedAt = _clock.GetTimestamp();
                if (!await HandleAsync(session, request, receivedAt, stopping).ConfigureAwait(false))
                {
                    _logger.LogWarning("1Salem Connect dropped a pipe client that sent a request without an id and op.");
                    break;
                }
            }
        }
        catch (InvalidDataException)
        {
            _logger.LogWarning("1Salem Connect dropped a pipe client that broke the line framing.");
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException ||
            (exception is OperationCanceledException && stopping.IsCancellationRequested))
        {
            // The client went away, or the Agent is stopping.
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "1Salem Connect pipe client failed: {Error}",
                SecretRedactor.Redact(exception.Message));
        }
        finally
        {
            lock (_gate)
            {
                _sessions.Remove(session);

                // The transport ended every live connection when this subscription ended. Any
                // subscription, not only the last one, because a restarted transport may
                // subscribe again before the Agent sees that the old one broke.
                if (_subscribers.Remove(session))
                {
                    _liveConnections.SubscriptionLost();
                }
            }

            session.Close();
        }
    }

    /// <param name="receivedAt">When the request was read, as a <see cref="TimeProvider"/> timestamp.</param>
    /// <returns>False when the request has no usable id or op; the connection is then dropped.</returns>
    private async Task<bool> HandleAsync(PipeSession session, JsonElement request, long receivedAt, CancellationToken stopping)
    {
        if (!TryReadEnvelope(request, out var id, out var operation))
        {
            return false;
        }

        if (operation == "hello")
        {
            var supported = IsSupportedVersion(request);
            session.Greeted |= supported;
            await session.WriteAsync(
                    supported
                        ? HostAuthorizationMessages.Hello(id)
                        : HostAuthorizationMessages.Error(id, HostAuthorizationMessages.Errors.UnsupportedVersion),
                    stopping)
                .ConfigureAwait(false);
            return true;
        }

        if (!session.Greeted)
        {
            // hello settles the protocol version; nothing is interpreted before it has.
            await session.WriteAsync(HostAuthorizationMessages.Error(id, HostAuthorizationMessages.Errors.HelloRequired), stopping)
                .ConfigureAwait(false);
            return true;
        }

        switch (operation)
        {
            case "authorize":
                await AuthorizeAsync(session, id, request, receivedAt, stopping).ConfigureAwait(false);
                break;
            case "closed":
                await session.WriteAsync(Closed(id, request), stopping).ConfigureAwait(false);
                break;
            case "subscribe":
                await SubscribeAsync(session, id, stopping).ConfigureAwait(false);
                break;
            default:
                await session.WriteAsync(HostAuthorizationMessages.Error(id, HostAuthorizationMessages.Errors.UnknownOperation), stopping)
                    .ConfigureAwait(false);
                break;
        }

        return true;
    }

    private static bool TryReadEnvelope(JsonElement request, out long id, out string operation)
    {
        id = 0;
        operation = string.Empty;
        if (!request.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.Number ||
            !idElement.TryGetInt64(out id) ||
            id < 0 ||
            !request.TryGetProperty("op", out var opElement) ||
            opElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        operation = opElement.GetString()!;
        return true;
    }

    private static bool IsSupportedVersion(JsonElement request) =>
        request.TryGetProperty("v", out var version) &&
        version.ValueKind == JsonValueKind.Number &&
        version.TryGetInt32(out var number) &&
        number == HostAuthorizationMessages.ProtocolVersion;

    private async Task AuthorizeAsync(PipeSession session, long id, JsonElement request, long receivedAt, CancellationToken stopping)
    {
        var (reply, connectionId) = await DecideAsync(id, request, receivedAt, stopping).ConfigureAwait(false);
        try
        {
            await session.WriteAsync(reply, stopping).ConfigureAwait(false);
        }
        catch when (connectionId is not null)
        {
            // The transport never learned this id, so it will never report it closed.
            lock (_gate)
            {
                _liveConnections.Remove(connectionId);
            }

            throw;
        }
    }

    /// <returns>The reply, and the connection id when the connection was allowed.</returns>
    private async Task<(byte[] Reply, string? ConnectionId)> DecideAsync(
        long id,
        JsonElement request,
        long receivedAt,
        CancellationToken stopping)
    {
        HostAuthorizationRequest parsed;
        try
        {
            parsed = HostAuthorizationRequest.Parse(request);
        }
        catch (FormatException)
        {
            _logger.LogInformation("1Salem Connect refused a connection: {Reason}.", AccessDenialReason.MalformedPreamble);
            return (HostAuthorizationMessages.Deny(id), null);
        }

        try
        {
            await _catalog.RefreshAsync(stopping).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                "1Salem Connect refused a connection because the server list could not be read: {Error}",
                SecretRedactor.Redact(exception.Message));
            return (HostAuthorizationMessages.Deny(id), null);
        }

        await CloseStaleConnectionsAsync(stopping).ConfigureAwait(false);

        HostAuthorizationResult? result = null;
        var tracked = false;
        TimeSpan elapsed;
        lock (_gate)
        {
            // Checked under the lock, just before the decision, so nothing slow can come between
            // the check and the allow. Refused before the authorizer runs, so a late request
            // does not use up its nonce or a slot of its ticket.
            elapsed = _clock.GetElapsedTime(receivedAt);
            if (_subscribers.Count > 0 && elapsed <= DecisionBudget)
            {
                result = _authorizer.Authorize(parsed);
                tracked = result.IsAllowed &&
                    _liveConnections.TryAdd(result.ConnectionId!, result.Claims!, result.Endpoint!.Port);
            }
        }

        if (result is null)
        {
            if (elapsed > DecisionBudget)
            {
                _logger.LogWarning(
                    "1Salem Connect refused a connection from node {NodeId}: the decision took {Elapsed}, so the host transport may already have stopped waiting for it.",
                    parsed.PeerNodeId,
                    elapsed);
            }
            else
            {
                _logger.LogWarning(
                    "1Salem Connect refused a connection from node {NodeId}: no host transport is subscribed to close events, so the connection could not be ended on revocation.",
                    parsed.PeerNodeId);
            }

            return (HostAuthorizationMessages.Deny(id), null);
        }

        if (!result.IsAllowed)
        {
            _logger.LogInformation(
                "1Salem Connect refused a connection from node {NodeId}: {Reason}.",
                parsed.PeerNodeId,
                result.Reason);
            return (HostAuthorizationMessages.Deny(id), null);
        }

        var claims = result.Claims!;
        if (!tracked)
        {
            _logger.LogWarning(
                "1Salem Connect refused a connection for device {DeviceId}: too many live connections are tracked.",
                claims.DeviceId);
            return (HostAuthorizationMessages.Deny(id), null);
        }

        _logger.LogInformation(
            "1Salem Connect allowed connection {ConnectionId} for device {DeviceId} (membership {MembershipId}) to server {ServerId} on {Endpoint}.",
            result.ConnectionId,
            claims.DeviceId,
            claims.MembershipId,
            claims.ServerIdText,
            result.Endpoint);
        return (HostAuthorizationMessages.Allow(id, result.Endpoint!, result.ConnectionId!), result.ConnectionId);
    }

    private byte[] Closed(long id, JsonElement request)
    {
        if (!request.TryGetProperty("connId", out var connectionElement) ||
            connectionElement.ValueKind != JsonValueKind.String ||
            connectionElement.GetString() is not { Length: > 0 } connectionId)
        {
            return HostAuthorizationMessages.Error(id, HostAuthorizationMessages.Errors.BadRequest);
        }

        bool removed;
        lock (_gate)
        {
            removed = _liveConnections.Remove(connectionId);
        }

        // An unknown id is still acknowledged: the report may repeat one the Agent already
        // dropped, and the transport has nothing better to do with an error.
        _logger.LogDebug(
            "1Salem Connect connection {ConnectionId} closed (known: {Known}, in: {BytesIn}, out: {BytesOut}).",
            connectionId,
            removed,
            ReadCount(request, "bytesIn"),
            ReadCount(request, "bytesOut"));
        return HostAuthorizationMessages.Ok(id);
    }

    private Task SubscribeAsync(PipeSession session, long id, CancellationToken stopping) =>
        // Registration happens while this session's writer is held, so the acknowledgement is
        // always the first line the subscriber reads, followed by any closes still pending.
        session.WriteAsync(
            () =>
            {
                IReadOnlyList<string> pending;
                lock (_gate)
                {
                    _subscribers.Add(session);
                    pending = _liveConnections.PendingClose();
                }

                return [HostAuthorizationMessages.Ok(id), .. HostAuthorizationMessages.CloseEvents(pending)];
            },
            stopping);

    private static long? ReadCount(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var count)
            ? count
            : null;

    /// <summary>One accepted pipe client. Writes are serialized; reads belong to its serve loop.</summary>
    private sealed class PipeSession(NamedPipeServerStream stream)
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public NamedPipeServerStream Stream { get; } = stream;

        public Task Completion { get; set; } = Task.CompletedTask;

        /// <summary>Set once hello succeeded. Read and written only by the serve loop.</summary>
        public bool Greeted { get; set; }

        public Task WriteAsync(byte[] line, CancellationToken cancellationToken) =>
            WriteAsync(() => [line], cancellationToken);

        /// <summary>
        /// Writes the produced lines back to back. <paramref name="produceLines"/> runs after the
        /// writer is acquired, so whatever it registers cannot be overtaken by another writer.
        /// </summary>
        public async Task WriteAsync(Func<IReadOnlyList<byte[]>> produceLines, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var line in produceLines())
                {
                    await JsonLines.WriteAsync(Stream, line, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }

        /// <summary>Ends the connection; a blocked read or write fails and the serve loop exits.</summary>
        public void Close() => Stream.Dispose();
    }
}
