using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Pipes;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Infrastructure.Connect;
using TsnetSmoke;

namespace Phase2Acceptance;

internal sealed partial class AcceptanceRun
{
    private sealed record ProbeCheck(string Name, bool Passed, string Detail);
    private sealed record ProbeResult(string? NodeId, IReadOnlyList<ProbeCheck>? Checks);

    private static string ForgeServer(string ticket)
    {
        var parts = ticket.Split('.');
        var payload = JsonNode.Parse(Base64Url.Decode(parts[1]))!.AsObject();
        payload["sid"] = Guid.NewGuid().ToString("D");
        parts[1] = Base64Url.Encode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return string.Join('.', parts);
    }

    private JsonObject OpenRequest(string ticket, ECDsa key, string? destination = null)
    {
        var bytes = key.ExportPkcs8PrivateKey();
        try
        {
            var request = new JsonObject { ["op"] = "open", ["node"] = TransportNodeNames.ForOwner(_ownerId!),
                ["ticket"] = ticket, ["sessionKey"] = Base64Url.Encode(bytes), ["preferredPort"] = 0 };
            if (destination is not null) request["destination"] = destination;
            return request;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task TestPipeRefusalsAsync(string ticket, ECDsa key)
    {
        await using var pipe = await RawPipeClient.ConnectAsync(PipePrefix + ".Friend");
        var forged = await pipe.CallAsync(OpenRequest(ForgeServer(ticket), key), cancellationToken: _stop);
        Require(!forged.Ok && forged.Error == TransportErrorCodes.TicketRejected,
            "forged ServerId refused by friend transport", "production ticket verification");
        var destination = await pipe.CallAsync(OpenRequest(ticket, key, "127.0.0.1:" + _gameA!.Port), cancellationToken: _stop);
        Require(!destination.Ok && destination.Error == "bad_request", "arbitrary destination refused", "strict production pipe request schema");
    }

    private async Task<ProbeResult> RunProbeAsync(string test, string label, Dictionary<string, string> environment)
    {
        var resultPath = Work(label + ".json");
        if (File.Exists(resultPath)) throw new InvalidOperationException("Probe evidence already exists.");
        environment["ONESALEM_SMOKE_RESULT"] = resultPath;
        using var process = StartProcess(_options.Probe, ["-test.run", "^" + test + "$", "-test.v", "-test.timeout", "5m"], environment: environment);
        environment.Clear();
        try { await process.WaitForExitAsync(_stop).WaitAsync(TimeSpan.FromMinutes(6), _stop); }
        finally { if (!process.HasExited) await StopOwnedProcessAsync(process); RecoverMarkers(); SaveLedger(); }
        var result = JsonSerializer.Deserialize<ProbeResult>(await File.ReadAllBytesAsync(resultPath, _stop), Json)
            ?? throw new InvalidDataException("Probe produced no metadata.");
        if (result.Checks is not null)
            foreach (var check in result.Checks) Record(label + ": " + check.Name, check.Passed, "real tsnet probe; metadata in " + label + ".json");
        Require(process.ExitCode == 0 && result.Checks is { Count: > 0 } && result.Checks.All(check => check.Passed),
            label + " completed", "probe exit " + process.ExitCode);
        return result;
    }

    private async Task RunWrongPeerProbeAsync(string ticket, ECDsa key)
    {
        var started = DateTimeOffset.UtcNow;
        var secret = await _api!.CreateKeyAsync([TailscaleApiProvisioner.FriendTag], "Phase2 disposable probe " + RunId,
            id => { _keys.Add(id, false); SaveLedger(); });
        var ports = new[] { 25565, 8211, 8212, 5251, 3389, 445, _options.ApiUrl.Port, _gameA!.Port };
        var result = await RunProbeAsync("TestRealTsnetProbe", "wrong-peer", new()
        {
            ["ONESALEM_SMOKE_PROBE_KEY"] = secret.AuthKey,
            ["ONESALEM_SMOKE_STATE_DIR"] = Work("probe-state"),
            ["ONESALEM_SMOKE_HOSTNAME"] = _probeNode.Hostname,
            ["ONESALEM_SMOKE_HOST"] = _hostIp!,
            ["ONESALEM_SMOKE_FRAMES_HEX"] = "valid:" + Frame(ticket, key) + ",forged:" + Frame(ForgeServer(ticket), key),
            ["ONESALEM_SMOKE_BLOCKED_PORTS"] = string.Join(',', ports)
        });
        _probeNode.NodeId = result.NodeId;
        await VerifyNodeAsync(_probeNode);
        var required = new[] { "probe node enrolls", "netstack dial reaches the host bridge",
            "host refuses a valid ticket from the wrong node", "host refuses a ticket edited to another ServerId",
            "the host node accepted none of the tested ports", "a non-tailnet destination is refused before any dial",
            "an address no peer owns is refused at WhoIs, before any dial",
            "the fallback guard classifies a real tsnet host-network connection" }
            .Concat(ports.Select(port => "the tailnet policy drops TCP " + port + " to the host"));
        Require(required.All(name => result.Checks!.Any(check => check.Name == name && check.Passed)),
            "fallback guard and sensitive ports", "all required real-network probe checks present");
        Require(await DecisionAsync(_probeNode.NodeId!, started, "WrongPeerNode") &&
            await DecisionAsync(_probeNode.NodeId!, started, "InvalidSignature"),
            "real WhoIs / wrong peer", "production Agent logged wrong-peer and invalid-signature refusals for the actual probe node");
    }

    private static string Frame(string ticket, ECDsa key) =>
        Convert.ToHexString(PreambleCodec.Encode(ConnectionPreamble.Create(ticket, key, TimeProvider.System)));

    private async Task RunAuthenticatedFramesAsync(string validTicket, ECDsa validKey, string deniedTicket,
        ECDsa deniedKey, bool replay, string label)
    {
        var started = DateTimeOffset.UtcNow;
        var frame = Frame(validTicket, validKey);
        var frames = new List<object> { new { name = "same-node positive control", frame, allow = true } };
        if (replay) frames.Add(new { name = "replayed preamble refused", frame, allow = false });
        var denialName = label == "expired" ? "naturally expired ticket refused" : "revoked ticket refused";
        frames.Add(new { name = denialName, frame = Frame(deniedTicket, deniedKey), allow = false });
        var result = await RunProbeAsync("TestPhase2AuthenticatedFrames", label, new()
        {
            ["ONESALEM_PHASE2_FRAMES"] = JsonSerializer.Serialize(frames),
            ["ONESALEM_PHASE2_NODE"] = TransportNodeNames.ForOwner(_ownerId!),
            ["ONESALEM_PHASE2_NODE_ID"] = _friendNode!.NodeId!,
            ["ONESALEM_PHASE2_HOST_NODE_ID"] = _hostNode.NodeId!,
            ["ONESALEM_SMOKE_STATE_DIR"] = Work("friend", "transport", "state"),
            ["ONESALEM_SMOKE_HOST"] = _hostIp!
        });
        Require(result.NodeId == _friendNode.NodeId && result.Checks!.Any(c => c.Name == "same-node positive control" && c.Passed) &&
            result.Checks!.Any(c => c.Name == denialName && c.Passed), label + " identity and control", "same real friend identity, accepted control and refused ticket");
        if (replay) Require(await DecisionAsync(_friendNode.NodeId!, started, "ReplayedNonce"), "replay refusal reason", "production Agent decision");
        Require(await DecisionAsync(_friendNode.NodeId!, started, label == "expired" ? ["Expired"] : ["MembershipRevoked", "AuthorizationVersionRevoked"]),
            label + " denial reason", "production Agent decision, not a transport timeout");
    }

    private async Task<bool> DecisionAsync(string node, DateTimeOffset after, params string[] reasons)
    {
        for (var i = 0; i < 20; i++)
        {
            if (_decisions.Any(d => d.NodeId == node && d.ObservedAt >= after && reasons.Contains(d.Reason))) return true;
            await Task.Delay(100, _stop);
        }
        return false;
    }

    private async Task<TcpClient> ConnectAsync(string address)
    {
        if (!IPEndPoint.TryParse(address, out var endpoint) || !IPAddress.IsLoopback(endpoint.Address)) throw new InvalidDataException();
        var client = new TcpClient();
        try { await client.ConnectAsync(endpoint, _stop).AsTask().WaitAsync(TimeSpan.FromSeconds(20), _stop); return client; }
        catch { client.Dispose(); throw; }
    }
    private async Task<string> ReadLineAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var line = new List<byte>();
        var next = new byte[1];
        while (line.Count < 256 && await stream.ReadAsync(next, timeout.Token) == 1)
        {
            if (next[0] == '\n') return Encoding.UTF8.GetString(line.ToArray());
            line.Add(next[0]);
        }
        throw new IOException("Missing bounded test banner.");
    }
    private async Task<bool> ExchangeAsync(string address, string banner)
    {
        using var client = await ConnectAsync(address);
        var stream = client.GetStream();
        if (await ReadLineAsync(stream) != banner) return false;
        var payload = "phase2-echo-" + RunId;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(payload + "\n"), _stop);
        return await ReadLineAsync(stream) == payload;
    }
    private async Task<bool> EndsAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { return await stream.ReadAsync(new byte[1], timeout.Token) == 0; }
        catch (IOException) { return true; }
        catch (OperationCanceledException) { return false; }
    }
}
