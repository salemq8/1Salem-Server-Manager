using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Enrollment;
using ServerManager.Connect.Core.Identity;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConnectHostWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "1salem-connect-host-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StartWithoutCredential_RemainsOffAndCreatesNoRuntime()
    {
        var factory = new RefusingRuntimeFactory();
        await using var host = CreateHost(new MemoryServerStore(), factory);

        await host.StartAsync(CancellationToken.None);

        Assert.Equal(ConnectSetupState.NotSetUp, host.Status.State);
        Assert.False(host.Status.CredentialStored);
        Assert.Equal(0, factory.CreateCalls);
        Assert.False(Directory.Exists(Path.Combine(_root, "connect")));
    }

    [Fact]
    public async Task ConfiguredStart_ValidatesNodeAndBridgeBeforeReportingReady()
    {
        using var keys = new ConnectTestBroker();
        var factory = new ReadyRuntimeFactory(keys.KeySet);
        var paths = new ConnectOwnerPaths(_root);
        new ConnectOAuthCredentialStore(paths).Save(
            new TailscaleOAuthCredential("client-test", "tskey-client-test-secret"));
        await using var host = CreateHost(new MemoryServerStore(), factory);

        await host.StartAsync(CancellationToken.None);

        Assert.Equal(ConnectSetupState.Ready, host.Status.State);
        Assert.True(host.Status.BridgeRunning);
        Assert.Equal("100.64.1.2:7780", host.Status.HostBridge);
        Assert.True(factory.Authorization.Started);
        Assert.True(factory.Supervisor.Started);
        Assert.StartsWith("1salem-", factory.Control.Hostname, StringComparison.Ordinal);
        Assert.InRange(factory.Control.Hostname!.Length, 1, 63);
        Assert.True(File.Exists(paths.TicketKeysFile));
    }

    [Fact]
    public async Task Stop_WhenInFlightReconciliationFailsAfterCancellation_StillDisposesRuntime()
    {
        using var keys = new ConnectTestBroker();
        var reconciliationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ReadyRuntimeFactory(keys.KeySet, async cancellationToken =>
        {
            var pendingRead = new TaskCompletionSource<IReadOnlyList<ConnectBrokerMembership>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => pendingRead.TrySetException(
                new ConnectOwnerBrokerException(
                    ConnectOwnerBrokerFailure.Unavailable, null, "test broker failure during shutdown")));
            reconciliationStarted.TrySetResult();
            return await pendingRead.Task;
        });
        new ConnectOAuthCredentialStore(new ConnectOwnerPaths(_root)).Save(
            new TailscaleOAuthCredential("client-test", "tskey-client-test-secret"));
        await using var host = CreateHost(new MemoryServerStore(), factory);
        await host.StartAsync(CancellationToken.None);
        await reconciliationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(factory.Supervisor.Started);
        Assert.True(factory.Authorization.Disposed);
    }

    [Fact]
    public async Task Eligibility_FailsClosedForProxyAndNonLoopbackBinding()
    {
        var serverRoot = Path.Combine(_root, "minecraft");
        Directory.CreateDirectory(serverRoot);
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "server.properties"),
            "prevent-proxy-connections=true\nserver-ip=192.168.1.10\n");
        var server = Definition(Guid.NewGuid(), GameType.Minecraft, serverRoot, 25565);
        var store = new MemoryServerStore(server);
        await using var host = CreateHost(store, new RefusingRuntimeFactory());
        var workflow = new ConnectOwnerWorkflow(host, TimeProvider.System);

        var result = await workflow.GetServerAsync(server.Id, CancellationToken.None);

        Assert.False(result.AccountReady);
        Assert.False(result.Eligible);
        Assert.Contains(ConnectEligibilityIssue.PreventProxyConnections, result.Issues);
        Assert.Contains(ConnectEligibilityIssue.NonLoopbackServerIp, result.Issues);
    }

    [Fact]
    public async Task Eligibility_DetectsAgentSharedAndUnsupportedPorts()
    {
        var rootA = Path.Combine(_root, "a");
        var rootB = Path.Combine(_root, "b");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        await File.WriteAllTextAsync(Path.Combine(rootA, "server.properties"), "server-ip=127.0.0.1\n");
        var minecraft = Definition(Guid.NewGuid(), GameType.Minecraft, rootA, 5251);
        var palworld = Definition(Guid.NewGuid(), GameType.Palworld, rootB, 5251);
        var store = new MemoryServerStore(minecraft, palworld);
        await using var host = CreateHost(store, new RefusingRuntimeFactory());
        var workflow = new ConnectOwnerWorkflow(host, TimeProvider.System);

        var minecraftResult = await workflow.GetServerAsync(minecraft.Id, CancellationToken.None);
        var palworldResult = await workflow.GetServerAsync(palworld.Id, CancellationToken.None);

        Assert.Contains(ConnectEligibilityIssue.AgentPort, minecraftResult.Issues);
        Assert.Contains(ConnectEligibilityIssue.SharedPort, minecraftResult.Issues);
        Assert.Contains(ConnectEligibilityIssue.NotMinecraft, palworldResult.Issues);
    }

    [Fact]
    public async Task Disable_ClearsPersistedChoiceEvenWhenEnforcementRuntimeIsOff()
    {
        var server = Definition(Guid.NewGuid(), GameType.Minecraft, Path.Combine(_root, "minecraft"), 25565);
        var store = new MemoryServerStore(server);
        await using var host = CreateHost(store, new RefusingRuntimeFactory());
        host.SetState(new ConnectOwnerState { EnabledServers = [server.Id] });
        var workflow = new ConnectOwnerWorkflow(host, TimeProvider.System);

        Assert.True((await workflow.GetServerAsync(server.Id, CancellationToken.None)).Enabled);
        await workflow.DisableAsync(server.Id, CancellationToken.None);

        Assert.False((await workflow.GetServerAsync(server.Id, CancellationToken.None)).Enabled);
        Assert.Empty(host.State.EnabledServers);
    }

    [Fact]
    public async Task EnableAndCreateInvite_PersistMetadataButNeverTheSecret()
    {
        using var keys = new ConnectTestBroker();
        var factory = new ReadyRuntimeFactory(keys.KeySet);
        var server = Definition(Guid.NewGuid(), GameType.Minecraft, Path.Combine(_root, "minecraft"), 25565);
        new ConnectOAuthCredentialStore(new ConnectOwnerPaths(_root)).Save(
            new TailscaleOAuthCredential("client-test", "tskey-client-test-secret"));
        await using var host = CreateHost(new MemoryServerStore(server), factory);
        await host.StartAsync(CancellationToken.None);
        var workflow = new ConnectOwnerWorkflow(host, TimeProvider.System);

        await workflow.EnableAsync(server.Id, CancellationToken.None);
        var invite = await workflow.CreateInviteAsync(server.Id, 3600, CancellationToken.None);

        Assert.True((await workflow.GetServerAsync(server.Id, CancellationToken.None)).Enabled);
        Assert.Equal(FakeBroker.InviteSecret, invite.Code);
        Assert.Contains("#" + FakeBroker.InviteSecret, invite.Link, StringComparison.Ordinal);
        var state = await File.ReadAllTextAsync(new ConnectOwnerPaths(_root).StateFile);
        Assert.DoesNotContain(FakeBroker.InviteSecret, state, StringComparison.Ordinal);
        Assert.Contains("inv_test", state, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoke_ReportsAllCompletedStepsAndDeletesUnusedFriendNode()
    {
        using var keys = new ConnectTestBroker();
        var factory = new ReadyRuntimeFactory(keys.KeySet);
        new ConnectOAuthCredentialStore(new ConnectOwnerPaths(_root)).Save(
            new TailscaleOAuthCredential("client-test", "tskey-client-test-secret"));
        await using var host = CreateHost(new MemoryServerStore(), factory);
        await host.StartAsync(CancellationToken.None);
        var membership = new ConnectMembershipState(
            "mem_test", "dev_test", Guid.NewGuid(), null, null, null, "nodeFriend1", "Friend",
            "approved", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(-1), "confirmed");
        host.SetState(host.State with { Memberships = [membership] });
        var workflow = new ConnectOwnerWorkflow(host, TimeProvider.System);

        var result = await workflow.RevokeAsync(membership.MembershipId, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(ConnectStepOutcome.Done, result.Broker);
        Assert.Equal(ConnectStepOutcome.Done, result.ThisPc);
        Assert.Equal(ConnectStepOutcome.Done, result.TailnetDevice);
        Assert.Equal(membership.MembershipId, factory.Authorization.RevokedMembership);
        Assert.Equal("nodeFriend1", factory.Provisioner.DeletedNode);
    }

    [Fact]
    public async Task FailedEnrollmentUpload_DeletesTheNewOneTimeKey()
    {
        using var keys = new ConnectTestBroker();
        using var deviceKey = Es256.CreateKey();
        var factory = new ReadyRuntimeFactory(keys.KeySet);
        new ConnectOAuthCredentialStore(new ConnectOwnerPaths(_root)).Save(
            new TailscaleOAuthCredential("client-test", "tskey-client-test-secret"));
        await using var host = CreateHost(new MemoryServerStore(), factory);
        await host.StartAsync(CancellationToken.None);
        var spki = Es256.ExportPublicKey(deviceKey);
        factory.Broker!.Memberships =
        [
            new ConnectBrokerMembership(
                "mem_enroll", Guid.NewGuid(), "Home", ConnectKeyIds.ForDevice(spki), Base64Url.Encode(spki),
                "approved", 1, null, null, null, null, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow)
        ];
        factory.Broker.FailEnrollmentUpload = true;

        await Assert.ThrowsAsync<ConnectOwnerBrokerException>(() => host.ReconcileOnceAsync(CancellationToken.None));

        Assert.Contains("keyFriend1", factory.Provisioner.DeletedKeys);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private ConnectHost CreateHost(IGameServerStore store, IConnectHostRuntimeFactory factory) =>
        new(new ConnectHostOptions(_root, Path.Combine(_root, "transport.exe"), new Uri("https://connect.example/"), [5251]),
            store, TimeProvider.System, factory, NullLogger<ConnectHost>.Instance);

    private static GameServerDefinition Definition(Guid id, GameType game, string root, int port) =>
        new(id, game, "Home", root, port, null, DateTimeOffset.UtcNow);

    private sealed class MemoryServerStore(params GameServerDefinition[] servers) : IGameServerStore
    {
        private readonly IReadOnlyList<GameServerDefinition> _servers = servers;
        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(_servers);
        public Task<GameServerDefinition?> GetAsync(Guid serverId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_servers.SingleOrDefault(server => server.Id == serverId));
        public Task UpsertAsync(GameServerDefinition server, ServerState state, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetStateAsync(Guid serverId, ServerState state, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RefusingRuntimeFactory : IConnectHostRuntimeFactory
    {
        public int CreateCalls { get; private set; }
        private T Refuse<T>() { CreateCalls++; throw new Xunit.Sdk.XunitException("Runtime must stay off without a credential."); }
        public IConnectOwnerBrokerClient CreateBroker(ConnectIdentity identity) => Refuse<IConnectOwnerBrokerClient>();
        public IConnectProvisioner CreateProvisioner(TailscaleOAuthCredential credential) => Refuse<IConnectProvisioner>();
        public IConnectHostAuthorizationServer CreateAuthorizationServer(ConnectHostAuthorizationOptions options, ConnectServerCatalog catalog) => Refuse<IConnectHostAuthorizationServer>();
        public IConnectHostTransportSupervisor CreateSupervisor(ConnectHostTransportOptions options) => Refuse<IConnectHostTransportSupervisor>();
        public IConnectHostTransportControlClient CreateControlClient(ConnectHostTransportOptions options, IConnectHostTransportSupervisor supervisor) => Refuse<IConnectHostTransportControlClient>();
    }

    private sealed class ReadyRuntimeFactory(
        TicketKeySet keySet,
        Func<CancellationToken, Task<IReadOnlyList<ConnectBrokerMembership>>>? readMemberships = null) : IConnectHostRuntimeFactory
    {
        private readonly TicketKeySet _keySet = keySet;
        public FakeAuthorization Authorization { get; } = new();
        public FakeSupervisor Supervisor { get; } = new();
        public FakeControl Control { get; } = new();
        public FakeProvisioner Provisioner { get; } = new();
        public FakeBroker? Broker { get; private set; }
        public IConnectOwnerBrokerClient CreateBroker(ConnectIdentity identity) => Broker = new FakeBroker(identity.KeyId, _keySet, readMemberships);
        public IConnectProvisioner CreateProvisioner(TailscaleOAuthCredential credential) => Provisioner;
        public IConnectHostAuthorizationServer CreateAuthorizationServer(ConnectHostAuthorizationOptions options, ConnectServerCatalog catalog) => Authorization;
        public IConnectHostTransportSupervisor CreateSupervisor(ConnectHostTransportOptions options) => Supervisor;
        public IConnectHostTransportControlClient CreateControlClient(ConnectHostTransportOptions options, IConnectHostTransportSupervisor supervisor) => Control;
    }

    private sealed class FakeBroker(
        string ownerId,
        TicketKeySet keySet,
        Func<CancellationToken, Task<IReadOnlyList<ConnectBrokerMembership>>>? readMemberships = null) : IConnectOwnerBrokerClient
    {
        public const string InviteSecret = "invite-secret-test-only";
        public IReadOnlyList<ConnectBrokerMembership> Memberships { get; set; } = [];
        public bool FailEnrollmentUpload { get; set; }
        public Task<string> RegisterOwnerAsync(CancellationToken cancellationToken) => Task.FromResult(ownerId);
        public Task<byte[]> GetTicketKeysAsync(CancellationToken cancellationToken) => Task.FromResult(Encoding.UTF8.GetBytes(keySet.ToJson()));
        public Task<ConnectBrokerRevocationPage> GetRevocationsAsync(long after, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectBrokerRevocationPage([], after, false));
        public Task<IReadOnlyList<ConnectBrokerMembership>> GetMembershipsAsync(CancellationToken cancellationToken) =>
            readMemberships?.Invoke(cancellationToken) ?? Task.FromResult(Memberships);
        public Task PutServerAsync(Guid serverId, string label, string hostBridge, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ConnectBrokerInvite> CreateInviteAsync(Guid serverId, int ttlSeconds, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectBrokerInvite("inv_test", InviteSecret, DateTimeOffset.UtcNow.AddSeconds(ttlSeconds)));
        public Task RevokeInviteAsync(string inviteId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ApproveMembershipAsync(string membershipId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RejectMembershipAsync(string membershipId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PutEnrollmentAsync(string membershipId, string ciphertext, CancellationToken cancellationToken) =>
            FailEnrollmentUpload
                ? Task.FromException(new ConnectOwnerBrokerException(
                    ConnectOwnerBrokerFailure.Unavailable, null, "test enrollment upload failure"))
                : Task.CompletedTask;
        public Task ConfirmNodeAsync(string membershipId, string nodeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RejectNodeAsync(string membershipId, string nodeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConnectBrokerRevocationResult> RevokeMembershipAsync(string membershipId, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectBrokerRevocationResult(membershipId, "revoked", 2));
        public Task RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RevokeSessionAsync(string ticketId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class FakeProvisioner : IConnectProvisioner
    {
        public string? DeletedNode { get; private set; }
        public List<string> DeletedKeys { get; } = [];

        private static readonly byte[] SafePolicy = Encoding.UTF8.GetBytes("""
            {"tagOwners":{"tag:onesalem-host":["autogroup:admin"],"tag:onesalem-client":["tag:onesalem-host"]},
             "grants":[{"src":["tag:onesalem-client"],"dst":["tag:onesalem-host"],"ip":["tcp:7780"]}]}
            """);
        public Task<EnrollmentSecret> CreateHostAuthKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new EnrollmentSecret("tskey-auth-host-test", "keyHost1"));
        public Task<EnrollmentSecret> CreateFriendAuthKeyAsync(string membershipId, CancellationToken cancellationToken) =>
            Task.FromResult(new EnrollmentSecret("tskey-auth-friend-test", "keyFriend1"));
        public Task<bool> DeleteAuthKeyAsync(string keyId, CancellationToken cancellationToken)
        {
            DeletedKeys.Add(keyId);
            return Task.FromResult(true);
        }
        public Task<ConnectTailnetDevice?> GetDeviceAsync(string nodeId, CancellationToken cancellationToken) =>
            Task.FromResult<ConnectTailnetDevice?>(new(nodeId, [TailscaleApiProvisioner.HostTag], DateTimeOffset.UtcNow,
                "host", ["100.64.1.2"], false, null));
        public Task<byte[]> GetPolicyAsync(CancellationToken cancellationToken) => Task.FromResult(SafePolicy.ToArray());
        public Task<bool> DeleteFriendDeviceAsync(string nodeId, string hostNodeId, CancellationToken cancellationToken)
        {
            DeletedNode = nodeId;
            return Task.FromResult(true);
        }
        public void Dispose() { }
    }

    private sealed class FakeAuthorization : IConnectHostAuthorizationServer
    {
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public string? RevokedMembership { get; private set; }
        public ConnectHostAuthorizationStatus Status => new(true, 0);
        public void Start() => Started = true;
        public Task<IReadOnlyList<string>> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> RevokeMembershipAsync(string membershipId, CancellationToken cancellationToken)
        {
            RevokedMembership = membershipId;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
        public Task<IReadOnlyList<string>> RevokeTicketAsync(string ticketId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> DisableConnectAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> CloseServerConnectionsAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeSupervisor : IConnectHostTransportSupervisor
    {
        public bool Started { get; private set; }
        public int? RunningProcessId => Started ? 123 : null;
        public void Start() => Started = true;
        public Task StopAsync() { Started = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Started = false; return ValueTask.CompletedTask; }
    }

    private sealed class FakeControl : IConnectHostTransportControlClient
    {
        public string? Hostname { get; private set; }
        public Task<ConnectHostTransportHello> HelloAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectHostTransportHello(1, "tsnet", "test"));
        public Task<ConnectHostTransportStatus> StatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectHostTransportStatus(
                Hostname is null ? [] : [new ConnectHostTransportNode("host", "nodeHost1", "running")],
                new ConnectHostBridgeStatus(Hostname is null ? "waiting-for-enroll" : "running", Hostname is null ? null : "100.64.1.2:7780", true, 0)));
        public Task<string> EnrollAsync(string authKey, string hostname, CancellationToken cancellationToken)
        {
            Hostname = hostname;
            return Task.FromResult("nodeHost1");
        }
        public Task<System.Text.Json.JsonElement> DiagnosticsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
