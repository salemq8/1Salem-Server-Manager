namespace TsnetSmoke;

/// <summary>
/// What the nodes' own files and logs show about log upload, port mapping, auth-key reuse and
/// Tailnet Lock. It runs in the cleanup, after the transports have stopped and before their
/// directories are deleted, and only for nodes that came up (their ids were recorded).
/// </summary>
internal static partial class Program
{
    // Logged once when a node's backend is created (envknob.LogCurrent), and on every netcheck.
    private const string PortMapperKnobLine = "envknob: TS_DISABLE_PORTMAPPER=\"true\"";
    private const string PortMapProbeLine = "netcheck: probePortMapServices: port mapping is disabled";

    // Expected and harmless: tsnet computes this folder but writes nothing there on Windows.
    private const string LogPolicyLine = "logpolicy: using LocalAppData dir";

    /// <summary>
    /// With the knob set, the port mapper refuses before it does anything it would log, so any
    /// line with its prefix means it did more than refuse. "Ignoring authkey" means a key reached
    /// a node that already had state; the other two appear only when Tailnet Lock is involved.
    /// </summary>
    private static readonly string[] ForbiddenLogText = ["portmapper: ", "Ignoring authkey", "tkaSyncIfNeeded", "this node is locked out"];

    private static readonly string[] NodeLogs = ["host-transport.log", HostDiagLog, FriendDiagLog, "probe.log", "probe-departed.log"];

    private static void CheckNodeEvidence(SmokeRun run)
    {
        var enrolled = run.Nodes.Where(node => node.NodeId is not null).ToList();
        foreach (var node in enrolled)
        {
            Evidence($"logtail kept nothing for upload on disk ({node.Role} node)", () => LogBuffersEmpty(run, node));
            Evidence($"port mapping is disabled on the {node.Role} node", () => PortMappingDisabled(run, node));
        }

        if (enrolled.Count > 0)
        {
            Evidence("no node log shows port mapping, a reused auth key or Tailnet Lock", () => NoForbiddenLines(run));
        }
    }

    /// <summary>
    /// With logtail.Disable() tsnet drops every line before its upload buffers, so both files stay
    /// empty. That is the evidence: no log line says that uploads are off. The probe is a go test
    /// binary, where tsnet starts no logger at all (startLogger returns under testenv.InTest), so
    /// its buffers are never created; the sidecars are the production evidence.
    /// </summary>
    private static (bool, string) LogBuffersEmpty(SmokeRun run, SmokeNode node)
    {
        var files = new[] { "tailscaled.log1.txt", "tailscaled.log2.txt" }
            .Select(name => new FileInfo(Path.Combine(node.StateDirectory!, name)))
            .ToList();
        var underTest = node == run.Probe;
        return (
            files.TrueForAll(file => underTest ? !file.Exists || file.Length == 0 : file.Exists && file.Length == 0),
            string.Join(", ", files.Select(file => file.Exists ? $"{file.Name} {file.Length} bytes" : $"{file.Name} missing")) +
            $" in {node.StateDirectory}");
    }

    private static (bool, string) PortMappingDisabled(SmokeRun run, SmokeNode node)
    {
        var (files, collector) = node == run.Host ? (new[] { HostDiagLog }, run.HostLog)
            : node == run.Friend ? (new[] { FriendDiagLog }, run.FriendLog)
            : (new[] { "probe.log", "probe-departed.log" }, null);
        var lines = ReadLogs(run, files);
        bool Has(string text) => lines.Any(line => line.Contains(text, StringComparison.Ordinal));
        return (
            Has(PortMapperKnobLine) && Has(PortMapProbeLine),
            $"'{PortMapperKnobLine}' {(Has(PortMapperKnobLine) ? "found" : "NOT found")}; " +
            $"'{PortMapProbeLine}' {(Has(PortMapProbeLine) ? "found" : "NOT found")}; " +
            $"'{LogPolicyLine}' {(Has(LogPolicyLine) ? "found (expected; nothing is written there)" : "not found")}; " +
            $"searched {string.Join(", ", files)} ({lines.Count} lines{(collector is null ? string.Empty : $"; {collector}")})");
    }

    private static (bool, string) NoForbiddenLines(SmokeRun run)
    {
        var hits = ReadLogs(run, NodeLogs)
            .Where(line => ForbiddenLogText.Any(text => line.Contains(text, StringComparison.Ordinal)))
            .ToList();
        return (
            hits.Count == 0,
            hits.Count == 0
                ? $"none of [{string.Join(", ", ForbiddenLogText.Select(text => $"'{text}'"))}] in {string.Join(", ", NodeLogs)}"
                : $"{hits.Count} line(s), for example: {string.Join(" | ", hits.Take(3))}");
    }

    private static List<string> ReadLogs(SmokeRun run, IEnumerable<string> files) =>
        files.Select(run.Work).Where(File.Exists).SelectMany(File.ReadLines).ToList();

    /// <summary>One evidence check, recorded; a check that could not be made is a FAIL with the reason.</summary>
    private static void Evidence(string name, Func<(bool Passed, string Detail)> check)
    {
        try
        {
            var (passed, detail) = check();
            Record(name, passed, detail);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Record(name, false, $"not run: {exception.Message}");
        }
    }
}
