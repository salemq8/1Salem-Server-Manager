using Microsoft.Data.Sqlite;
using ServerManager.Infrastructure.Connect;

namespace Phase2Acceptance;

internal sealed partial class AcceptanceRun
{
    private int? _temporaryNodesRemaining;

    public async Task CleanupAsync()
    {
        var complete = true;
        async Task Attempt(string label, Func<Task> action)
        {
            try { await action(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { complete = false; Record(label, false, exception.GetType().Name); }
        }
        await Attempt("close owned sessions", () =>
        {
            _connection?.StopMonitoring();
            _sessions?.CloseOpenSessionsAtExit(TimeSpan.FromSeconds(3));
            _friendProcess?.Dispose(); _friendProcess = null;
            return Task.CompletedTask;
        });
        await Attempt("stop isolated Agent", StopAgentAsync);
        await Attempt("close owned process job", () => { _job.Dispose(); return Task.CompletedTask; });
        await Attempt("close disposable loopback targets", () =>
        {
            _gameA?.Dispose(); _gameA = null; _gameB?.Dispose(); _gameB = null;
            return Task.CompletedTask;
        });
        await Attempt("recover exact resource ledger", () =>
        {
            ReadAgentJournal(); RecoverMarkers(); SaveLedger(); return Task.CompletedTask;
        });
        if (_api is not null)
        {
            // Never discover a deletion target by tag or hostname. The creation journal or
            // owned marker must have recorded its ID, and the real API must match it exactly.
            foreach (var node in _nodes.Where(node => node.NodeId is not null))
            {
                await Attempt("cleanup " + node.Role, async () =>
                {
                    CleanupNodeGate.RequireNotBaseline(_baselineNodes.Contains(node.NodeId!));
                    var before = await _api.GetDeviceAsync(node.NodeId!);
                    var decision = CleanupNodeGate.Evaluate(before.Status,
                        before.Device is not null && IsRunNode(before.Device, node),
                        node.SeenByApi, _deviceReads.Contains(node.NodeId!));
                    node.SeenByApi = decision.SeenByApi;
                    // A prior journal delete attempt is not a substitute for visibility and
                    // final confirmation. Keep removal false if this attempt is ambiguous.
                    node.Removed = false;
                    SaveLedger();
                    if (decision.PreserveRecoveryState) throw new InvalidOperationException(decision.Failure);
                    if (decision.DeleteDevice)
                        await _api.DeleteDeviceAsync(node.NodeId!);
                    node.Removed = decision.ConfirmsRemoval((await _api.GetDeviceAsync(node.NodeId!)).Status);
                    SaveLedger();
                    if (!node.Removed) throw new InvalidOperationException("Device deletion unconfirmed.");
                    Record("cleanup " + node.Role, true, "real Devices API GET 404");
                });
            }
            foreach (var id in _keys.Keys.ToArray())
            {
                await Attempt("cleanup one-time key", async () =>
                {
                    var status = await _api.DeleteKeyAsync(id);
                    _keys[id] = status is >= 200 and < 300 or 404;
                    SaveLedger();
                    if (!_keys[id]) throw new InvalidOperationException("Key removal unconfirmed.");
                    Record("cleanup one-time key", true, "DELETE HTTP " + status);
                });
            }
            await Attempt("verify no remaining run nodes", async () =>
            {
                var remaining = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tag in new[] { TailscaleApiProvisioner.HostTag, TailscaleApiProvisioner.FriendTag })
                    foreach (var device in await _api.ListDevicesAsync(tag))
                        if (!_baselineNodes.Contains(device.NodeId) && _nodes.Any(node => node.NodeId == device.NodeId ||
                            string.Equals(node.Hostname, device.Hostname, StringComparison.OrdinalIgnoreCase)))
                            remaining.Add(device.NodeId);
                _temporaryNodesRemaining = remaining.Count;
                if (remaining.Count != 0) throw new InvalidOperationException("Run nodes remain; preserve recovery state.");
                Record("temporary nodes remaining", true, "0");
            });
        }
        else _temporaryNodesRemaining = 0; // No API or enrollment was possible.

        // Preserve recovery material if any cloud deletion or ownership check failed.
        if (complete && _temporaryNodesRemaining == 0 && _keys.Values.All(removed => removed))
        {
            await Attempt("remove isolated credential and transport state", () =>
            {
                SqliteConnection.ClearAllPools();
                foreach (var directory in new[] { AgentRoot, Work("friend"), Work("probe-state") }) RemoveOwnedDirectory(directory);
                return Task.CompletedTask;
            });
        }
        else complete = false;
        Record("cleanup complete", complete, complete ? "owned nodes, keys, processes, cloned credential and state removed; metadata retained" : "recovery metadata/state retained; no broad deletion attempted");
    }

    private void RemoveOwnedDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        var root = Path.GetFullPath(_options.Work).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Cleanup target escaped the run root.");
        AcceptanceOptions.AssertNoReparse(full);
        if (!Directory.Exists(full)) return;
        var pending = new Stack<string>(); pending.Push(full);
        while (pending.TryPop(out var current))
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Cleanup refuses reparse contents.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        Directory.Delete(full, recursive: true);
    }
}
