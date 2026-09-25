using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Identity;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.App.ViewModels;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Enrollment;
using ServerManager.Connect.Core.Identity;
using ServerManager.Infrastructure.Connect;

namespace ConnectProof;

/// <summary>What the friend-app section needs from the first part of the run.</summary>
internal sealed record FriendAppRun(
    ProofOptions Options,
    BrokerCaller Owner,
    string OwnerId,
    ConnectHostAuthorizationPipeServer Host,
    Guid ServerId,
    string ServerLabel,
    string OtherFriendLocal,
    string UsedInviteSecret);

/// <summary>
/// A second friend goes through the whole lifecycle with 1Salem Connect's own code
/// (src/ServerManager.Connect.App): its settings loader, device identity, broker client, page view
/// models, enrollment and session services, pipe client, and the transport process it starts
/// itself, against the same local broker, the real Go friend transport (fake network mode) and the
/// Agent's real host authorization. The driver plays the owner and a game client and stands in for
/// the window: it runs the pages' commands and reads their properties, nothing else.
/// </summary>
internal static partial class Program
{
    /// <summary>How long one page step (a poll, an enrollment, a session check) may take.</summary>
    private static readonly TimeSpan PageStepTimeout = TimeSpan.FromSeconds(45);

    private static async Task RunFriendAppAsync(FriendAppRun run)
    {
        var root = Path.Combine(run.Options.WorkDirectory, "friend-app");
        Directory.CreateDirectory(root);
        var fakeNodeId = "fakeAppNode" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));

        // The app's own settings file and loader. Development mode is what permits plain HTTP to
        // the local broker and the fake transport; a release refuses both.
        var settingsPath = Path.Combine(root, ConnectAppSettingsLoader.FileName);
        await File.WriteAllTextAsync(settingsPath, new JsonObject
        {
            ["brokerUrl"] = run.Options.Broker.AbsoluteUri,
            ["developmentMode"] = true,
            ["transportMode"] = "fake",
            ["fakeNodeId"] = fakeNodeId,
            ["transportPath"] = run.Options.FriendTransport,
        }.ToJsonString());
        var loaded = ConnectAppSettingsLoader.Load(settingsPath);
        Require(loaded.Problem is null && loaded.Settings.IsConfigured, $"the app refused the proof settings: {loaded.Problem}");
        var settings = loaded.Settings;

        // The same identity store the app uses, in the work directory instead of %LOCALAPPDATA%;
        // and a private pipe name, so a real 1Salem Connect of this user is never touched.
        using var identity = new DeviceIdentity(
            new ConnectIdentityStore(Path.Combine(root, "identity"), ConnectIdentityKind.Device).LoadOrCreate(),
            TimeProvider.System);
        using var broker = new BrokerClient(settings.BrokerUrl!, settings.DevelopmentMode, identity);
        var pipeName = "1Salem.Connect.Transport.proof.app." + Guid.NewGuid().ToString("N");
        var server = TransportServerVerifier.ForTransport(settings.TransportExecutablePath);
        using var transport = PipeTransportClient.ForPipe(pipeName, server);
        using var transportProcess = new TransportProcess(settings, broker, transport, Path.Combine(root, "transport"), pipeName, server);
        var clock = new ProofClock();
        var clipboard = new ProofClipboard();
        var main = new MainViewModel(new ConnectAppContext
        {
            Settings = settings,
            Services = new ConnectServices(broker, identity, transportProcess),
            Transport = transport,
            Clock = clock,
            Log = new DiagnosticsLog(clock),
            Clipboard = clipboard,
        });
        try
        {
            await DriveFriendAppAsync(run, main, identity, broker, transport, clock, clipboard, fakeNodeId);
        }
        finally
        {
            // What the app does at exit: stop its page loops and the transport it started.
            main.Shutdown();
        }
    }

    private static async Task DriveFriendAppAsync(
        FriendAppRun run,
        MainViewModel main,
        DeviceIdentity identity,
        BrokerClient broker,
        PipeTransportClient transport,
        ProofClock clock,
        ProofClipboard clipboard,
        string fakeNodeId)
    {
        // ---- start: device registration, Welcome ------------------------------------------------
        await main.InitializeAsync();
        var welcome = main.CurrentPage as WelcomeViewModel;
        Record(
            "app: registers its device key with the broker and opens on Welcome",
            welcome is { CanGetStarted: true, HasError: false },
            $"page {PageName(main.CurrentPage)}; device {identity.DeviceId}");
        Require(welcome is not null, "the app did not open on Welcome");

        // ---- invite: a used one is refused generically; a link's #fragment is redeemed ------------
        var invite = await run.Owner.SendAsync(HttpMethod.Post, "/v1/invites", new { serverId = run.ServerId.ToString("D"), ttlSeconds = 3600 });
        var inviteSecret = invite.Text("secret") ?? string.Empty;
        welcome!.GetStartedCommand.Execute(null);
        var invitePage = main.CurrentPage as InviteViewModel ?? throw new InvalidOperationException("Get started did not open the invite page");
        invitePage.InviteText = $"https://connect.1salem.app/i#{run.UsedInviteSecret}";
        await invitePage.JoinAsync();
        var usedInviteError = invitePage.ErrorText;
        invitePage.InviteText = $"https://connect.1salem.app/i#{inviteSecret}";
        await invitePage.JoinAsync();
        var waiting = main.CurrentPage as WaitingViewModel;
        Record(
            "app: a used invite gets the generic answer; a link's #fragment secret is redeemed and the app waits",
            usedInviteError == Text.InviteNotValid &&
            waiting is { IsWaiting: true } && waiting.ServerLabel == run.ServerLabel && invitePage.InviteText.Length == 0,
            $"used invite: '{usedInviteError}'; new link -> page {PageName(main.CurrentPage)} for '{waiting?.ServerLabel}'");
        Require(waiting is not null, "the app is not waiting for approval");

        // ---- the owner approves and sends the enrollment blob, encrypted to the app's device key ----
        var pending = (await run.Owner.SendAsync(HttpMethod.Get, "/v1/owners/me/memberships", null)).Items("memberships")
            .FirstOrDefault(item => item.GetProperty("deviceId").GetString() == identity.DeviceId);
        Require(pending.ValueKind == JsonValueKind.Object, "the owner does not see the app's request");
        var membershipId = pending.GetProperty("membershipId").GetString() ?? string.Empty;
        Require((await run.Owner.SendAsync(HttpMethod.Post, $"/v1/memberships/{membershipId}/approve", new { })).IsSuccess, "approving the app's device failed");
        var authKey = "tskey-auth-proofapp" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var envelope = EnrollmentCrypto.Encrypt(
            new EnrollmentSecret(authKey, "kProofApp1"),
            Base64Url.Decode(pending.GetProperty("deviceSpki").GetString() ?? string.Empty),
            new EnrollmentBinding(membershipId, identity.DeviceId, run.OwnerId));
        var stored = await run.Owner.SendAsync(HttpMethod.Post, $"/v1/memberships/{membershipId}/enrollment", new { ciphertext = envelope.ToJson() });
        Require(stored.IsSuccess, $"storing the app's enrollment returned {stored.Status}");

        // ---- the waiting page's next poll: approved -> server list -> enrollment -------------------
        var settled = await StepPagesAsync(clock);
        var servers = main.CurrentPage as ServersViewModel;
        var server = servers?.Servers.SingleOrDefault();
        var hello = await transport.HelloAsync(CancellationToken.None);
        var node = (await transport.StatusAsync(CancellationToken.None)).Nodes.SingleOrDefault();
        var secondPickup = await broker.TakeEnrollmentAsync(membershipId, CancellationToken.None);
        var boundNode = (await run.Owner.SendAsync(HttpMethod.Get, "/v1/owners/me/memberships", null)).Items("memberships")
            .FirstOrDefault(item => item.GetProperty("membershipId").GetString() == membershipId)
            .GetProperty("nodeId").GetString();
        Record(
            "app: approval leads to its server list; it started its transport, enrolled it from the one-time blob and reported the node id",
            settled && server is { CanOpen: true } && server.StatusText == Text.StatusReady &&
            hello.Mode == "fake" && node?.Node == run.OwnerId && node.NodeId == fakeNodeId &&
            secondPickup is null && boundNode == fakeNodeId,
            $"page {PageName(main.CurrentPage)}, '{server?.Label}' {server?.StatusText}; transport {hello.Mode} node {node?.NodeId}; " +
            $"blob left on the broker: {secondPickup is not null}; broker node id {boundNode}");
        Require(server is { CanOpen: true }, "the server is not ready to open");

        // ---- Connect: a loopback address with Copy, next to a port that is already taken --------------
        server!.OpenCommand.Execute(null);
        var connection = main.CurrentPage as ConnectionViewModel ?? throw new InvalidOperationException("Open did not show the connection page");
        await connection.ConnectAsync();
        var appLocal = connection.LocalAddress ?? string.Empty;
        connection.CopyAddressCommand.Execute(null);
        var otherStillAccepting = await ConnectsAsync(PortOf(run.OtherFriendLocal));
        Record(
            "app: Connect shows a loopback address with Copy, and a port already in use is skipped, not taken",
            connection.State == ConnectionState.Connected && connection.StateText == Text.StateConnected &&
            IPEndPoint.TryParse(appLocal, out var endpoint) && IPAddress.IsLoopback(endpoint.Address) &&
            appLocal != run.OtherFriendLocal && clipboard.Copied == appLocal && otherStillAccepting,
            $"'{connection.StateText}' at {appLocal} (copied '{clipboard.Copied}'); {run.OtherFriendLocal}, held by the other friend's transport, still accepting: {otherStillAccepting}");
        Require(connection.State == ConnectionState.Connected, "the app did not connect");

        var banner = await ReadBannerWithRetryAsync(appLocal);
        var echo = await EchoAsync(appLocal, "hello through the app");
        Record(
            "app: a game client on the app's address reaches the authorized server, both ways",
            banner == "GAME-A" && echo == "hello through the app",
            $"banner '{banner}', echo '{echo}'");

        // ---- ticket refresh: the page's clock is moved to one minute before the ticket ends ----------
        var before = (await transport.DiagnosticsAsync(CancellationToken.None)).Sessions.Single();
        clock.Offset = DateTimeOffset.FromUnixTimeSeconds(before.ExpiresAt) - TimeSpan.FromMinutes(1) - DateTimeOffset.UtcNow;
        var checkedSession = await StepPagesAsync(clock);
        clock.Offset = TimeSpan.Zero;
        var after = await transport.DiagnosticsAsync(CancellationToken.None);
        var refreshed = after.Log.Any(line => line.Contains($"friend: session {before.SessionId} refreshed", StringComparison.Ordinal));
        var bannerAfterRefresh = await ReadBannerWithRetryAsync(appLocal);
        Record(
            "app: the ticket is renewed before it expires (new ticket and session key accepted by the transport) and new connections use it",
            checkedSession && refreshed && connection.State == ConnectionState.Connected && bannerAfterRefresh == "GAME-A",
            $"transport logged the refresh: {refreshed}; ticket expiry {before.ExpiresAt} -> {after.Sessions.SingleOrDefault()?.ExpiresAt}; " +
            $"state '{connection.StateText}'; banner after '{bannerAfterRefresh}'");

        // ---- Diagnostics: technical detail, nothing secret --------------------------------------------
        main.ShowDiagnostics();
        var diagnostics = main.CurrentPage as DiagnosticsViewModel;
        await WaitUntilAsync(() => Task.FromResult(diagnostics is { ReportText.Length: > 0 }), PageStepTimeout);
        var report = diagnostics?.ReportText ?? string.Empty;
        Record(
            "app: Diagnostics show mode, node id and device, and no auth key, ticket or invite secret",
            report.Contains(fakeNodeId, StringComparison.Ordinal) && report.Contains(identity.DeviceId, StringComparison.Ordinal) &&
            report.Contains("fake", StringComparison.Ordinal) && !report.Contains(authKey, StringComparison.Ordinal) &&
            !report.Contains(inviteSecret, StringComparison.Ordinal) && !report.Contains("eyJ", StringComparison.Ordinal),
            $"{report.Length} characters; node id and device id present; secrets absent");

        // ---- revocation reaches the app ----------------------------------------------------------------
        using var live = new TcpClient();
        await live.ConnectAsync(IPAddress.Loopback, PortOf(appLocal));
        var liveStream = live.GetStream();
        var liveBanner = await ReadLineAsync(liveStream, TimeSpan.FromSeconds(10));
        var revoke = await run.Owner.SendAsync(HttpMethod.Post, $"/v1/memberships/{membershipId}/revoke", new { });
        var closedByHost = await run.Host.RevokeMembershipAsync(membershipId, CancellationToken.None);
        var liveEnded = await EndsWithinAsync(liveStream, TimeSpan.FromSeconds(10));
        var bannerAfterRevoke = await ReadBannerOnceAsync(appLocal);
        var revokedShown = await StepPagesAsync(clock, () => connection.State == ConnectionState.AccessRevoked);
        var addressClosed = await WaitUntilAsync(async () => !await ConnectsAsync(PortOf(appLocal)), TimeSpan.FromSeconds(10));
        Record(
            "app: revoking the friend ends the live game connection; the app then says 'Access revoked' and closes its address",
            liveBanner == "GAME-A" && revoke.IsSuccess && liveEnded && bannerAfterRevoke is null &&
            revokedShown && connection.StateText == Text.StateAccessRevoked && connection.LocalAddress is null && !connection.CanConnect && addressClosed,
            $"broker {revoke.Status}; host closed {closedByHost.Count}; live ended: {liveEnded}; new connection: {bannerAfterRevoke ?? "refused"}; " +
            $"state '{connection.StateText}'; local address closed: {addressClosed}");

        var waitsBefore = clock.Waits;
        main.ShowServersCommand.Execute(null);
        var listed = await WaitUntilAsync(() => Task.FromResult(clock.Waits > waitsBefore), PageStepTimeout);
        var revokedServer = (main.CurrentPage as ServersViewModel)?.Servers.SingleOrDefault();
        Record(
            "app: the server list shows 'Access revoked' and offers no Connect",
            listed && revokedServer is { CanOpen: false } && revokedServer.StatusText == Text.StateAccessRevoked,
            $"'{revokedServer?.Label}' {revokedServer?.StatusText}; open offered: {revokedServer?.CanOpen}");

        // ---- exit ---------------------------------------------------------------------------------
        main.Shutdown();
        var transportGone = await WaitUntilAsync(() => IsGoneAsync(transport), TimeSpan.FromSeconds(10));
        Record("app: closing it stops the transport it started", transportGone, $"pipe {(transportGone ? "gone" : "still answering")}");
    }

    /// <summary>
    /// Ends every wait the pages are in and returns once a page loop waits again (its step is done)
    /// or <paramref name="finished"/> holds (a loop that ended does not wait again).
    /// </summary>
    private static Task<bool> StepPagesAsync(ProofClock clock, Func<bool>? finished = null)
    {
        var waitsBefore = clock.Waits;
        clock.Release();
        return WaitUntilAsync(() => Task.FromResult(clock.Waits > waitsBefore || (finished?.Invoke() ?? false)), PageStepTimeout);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(100);
        }

        return true;
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

    private static string PageName(object? page) => page?.GetType().Name ?? "none";
}

/// <summary>
/// The app's clock for the proof. Time is real (plus <see cref="Offset"/>), but a page's wait
/// between polls lasts until the driver calls <see cref="Release"/>, so each poll happens exactly
/// when the proof has set up what it should see, not after the app's 5-second-plus intervals.
/// </summary>
internal sealed class ProofClock : IAppClock
{
    private readonly object _gate = new();
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waits;

    public TimeSpan Offset { get; set; }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + Offset;

    /// <summary>How many waits the pages have started so far.</summary>
    public int Waits
    {
        get
        {
            lock (_gate)
            {
                return _waits;
            }
        }
    }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        Task released;
        lock (_gate)
        {
            _waits++;
            released = _release.Task;
        }

        return released.WaitAsync(cancellationToken);
    }

    public void Release()
    {
        TaskCompletionSource released;
        lock (_gate)
        {
            released = _release;
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        released.SetResult();
    }
}

/// <summary>Stands in for the Windows clipboard, which needs an STA thread the console driver does not have.</summary>
internal sealed class ProofClipboard : IClipboardService
{
    public string? Copied { get; private set; }

    public bool TrySetText(string text)
    {
        Copied = text;
        return true;
    }
}
