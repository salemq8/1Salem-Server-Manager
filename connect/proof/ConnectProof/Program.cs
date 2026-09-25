using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ServerManager.Connect.Core.Broker;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Diagnostics;
using ServerManager.Connect.Core.Enrollment;
using ServerManager.Connect.Core.Identity;
using ServerManager.Connect.Core.Pipes;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Connect;

[assembly: SupportedOSPlatform("windows")]

namespace ConnectProof;

/// <summary>
/// Drives one disposable end-to-end run of 1Salem Connect Phase 1: the local broker's whole
/// lifecycle, the Agent's real host authorization pipe, and the real Go friend and host
/// transports in FAKE network mode (loopback stands in for the tailnet, so this is not a tsnet
/// test). A second friend then goes through the same lifecycle with the friend app's own code
/// (Program.FriendApp.cs). Every check is recorded; the process exits non-zero if any fails.
/// </summary>
internal static partial class Program
{
    private static readonly List<Check> Checks = [];

    public static async Task<int> Main(string[] args)
    {
        var options = ProofOptions.Parse(args);
        Directory.CreateDirectory(options.WorkDirectory);
        var cleanup = new List<IAsyncDisposable>();
        var processes = new List<Process>();
        var exit = 1;
        try
        {
            await RunAsync(options, cleanup, processes);
            exit = Checks.TrueForAll(check => check.Passed) ? 0 : 1;
        }
        catch (Exception exception)
        {
            Record("unexpected failure", false, SecretRedactor.Redact(exception.ToString()));
        }
        finally
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or Win32Exception)
                {
                    Console.WriteLine($"  could not stop pid {process.Id}: {exception.Message}");
                }
            }

            for (var i = cleanup.Count - 1; i >= 0; i--)
            {
                await cleanup[i].DisposeAsync();
            }
        }

        var passed = Checks.Count(check => check.Passed);
        Console.WriteLine();
        Console.WriteLine($"=== {passed}/{Checks.Count} checks passed ===");
        var summary = new JsonObject
        {
            ["passed"] = passed,
            ["total"] = Checks.Count,
            ["ok"] = exit == 0,
            ["mode"] = "fake network (loopback stands in for the tailnet); not a tsnet test",
            ["checks"] = new JsonArray(Checks.Select(check => (JsonNode)new JsonObject
            {
                ["name"] = check.Name,
                ["passed"] = check.Passed,
                ["detail"] = check.Detail,
            }).ToArray()),
        };
        await File.WriteAllTextAsync(options.ResultPath, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return exit;
    }

    private static async Task RunAsync(ProofOptions options, List<IAsyncDisposable> cleanup, List<Process> processes)
    {
        using var http = new HttpClient { BaseAddress = options.Broker, Timeout = TimeSpan.FromSeconds(30) };

        // ---- identities: throwaway owner and device keys in the work directory --------------
        using var owner = new ConnectIdentityStore(Path.Combine(options.WorkDirectory, "owner"), ConnectIdentityKind.Owner).Create();
        using var device = new ConnectIdentityStore(Path.Combine(options.WorkDirectory, "device"), ConnectIdentityKind.Device).Create();
        var asOwner = new BrokerCaller(http, new SignedRequestSigner(owner, TimeProvider.System));
        var asDevice = new BrokerCaller(http, new SignedRequestSigner(device, TimeProvider.System));

        var registeredOwner = await asOwner.SendAsync(HttpMethod.Post, "/v1/owners", new { spki = owner.PublicKeySpkiBase64Url });
        var registeredDevice = await asDevice.SendAsync(HttpMethod.Post, "/v1/devices", new { spki = device.PublicKeySpkiBase64Url });
        Record(
            "owner and device register with self-signed keys",
            registeredOwner.IsSuccess && registeredDevice.IsSuccess &&
            registeredOwner.Text("ownerId") == owner.KeyId && registeredDevice.Text("deviceId") == device.KeyId,
            $"owner {registeredOwner.Status}, device {registeredDevice.Status}; ids derived from the keys");

        // ---- two throwaway game servers and a free bridge port ------------------------------
        var gameA = FakeGameServer.Start("GAME-A");
        var gameB = FakeGameServer.Start("GAME-B");
        cleanup.Add(gameA);
        cleanup.Add(gameB);
        var bridgePort = FreeLoopbackPort();
        var serverA = Guid.NewGuid();
        var serverB = Guid.NewGuid();
        var palworld = Guid.NewGuid();
        var hostBridge = $"127.0.0.1:{bridgePort}";
        foreach (var (id, label) in new[] { (serverA, "Proof server A"), (serverB, "Proof server B") })
        {
            var put = await asOwner.SendAsync(HttpMethod.Put, $"/v1/servers/{id:D}", new { label, protocol = "tcp", hostBridge });
            Require(put.IsSuccess, $"registering {label} returned {put.Status}");
        }

        // ---- invite, redeem, approve ------------------------------------------------------------
        var invite = await asOwner.SendAsync(HttpMethod.Post, "/v1/invites", new { serverId = serverA.ToString("D"), ttlSeconds = 3600 });
        var secret = invite.Text("secret");
        Record("invite issued for one ServerId", invite.IsSuccess && secret is { Length: 43 }, $"status {invite.Status}; 32-byte secret");

        var redeem = await asDevice.SendAsync(HttpMethod.Post, "/v1/invites/redeem", new { secret });
        var membershipId = redeem.Text("membershipId") ?? string.Empty;
        Record("redeeming gives a PENDING membership only", redeem.Status == 201 && redeem.Text("state") == "pending", $"status {redeem.Status}");
        var again = await asDevice.SendAsync(HttpMethod.Post, "/v1/invites/redeem", new { secret });
        Record("an invite works once", again.Status == 404, $"second redemption {again.Status}");

        var early = await asDevice.SendAsync(HttpMethod.Post, "/v1/sessions", new { membershipId, sessionSpki = Base64Url.Encode(Es256.ExportPublicKey(Es256.CreateKey())) });
        Record("no session before the owner approves", !early.IsSuccess, $"status {early.Status}");

        var pending = await asOwner.SendAsync(HttpMethod.Get, "/v1/owners/me/memberships", null);
        var deviceSpki = pending.Items("memberships").FirstOrDefault(item => item.GetProperty("membershipId").GetString() == membershipId)
            .GetProperty("deviceSpki").GetString() ?? string.Empty;
        var approve = await asOwner.SendAsync(HttpMethod.Post, $"/v1/memberships/{membershipId}/approve", new { });
        Record("owner approves the friend", approve.IsSuccess, $"status {approve.Status}");

        // ---- enrollment relay: the broker only ever sees ciphertext ----------------------------
        var fakeAuthKey = "tskey-auth-proof" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var binding = new EnrollmentBinding(membershipId, device.KeyId, owner.KeyId);
        var envelope = EnrollmentCrypto.Encrypt(new EnrollmentSecret(fakeAuthKey, "kProofFake1"), Base64Url.Decode(deviceSpki), binding);
        var stored = await asOwner.SendAsync(HttpMethod.Post, $"/v1/memberships/{membershipId}/enrollment", new { ciphertext = envelope.ToJson() });
        var fetched = await asDevice.SendAsync(HttpMethod.Get, $"/v1/memberships/{membershipId}/enrollment", null);
        var opened = EnrollmentCrypto.Decrypt(EnrollmentEnvelope.Parse(fetched.Text("ciphertext") ?? "{}"), device, binding);
        var refetch = await asDevice.SendAsync(HttpMethod.Get, $"/v1/memberships/{membershipId}/enrollment", null);
        Record(
            "enrollment is end-to-end encrypted and picked up once",
            stored.IsSuccess && opened.AuthKey == fakeAuthKey && refetch.Status == 404 &&
            !(fetched.Raw ?? string.Empty).Contains(fakeAuthKey, StringComparison.Ordinal),
            $"stored {stored.Status}; decrypted by the device only; second pickup {refetch.Status}");

        // ---- the Agent side: the real catalog and host authorization pipe ---------------------
        var keys = await http.GetStringAsync("/v1/keys");
        var keysPath = Path.Combine(options.WorkDirectory, "keys.json");
        await File.WriteAllTextAsync(keysPath, keys);

        var store = new InMemoryServerStore(
            new GameServerDefinition(serverA, GameType.Minecraft, "Proof server A", options.WorkDirectory, gameA.Port, "1.21", DateTimeOffset.UtcNow),
            new GameServerDefinition(serverB, GameType.Minecraft, "Proof server B", options.WorkDirectory, gameB.Port, "1.21", DateTimeOffset.UtcNow),
            new GameServerDefinition(palworld, GameType.Palworld, "Proof Palworld", options.WorkDirectory, FreeLoopbackPort(), "1.0", DateTimeOffset.UtcNow));
        var enabled = new ConnectEnabledServers();
        enabled.Enable(serverA);
        enabled.Enable(serverB);
        enabled.Enable(palworld);
        var catalog = new ConnectServerCatalog(store, enabled);
        await catalog.RefreshAsync(CancellationToken.None);
        var authzPipe = "1Salem.Connect.HostAuthz.proof." + Guid.NewGuid().ToString("N");
        var hostServer = new ConnectHostAuthorizationPipeServer(
            new ConnectHostAuthorizationOptions(owner.KeyId, TicketKeySet.Parse(keys), authzPipe),
            catalog,
            TimeProvider.System,
            new ConsoleLogger<ConnectHostAuthorizationPipeServer>());
        cleanup.Add(hostServer);
        hostServer.Start();
        Record(
            "Palworld and unknown ServerIds are not in the Connect catalog",
            catalog.Find(palworld) is null && catalog.Find(Guid.NewGuid()) is null && catalog.Find(serverA)?.LocalPort == gameA.Port,
            "only Minecraft/TCP servers map to 127.0.0.1:<their port>");

        // ---- the Go transports -----------------------------------------------------------
        var currentUser = ConnectPipeSecurity.CurrentUser.Value;
        processes.Add(StartSidecar(
            options.HostTransport,
            options.WorkDirectory,
            "host",
            "--mode", "fake",
            "--bridge-listen", hostBridge,
            "--authz-pipe", $@"\\.\pipe\{authzPipe}",
            "--expected-authz-owner", currentUser,
            "--pipe", $@"\\.\pipe\1Salem.Connect.Host.Transport.proof.{Guid.NewGuid():N}"));

        var fakeNodeId = "fakeProofNode" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        var friendPipe = "1Salem.Connect.Transport.proof." + Guid.NewGuid().ToString("N");
        processes.Add(StartSidecar(
            options.FriendTransport,
            options.WorkDirectory,
            "friend",
            "--mode", "fake",
            "--keys", keysPath,
            "--fake-node-id", fakeNodeId,
            "--pipe", $@"\\.\pipe\{friendPipe}"));

        await using var transport = await FriendTransport.ConnectAsync(friendPipe);
        var hello = await transport.CallAsync(new JsonObject { ["op"] = "hello", ["v"] = 1 });
        Record("friend transport pipe is owned by this user and answers", hello.Ok, $"mode {hello.Body?["mode"]}");

        var enroll = await transport.CallAsync(new JsonObject
        {
            ["op"] = "enroll", ["node"] = "proof", ["authKey"] = opened.AuthKey, ["hostname"] = "proof-friend",
        });
        var clientSecret = await transport.CallAsync(new JsonObject
        {
            ["op"] = "enroll", ["node"] = "proof2", ["authKey"] = "tskey-client-notreal-" + Guid.NewGuid().ToString("N"), ["hostname"] = "proof-friend2",
        });
        Record(
            "the decrypted auth key is handed to the transport once; OAuth secrets are refused",
            enroll.Ok && !clientSecret.Ok && clientSecret.Error == "bad_auth_key",
            $"enroll {(enroll.Ok ? "ok" : enroll.Error)}; tskey-client- value: {clientSecret.Error}");

        var bind = await asDevice.SendAsync(HttpMethod.Post, $"/v1/memberships/{membershipId}/node", new { nodeId = fakeNodeId });
        Require(bind.IsSuccess, $"binding the node returned {bind.Status}");

        // ---- a session and the positive path -------------------------------------------------
        using var sessionKey = Es256.CreateKey();
        var session = await asDevice.SendAsync(HttpMethod.Post, "/v1/sessions", new { membershipId, sessionSpki = Base64Url.Encode(Es256.ExportPublicKey(sessionKey)) });
        var ticket = session.Text("ticket") ?? string.Empty;
        Record("an approved, enrolled friend gets a signed ticket", session.Status == 201 && ticket.Count(c => c == '.') == 2, $"status {session.Status}");

        var open = await transport.CallAsync(new JsonObject
        {
            ["op"] = "open", ["node"] = "proof", ["ticket"] = ticket,
            ["sessionKey"] = Base64Url.Encode(sessionKey.ExportPkcs8PrivateKey()), ["preferredPort"] = 0,
        });
        var local = open.Body?["local"]?.GetValue<string>() ?? string.Empty;
        Record(
            "the local endpoint is loopback only",
            open.Ok && local.StartsWith("127.0.0.1:", StringComparison.Ordinal),
            $"local address {local}");

        var banner = await ReadBannerWithRetryAsync(local);
        Record("a client on the local endpoint reaches the authorized server", banner == "GAME-A", $"banner '{banner}'");

        var echo = await EchoAsync(local, "hello through connect");
        Record("bytes flow both ways unchanged", echo == "hello through connect", $"echo '{echo}'");

        // ---- attacks --------------------------------------------------------------------
        // A second friend is approved for server B; this friend must not be able to use that membership,
        // and a real foreign id must look exactly like one that does not exist.
        using var otherDevice = new ConnectIdentityStore(Path.Combine(options.WorkDirectory, "device2"), ConnectIdentityKind.Device).Create();
        var asOtherDevice = new BrokerCaller(http, new SignedRequestSigner(otherDevice, TimeProvider.System));
        Require((await asOtherDevice.SendAsync(HttpMethod.Post, "/v1/devices", new { spki = otherDevice.PublicKeySpkiBase64Url })).IsSuccess, "registering the second device failed");
        var inviteB = await asOwner.SendAsync(HttpMethod.Post, "/v1/invites", new { serverId = serverB.ToString("D"), ttlSeconds = 3600 });
        var redeemB = await asOtherDevice.SendAsync(HttpMethod.Post, "/v1/invites/redeem", new { secret = inviteB.Text("secret") });
        var membershipB = redeemB.Text("membershipId") ?? string.Empty;
        Require((await asOwner.SendAsync(HttpMethod.Post, $"/v1/memberships/{membershipB}/approve", new { })).IsSuccess, "approving the second friend failed");
        var sessionSpkiText = Base64Url.Encode(Es256.ExportPublicKey(sessionKey));
        var foreign = await asDevice.SendAsync(HttpMethod.Post, "/v1/sessions", new { membershipId = membershipB, sessionSpki = sessionSpkiText });
        var unknownMembership = "mem_" + new string('a', 26);
        var missing = await asDevice.SendAsync(HttpMethod.Post, "/v1/sessions", new { membershipId = unknownMembership, sessionSpki = sessionSpkiText });
        Record(
            "no ticket for another friend's membership, and it looks like a missing one",
            foreign.Status == 404 && missing.Status == 404 && foreign.Raw == missing.Raw,
            $"foreign {foreign.Status}, unknown {missing.Status}; bodies identical: {foreign.Raw == missing.Raw}");

        var forged = ForgeServerId(ticket, serverB);
        var forgedOpen = await transport.CallAsync(new JsonObject
        {
            ["op"] = "open", ["node"] = "proof", ["ticket"] = forged,
            ["sessionKey"] = Base64Url.Encode(sessionKey.ExportPkcs8PrivateKey()), ["preferredPort"] = 0,
        });
        Record("a ticket edited to another ServerId is rejected by the friend transport", !forgedOpen.Ok, $"error {forgedOpen.Error}");
        Record(
            "a ticket edited to another ServerId is refused by the host bridge",
            await RawBridgeAsync(bridgePort, fakeNodeId, PreambleCodec.Encode(ConnectionPreamble.Create(forged, sessionKey, TimeProvider.System))) == Refused,
            "sent straight to the bridge, bypassing the friend transport");

        var smuggled = await transport.CallAsync(new JsonObject
        {
            ["op"] = "open", ["node"] = "proof", ["ticket"] = ticket,
            ["sessionKey"] = Base64Url.Encode(sessionKey.ExportPkcs8PrivateKey()), ["preferredPort"] = 0,
            ["destination"] = $"127.0.0.1:{gameB.Port}",
        });
        Record("a request carrying a destination is refused outright", !smuggled.Ok && smuggled.Error == "bad_request", $"error {smuggled.Error}");

        var frame = PreambleCodec.Encode(ConnectionPreamble.Create(ticket, sessionKey, TimeProvider.System));
        var first = await RawBridgeAsync(bridgePort, fakeNodeId, frame);
        var replay = await RawBridgeAsync(bridgePort, fakeNodeId, frame);
        Record("a captured connection preamble cannot be replayed", first == Accepted && replay == Refused, $"first {first}, replay {replay}");

        var otherNode = await RawBridgeAsync(bridgePort, "someOtherNode", PreambleCodec.Encode(ConnectionPreamble.Create(ticket, sessionKey, TimeProvider.System)));
        Record("a valid ticket from a different node is refused", otherNode == Refused, $"result {otherNode}");

        using var wrongSessionKey = Es256.CreateKey();
        var wrongProof = await RawBridgeAsync(bridgePort, fakeNodeId, PreambleCodec.Encode(ConnectionPreamble.Create(ticket, wrongSessionKey, TimeProvider.System)));
        Record("a ticket without its session key cannot open a connection", wrongProof == Refused, $"result {wrongProof}");

        var garbage = await RawBridgeAsync(bridgePort, fakeNodeId, Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));
        Record("anything that is not a preamble gets nothing", garbage != Accepted, $"result {garbage}");

        // ---- local port collision: another program's listener is left alone ------------------------
        using var occupier = new TcpListener(IPAddress.Loopback, 18215);
        occupier.Start();
        using var secondKey = Es256.CreateKey();
        var second = await asDevice.SendAsync(HttpMethod.Post, "/v1/sessions", new { membershipId, sessionSpki = Base64Url.Encode(Es256.ExportPublicKey(secondKey)) });
        var secondOpen = await transport.CallAsync(new JsonObject
        {
            ["op"] = "open", ["node"] = "proof", ["ticket"] = second.Text("ticket") ?? string.Empty,
            ["sessionKey"] = Base64Url.Encode(secondKey.ExportPkcs8PrivateKey()), ["preferredPort"] = 18215,
        });
        var secondLocal = secondOpen.Body?["local"]?.GetValue<string>() ?? string.Empty;
        var occupierStillWorks = await ConnectsAsync(18215);
        Record(
            "an occupied port is skipped, never taken over",
            secondOpen.Ok && secondLocal != "127.0.0.1:18215" && secondLocal.StartsWith("127.0.0.1:", StringComparison.Ordinal) && occupierStillWorks,
            $"asked for 18215 (occupied), got {secondLocal}; occupier still accepting: {occupierStillWorks}");
        occupier.Stop();

        // ---- revocation ends live connections and future ones ----------------------------------
        using var live = new TcpClient();
        await live.ConnectAsync(IPAddress.Loopback, PortOf(local));
        var liveStream = live.GetStream();
        var liveBanner = await ReadLineAsync(liveStream, TimeSpan.FromSeconds(10));
        var revoke = await asOwner.SendAsync(HttpMethod.Post, $"/v1/memberships/{membershipId}/revoke", new { });
        var closedIds = await hostServer.RevokeMembershipAsync(membershipId, CancellationToken.None);
        var liveEnded = await EndsWithinAsync(liveStream, TimeSpan.FromSeconds(10));
        Record(
            "revoking a friend ends their live connection",
            liveBanner == "GAME-A" && revoke.IsSuccess && liveEnded,
            $"broker {revoke.Status}; host closed {closedIds.Count} connection(s); live stream ended: {liveEnded}");

        var afterRevoke = await asDevice.SendAsync(HttpMethod.Post, "/v1/sessions", new { membershipId, sessionSpki = Base64Url.Encode(Es256.ExportPublicKey(sessionKey)) });
        var bannerAfter = await ReadBannerOnceAsync(local);
        Record(
            "after revocation: no new ticket, and the old ticket opens nothing",
            !afterRevoke.IsSuccess && bannerAfter is null,
            $"new session {afterRevoke.Status}; old ticket banner: {bannerAfter ?? "none"}");

        // ---- a second friend, driven through the friend app's own code ---------------------------
        await RunFriendAppAsync(new FriendAppRun(options, asOwner, owner.KeyId, hostServer, serverA, "Proof server A", local, secret ?? string.Empty));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private const string Accepted = "accepted";
    private const string Refused = "refused";

    private static void Record(string name, bool passed, string detail)
    {
        Checks.Add(new Check(name, passed, detail));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}  --  {detail}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static int PortOf(string endpoint) => int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Starts a Go sidecar with none of the TS_* variables tsnet would otherwise read.</summary>
    private static Process StartSidecar(string executable, string workDirectory, string name, params string[] arguments)
    {
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

        foreach (var variable in new[] { "TS_AUTHKEY", "TS_AUTH_KEY", "TS_CLIENT_SECRET", "TS_CONTROL_URL", "TSNET_FORCE_LOGIN", "TS_CLIENT_ID", "TS_ID_TOKEN", "TS_AUDIENCE" })
        {
            start.Environment.Remove(variable);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"{name} transport did not start");
        var logPath = Path.Combine(workDirectory, $"{name}-transport.log");
        _ = PumpAsync(process.StandardError, logPath);
        _ = PumpAsync(process.StandardOutput, logPath);
        return process;
    }

    private static async Task PumpAsync(StreamReader reader, string logPath)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (Checks)
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

    /// <summary>Talks to the host bridge directly, as an attacker on the tailnet would.</summary>
    private static async Task<string> RawBridgeAsync(int bridgePort, string claimedNode, byte[] frame)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, bridgePort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"FAKENODE {claimedNode}\n"));
        await stream.WriteAsync(frame);
        var status = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var read = await stream.ReadAsync(status, timeout.Token);
            return read == 0 ? "closed without answer" : status[0] == 0 ? Accepted : status[0] == 1 ? Refused : $"status 0x{status[0]:x2}";
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return "closed without answer";
        }
    }

    /// <summary>The bridge refuses everything until its revocation channel is up, so allow a moment.</summary>
    private static async Task<string?> ReadBannerWithRetryAsync(string local)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
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
            return await ReadLineAsync(client.GetStream(), TimeSpan.FromSeconds(5));
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
        _ = await ReadLineAsync(stream, TimeSpan.FromSeconds(5));
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\n"));
        return await ReadLineAsync(stream, TimeSpan.FromSeconds(5));
    }

    private static async Task<bool> ConnectsAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
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

    private sealed record Check(string Name, bool Passed, string Detail);
}

internal sealed record ProofOptions(Uri Broker, string HostTransport, string FriendTransport, string WorkDirectory, string ResultPath)
{
    public static ProofOptions Parse(string[] args)
    {
        string Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"{name} is required");
        }

        var broker = new Uri(Value("--broker"));
        if (!IPAddress.TryParse(broker.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("The proof only talks to a broker on a loopback address.");
        }

        return new ProofOptions(broker, Value("--host-transport"), Value("--friend-transport"), Value("--work"), Value("--result"));
    }
}

/// <summary>Signed calls to the broker (contract §5) and the parsed answers.</summary>
internal sealed class BrokerCaller(HttpClient http, SignedRequestSigner signer)
{
    public async Task<BrokerAnswer> SendAsync(HttpMethod method, string path, object? body)
    {
        // The signer signs the exact path of an absolute URI, so the request is built absolute.
        using var request = new HttpRequestMessage(method, new Uri(http.BaseAddress!, path));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        await signer.SignAsync(request, CancellationToken.None);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new BrokerAnswer((int)response.StatusCode, text);
    }
}

internal sealed record BrokerAnswer(int Status, string? Raw)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    private JsonElement? Root
    {
        get
        {
            try
            {
                return string.IsNullOrWhiteSpace(Raw) ? null : JsonDocument.Parse(Raw).RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public string? Text(string name) =>
        Root is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public IEnumerable<JsonElement> Items(string name) =>
        Root is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : [];
}

/// <summary>The friend UI's side of the transport pipe, checking the pipe's owner first.</summary>
internal sealed class FriendTransport : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly JsonLineReader _reader;
    private int _nextId;

    private FriendTransport(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new JsonLineReader(pipe);
    }

    public static async Task<FriendTransport> ConnectAsync(string pipeName)
    {
        for (var attempt = 0; ; attempt++)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(2000);
                ConnectPipeSecurity.VerifyServerOwner(pipe, ConnectPipeSecurity.CurrentUser);
                return new FriendTransport(pipe);
            }
            catch (TimeoutException) when (attempt < 15)
            {
                await pipe.DisposeAsync();
            }
        }
    }

    public async Task<PipeAnswer> CallAsync(JsonObject request)
    {
        request["id"] = ++_nextId;
        await JsonLines.WriteAsync(_pipe, Encoding.UTF8.GetBytes(request.ToJsonString()), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reply = await _reader.ReadAsync(timeout.Token) ?? throw new IOException("the transport closed the pipe");
        var body = JsonNode.Parse(reply.GetRawText())?.AsObject();
        var ok = body?["ok"]?.GetValue<bool>() == true;
        return new PipeAnswer(ok, ok ? null : body?["error"]?.GetValue<string>(), body);
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}

internal sealed record PipeAnswer(bool Ok, string? Error, JsonObject? Body);

/// <summary>A throwaway "game server": it says its name, then echoes lines back.</summary>
internal sealed class FakeGameServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    private FakeGameServer(string name)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptAsync(name);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static FakeGameServer Start(string name) => new(name);

    private async Task AcceptAsync(string name)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(name + "\n"), _stop.Token);
                        var buffer = new byte[4096];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                        {
                            await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                    }
                }
            });
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The Agent's server registrations, in memory, for the proof only.</summary>
internal sealed class InMemoryServerStore(params GameServerDefinition[] servers) : IGameServerStore
{
    public Task<IReadOnlyList<GameServerDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GameServerDefinition>>(servers);

    public Task<GameServerDefinition?> GetAsync(Guid serverId, CancellationToken cancellationToken = default) =>
        Task.FromResult(servers.FirstOrDefault(server => server.Id == serverId));

    public Task UpsertAsync(GameServerDefinition server, ServerState state, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The proof's server list is fixed.");

    public Task SetStateAsync(Guid serverId, ServerState state, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>Host-side log lines, redacted, so the proof shows why the host refused something.</summary>
internal sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            Console.WriteLine($"  [host {logLevel}] {SecretRedactor.Redact(formatter(state, exception))}");
        }
    }
}
