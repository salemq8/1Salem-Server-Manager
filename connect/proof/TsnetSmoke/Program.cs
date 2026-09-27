using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Identity;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.App.ViewModels;
using ServerManager.Connect.Core.Broker;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Enrollment;
using ServerManager.Connect.Core.Identity;
using ServerManager.Connect.Core.Pipes;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Connect;

[assembly: SupportedOSPlatform("windows")]

namespace TsnetSmoke;

/// <summary>
/// Drives one run of the real tsnet smoke test (connect/proof/TSNET_SMOKE.md). It runs the same
/// production components as connect/proof/ConnectProof (the local broker, the Agent's host
/// authorization pipe and catalog, the Go host and friend transports, the friend app's own
/// services, <see cref="TailscaleApiProvisioner"/>), but with the transports in tsnet mode on the
/// owner's real tailnet through temporary tagged nodes, which it deletes again. That proves what
/// fake mode cannot: that the host refuses a wrong peer by its real WhoIs identity, and that the
/// guard against tsnet's host-network fallback holds on a real connection. Without --live it only
/// checks its arguments; nothing starts and nothing online is contacted.
/// </summary>
internal static partial class Program
{
    private const string Mode = "tsnet (real tailnet, temporary tagged nodes)";
    private const string SmokeGame = "SMOKE-GAME";
    private const string HostDiagLog = "host-transport-diag.log";
    private const string FriendDiagLog = "friend-transport-diag.log";

    /// <summary>The host bridge port (the host transport's default ":7780"), which the tailnet policy opens to friends.</summary>
    private const int BridgePort = 7780;

    /// <summary>
    /// Host ports the probe must not reach over the tailnet, besides the test service's own:
    /// Minecraft, Palworld's game and REST ports, the Agent's loopback API, RDP and SMB.
    /// </summary>
    private static readonly int[] BlockedPorts = [25565, 8211, 8212, 5251, 3389, 445];

    /// <summary>The host transport waits up to 90 s for a new node; the call allows a little more.</summary>
    private static readonly TimeSpan HostEnrollTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long the friend app may report its enrollment as pending before the run gives up.</summary>
    private static readonly TimeSpan FriendEnrollmentWait = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan BridgeStartWait = TimeSpan.FromSeconds(60);

    private static readonly List<Check> Checks = [];

    /// <summary>The Agent side's redacted log lines, where the host's refusal reasons appear.</summary>
    private static readonly ConcurrentQueue<string> HostLog = new();

    /// <summary>Cancelled by Ctrl+C: the run stops at its next step and goes to the cleanup.</summary>
    private static readonly CancellationTokenSource Interruption = new();

    private static CancellationToken Interrupted => Interruption.Token;

    // Set when the cleanup starts, so a first Ctrl+C then is told the cleanup goes on.
    private static bool _cleaningUp;

    public static async Task<int> Main(string[] args)
    {
        SmokeOptions options;
        try
        {
            options = SmokeOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.WriteLine($"refused: {exception.Message}");
            return 2;
        }

        Console.WriteLine($"host tag: {options.HostTag}");
        Console.WriteLine($"client tag: {options.ClientTag} (TailscaleApiProvisioner.FriendTag, the tag the production provisioner mints for friends)");
        foreach (var warning in options.Warnings)
        {
            Console.WriteLine($"WARNING: {warning}");
        }

        if (!options.Live)
        {
            Console.WriteLine("Arguments accepted. Without --live nothing is started and nothing online is contacted.");
            return 0;
        }

        SmokeJob job;
        try
        {
            job = SmokeJob.Create();
        }
        catch (Win32Exception exception)
        {
            Console.WriteLine($"refused: Windows would not create the job that stops the driver's processes with it: {exception.Message}");
            return 2;
        }

        // The cleanup deletes folders in the work directory and reads the probe's result files from
        // it, so it must be this run's alone: a reused one could hand it another run's leftovers.
        if (Directory.Exists(options.WorkDirectory) && Directory.EnumerateFileSystemEntries(options.WorkDirectory).Any())
        {
            job.Dispose();
            Console.WriteLine($"refused: --work {options.WorkDirectory} already holds files; the driver takes only a new or empty folder.");
            return 2;
        }

        // Disposed last: closing the job ends any process of the run that is somehow still running.
        using (job)
        {
            Directory.CreateDirectory(options.WorkDirectory);
            var run = new SmokeRun(options, job);
            Console.CancelKeyPress += OnCancelKeyPress;
            using var stopWatch = new CancellationTokenSource();
            var stopRequest = WatchForStopRequestAsync(run.Work(StopRequestName), stopWatch.Token);
            Console.WriteLine($"run {run.RunId} started {run.StartedAt:u}; its cleanup deletes every node it enrolls, and {run.Work("nodes.json")} lists them meanwhile");
            try
            {
                await RunAsync(run);
            }
            catch (OperationCanceledException) when (Interrupted.IsCancellationRequested)
            {
                Record("run completed", false, "interrupted (Ctrl+C or the runner's stop request); the cleanup runs next");
            }
            catch (Exception exception)
            {
                Record("unexpected failure", false, exception.ToString());
            }
            finally
            {
                // The cleanup records every step itself and does not stop at a failed one; this
                // only catches what would otherwise skip the result file.
                Volatile.Write(ref _cleaningUp, true);
                try
                {
                    await CleanUpAsync(run);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Record("cleanup complete", false, $"the cleanup stopped unexpectedly: {exception.Message}; {run.Work("nodes.json")} says what may be left");
                }

                await WriteResultAsync(run);
                await stopWatch.CancelAsync();
                await stopRequest;
            }

            return Checks.Count > 0 && Checks.TrueForAll(check => check.Passed) ? 0 : 1;
        }
    }

    /// <summary>The file the runner creates when its time budget runs out, asking the driver to stop and clean up.</summary>
    private const string StopRequestName = "stop.request";

    /// <summary>
    /// A runner that gives up on a driver must not just kill it: that would leave the nodes on the
    /// tailnet. It creates the stop file instead, and the driver treats it exactly like Ctrl+C.
    /// </summary>
    private static async Task WatchForStopRequestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (File.Exists(path) && !Interrupted.IsCancellationRequested && !Volatile.Read(ref _cleaningUp))
                {
                    Console.WriteLine("stop requested by the runner: the run stops at its next step, then the cleanup deletes what it created");
                    await Interruption.CancelAsync();
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Ctrl+C or Ctrl+Break would end the driver at once, skip the cleanup and leave live nodes on
    /// the owner's tailnet. The first press stops the run at its next step, and the cleanup then
    /// runs as usual. Later presses are refused as well: a cleanup cut short is exactly what leaves
    /// nodes behind, and every cleanup step has its own time limit. Closing the window cannot be
    /// refused (Windows ends the program within seconds; logoff and shutdown events reach services
    /// only), so nodes.json then says what to remove by hand.
    /// </summary>
    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (Interrupted.IsCancellationRequested || Volatile.Read(ref _cleaningUp))
        {
            Console.WriteLine("  the cleanup is still running and ends by itself; closing the window now would leave the nodes in nodes.json on the tailnet");
            return;
        }

        Console.WriteLine("interrupted: the run stops at its next step, then the cleanup deletes what it created (this can take a few minutes)");

        // Not Cancel(): that would run the run's continuations here, on the handler's thread.
        _ = Interruption.CancelAsync();
    }

    private static async Task RunAsync(SmokeRun run)
    {
        var options = run.Options;

        // ---- 1. the owner's OAuth client: the production provisioner, and the test-only calls -----
        var credential = SmokeCredential.Load(options.CredentialPath);
        run.Provisioner = new TailscaleApiProvisioner(
            run.TailnetHttp,
            credential.Production,
            TimeProvider.System,
            new ConsoleLogger<TailscaleApiProvisioner>("tailnet"));
        run.Api = new SmokeTailnetApi(run.TailnetHttp, credential);

        // ---- 2. the local broker: an owner identity, and the friend app's own device identity ------
        var brokerHttp = run.Own(new HttpClient { BaseAddress = options.Broker, Timeout = TimeSpan.FromSeconds(30) });
        var owner = run.Own(new ConnectIdentityStore(run.Work("owner"), ConnectIdentityKind.Owner).Create());
        var asOwner = new BrokerCaller(brokerHttp, new SignedRequestSigner(owner, TimeProvider.System));
        var registered = await asOwner.SendAsync(HttpMethod.Post, "/v1/owners", new { spki = owner.PublicKeySpkiBase64Url });
        Require(registered.IsSuccess, $"registering the owner returned {registered.Status}");
        var friendRoot = run.Work("friend-app");
        var identity = run.Own(new DeviceIdentity(
            new ConnectIdentityStore(Path.Combine(friendRoot, "identity"), ConnectIdentityKind.Device).LoadOrCreate(),
            TimeProvider.System));

        // The app names its node after its device and keeps it in <data>\transport\state\nodes\<owner id>
        // (TransportProcess's "state" folder, the transport's "nodes" folder).
        run.Name(
            run.Friend,
            TransportNodeNames.Hostname(identity.DeviceId),
            Path.Combine(friendRoot, "transport", "state", "nodes", TransportNodeNames.ForOwner(owner.KeyId)));

        // ---- 3. the test service and the Agent side: a catalog with server A only ------------------
        var game = run.OwnAsync(FakeGameServer.Start(SmokeGame));
        var serverA = Guid.NewGuid();
        var serverX = Guid.NewGuid();
        var enabled = new ConnectEnabledServers();
        enabled.Enable(serverA);
        var catalog = new ConnectServerCatalog(
            new InMemoryServerStore(new GameServerDefinition(
                serverA, GameType.Minecraft, "Smoke server A", options.WorkDirectory, game.Port, "1.21", DateTimeOffset.UtcNow)),
            enabled);
        await catalog.RefreshAsync(CancellationToken.None);
        var keys = await brokerHttp.GetStringAsync("/v1/keys");
        var authzPipe = "1Salem.Connect.HostAuthz.smoke." + Guid.NewGuid().ToString("N");
        var hostServer = run.OwnAsync(new ConnectHostAuthorizationPipeServer(
            new ConnectHostAuthorizationOptions(owner.KeyId, TicketKeySet.Parse(keys), authzPipe),
            catalog,
            TimeProvider.System,
            new ConsoleLogger<ConnectHostAuthorizationPipeServer>("host", HostLog)));
        hostServer.Start();

        // ---- 4. the host node ----------------------------------------------------------------
        Interrupted.ThrowIfCancellationRequested();
        var hostBridge = $"{await StartHostAsync(run, authzPipe)}:{BridgePort}";

        // ---- 5. the friend app, both servers at the broker, and the friend's two memberships -------
        Interrupted.ThrowIfCancellationRequested();
        var app = await StartFriendAppAsync(run, identity, friendRoot);
        foreach (var (id, label) in new[] { (serverA, "Smoke server A"), (serverX, "Smoke server X") })
        {
            var put = await asOwner.SendAsync(HttpMethod.Put, $"/v1/servers/{id:D}", new { label, protocol = "tcp", hostBridge });
            Require(put.IsSuccess, $"registering {label} with bridge {hostBridge} returned {put.Status}");
        }

        await app.Broker.RegisterDeviceAsync(CancellationToken.None);
        var membershipA = await JoinAsync(asOwner, app.Broker, serverA);
        var membershipX = await JoinAsync(asOwner, app.Broker, serverX);

        // ---- 6. the friend's key, minted and sealed by production code ----------------------------
        var sealedKey = await SealFriendKeyAsync(run, asOwner, membershipA, identity.DeviceId, owner.KeyId);
        var stored = await asOwner.SendAsync(HttpMethod.Post, $"/v1/memberships/{membershipA}/enrollment", new { ciphertext = sealedKey });
        Require(stored.IsSuccess, $"storing the enrollment returned {stored.Status}");

        // ---- 7. the app enrolls its node from the blob, then binds the same node for X, keyless ----
        Interrupted.ThrowIfCancellationRequested();
        var friend = await EnrollFriendNodeAsync(run, app, asOwner, membershipA, owner.KeyId);
        var boundX = await EnrollFriendAsync(app, membershipX);
        Require(
            boundX.Outcome == EnrollmentOutcome.Completed && boundX.NodeId == friend.Enrolled.NodeId,
            $"binding the friend node for server X ended {boundX.Outcome}{AppLog(app.Log)}");
        await ConfirmCandidateAsync(asOwner, membershipX, friend.Enrolled.NodeId!, "server X");

        // ---- 8. Connect through the app: a loopback address that reaches the test service ---------
        Interrupted.ThrowIfCancellationRequested();
        var membership = (await app.Broker.GetMembershipsAsync(CancellationToken.None)).Single(item => item.MembershipId == membershipA);
        app.Main.ShowConnection(membership);
        var connection = app.Main.CurrentPage as ConnectionViewModel ?? throw new InvalidOperationException("the connection page did not open");
        await connection.ConnectAsync();
        var local = connection.LocalAddress ?? string.Empty;
        Record(
            "friend local listener is loopback",
            connection.State == ConnectionState.Connected &&
            IPEndPoint.TryParse(local, out var endpoint) && endpoint.Address.Equals(IPAddress.Loopback),
            $"'{connection.StateText}' at {local}{(connection.Message is { } message ? $" ({message})" : string.Empty)}{AppLog(app.Log)}");
        Require(connection.State == ConnectionState.Connected, "the app did not connect");
        var banner = await ReadBannerWithRetryAsync(local);
        var echo = await EchoAsync(local, "hello over the tailnet");
        var flowed = banner == SmokeGame && echo == "hello over the tailnet";
        Record("authorized traffic reaches the test service", flowed, $"banner '{banner}', echo '{echo}'");
        var pathLines = (await CaptureFriendLogAsync(run, app)).Where(IsPathLine).TakeLast(5).ToList();
        Record(
            "tailnet path established",
            flowed,
            (flowed ? "traffic flowed" : "no traffic flowed") + (pathLines.Count == 0
                ? "; the transport's recent log says nothing about derp, direct or magicsock"
                : "; the transport's log says: " + string.Join(" | ", pathLines)));

        // ---- 9 and 10. a forged ServerId, a ServerId the host does not serve, a destination -------
        Interrupted.ThrowIfCancellationRequested();
        await AttackThroughPipeAsync(run, app, owner.KeyId, membershipA, membershipX, serverX, game.Port);
        await RecordFriendIdentityAsync(app, owner.KeyId, friend);

        // ---- 11. the probe node: real WhoIs, the fallback guard, the policy --------------------
        Interrupted.ThrowIfCancellationRequested();
        await RunProbeAsync(run, app, membershipA, serverX, game.Port);

        // ---- 12. revocation over the real tailnet ---------------------------------------------
        Interrupted.ThrowIfCancellationRequested();
        await RevokeAsync(asOwner, hostServer, app, connection, membershipA, local);
    }

    /// <summary>
    /// Starts the host transport in tsnet mode against this run's authorization pipe, enrolls the
    /// host node and waits for its bridge. Returns the host node's tailnet IPv4 address.
    /// </summary>
    private static async Task<string> StartHostAsync(SmokeRun run, string authzPipe)
    {
        var hostPipe = "1Salem.Connect.Host.Transport.smoke." + Guid.NewGuid().ToString("N");
        run.HostTransport = StartSidecar(
            run,
            run.Options.HostTransport,
            "host",
            "--mode", "tsnet",
            "--state-dir", run.Work("host-state"),
            "--authz-pipe", $@"\\.\pipe\{authzPipe}",
            "--expected-authz-owner", ConnectPipeSecurity.CurrentUser.Value,
            "--pipe", $@"\\.\pipe\{hostPipe}");
        var control = run.HostControl = await RawPipeClient.ConnectAsync(hostPipe);
        var hello = await control.CallAsync(new JsonObject { ["op"] = "hello", ["v"] = 1 });
        Require(hello.Ok && hello.Text("mode") == "tsnet", $"the host transport answered hello with {hello.Error ?? hello.Text("mode")}");

        // A second connection feeds the log collector: the first is busy for as long as enroll runs.
        var diagPipe = run.OwnAsync(await RawPipeClient.ConnectAsync(hostPipe));
        run.HostLog = TransportLogCollector.Start(
            async () => DiagLines(await diagPipe.CallAsync(new JsonObject { ["op"] = "diag" })),
            lines => run.AppendEvidenceAsync(HostDiagLog, lines));

        var enrolled = await EnrollHostNodeAsync(run, control);
        if (enrolled.Text("nodeId") is { } nodeId)
        {
            run.RecordNodeId(run.Host, nodeId);
        }

        Record(
            "host node enrolls",
            enrolled.Ok && run.Host.NodeId is not null,
            enrolled.Ok ? $"node {run.Host.NodeId}" : $"enroll failed: {enrolled.Error} (see {HostDiagLog})");
        Require(run.Host.NodeId is not null, "the host node did not enroll");
        var device = await CheckEnrolledDeviceAsync(run, run.Host);

        var address = device.Addresses.FirstOrDefault(IsTailnetIPv4) ??
            throw new InvalidOperationException("the tailnet reports no 100.64.0.0/10 address for the host node");
        run.HostIPv4 = address;
        var running = await WaitUntilAsync(
            async () => IsBridgeRunning(await control.CallAsync(new JsonObject { ["op"] = "status" })),
            BridgeStartWait,
            Interrupted);
        Require(running, $"the host bridge did not start (see {HostDiagLog})");
        return address;
    }

    /// <summary>
    /// The host's one-off key exists only here: minted with exactly the host tag (the OAuth client
    /// carries only that tag, so this is an exact match), handed to the host transport once, dropped.
    /// </summary>
    private static async Task<PipeAnswer> EnrollHostNodeAsync(SmokeRun run, RawPipeClient control)
    {
        var key = await run.Api!.CreateKeyAsync([run.Options.HostTag], $"1salem smoke host {run.RunId}", run.RecordKey);
        return await control.CallAsync(
            new JsonObject { ["op"] = "enroll", ["authKey"] = key.AuthKey, ["hostname"] = run.Host.Hostname },
            HostEnrollTimeout,
            Interrupted);
    }

    /// <summary>
    /// The friend app's own services, built as ConnectProof's friend-app section builds them but in
    /// tsnet mode: its settings loader (development mode permits the local broker's plain HTTP),
    /// device identity, broker client, pipe client and verifier, and the transport process it starts
    /// itself, with its data in the work directory and a private pipe name, so a real 1Salem
    /// Connect of this user is never touched.
    /// </summary>
    private static async Task<FriendApp> StartFriendAppAsync(SmokeRun run, DeviceIdentity identity, string root)
    {
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, ConnectAppSettingsLoader.FileName);
        await File.WriteAllTextAsync(settingsPath, new JsonObject
        {
            ["brokerUrl"] = run.Options.Broker.AbsoluteUri,
            ["developmentMode"] = true,
            ["transportMode"] = "tsnet",
            ["transportPath"] = run.Options.FriendTransport,
        }.ToJsonString());
        var loaded = ConnectAppSettingsLoader.Load(settingsPath);
        Require(
            loaded.Problem is null && loaded.Settings is { IsConfigured: true, TransportMode: TransportMode.Tsnet },
            $"the app refused the smoke settings: {loaded.Problem}");
        var settings = loaded.Settings;
        var broker = run.Own(new BrokerClient(settings.BrokerUrl!, settings.DevelopmentMode, identity));
        var pipeName = "1Salem.Connect.Transport.smoke." + Guid.NewGuid().ToString("N");
        var server = run.Own(TransportServerVerifier.ForTransport(settings.TransportExecutablePath));
        var transport = run.Own(PipeTransportClient.ForPipe(pipeName, server));
        var clock = new SmokeClock();
        var log = new DiagnosticsLog(clock);
        var process = run.Own(new TransportProcess(settings, broker, transport, Path.Combine(root, "transport"), pipeName, server, log));
        var main = new MainViewModel(new ConnectAppContext
        {
            Settings = settings,
            Services = new ConnectServices(broker, identity, process),
            Transport = transport,
            Clock = clock,
            Log = log,
            Clipboard = new SmokeClipboard(),
        });

        // The used-blob list is kept for this run only (null path), as in a test.
        var enrollment = new EnrollmentCoordinator(broker, transport, process, identity, new ConsumedEnrollments(null, log), log);
        return run.App = new FriendApp(broker, transport, process, clock, log, main, enrollment, pipeName);
    }

    /// <summary>Invite (owner), redeem (the app's broker client), approve (owner).</summary>
    private static async Task<string> JoinAsync(BrokerCaller owner, BrokerClient friend, Guid serverId)
    {
        var invite = await owner.SendAsync(HttpMethod.Post, "/v1/invites", new { serverId = serverId.ToString("D"), ttlSeconds = 3600 });
        var secret = invite.Text("secret") ?? throw new InvalidOperationException($"the invite for {serverId} returned {invite.Status}");
        var redeemed = await friend.RedeemInviteAsync(secret, CancellationToken.None);
        var approve = await owner.SendAsync(HttpMethod.Post, $"/v1/memberships/{redeemed.MembershipId}/approve", new { });
        Require(approve.IsSuccess, $"approving the membership for {serverId} returned {approve.Status}");
        return redeemed.MembershipId;
    }

    /// <summary>
    /// The production path of contract §7: the provisioner mints the friend's one-off key, and it is
    /// sealed at once to the device key the broker lists for the membership. The key exists only here.
    /// </summary>
    private static async Task<string> SealFriendKeyAsync(SmokeRun run, BrokerCaller owner, string membershipId, string deviceId, string ownerId)
    {
        var listed = (await owner.SendAsync(HttpMethod.Get, "/v1/owners/me/memberships", null)).Items("memberships")
            .FirstOrDefault(item => item.GetProperty("membershipId").GetString() == membershipId);
        Require(listed.ValueKind == JsonValueKind.Object, "the owner does not see the friend's membership");
        var key = await run.Provisioner!.CreateFriendAuthKeyAsync(membershipId, Interrupted);
        run.RecordKey(key.KeyId);
        return EnrollmentCrypto.Encrypt(
                key,
                Base64Url.Decode(listed.GetProperty("deviceSpki").GetString() ?? string.Empty),
                new EnrollmentBinding(membershipId, deviceId, ownerId))
            .ToJson();
    }

    /// <summary>
    /// The app enrolls its node for server A. Its transport is started first, so that the log
    /// collector sees the new node from its first line (the one-time port-mapping line among them).
    /// </summary>
    private static async Task<FriendEnrollment> EnrollFriendNodeAsync(
        SmokeRun run,
        FriendApp app,
        BrokerCaller owner,
        string membershipA,
        string ownerId)
    {
        await app.Process.EnsureRunningAsync(Interrupted);

        // The production app only records a failed job assignment and carries on. Here that would
        // let a killed driver leave the friend transport running as a live tailnet node.
        var untied = app.Log.Entries.FirstOrDefault(entry => entry.Contains("could not be tied to this app", StringComparison.Ordinal));
        Record("the friend transport ends with the driver (kill-on-close job)", untied is null, untied ?? "the app placed the transport it started in its job");
        Require(untied is null, "the friend transport could not be tied to the driver's lifetime");

        run.FriendLog = TransportLogCollector.Start(
            async () => (await app.Transport.DiagnosticsAsync(CancellationToken.None)).Log,
            lines => run.AppendEvidenceAsync(FriendDiagLog, lines));

        var enrolled = await EnrollFriendAsync(app, membershipA);
        if ((enrolled.NodeId ?? await TransportNodeIdAsync(app.Transport, ownerId)) is { } nodeId)
        {
            run.RecordNodeId(run.Friend, nodeId);
        }

        var bound = await BoundNodeAsync(owner, membershipA);
        Record(
            "friend node enrolls",
            enrolled.Outcome == EnrollmentOutcome.Completed && bound is not null && bound == enrolled.NodeId,
            $"outcome {enrolled.Outcome}; node {enrolled.NodeId}; bound at the broker: {bound ?? "none"}{AppLog(app.Log)}");
        Require(enrolled.Outcome == EnrollmentOutcome.Completed, "the friend node did not enroll");
        var device = await CheckEnrolledDeviceAsync(run, run.Friend);
        await ConfirmCandidateAsync(owner, membershipA, enrolled.NodeId!, "server A");
        return new FriendEnrollment(enrolled, bound, device);
    }

    /// <summary>
    /// The smoke driver acts as the Agent only after CheckEnrolledDeviceAsync has verified the
    /// real Tailscale device. A candidate cannot receive tickets before this owner-signed step.
    /// </summary>
    private static async Task ConfirmCandidateAsync(
        BrokerCaller owner,
        string membershipId,
        string nodeId,
        string label)
    {
        var confirmation = await owner.SendAsync(
            HttpMethod.Post,
            $"/v1/memberships/{membershipId}/node/confirm",
            new { nodeId });
        var confirmed = confirmation.IsSuccess && confirmation.Text("nodeState") == "confirmed";
        Record(
            $"owner confirms the verified friend node for {label}",
            confirmed,
            $"status {confirmation.Status}; state {confirmation.Text("nodeState") ?? "missing"}; node {nodeId}");
        Require(confirmed, $"confirming the friend node for {label} returned {confirmation.Status}");
    }

    /// <summary>The app's enrollment, retried while it reports "pending" (the blob not visible yet).</summary>
    private static async Task<EnrollmentResult> EnrollFriendAsync(FriendApp app, string membershipId)
    {
        var deadline = DateTime.UtcNow + FriendEnrollmentWait;
        while (true)
        {
            var membership = (await app.Broker.GetMembershipsAsync(Interrupted)).Single(item => item.MembershipId == membershipId);
            var result = await app.Enrollment.TryCompleteAsync(membership, Interrupted);
            if (result.Outcome != EnrollmentOutcome.Pending || DateTime.UtcNow >= deadline)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), Interrupted);
        }
    }

    /// <summary>The owner's node as the friend transport's status reports it.</summary>
    private static async Task<string?> TransportNodeIdAsync(PipeTransportClient transport, string ownerId)
    {
        try
        {
            var status = await transport.StatusAsync(CancellationToken.None);
            return status.Nodes.FirstOrDefault(node => node.Node == TransportNodeNames.ForOwner(ownerId))?.NodeId;
        }
        catch (TransportException)
        {
            return null;
        }
    }

    private static async Task<string?> BoundNodeAsync(BrokerCaller owner, string membershipId)
    {
        var listed = (await owner.SendAsync(HttpMethod.Get, "/v1/owners/me/memberships", null)).Items("memberships")
            .FirstOrDefault(item => item.GetProperty("membershipId").GetString() == membershipId);
        return listed.ValueKind == JsonValueKind.Object && listed.GetProperty("nodeId").ValueKind == JsonValueKind.String
            ? listed.GetProperty("nodeId").GetString()
            : null;
    }

    /// <summary>
    /// Checks a node this run just enrolled: the production provisioner's read (the Agent confirms
    /// a friend's node this way) and a raw read with the fields the provisioner does not parse. The
    /// run only means something with Tailnet Lock off: a locked-out node still reaches Running, so
    /// a non-empty tailnetLockError stops the run here and it goes straight to the cleanup.
    /// </summary>
    private static async Task<SmokeDevice> CheckEnrolledDeviceAsync(SmokeRun run, SmokeNode node)
    {
        var nodeId = node.NodeId ?? throw new InvalidOperationException($"the {node.Role} node has no id");
        var confirmed = await run.Provisioner!.GetDeviceAsync(nodeId, Interrupted);
        var answer = await run.Api!.GetDeviceAsync(nodeId);
        var device = answer.Device;
        node.SeenByApi |= device is not null;
        var problem = confirmed is null ? "the production provisioner finds no such device"
            : device is null ? $"GET device/{nodeId} answered HTTP {answer.Status}"
            : NotThisRuns(device, node, run.StartedAt);
        Record(
            $"{node.Role} node is the {node.Tag} device this run created",
            problem is null,
            problem ?? $"the production provisioner and GET device/{nodeId} (HTTP {answer.Status}) both know node {nodeId}: " +
                $"tags [{string.Join(",", device!.Tags)}]; created {device.Created:u}; ephemeral {device.IsEphemeral}; key expiry disabled {device.KeyExpiryDisabled}");

        var lockError = device is null ? "not checked: the device could not be read"
            : string.IsNullOrEmpty(device.TailnetLockError) ? null
            : device.TailnetLockError;
        Record(
            $"no Tailnet Lock error on the {node.Role} node",
            lockError is null,
            lockError ?? $"tailnetLockError is empty; tailnetLockKey {device!.TailnetLockKey ?? "absent"} (recorded only)");
        Require(lockError is null, $"Tailnet Lock is on, or could not be checked, for the {node.Role} node; every later check would be meaningless");
        return device!;
    }

    /// <summary>
    /// Steps 9 and 10: the app's own transport pipe, used as another program of this user could.
    /// A ticket edited to another ServerId, a properly signed ticket for a ServerId the host does
    /// not serve, and a request that names a destination.
    /// </summary>
    private static async Task AttackThroughPipeAsync(
        SmokeRun run,
        FriendApp app,
        string ownerId,
        string membershipA,
        string membershipX,
        Guid serverX,
        int gamePort)
    {
        await using var raw = await RawPipeClient.ConnectAsync(app.PipeName);
        using var keyA = Es256.CreateKey();
        var ticketA = (await app.Broker.CreateSessionAsync(membershipA, Spki(keyA), CancellationToken.None)).Ticket;
        var forged = await raw.CallAsync(OpenRequest(ownerId, ForgeServerId(ticketA, serverX), keyA));
        Record(
            "friend transport rejects a ticket edited to another ServerId",
            !forged.Ok && forged.Error == TransportErrorCodes.TicketRejected,
            $"error {forged.Error ?? "none: it opened"} (expected {TransportErrorCodes.TicketRejected})");

        using var keyX = Es256.CreateKey();
        var ticketX = (await app.Broker.CreateSessionAsync(membershipX, Spki(keyX), CancellationToken.None)).Ticket;
        var openX = await raw.CallAsync(OpenRequest(ownerId, ticketX, keyX));
        var localX = openX.Text("local");
        var bannerX = localX is null ? null : await ReadBannerOnceAsync(localX);
        var refused = await HostRefusedAsync(run.Friend.NodeId, AccessDenialReason.UnknownServer);
        Record(
            "host refuses a ServerId it does not serve",
            openX.Ok && bannerX is null && refused,
            $"signed ticket for X opened {(openX.Ok ? localX : openX.Error)}; banner {bannerX ?? "none"}; host log: {HostLogFor(run.Friend.NodeId)}");
        if (openX.Text("sessionId") is { } sessionX)
        {
            await raw.CallAsync(new JsonObject { ["op"] = "close", ["sessionId"] = sessionX });
        }

        var smuggled = await raw.CallAsync(OpenRequest(ownerId, ticketA, keyA, $"127.0.0.1:{gamePort}"));
        Record(
            "a request carrying a destination is refused outright",
            !smuggled.Ok && smuggled.Error == "bad_request",
            $"error {smuggled.Error ?? "none: it opened"}");
    }

    /// <summary>
    /// The friend's node id as each party sees it: the app's enrollment, the friend transport's
    /// status (Status.Self.ID), the broker's binding, the tailnet API, and the host's WhoIs, which
    /// the Agent logged when it refused the ticket for X in step 9. All must be one id.
    /// </summary>
    private static async Task RecordFriendIdentityAsync(FriendApp app, string ownerId, FriendEnrollment friend)
    {
        var ids = new (string Source, string? Id)[]
        {
            ("enroll result", friend.Enrolled.NodeId),
            ("friend transport status", await TransportNodeIdAsync(app.Transport, ownerId)),
            ("broker binding", friend.Bound),
            ("tailnet API", friend.Device.NodeId),
            ("host WhoIs in the Agent log", HostWhoIsNode(AccessDenialReason.UnknownServer)),
        };
        Record(
            "the friend's node id is the same everywhere",
            ids.All(item => item.Id is not null && item.Id == ids[0].Id),
            string.Join("; ", ids.Select(item => $"{item.Source}: {item.Id ?? "none"}")));
    }

    /// <summary>Step 12, as ConnectProof: the live stream ends, and the app ends as "Access revoked".</summary>
    private static async Task RevokeAsync(
        BrokerCaller owner,
        ConnectHostAuthorizationPipeServer host,
        FriendApp app,
        ConnectionViewModel connection,
        string membershipId,
        string local)
    {
        // The page renews its ticket from its monitor loop, which the smoke clock holds between
        // steps. After the probe's minutes the ticket may be close to expiry, so the page gets one
        // step first, as it would every few seconds on its own; a revocation test must not fail
        // because the ticket simply ran out.
        await StepPagesAsync(app.Clock, () => connection.State != ConnectionState.Connected, Interrupted);

        using var live = new TcpClient();
        await live.ConnectAsync(IPAddress.Loopback, PortOf(local));
        var stream = live.GetStream();
        var liveBanner = await ReadLineAsync(stream, TimeSpan.FromSeconds(20));
        var revoke = await owner.SendAsync(HttpMethod.Post, $"/v1/memberships/{membershipId}/revoke", new { });
        var closed = await host.RevokeMembershipAsync(membershipId, CancellationToken.None);
        var ended = await EndsWithinAsync(stream, TimeSpan.FromSeconds(15));
        Record(
            "revoking the friend ends their live connection over the tailnet",
            liveBanner == SmokeGame && revoke.IsSuccess && ended,
            $"page '{connection.StateText}' before; banner '{liveBanner}'; broker {revoke.Status}; host closed {closed.Count} connection(s); live stream ended: {ended}");

        // The page's monitor steps only when the run releases its clock. A few steps leave room
        // for the transport's log to show the ended connection the page reacts to.
        for (var step = 0; step < 3 && connection.State != ConnectionState.AccessRevoked; step++)
        {
            await StepPagesAsync(app.Clock, () => connection.State == ConnectionState.AccessRevoked, Interrupted);
        }

        Record(
            "the app then shows Access revoked and closes its address",
            connection.State == ConnectionState.AccessRevoked && connection.LocalAddress is null,
            $"state '{connection.StateText}'; address {connection.LocalAddress ?? "closed"}{AppLog(app.Log)}");
    }

    private static JsonObject OpenRequest(string node, string ticket, ECDsa sessionKey, string? destination = null)
    {
        var pkcs8 = sessionKey.ExportPkcs8PrivateKey();
        var request = new JsonObject
        {
            ["op"] = "open",
            ["node"] = node,
            ["ticket"] = ticket,
            ["sessionKey"] = Base64Url.Encode(pkcs8),
            ["preferredPort"] = 0,
        };
        CryptographicOperations.ZeroMemory(pkcs8);
        if (destination is not null)
        {
            request["destination"] = destination;
        }

        return request;
    }

    private static async Task<IReadOnlyList<string>> CaptureFriendLogAsync(SmokeRun run, FriendApp app)
    {
        var log = (await app.Transport.DiagnosticsAsync(CancellationToken.None)).Log;
        await run.AppendEvidenceAsync(FriendDiagLog, log);
        return log;
    }

    private static IReadOnlyList<string> DiagLines(PipeAnswer diag) =>
        (diag.Body?["log"] as JsonArray ?? []).Select(line => line?.GetValue<string>() ?? string.Empty).ToList();

    private static async Task WriteResultAsync(SmokeRun run)
    {
        var passed = Checks.Count(check => check.Passed);
        Console.WriteLine();
        Console.WriteLine($"=== {passed}/{Checks.Count} checks passed ===");
        var summary = new JsonObject
        {
            ["mode"] = Mode,
            ["runId"] = run.RunId,
            ["startedAt"] = run.StartedAt.ToString("o"),
            ["ok"] = Checks.Count > 0 && passed == Checks.Count,
            ["passed"] = passed,
            ["total"] = Checks.Count,
            ["tags"] = new JsonObject { ["host"] = run.Options.HostTag, ["client"] = run.Options.ClientTag },
            ["nodes"] = new JsonArray(run.Nodes.Select(node => (JsonNode)new JsonObject
            {
                ["role"] = node.Role,
                ["hostname"] = node.Hostname,
                ["nodeId"] = node.NodeId,
                ["removed"] = node.Removed,
            }).ToArray()),
            ["checks"] = new JsonArray(Checks.Select(check => (JsonNode)new JsonObject
            {
                ["name"] = check.Name,
                ["passed"] = check.Passed,
                ["detail"] = check.Detail,
            }).ToArray()),
        };
        await File.WriteAllTextAsync(run.Options.ResultPath, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Check(string Name, bool Passed, string Detail);

    /// <summary>What step 7 learned about the friend's node, for the id comparison after step 9.</summary>
    private sealed record FriendEnrollment(EnrollmentResult Enrolled, string? Bound, SmokeDevice Device);
}
