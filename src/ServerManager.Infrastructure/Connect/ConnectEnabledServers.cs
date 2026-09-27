namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// The in-memory enforcement switch for the servers the owner has turned Connect on for. Phase 2
/// restores it from <see cref="ConnectStateStore"/> only after the policy and host node pass their
/// checks; until then every server remains fail-closed.
/// </summary>
public sealed class ConnectEnabledServers
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _serverIds = [];

    public void Enable(Guid serverId)
    {
        lock (_gate)
        {
            _serverIds.Add(serverId);
        }
    }

    public void Disable(Guid serverId)
    {
        lock (_gate)
        {
            _serverIds.Remove(serverId);
        }
    }

    public bool IsEnabled(Guid serverId)
    {
        lock (_gate)
        {
            return _serverIds.Contains(serverId);
        }
    }

    public IReadOnlyList<Guid> Snapshot()
    {
        lock (_gate)
        {
            return _serverIds.Order().ToArray();
        }
    }

    public void Replace(IEnumerable<Guid> serverIds)
    {
        ArgumentNullException.ThrowIfNull(serverIds);
        lock (_gate)
        {
            _serverIds.Clear();
            foreach (var serverId in serverIds.Where(id => id != Guid.Empty))
            {
                _serverIds.Add(serverId);
            }
        }
    }
}
