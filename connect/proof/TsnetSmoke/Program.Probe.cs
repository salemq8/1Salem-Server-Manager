using System.Diagnostics;
using System.Text.Json;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Tickets;

namespace TsnetSmoke;

/// <summary>What the Go probe (connect/transport/internal/transport/smoke_test.go) writes to its result file.</summary>
internal sealed record ProbeResult(string? NodeId, IReadOnlyList<ProbeCheck>? Checks);

internal sealed record ProbeCheck(string Name, bool Passed, string Detail);

/// <summary>
/// Step 11 and the departed-peer check: the Go probe, a third node on the tailnet that uses the
/// production Tsnet code directly, as an attacker's node would.
/// </summary>
internal static partial class Program
{
    private const string ProbeTest = "TestRealTsnetProbe";
    private const string DepartedTest = "TestRealTsnetDepartedPeer";
    private const string DepartedCheck = "a departed peer is refused before any byte";

    /// <summary>The probe's check that its dial reached the host bridge through the tailnet; later checks mean nothing without it.</summary>
    private const string ProbeBridgeCheck = "probe: netstack dial reaches the host bridge";

    /// <summary>Longer than the probe's own -test.timeout, which is longer than its internal deadline.</summary>
    private static readonly TimeSpan ProbeProcessTimeout = TimeSpan.FromMinutes(6);

    private static readonly JsonSerializerOptions ProbeJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Mints the probe's key with the production provisioner (a friend key, with the client tag) and
    /// builds its two frames with production Core code from a fresh ticket of membership A, which
    /// the broker binds to the FRIEND's node. The host must refuse both, and its log must say why
    /// with the node id WhoIs gave it: the probe's.
    /// </summary>
    private static async Task RunProbeAsync(SmokeRun run, FriendApp app, string membershipA, Guid serverX, int gamePort)
    {
        using var sessionKey = Es256.CreateKey();
        var ticket = (await app.Broker.CreateSessionAsync(membershipA, Spki(sessionKey), CancellationToken.None)).Ticket;
        var framesBuilt = DateTimeOffset.UtcNow;
        var valid = PreambleCodec.Encode(ConnectionPreamble.Create(ticket, sessionKey, TimeProvider.System));
        var forged = PreambleCodec.Encode(ConnectionPreamble.Create(ForgeServerId(ticket, serverX), sessionKey, TimeProvider.System));

        // The key goes into the probe's environment only. .NET strings cannot be wiped; this one is
        // dropped when the method returns, and the key is one-off and deleted in the cleanup.
        var key = await run.Provisioner!.CreateFriendAuthKeyAsync($"smoke-probe-{run.RunId}", Interrupted);
        run.RecordKey(key.KeyId);
        var resultPath = run.Work("probe-result.json");

        // Only this probe's own answer may be read back; the work folder is new, but make it certain.
        File.Delete(resultPath);
        var ports = BlockedPorts.Append(gamePort).ToList();
        var exit = await RunProbeProcessAsync(run, ProbeTest, "probe.log", new Dictionary<string, string>
        {
            ["ONESALEM_SMOKE_PROBE_KEY"] = key.AuthKey,
            ["ONESALEM_SMOKE_STATE_DIR"] = run.Work("probe-state"),
            ["ONESALEM_SMOKE_HOSTNAME"] = run.Probe.Hostname!,
            ["ONESALEM_SMOKE_HOST"] = run.HostIPv4!,
            ["ONESALEM_SMOKE_FRAMES_HEX"] = $"valid:{Convert.ToHexString(valid)},forged:{Convert.ToHexString(forged)}",
            ["ONESALEM_SMOKE_BLOCKED_PORTS"] = string.Join(',', ports),
            ["ONESALEM_SMOKE_RESULT"] = resultPath,
        }, Interrupted);
        var framesAge = DateTimeOffset.UtcNow - framesBuilt;

        var result = ReadProbeResult(resultPath);
        if (result?.NodeId is { } nodeId)
        {
            run.RecordNodeId(run.Probe, nodeId);
        }

        RecoverNodeIdFromMarker(run, run.Probe);
        if (run.Probe.NodeId is not null)
        {
            // Before any probe check is recorded: with Tailnet Lock on they would prove nothing.
            await CheckEnrolledDeviceAsync(run, run.Probe);
        }

        RecordProbe(result, exit, "probe.log", ports);
        var node = run.Probe.NodeId;
        Record(
            "host refused the friend's valid ticket because WhoIs named the probe's node",
            await HostRefusedAsync(node, AccessDenialReason.WrongPeerNode),
            $"host log: {HostLogFor(node)}; the frame was built {framesAge.TotalSeconds:F0} s before the probe exited, so it may be " +
            "outside the +/-60 s proof window, but the host checks the peer's node before that window, so a WrongPeerNode reason still stands");
        Record(
            "host refused the forged ServerId from the probe's node",
            await HostRefusedAsync(node, AccessDenialReason.InvalidSignature),
            $"host log: {HostLogFor(node)}");
    }

    /// <summary>
    /// Runs after the host node was deleted and before the probe node is: the probe node restarts
    /// from its state, without a key, and must be refused before it sends a byte. Part of the
    /// cleanup, so it never throws: whatever goes wrong is recorded and the cleanup goes on.
    /// </summary>
    private static async Task RunDepartedProbeAsync(SmokeRun run)
    {
        if (!run.Host.Removed || run.HostIPv4 is null)
        {
            Record(DepartedCheck, false, "not run: the host node was not confirmed deleted");
            return;
        }

        if (!Checks.Any(check => check.Name == ProbeBridgeCheck && check.Passed))
        {
            Record(DepartedCheck, false, "not run: the probe never reached the host bridge, so a refusal now would prove nothing");
            return;
        }

        try
        {
            var resultPath = run.Work("probe-departed.json");
            File.Delete(resultPath);
            var exit = await RunProbeProcessAsync(run, DepartedTest, "probe-departed.log", new Dictionary<string, string>
            {
                ["ONESALEM_SMOKE_DEPARTED"] = "1",
                ["ONESALEM_SMOKE_STATE_DIR"] = run.Work("probe-state"),
                ["ONESALEM_SMOKE_HOST"] = run.HostIPv4,
                ["ONESALEM_SMOKE_RESULT"] = resultPath,
            }, CancellationToken.None);
            var check = ReadProbeResult(resultPath)?.Checks?.FirstOrDefault();

            // A node that started from a cached network map may need a few attempts (smoke_test.go).
            var cachedMap = ReadLogs(run, ["probe-departed.log"]).Any(line => line.Contains("loaded netmap from disk cache", StringComparison.Ordinal));
            Record(
                DepartedCheck,
                exit == 0 && check is { Passed: true },
                (check?.Detail ?? $"no result (exit code {exit}; see probe-departed.log)") +
                $"; the node started from a cached network map: {(cachedMap ? "yes" : "no")}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Record(DepartedCheck, false, $"the probe could not run: {exception.Message}");
        }
    }

    /// <summary>
    /// Runs one probe test with its inputs in that child's environment only (with every TS_/TSNET_
    /// variable removed), in the run's job, and keeps its output, redacted, as evidence. On Ctrl+C
    /// the probe is stopped and the interruption goes on to the caller.
    /// </summary>
    private static async Task<int> RunProbeProcessAsync(
        SmokeRun run,
        string test,
        string logName,
        Dictionary<string, string> inputs,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(run.Options.Probe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = run.Options.WorkDirectory,
        };
        foreach (var argument in new[] { "-test.run", $"^{test}$", "-test.v", "-test.timeout", "5m" })
        {
            start.ArgumentList.Add(argument);
        }

        ScrubTailscaleEnvironment(start.Environment);
        foreach (var (name, value) in inputs)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("the probe did not start");
        run.Job.Assign(process);

        // Dropped from this process's copy of the environment at once.
        foreach (var name in inputs.Keys)
        {
            start.Environment.Remove(name);
        }

        inputs.Clear();
        var logPath = run.Work(logName);
        var output = Task.WhenAll(PumpAsync(process.StandardOutput, logPath), PumpAsync(process.StandardError, logPath));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ProbeProcessTimeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        await output;
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static ProbeResult? ReadProbeResult(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ProbeResult>(File.ReadAllBytes(path), ProbeJson);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records each probe check, and a FAIL for every check the probe should have reported and did
    /// not (a check skipped silently must not pass by absence). A non-zero exit that no failed
    /// check explains (a panic, a timeout) fails too.
    /// </summary>
    private static void RecordProbe(ProbeResult? result, int exit, string logName, IReadOnlyList<int> ports)
    {
        if (result?.Checks is not { } checks)
        {
            Record("probe ran", false, $"exit code {exit} and no result (see {logName})");
            return;
        }

        foreach (var check in checks)
        {
            Record($"probe: {check.Name}", check.Passed, check.Detail);
        }

        foreach (var missing in ExpectedProbeChecks(ports).Except(checks.Select(check => check.Name), StringComparer.Ordinal))
        {
            Record($"probe: {missing}", false, $"not reported by the probe (see {logName})");
        }

        if (exit != 0 && checks.All(check => check.Passed))
        {
            Record("probe ran to the end", false, $"exit code {exit} although every recorded check passed (see {logName})");
        }
    }

    /// <summary>The check names smoke_test.go reports, in its order.</summary>
    private static IEnumerable<string> ExpectedProbeChecks(IEnumerable<int> ports) =>
    [
        "probe node enrolls",
        "netstack dial reaches the host bridge",
        "host refuses a valid ticket from the wrong node",
        "host refuses a ticket edited to another ServerId",
        .. ports.Select(port => $"the tailnet policy drops TCP {port} to the host"),
        "the host node accepted none of the tested ports",
        "a non-tailnet destination is refused before any dial",
        "an address no peer owns is refused at WhoIs, before any dial",
        "the fallback guard classifies a real tsnet host-network connection",
    ];
}
