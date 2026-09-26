using System.Text.Json;
using System.Text.Json.Nodes;
using ServerManager.Connect.App.Transport;

namespace TsnetSmoke;

/// <summary>
/// Step 13: always runs (on success, on failure and after Ctrl+C), and records every step. Each
/// step catches its own failure, so one that fails never keeps a later one from deleting what it
/// can. It deletes only tailnet devices whose node id this run recorded when it enrolled them, and
/// only after the API shows exactly the expected tag, a creation time after the run started and
/// not ephemeral; it never lists devices to delete them. A device that does not match is
/// reported and left alone, with the way to remove it by hand.
/// </summary>
internal static partial class Program
{
    /// <summary>Allowed difference between this PC's clock and Tailscale's when judging a device's age.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private static async Task CleanUpAsync(SmokeRun run)
    {
        Console.WriteLine();
        Console.WriteLine("--- cleanup ---");
        var steps = new List<bool>();

        // Processes first: a node's state must be released before it is deleted, and a node should
        // be offline before its device is removed from the tailnet.
        steps.Add(await StopFriendAppAsync(run));
        steps.Add(await StopHostTransportAsync(run));
        steps.Add(await StepAsync("local test objects released", async () =>
        {
            await run.DisposeOwnedAsync();
            return (true, "authorization pipe, test service, clients and identities");
        }));

        foreach (var node in run.Nodes)
        {
            RecoverNodeIdFromMarker(run, node);
        }

        if (run.Api is { } api)
        {
            steps.Add(await DeleteNodeAsync(run, api, run.Host));
            steps.Add(await DeleteNodeAsync(run, api, run.Friend));
            if (run.Probe.NodeId is not null)
            {
                // After the host is gone and before the probe node is: the probe restarts from its state.
                await RunDepartedProbeAsync(run);
                steps.Add(await DeleteNodeAsync(run, api, run.Probe));
            }

            foreach (var keyId in run.KeyIds)
            {
                steps.Add(await DeleteKeyAsync(run, api, keyId));
            }

            steps.Add(await VerifyNothingLeftAsync(run, api));
        }

        // The node directories hold evidence (the log buffers) and go last.
        CheckNodeEvidence(run);
        foreach (var name in new[] { "host-state", "probe-state", "friend-app", "owner" })
        {
            steps.Add(await DeleteDirectoryAsync(run, name));
        }

        run.TailnetHttp.Dispose();
        Record("cleanup complete", steps.TrueForAll(ok => ok), $"{steps.Count(ok => ok)}/{steps.Count} cleanup steps succeeded");
    }

    /// <summary>What the app does at exit (close its sessions, stop its transport), after the transport's last log lines are kept.</summary>
    private static async Task<bool> StopFriendAppAsync(SmokeRun run)
    {
        if (run.FriendLog is { } collector)
        {
            await collector.StopAsync();
        }

        if (run.App is not { } app)
        {
            return true;
        }

        return await StepAsync("the friend app stopped its transport", async () =>
        {
            app.Main.Shutdown();
            var gone = await WaitUntilAsync(() => IsGoneAsync(app.Transport), TimeSpan.FromSeconds(10), CancellationToken.None);
            return (gone, gone ? "pipe gone" : "still answering");
        });
    }

    private static async Task<bool> StopHostTransportAsync(SmokeRun run)
    {
        if (run.HostLog is { } collector)
        {
            await collector.StopAsync();
        }

        if (run.HostTransport is not { } process)
        {
            return true;
        }

        return await StepAsync("host transport stopped", async () =>
        {
            if (run.HostControl is { } control)
            {
                await control.DisposeAsync();
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            return (process.HasExited, $"pid {process.Id} exited");
        });
    }

    /// <summary>
    /// A node whose enrollment was cut short (a timeout, Ctrl+C, a crash of the probe) has no
    /// recorded id, but its transport wrote the id into the node's 1salem-node.json marker the
    /// moment it came up. The marker is used only when it names the hostname this run chose for
    /// that node, and the deletion still requires the tag, age and isEphemeral checks.
    /// </summary>
    private static void RecoverNodeIdFromMarker(SmokeRun run, SmokeNode node)
    {
        if (node.NodeId is not null || node.StateDirectory is null)
        {
            return;
        }

        var marker = Path.Combine(node.StateDirectory, "1salem-node.json");
        try
        {
            if (!File.Exists(marker))
            {
                return;
            }

            var saved = JsonNode.Parse(File.ReadAllText(marker));
            if (saved?["hostname"]?.GetValue<string>() == node.Hostname && saved?["nodeId"]?.GetValue<string>() is { Length: > 0 } nodeId)
            {
                run.RecordNodeId(node, nodeId);
                Console.WriteLine($"  {node.Role} node id {nodeId} recovered from its marker file");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"  the {node.Role} node's marker could not be read: {exception.Message}");
        }
    }

    /// <summary>
    /// Deletes one device this run enrolled, if the API still shows it as this run's, and confirms
    /// it is gone with a second read. Every HTTP status is recorded: whether a client that carries
    /// only the host tag may read and delete client-tagged devices is not documented.
    /// </summary>
    private static Task<bool> DeleteNodeAsync(SmokeRun run, SmokeTailnetApi api, SmokeNode node)
    {
        if (node.NodeId is not { } nodeId)
        {
            // Nothing was recorded, so nothing is deleted. A node that came up anyway carries this
            // run's hostname and is reported by VerifyNothingLeftAsync.
            return Task.FromResult(true);
        }

        return StepAsync($"{node.Role} node {nodeId} removed from the tailnet", async () =>
        {
            try
            {
                var before = await api.GetDeviceAsync(nodeId);
                if (before.Device is null)
                {
                    if (!node.SeenByApi)
                    {
                        // Never read by this client, so the 404 may mean "not visible to it" rather than gone.
                        return (false, $"GET {before.Status}: this OAuth client never saw the device, so its removal cannot be confirmed; {run.ManualRemoval(node)}");
                    }

                    run.MarkRemoved(node);
                    return (true, $"already gone (GET {before.Status})");
                }

                node.SeenByApi = true;
                if (NotThisRuns(before.Device, node, run.StartedAt) is { } reason)
                {
                    return (false, $"NOT deleted (GET {before.Status}): {reason}; look at it on {run.MachinesFilter}");
                }

                var deleted = await api.DeleteDeviceAsync(nodeId);
                var after = await api.GetDeviceAsync(nodeId);
                if (after.Device is not null)
                {
                    return (false, $"GET {before.Status}, DELETE {deleted}, GET {after.Status}: still there; {run.ManualRemoval(node)}");
                }

                run.MarkRemoved(node);
                return (true, $"GET {before.Status}, DELETE {deleted}, GET {after.Status}");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return (false, $"{exception.Message}; {run.ManualRemoval(node)}");
            }
        });
    }

    /// <summary>A used one-off key is revoked by Tailscale already; the status of deleting it is undocumented and recorded.</summary>
    private static Task<bool> DeleteKeyAsync(SmokeRun run, SmokeTailnetApi api, string keyId) =>
        StepAsync($"auth key {keyId} removed", async () =>
        {
            try
            {
                var status = await api.DeleteKeyAsync(keyId);
                run.MarkKeyRemoved(keyId);
                return (true, status == 404 ? "already used up or gone (HTTP 404)" : $"deleted (HTTP {status})");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return (false, $"{exception.Message}; the key expires by itself one day after it was minted");
            }
        });

    /// <summary>
    /// Lists the devices that carry either tag (exact-match filters, so no other device of the
    /// owner is read) to show that none of this run's is left: neither a recorded one nor one
    /// carrying a hostname of this run whose id was never recorded (reported, never deleted).
    /// Other tagged devices are counted and not touched.
    /// </summary>
    private static Task<bool> VerifyNothingLeftAsync(SmokeRun run, SmokeTailnetApi api) =>
        StepAsync("none of this run's nodes remain on the tailnet", async () =>
        {
            var hostTagged = await api.ListDevicesAsync(run.Options.HostTag);
            var clientTagged = await api.ListDevicesAsync(run.Options.ClientTag);
            var devices = hostTagged.Concat(clientTagged).DistinctBy(device => device.NodeId).ToList();
            var recorded = run.Nodes.Select(node => node.NodeId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var left = devices.Where(device => recorded.Contains(device.NodeId)).Select(device => $"{device.NodeId} ({device.Hostname})").ToList();
            var unrecorded = devices
                .Where(device => !recorded.Contains(device.NodeId) && IsNamedForRun(run, device.Hostname))
                .Select(device => $"{device.NodeId} ({device.Hostname})")
                .ToList();
            var others = devices.Count - left.Count - unrecorded.Count;
            var clean = left.Count == 0 && unrecorded.Count == 0;
            return (clean,
                $"{hostTagged.Count} device(s) with {run.Options.HostTag} and {clientTagged.Count} with {run.Options.ClientTag}; " +
                $"recorded nodes left: {(left.Count == 0 ? "none" : string.Join(", ", left))}; " +
                $"unrecorded nodes with this run's hostnames (not deleted): {(unrecorded.Count == 0 ? "none" : string.Join(", ", unrecorded))}; " +
                $"{others} other device(s) carry these tags and were not touched" +
                (clean ? string.Empty : $"; remove what is left by hand on {run.MachinesFilter} (... menu, Remove, Remove machine)"));
        });

    private static Task<bool> DeleteDirectoryAsync(SmokeRun run, string name) =>
        StepAsync($"{name} deleted", async () =>
        {
            var path = run.Work(name);
            for (var attempt = 1; Directory.Exists(path); attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && attempt < 10)
                {
                    // A process that was just stopped can hold a file for a moment.
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }

            return (true, path);
        });

    /// <summary>
    /// Tailscale may add a suffix to a hostname that is taken, so a device whose name starts with
    /// one of this run's hostnames counts as this run's.
    /// </summary>
    private static bool IsNamedForRun(SmokeRun run, string hostname) =>
        run.Nodes.Any(node => node.Hostname is { } name && hostname.StartsWith(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One cleanup step, recorded. Any failure is caught and recorded: one step that fails must not
    /// keep the next from deleting what it can.
    /// </summary>
    private static async Task<bool> StepAsync(string name, Func<Task<(bool Passed, string Detail)>> step)
    {
        try
        {
            var (passed, detail) = await step();
            return Record($"cleanup: {name}", passed, detail);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Record($"cleanup: {name}", false, exception.Message);
        }
    }

    private static async Task<bool> IsGoneAsync(ITransportClient transport)
    {
        try
        {
            await transport.HelloAsync(CancellationToken.None);
            return false;
        }
        catch (TransportException exception) when (exception.Code == TransportErrorCodes.Unavailable)
        {
            return true;
        }
    }
}
