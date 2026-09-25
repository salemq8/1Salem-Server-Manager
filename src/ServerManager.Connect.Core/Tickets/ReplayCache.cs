namespace ServerManager.Connect.Core.Tickets;

public enum ReplayCheckResult
{
    Registered,
    Replayed,
    CapacityExceeded,
    TicketLimitReached
}

/// <summary>
/// Remembers every accepted <c>(jti, n)</c> pair until its ticket can no longer verify
/// (contract §10 step 3), so an exact replay of a captured preamble is refused even inside the
/// ±60 s proof window.
/// The cache is bounded twice, and in both cases it refuses new connections rather than evicting
/// an entry early, because an evicted pair could be replayed. A flood of valid connections
/// therefore degrades to "refused", never to "replay accepted".
/// <list type="bullet">
/// <item>Each ticket may register at most <see cref="DefaultPerTicketLimit"/> connections over its
/// lifetime. A Minecraft session needs a handful (the login and the odd server-list ping), and the
/// friend app replaces its ticket every few minutes. Without this limit one approved friend could
/// mint fresh nonces with their own session key until the whole cache was full, and every other
/// friend would be refused until those entries expired.</item>
/// <item>The whole cache holds at most <see cref="DefaultCapacity"/> pairs, as a last resort
/// against a friend who holds many tickets at once.</item>
/// </list>
/// The entries of a revoked ticket, device or membership can be forgotten at once
/// (<see cref="ForgetTicket"/>, <see cref="ForgetDevice"/>, <see cref="ForgetMembership"/>), so a
/// revoked friend stops occupying the cache. That is safe only because the verifier refuses
/// revoked tickets before the replay check: forgetting must follow the revocation, never replace it.
/// </summary>
public sealed class ReplayCache
{
    public const int DefaultCapacity = 100_000;

    public const int DefaultPerTicketLimit = 64;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly int _capacity;
    private readonly int _perTicketLimit;
    private readonly Dictionary<string, TicketNonces> _tickets = new(StringComparer.Ordinal);

    // A ticket forgotten early leaves its item here until the item's time comes; Purge skips it.
    private readonly PriorityQueue<string, DateTimeOffset> _expiries = new();
    private int _count;

    public ReplayCache(TimeProvider clock, int capacity = DefaultCapacity, int perTicketLimit = DefaultPerTicketLimit)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perTicketLimit, 1);
        _capacity = capacity;
        _perTicketLimit = perTicketLimit;
    }

    /// <summary>The number of remembered pairs.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                Purge(_clock.GetUtcNow());
                return _count;
            }
        }
    }

    /// <summary>
    /// Atomically checks and records the pair for the verified <paramref name="claims"/>. The pair
    /// is kept until the last instant the ticket could still be accepted: its <c>exp</c> plus
    /// <see cref="TicketVerifier.ClockSkew"/>.
    /// </summary>
    public ReplayCheckResult TryRegister(TicketClaims claims, string nonce)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        lock (_gate)
        {
            Purge(_clock.GetUtcNow());
            if (_tickets.TryGetValue(claims.TicketId, out var ticket))
            {
                if (ticket.Nonces.Contains(nonce))
                {
                    return ReplayCheckResult.Replayed;
                }

                if (ticket.Nonces.Count >= _perTicketLimit)
                {
                    return ReplayCheckResult.TicketLimitReached;
                }
            }

            if (_count >= _capacity)
            {
                return ReplayCheckResult.CapacityExceeded;
            }

            if (ticket is null)
            {
                var retainUntil = DateTimeOffset.FromUnixTimeSeconds(claims.ExpiresAt) + TicketVerifier.ClockSkew;
                ticket = new TicketNonces(claims.DeviceId, claims.MembershipId);
                _tickets.Add(claims.TicketId, ticket);
                _expiries.Enqueue(claims.TicketId, retainUntil);
            }

            ticket.Nonces.Add(nonce);
            _count++;
            return ReplayCheckResult.Registered;
        }
    }

    /// <summary>Drops the pairs of a revoked ticket. Call only after the revocation took effect.</summary>
    public void ForgetTicket(string ticketId)
    {
        ArgumentException.ThrowIfNullOrEmpty(ticketId);
        lock (_gate)
        {
            if (_tickets.Remove(ticketId, out var ticket))
            {
                _count -= ticket.Nonces.Count;
            }
        }
    }

    /// <summary>Drops the pairs of every ticket of a revoked device. Call only after the revocation took effect.</summary>
    public void ForgetDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        Forget(ticket => string.Equals(ticket.DeviceId, deviceId, StringComparison.Ordinal));
    }

    /// <summary>Drops the pairs of every ticket of a revoked membership. Call only after the revocation took effect.</summary>
    public void ForgetMembership(string membershipId)
    {
        ArgumentException.ThrowIfNullOrEmpty(membershipId);
        Forget(ticket => string.Equals(ticket.MembershipId, membershipId, StringComparison.Ordinal));
    }

    private void Forget(Func<TicketNonces, bool> revoked)
    {
        lock (_gate)
        {
            foreach (var (ticketId, ticket) in _tickets.Where(pair => revoked(pair.Value)).ToArray())
            {
                _tickets.Remove(ticketId);
                _count -= ticket.Nonces.Count;
            }
        }
    }

    private void Purge(DateTimeOffset now)
    {
        while (_expiries.TryPeek(out var ticketId, out var retainUntil) && retainUntil < now)
        {
            _expiries.Dequeue();
            if (_tickets.Remove(ticketId, out var ticket))
            {
                _count -= ticket.Nonces.Count;
            }
        }
    }

    /// <summary>
    /// The nonces seen for one ticket. They all expire with the ticket, so the ticket is queued
    /// for expiry once, and its device and membership let a revocation find them.
    /// </summary>
    private sealed class TicketNonces(string deviceId, string membershipId)
    {
        public string DeviceId { get; } = deviceId;

        public string MembershipId { get; } = membershipId;

        public HashSet<string> Nonces { get; } = new(StringComparer.Ordinal);
    }
}
