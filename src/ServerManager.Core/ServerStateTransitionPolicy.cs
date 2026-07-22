using ServerManager.Contracts;

namespace ServerManager.Core;

public static class ServerStateTransitionPolicy
{
    private static readonly IReadOnlyDictionary<ServerState, HashSet<ServerState>> Allowed =
        new Dictionary<ServerState, HashSet<ServerState>>
        {
            [ServerState.NotInstalled] = [ServerState.Stopped, ServerState.Error],
            [ServerState.Stopped] =
            [
                ServerState.Starting,
                ServerState.Updating,
                ServerState.BackingUp,
                ServerState.Restoring,
                ServerState.NotInstalled,
                ServerState.Error
            ],
            [ServerState.Starting] = [ServerState.Running, ServerState.Crashed, ServerState.Error, ServerState.Stopping],
            [ServerState.Running] =
            [
                ServerState.Stopping,
                ServerState.Restarting,
                ServerState.BackingUp,
                ServerState.Crashed,
                ServerState.Error
            ],
            [ServerState.Stopping] = [ServerState.Stopped, ServerState.Crashed, ServerState.Error],
            [ServerState.Restarting] = [ServerState.Running, ServerState.Stopped, ServerState.Crashed, ServerState.Error],
            [ServerState.Updating] = [ServerState.Stopped, ServerState.Starting, ServerState.Error],
            [ServerState.BackingUp] = [ServerState.Stopped, ServerState.Running, ServerState.Error],
            [ServerState.Restoring] = [ServerState.Stopped, ServerState.Starting, ServerState.Error],
            [ServerState.Crashed] = [ServerState.Starting, ServerState.Stopped, ServerState.Error],
            [ServerState.Error] = [ServerState.Stopped, ServerState.Starting, ServerState.NotInstalled]
        };

    public static bool CanTransition(ServerState from, ServerState to) =>
        from == to || Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureAllowed(ServerState from, ServerState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Server state cannot transition from {from} to {to}.");
        }
    }
}
