using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.ViewModels;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Connect;
using ServerManager.Infrastructure.Persistence;
using TsnetSmoke;

namespace Phase2Acceptance;

internal sealed partial class AcceptanceRun
{
    private ConnectionViewModel? _connection;
    private string? _hostIp;
    private readonly HashSet<string> _baselineNodes = new(StringComparer.Ordinal);

    public async Task ExecuteAsync()
    {
        var credential = SmokeCredential.Load(_options.Credential);
        _api = new SmokeTailnetApi(_tailnetHttp, credential);
        foreach (var tag in new[] { TailscaleApiProvisioner.HostTag, TailscaleApiProvisioner.FriendTag })
            foreach (var device in await _api.ListDevicesAsync(tag)) _baselineNodes.Add(device.NodeId);
        Require(_baselineNodes.Count == 0, "disposable tailnet baseline", "approved tags have no pre-existing devices");
        await SeedServersAsync();
        await StartAgentAsync();
        await AgentAsync<ConnectStatusResponse>(HttpMethod.Put, "/api/v1/connect/credential",
            new ConnectCredentialRequest(credential.ClientId, credential.ClientSecret));
        var ready = await WaitReadyAsync();
        _ownerId = ready.OwnerId ?? throw new InvalidDataException();
        _hostNode.NodeId = ready.HostNodeId;
        var host = await VerifyNodeAsync(_hostNode);
        _hostIp = host.Addresses.First(address => IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork);
        Require(ready.HostBridge == _hostIp + ":7780", "Agent-hosted host enrollment", "real Agent lifecycle, host tag, bridge TCP 7780, protected DPAPI store");
        var ownerPaths = new ConnectOwnerPaths(AgentRoot);
        var savedCredential = new ConnectOAuthCredentialStore(ownerPaths).TryLoad();
        Require(savedCredential?.ClientId == credential.ClientId, "Agent native credential path", "production CurrentUser DPAPI store reopens under isolated Agent identity");
        savedCredential = null;
        credential = null!;
        Require((await ActionAsync($"/api/v1/servers/{_serverA:D}/connect/enable")).Success,
            "enable disposable Minecraft identity", "Agent owner endpoint");
        Require((await ActionAsync($"/api/v1/servers/{_serverB:D}/connect/enable")).Success,
            "enable second disposable identity", "used only for shared-node protection");
        StartFriend();
        _membershipA = await JoinThroughAppAsync(_serverA);
        var pending = await MembershipAsync(_membershipA);
        Require(pending.State == MembershipState.Pending, "friend pending", "actual friend broker membership is pending");
        Require((await ActionAsync($"/api/v1/connect/memberships/{_membershipA}/approve")).Success,
            "owner approval", "Agent accepted owner approval");
        // Approval and sealed-key delivery are separate production reconciliation stages.
        // Wait for the persisted delivery checkpoint before stopping the owner for the gate test.
        var keyDeadline = DateTimeOffset.UtcNow.AddSeconds(45);
        DateTimeOffset? mintedAt = null;
        while (DateTimeOffset.UtcNow < keyDeadline)
        {
            var state = await new ConnectStateStore(ownerPaths).LoadStateAsync(_stop);
            var member = state.Memberships.SingleOrDefault(member => member.MembershipId == _membershipA);
            if (member?.EnrollmentPostedAt is not null && member.KeyMintedAt is not null)
            { mintedAt = member.KeyMintedAt; break; }
            await Task.Delay(500, _stop);
        }
        Require(mintedAt is not null, "one-time client key delivered", "Agent persisted sealed enrollment delivery before candidate-gate interruption");
        await StopAgentAsync(); // Deterministic candidate gate: the real owner cannot confirm while stopped.
        var enrolled = await EnrollAsync(_membershipA);
        _friendNode!.NodeId = enrolled.NodeId;
        var friend = await VerifyNodeAsync(_friendNode);
        var candidate = await MembershipAsync(_membershipA);
        Require(candidate.ConfirmationPending && !candidate.CanConnect, "friend enrollment is candidate", "real friend EnrollmentCoordinator; not yet owner-confirmed");
        await TicketRefusedAsync(_membershipA, "candidate ticket gating");
        Require(await _broker!.TakeEnrollmentAsync(_membershipA, _stop) is null,
            "one-time enrollment pickup", "no second envelope remains");
        await StartAgentAsync();
        var recovered = await WaitReadyAsync();
        Require(recovered.HostNodeId == _hostNode.NodeId && recovered.OwnerId == _ownerId,
            "host restart recovery", "same DPAPI owner and cached host node; no replacement enrollment");
        var confirmed = await WaitConfirmedAsync(_membershipA);
        ReadAgentJournal();
        Require(_deviceReads.Contains(friend.NodeId) && confirmed.NodeId == friend.NodeId &&
            friend.NodeId != _hostNode.NodeId && friend.Tags.SequenceEqual(new[] { TailscaleApiProvisioner.FriendTag }) &&
            mintedAt is not null && friend.Created >= mintedAt - ConnectNodeVerifier.CreationClockSkew,
            "owner-confirmed node binding", "Agent real Devices API read, exact tag, non-host identity and creation-time gate; broker confirmed only after restart");
        using var earlyKey = Es256.CreateKey();
        var earlyTicket = await _broker.CreateSessionAsync(_membershipA, Spki(earlyKey), _stop);
        Require(!string.IsNullOrWhiteSpace(earlyTicket.Ticket), "ticket issued after confirmation", "real broker session response");

        _membershipB = await JoinThroughAppAsync(_serverB);
        Require((await ActionAsync($"/api/v1/connect/memberships/{_membershipB}/approve")).Success,
            "second membership approval", "same friend device");
        await EnrollAsync(_membershipB);
        var shared = await WaitConfirmedAsync(_membershipB);
        Require(shared.NodeId == friend.NodeId, "shared-node binding", "production friend flow reused its already confirmed owner node");
        using var expiryKey = Es256.CreateKey();
        var expiryTicket = await _broker.CreateSessionAsync(_membershipB, Spki(expiryKey), _stop);

        var clock = new SystemAppClock();
        _connection = new ConnectionViewModel(confirmed, _sessions!, clock, new NoClipboard(), new DiagnosticsLog(clock), new TestNavigator());
        await _connection.ConnectAsync();
        Require(_connection.State == ConnectionState.Connected && _connection.LocalAddress is { } loopback && IPEndPoint.TryParse(loopback, out var local) && IPAddress.IsLoopback(local.Address),
            "friend app loopback listener", "actual ConnectionViewModel and SessionService");
        var address = _connection.LocalAddress!;
        Require(await ExchangeAsync("127.0.0.1:" + _gameA!.Port, _gameA.Banner),
            "disposable target positive control", "owned local Minecraft test identity answers directly");
        Require(await ExchangeAsync(address, _gameA!.Banner), "Minecraft loopback path", "disposable Minecraft identity; real tsnet path; banner and echo both directions (not gameplay)");
        await TestPipeRefusalsAsync(earlyTicket.Ticket, earlyKey);
        await RunWrongPeerProbeAsync(earlyTicket.Ticket, earlyKey);

        using var live = await ConnectAsync(address);
        var liveStream = live.GetStream();
        Require(await ReadLineAsync(liveStream) == _gameA.Banner, "live stream before revocation", "authorized test connection open");
        // Give the later revocation checks their full lifetime after the network probe.
        // An expired ticket is refused before its revocation is considered.
        using var revokedKey = Es256.CreateKey();
        var revokedTicket = await _broker.CreateSessionAsync(_membershipA, Spki(revokedKey), _stop);
        var revoke = await AgentAsync<ConnectRevokeResult>(HttpMethod.Post, $"/api/v1/connect/memberships/{_membershipA}/revoke");
        Require(revoke.Completed && revoke.Broker == ConnectStepOutcome.Done && revoke.ThisPc == ConnectStepOutcome.Done &&
            revoke.TailnetDevice == ConnectStepOutcome.KeptInUse, "Agent revocation and shared-node protection", "broker and local revoke complete; shared friend node retained");
        Require(await EndsAsync(liveStream), "revocation closes live stream", "closed without stopping friend or host");
        await TicketRefusedAsync(_membershipA, "revoked membership cannot obtain ticket");
        var uiDeadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (_connection.State != ConnectionState.AccessRevoked && DateTimeOffset.UtcNow < uiDeadline) await Task.Delay(500, _stop);
        Require(_connection.State == ConnectionState.AccessRevoked && _connection.LocalAddress is null,
            "friend UI revocation state", "production ConnectionViewModel says Access revoked and removed its loopback address");
        _connection.StopMonitoring();

        // Hand off only this disposable friend's cached node to the tagged test probe, so direct
        // preamble/replay checks still use the same real Tailscale identity and Agent bridge.
        _friendProcess!.Stop();
        await Task.Delay(1000, _stop);
        using var replayKey = Es256.CreateKey();
        var replayTicket = await _broker.CreateSessionAsync(_membershipB, Spki(replayKey), _stop);
        await RunAuthenticatedFramesAsync(replayTicket.Ticket, replayKey, revokedTicket.Ticket, revokedKey, replay: true, "before-restart");
        await StopAgentAsync();
        await StartAgentAsync();
        var afterRestart = await WaitReadyAsync();
        Require(afterRestart.HostNodeId == _hostNode.NodeId, "restart preserves host identity", "same real host node");
        using var restartKey = Es256.CreateKey();
        var restartTicket = await _broker.CreateSessionAsync(_membershipB, Spki(restartKey), _stop);
        await RunAuthenticatedFramesAsync(restartTicket.Ticket, restartKey, revokedTicket.Ticket, revokedKey, replay: false, "after-restart");
        var revocations = await new ConnectStateStore(ownerPaths).LoadRevocationsAsync(_stop);
        Require(revocations.Memberships.Contains(_membershipA), "restart recovery", "persisted revocation rejects the real friend's old ticket after Agent restart");

        // A naturally expired, broker-issued ticket, not a synthetic clock or re-signed ticket.
        var expiresAfterSkew = expiryTicket.ExpiresAt.AddSeconds(32);
        while (true)
        {
            var remaining = expiresAfterSkew - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            Console.WriteLine("WAIT natural broker ticket expiry; remaining seconds " + (int)remaining.TotalSeconds);
            await Task.Delay(remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30), _stop);
        }
        using var freshKey = Es256.CreateKey();
        var freshTicket = await _broker.CreateSessionAsync(_membershipB, Spki(freshKey), _stop);
        await RunAuthenticatedFramesAsync(freshTicket.Ticket, freshKey, expiryTicket.Ticket, expiryKey, replay: false, "expired");

        var finalRevoke = await AgentAsync<ConnectRevokeResult>(HttpMethod.Post, $"/api/v1/connect/memberships/{_membershipB}/revoke");
        Require(finalRevoke.Completed && finalRevoke.TailnetDevice is ConnectStepOutcome.Done or ConnectStepOutcome.NotNeeded,
            "last membership safely deletes friend node", "real Agent ownership/tag-checked deletion");
        Require((await _api.GetDeviceAsync(friend.NodeId)).Status == 404,
            "revoked friend device deletion confirmed", "Devices API GET 404");
        _friendNode.Removed = true;
        SaveLedger();
    }

    private async Task SeedServersAsync()
    {
        _gameA = new IdentityEndpoint("PHASE2-A-" + RunId);
        _gameB = new IdentityEndpoint("PHASE2-B-" + RunId);
        _serverA = Guid.NewGuid(); _serverB = Guid.NewGuid();
        var storage = new SqliteStorageOptions(AgentRoot);
        var factory = new SqliteConnectionFactory(storage);
        await new SqliteApplicationDatabase(storage, factory).InitializeAsync(_stop);
        var store = new SqliteGameServerStore(factory);
        foreach (var (id, game) in new[] { (_serverA, _gameA), (_serverB, _gameB) })
        {
            var root = Path.Combine(AgentRoot, "test-servers", id.ToString("N"));
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "server.properties"), "server-ip=127.0.0.1\nprevent-proxy-connections=false\n", _stop);
            await store.UpsertAsync(new GameServerDefinition(id, GameType.Minecraft, "Phase2 disposable " + id.ToString("N")[..8],
                root, game.Port, "acceptance-identity", StartedAt), ServerState.Stopped, _stop);
        }
    }

    private async Task<string> JoinThroughAppAsync(Guid serverId)
    {
        var invite = await AgentAsync<ConnectInviteCreated>(HttpMethod.Post, $"/api/v1/servers/{serverId:D}/connect/invites", new ConnectInviteRequest(3600));
        var navigation = new TestNavigator();
        var model = new InviteViewModel(_broker!, new DeviceRegistration(_broker!), new DiagnosticsLog(new SystemAppClock()), navigation, () => { }) { InviteText = invite.Link };
        await model.JoinAsync();
        Require(navigation.Redemption is not null && !model.HasError && model.InviteText.Length == 0,
            "invite flow", "production InviteViewModel redeemed and cleared the one-time secret");
        var replayRefused = false;
        try { await _broker!.RedeemInviteAsync(invite.Code, _stop); }
        catch (BrokerException exception) when (exception.Failure is BrokerFailure.NotFound or BrokerFailure.Rejected) { replayRefused = true; }
        Require(replayRefused, "invite is one-time", "second redemption refused");
        // GetServer is the owner's reconciled local view, not an immediate broker fetch.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ownerView = await AgentAsync<ServerConnectResponse>(HttpMethod.Get, $"/api/v1/servers/{serverId:D}/connect");
            if (ownerView.Friends.Any(friend => friend.MembershipId == navigation.Redemption!.MembershipId && friend.State == ConnectFriendState.Pending))
            {
                Record("owner sees pending friend", true, "production Agent reconciliation populated the owner view");
                return navigation.Redemption!.MembershipId;
            }
            await Task.Delay(500, _stop);
        }
        Require(false, "owner sees pending friend", "not present after 45 seconds of production reconciliation");
        return navigation.Redemption!.MembershipId;
    }

    private async Task<Membership> MembershipAsync(string id) => (await _broker!.GetMembershipsAsync(_stop)).Single(member => member.MembershipId == id);
    private async Task<EnrollmentResult> EnrollAsync(string id)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await _enrollment!.TryCompleteAsync(await MembershipAsync(id), _stop);
            RecoverMarkers(); SaveLedger();
            if (result.Outcome == EnrollmentOutcome.Completed) return result;
            if (result.Outcome == EnrollmentOutcome.Failed) throw new InvalidOperationException("Friend enrollment refused.");
            await Task.Delay(1000, _stop);
        }
        throw new TimeoutException("Friend enrollment timeout.");
    }
    private async Task<Membership> WaitConfirmedAsync(string id)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var member = await MembershipAsync(id);
            if (member.CanConnect) return member;
            await Task.Delay(1000, _stop);
        }
        throw new TimeoutException("Owner confirmation timeout.");
    }
    private async Task TicketRefusedAsync(string membership, string check)
    {
        using var key = Es256.CreateKey();
        var refused = false;
        try { await _broker!.CreateSessionAsync(membership, Spki(key), _stop); }
        catch (BrokerException exception) when (exception.Failure is BrokerFailure.NotFound or BrokerFailure.Rejected or BrokerFailure.Conflict) { refused = true; }
        Require(refused, check, "real broker refused session issuance");
    }
    private static string Spki(ECDsa key) => Base64Url.Encode(key.ExportSubjectPublicKeyInfo());
}
