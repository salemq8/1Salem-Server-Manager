namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// The servers the owner has turned 1Salem Connect on for. Phase 1 keeps this in memory only:
/// there is no schema change and nothing survives an Agent restart, so every server starts with
/// Connect off. That is the safe default, because a server that is not in this set can never be
/// reached through a ticket.
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
}
