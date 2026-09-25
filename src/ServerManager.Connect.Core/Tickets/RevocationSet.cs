namespace ServerManager.Connect.Core.Tickets;

/// <summary>
/// The host's local revocation state (contract §10 step 5, §12): revoked ticket ids, devices and
/// memberships, and a per-membership floor for the authorization version <c>av</c>. Revocations
/// take effect on the next check, so a revoked friend is refused immediately, without waiting
/// for the broker or for ticket expiry.
/// </summary>
public sealed class RevocationSet
{
    /// <summary>
    /// A ticket lives at most 900 s and is accepted for at most 30 s of skew beyond that, so a
    /// ticket revoked more than an hour ago can no longer verify anyway. Only ticket revocations
    /// age out. Device and membership revocations are permanent.
    /// </summary>
    public static readonly TimeSpan TicketRevocationRetention = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, DateTimeOffset> _tickets = new(StringComparer.Ordinal);
    private readonly Queue<(string TicketId, DateTimeOffset RevokedAt)> _ticketOrder = new();
    private readonly HashSet<string> _devices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _memberships = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _authorizationFloors = new(StringComparer.Ordinal);

    public RevocationSet(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public void RevokeTicket(string ticketId)
    {
        ArgumentException.ThrowIfNullOrEmpty(ticketId);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            PruneTickets(now);
            if (_tickets.TryAdd(ticketId, now))
            {
                _ticketOrder.Enqueue((ticketId, now));
            }
        }
    }

    public void RevokeDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        lock (_gate)
        {
            _devices.Add(deviceId);
        }
    }

    public void RevokeMembership(string membershipId)
    {
        ArgumentException.ThrowIfNullOrEmpty(membershipId);
        lock (_gate)
        {
            _memberships.Add(membershipId);
        }
    }

    /// <summary>
    /// Tickets for <paramref name="membershipId"/> with <c>av</c> below
    /// <paramref name="minimumVersion"/> are refused. The floor only ever rises: lowering it
    /// would silently re-enable tickets that were revoked.
    /// </summary>
    public void RaiseAuthorizationFloor(string membershipId, long minimumVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(membershipId);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumVersion);
        lock (_gate)
        {
            if (!_authorizationFloors.TryGetValue(membershipId, out var current) || minimumVersion > current)
            {
                _authorizationFloors[membershipId] = minimumVersion;
            }
        }
    }

    public bool IsTicketRevoked(string ticketId)
    {
        lock (_gate)
        {
            PruneTickets(_clock.GetUtcNow());
            return _tickets.ContainsKey(ticketId);
        }
    }

    public bool IsDeviceRevoked(string deviceId)
    {
        lock (_gate)
        {
            return _devices.Contains(deviceId);
        }
    }

    public bool IsMembershipRevoked(string membershipId)
    {
        lock (_gate)
        {
            return _memberships.Contains(membershipId);
        }
    }

    public bool IsBelowAuthorizationFloor(string membershipId, long authorizationVersion)
    {
        lock (_gate)
        {
            return _authorizationFloors.TryGetValue(membershipId, out var floor) && authorizationVersion < floor;
        }
    }

    private void PruneTickets(DateTimeOffset now)
    {
        while (_ticketOrder.TryPeek(out var oldest) && now - oldest.RevokedAt > TicketRevocationRetention)
        {
            _ticketOrder.Dequeue();
            _tickets.Remove(oldest.TicketId);
        }
    }
}
