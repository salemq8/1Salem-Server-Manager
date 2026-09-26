using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Diagnostics;
using ServerManager.Connect.Core.Tickets;

namespace TsnetSmoke;

/// <summary>Recording, processes, and the small socket helpers, most of them copied from ConnectProof.</summary>
internal static partial class Program
{
    /// <summary>How long one page step (a monitor check) may take.</summary>
    private static readonly TimeSpan PageStepTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How long the host may take to log its decision on a connection.</summary>
    private static readonly TimeSpan HostDecisionWait = TimeSpan.FromSeconds(20);

    private static readonly string[] PathWords = ["derp", "direct", "magicsock"];

    private static readonly object LogFileGate = new();

    /// <summary>Records a check as <c>PASS|FAIL  name  --  detail</c>; the detail is redacted.</summary>
    private static bool Record(string name, bool passed, string detail)
    {
        var redacted = SecretRedactor.Redact(detail);
        Checks.Add(new Check(name, passed, redacted));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}  --  {redacted}");
        return passed;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Why <paramref name="device"/> is not the node this run created as <paramref name="node"/>, or
    /// null when it is: the hostname this run chose for it, exactly the one tag its key carried, not
    /// ephemeral (the run's keys never are), and created after the run started. The node id alone
    /// already comes only from this run; every other test is a second guard before a deletion.
    /// Tailscale parses the policy's tags as lower case, hence the case-insensitive comparison.
    /// </summary>
    private static string? NotThisRuns(SmokeDevice device, SmokeNode node, DateTimeOffset startedAt)
    {
        var tag = node.Tag;
        if (node.Hostname is not { Length: > 0 } hostname || !string.Equals(device.Hostname, hostname, StringComparison.OrdinalIgnoreCase))
        {
            return $"its hostname is '{device.Hostname}', not '{node.Hostname ?? "(unknown)"}', the one this run chose";
        }

        if (device.Tags.Count != 1 || !string.Equals(device.Tags[0], tag, StringComparison.OrdinalIgnoreCase))
        {
            return $"its tags are [{string.Join(",", device.Tags)}], not exactly [{tag}]";
        }

        // The API leaves isEphemeral out for a non-ephemeral device, even with fields=all (seen in
        // live run 7e66a9), so only an explicit true refuses.
        if (device.IsEphemeral == true)
        {
            return "it is ephemeral";
        }

        if (device.Created is not { } created)
        {
            return "the API reports no creation time";
        }

        return created < startedAt - ClockSkew ? $"it was created {created:u}, before this run started {startedAt:u}" : null;
    }

    /// <summary>
    /// Starts a Go sidecar with no TS_*/TSNET_* variable, in the run's job so it cannot outlive the
    /// driver, and keeps its output, redacted, as evidence.
    /// </summary>
    private static Process StartSidecar(SmokeRun run, string executable, string name, params string[] arguments)
    {
        var workDirectory = run.Options.WorkDirectory;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = workDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        ScrubTailscaleEnvironment(start.Environment);
        var process = Process.Start(start) ?? throw new InvalidOperationException($"{name} transport did not start");
        run.Job.Assign(process);
        var logPath = Path.Combine(workDirectory, $"{name}-transport.log");
        _ = PumpAsync(process.StandardError, logPath);
        _ = PumpAsync(process.StandardOutput, logPath);
        return process;
    }

    /// <summary>
    /// Removes every TS_*/TSNET_* variable, as the friend app's TransportProcess does: tsnet reads
    /// auth keys and a control-server override from them.
    /// </summary>
    private static void ScrubTailscaleEnvironment(IDictionary<string, string?> environment)
    {
        var names = environment.Keys
            .Where(name => name.StartsWith("TS_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("TSNET_", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var name in names)
        {
            environment.Remove(name);
        }
    }

    private static async Task PumpAsync(StreamReader reader, string logPath)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (LogFileGate)
            {
                File.AppendAllText(logPath, SecretRedactor.Redact(line) + Environment.NewLine);
            }
        }
    }

    private static string ForgeServerId(string ticket, Guid serverId)
    {
        var parts = ticket.Split('.');
        var payload = JsonNode.Parse(Base64Url.Decode(parts[1]))!.AsObject();
        payload["sid"] = serverId.ToString("D");
        return $"{parts[0]}.{Base64Url.Encode(Encoding.UTF8.GetBytes(payload.ToJsonString()))}.{parts[2]}";
    }

    private static string Spki(ECDsa key) => Base64Url.Encode(Es256.ExportPublicKey(key));

    private static bool IsTailnetIPv4(string text) =>
        IPAddress.TryParse(text, out var address) &&
        address.AddressFamily == AddressFamily.InterNetwork &&
        address.GetAddressBytes() is [100, var second, _, _] &&
        (second & 0xC0) == 0x40;

    private static bool IsBridgeRunning(PipeAnswer status) =>
        status.Body?["bridge"] is JsonObject bridge &&
        bridge["state"] is JsonValue state && state.TryGetValue<string>(out var text) && text == "running" &&
        bridge["revocationChannel"] is JsonValue channel && channel.TryGetValue<bool>(out var up) && up;

    private static bool IsPathLine(string line) =>
        PathWords.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The app's last Diagnostics entries, to show why a step of the app failed.</summary>
    private static string AppLog(DiagnosticsLog log) =>
        log.Entries.Count == 0 ? string.Empty : "; app log: " + string.Join(" | ", log.Entries.TakeLast(3));

    /// <summary>Whether the host logged refusing a connection from <paramref name="nodeId"/> (as WhoIs named it) for <paramref name="reason"/>.</summary>
    private static Task<bool> HostRefusedAsync(string? nodeId, AccessDenialReason reason) =>
        nodeId is null
            ? Task.FromResult(false)
            : WaitUntilAsync(
                () => Task.FromResult(HostLog.Any(line => line.Contains($"refused a connection from node {nodeId}: {reason}", StringComparison.Ordinal))),
                HostDecisionWait,
                Interrupted);

    /// <summary>The node id WhoIs gave the host for the latest connection it refused for <paramref name="reason"/>.</summary>
    private static string? HostWhoIsNode(AccessDenialReason reason)
    {
        var pattern = new Regex($@"refused a connection from node (\S+): {reason}\.", RegexOptions.CultureInvariant);
        return HostLog.Select(line => pattern.Match(line)).LastOrDefault(match => match.Success)?.Groups[1].Value;
    }

    private static string HostLogFor(string? nodeId)
    {
        var lines = nodeId is null ? [] : HostLog.Where(line => line.Contains($"node {nodeId}", StringComparison.Ordinal)).TakeLast(3).ToList();
        return lines.Count == 0 ? $"nothing about node {nodeId ?? "(unknown)"}" : string.Join(" | ", lines);
    }

    /// <summary>
    /// Ends every wait the pages are in and returns once a page loop waits again (its step is done)
    /// or <paramref name="finished"/> holds (a loop that ended does not wait again).
    /// </summary>
    private static Task<bool> StepPagesAsync(SmokeClock clock, Func<bool> finished, CancellationToken cancellationToken)
    {
        var waitsBefore = clock.Waits;
        clock.Release();
        return WaitUntilAsync(() => Task.FromResult(clock.Waits > waitsBefore || finished()), PageStepTimeout, cancellationToken);
    }

    /// <summary>The run's waits pass <see cref="Interrupted"/>; the cleanup's pass none, so Ctrl+C never cuts them short.</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(100, cancellationToken);
        }

        return true;
    }

    private static int PortOf(string endpoint) =>
        int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);

    /// <summary>The first connection over a new tailnet path can take a while, so allow some attempts.</summary>
    private static async Task<string?> ReadBannerWithRetryAsync(string local)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Interrupted.ThrowIfCancellationRequested();
            if (await ReadBannerOnceAsync(local) is { } banner)
            {
                return banner;
            }

            await Task.Delay(500);
        }

        return null;
    }

    private static async Task<string?> ReadBannerOnceAsync(string local)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, PortOf(local));
            return await ReadLineAsync(client.GetStream(), TimeSpan.FromSeconds(10));
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static async Task<string?> EchoAsync(string local, string text)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, PortOf(local));
        var stream = client.GetStream();
        _ = await ReadLineAsync(stream, TimeSpan.FromSeconds(10));
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\n"));
        return await ReadLineAsync(stream, TimeSpan.FromSeconds(10));
    }

    private static async Task<string?> ReadLineAsync(Stream stream, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        var buffer = new List<byte>();
        var one = new byte[1];
        try
        {
            while (buffer.Count < 256)
            {
                if (await stream.ReadAsync(one, cancel.Token) == 0)
                {
                    return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
                }

                if (one[0] == '\n')
                {
                    return Encoding.UTF8.GetString(buffer.ToArray());
                }

                buffer.Add(one[0]);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<bool> EndsWithinAsync(Stream stream, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        var one = new byte[1];
        try
        {
            while (true)
            {
                if (await stream.ReadAsync(one, cancel.Token) == 0)
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
