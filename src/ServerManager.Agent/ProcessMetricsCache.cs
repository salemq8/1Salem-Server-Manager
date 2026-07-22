using System.Collections.Concurrent;
using ServerManager.Core;

namespace ServerManager.Agent;

public sealed class ProcessMetricsCache
{
    private readonly ConcurrentDictionary<Guid, ProcessSnapshot> _snapshots = new();

    public IReadOnlyList<ProcessSnapshot> Snapshot() =>
        _snapshots.Values.OrderBy(snapshot => snapshot.ProcessId).ToArray();

    public void Replace(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        _snapshots.Clear();
        foreach (var snapshot in snapshots)
        {
            _snapshots[snapshot.ServerId] = snapshot;
        }
    }
}
