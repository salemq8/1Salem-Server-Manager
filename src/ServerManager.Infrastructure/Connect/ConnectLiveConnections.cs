using ServerManager.Connect.Core.Tickets;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// The bridged connections the Agent has allowed and not yet heard <c>closed</c> for. Each one
/// records the friend and the server endpoint it belongs to, so a revocation or a server change
/// can name the connections to end (contract §10 step 5, §12 step 2).
/// A connection the Agent asked to close stays here, marked, until the host transport reports
/// it closed or its close has been repeated <see cref="CloseRepeats"/> times
/// (<see cref="TakeCloseRepeats"/>). While no subscriber is listening, nothing is used up, so the
/// next <c>subscribe</c> still receives it. A revocation is never lost because the transport
/// was reconnecting at that moment.
/// The repeats exist because the allow and the close event travel on different pipe
/// connections. The event can reach the transport a moment before the transport has registered
/// the connection, and the transport then has nothing to close. The repeats cover the host
/// transport's whole 8 s wait for an allow: with the default 2 s interval the last repeat comes
/// more than 8 s after the close was requested, and so after any connection the transport could
/// still register. After that last repeat the entry is forgotten. A connection the transport
/// knew has been ended by then, and an id it never learned (it gave up waiting for the allow)
/// would never be reported closed and would otherwise hold its slot for good.
/// The table is bounded. When it is full, new connections are refused rather than tracked
/// loosely, because an untracked connection is one a revocation could not end.
/// Not thread-safe: the pipe server guards it together with the revocation set.
/// </summary>
internal sealed class ConnectLiveConnections
{
    public const int DefaultCapacity = 10_000;

    /// <summary>How many times a close is repeated after it was first requested.</summary>
    public const int CloseRepeats = 5;

    private readonly int _capacity;
    private readonly Dictionary<string, LiveConnection> _connections = new(StringComparer.Ordinal);

    public ConnectLiveConnections(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Count => _connections.Count;

    /// <summary>
    /// Records a connection allowed to 127.0.0.1:<paramref name="localPort"/>. False when the
    /// table is full or the id is already live; the caller must then refuse the connection.
    /// </summary>
    public bool TryAdd(string connectionId, TicketClaims claims, int localPort)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        ArgumentNullException.ThrowIfNull(claims);
        if (_connections.Count >= _capacity)
        {
            return false;
        }

        return _connections.TryAdd(
            connectionId,
            new LiveConnection(claims.DeviceId, claims.MembershipId, claims.TicketId, claims.ServerId, localPort));
    }

    public bool Remove(string connectionId) => _connections.Remove(connectionId);

    /// <summary>
    /// Marks every connection that matches as closing and returns all of their ids, including
    /// ones an earlier request already marked when the predicate admits them, so asking again
    /// repeats the request and renews its repeats.
    /// </summary>
    public IReadOnlyList<string> RequestClose(Func<LiveConnection, bool> matches)
    {
        var ids = new List<string>();
        foreach (var (id, connection) in _connections)
        {
            if (matches(connection))
            {
                connection.CloseRequested = true;
                connection.CloseRepeatsLeft = CloseRepeats;
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// A subscription ended. The host transport ends every live connection when its
    /// subscription ends (a connection the Agent cannot end must not exist), and a transport
    /// that crashed or restarted will never report them closed.
    /// They are not dropped on the spot. An allow decided just before the loss can still reach
    /// the transport after it has subscribed again, and it would then run a connection this
    /// table no longer knew. So every entry is asked to close: the next subscription receives
    /// them all and their repeats, and then they are forgotten.
    /// </summary>
    public void SubscriptionLost() => RequestClose(_ => true);

    /// <summary>Connections asked to close that the transport has not reported closed.</summary>
    public IReadOnlyList<string> PendingClose() =>
        _connections.Where(pair => pair.Value.CloseRequested).Select(pair => pair.Key).ToArray();

    /// <summary>
    /// The pending closes that still have a repeat left, each using one up. An entry whose last
    /// repeat this takes is forgotten.
    /// </summary>
    public IReadOnlyList<string> TakeCloseRepeats()
    {
        var ids = new List<string>();
        foreach (var (id, connection) in _connections)
        {
            if (connection.CloseRequested && connection.CloseRepeatsLeft > 0)
            {
                connection.CloseRepeatsLeft--;
                ids.Add(id);
            }
        }

        foreach (var id in ids)
        {
            if (_connections[id].CloseRepeatsLeft == 0)
            {
                _connections.Remove(id);
            }
        }

        return ids;
    }

    internal sealed class LiveConnection(string deviceId, string membershipId, string ticketId, Guid serverId, int localPort)
    {
        public string DeviceId { get; } = deviceId;

        public string MembershipId { get; } = membershipId;

        public string TicketId { get; } = ticketId;

        public Guid ServerId { get; } = serverId;

        /// <summary>The loopback port the connection was allowed to.</summary>
        public int LocalPort { get; } = localPort;

        public bool CloseRequested { get; set; }

        public int CloseRepeatsLeft { get; set; }
    }
}
