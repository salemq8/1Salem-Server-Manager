using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServerManager.Agent;
using ServerManager.Connect.Core.Enrollment;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Agent.IntegrationTests;

public sealed class ConnectAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "1salem-acceptance-options-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OrdinaryOptions_DoNotEnableAcceptance()
    {
        var options = AgentOptions.Parse(["--data-root", _root, "--api-url", "http://127.0.0.1:5251"]);
        Assert.Null(options.ConnectAcceptance);
        Assert.Equal(AgentTransportDefaults.PipeName, options.PipeName);
        Assert.False(options.LanEnabled);
    }

#if DEBUG
    [Fact]
    public void Parse_AcceptsOnlyExplicitIsolatedConfiguration()
    {
        var options = AgentOptions.Parse(Arguments());

        Assert.Equal(_root, options.DataRoot);
        Assert.False(options.LanEnabled);
        Assert.Equal("http://127.0.0.1:18521", options.ApiUrl);
        Assert.Equal("1Salem.Connect.Acceptance.test.Agent", options.PipeName);
        Assert.Equal(new Uri("http://127.0.0.1:18522"), options.ConnectAcceptance!.BrokerOrigin);
        Assert.Equal("1Salem.Connect.Acceptance.test.Authz", options.ConnectAcceptance.AuthorizationPipeName);
        Assert.Equal("1Salem.Connect.Acceptance.test.Control", options.ConnectAcceptance.ControlPipeName);
        Assert.Equal(Path.Combine(_root, "1Salem.Connect.Host.Transport.exe"), options.ConnectAcceptance.TransportExecutablePath);
    }

    [Theory]
    [InlineData("--connect-acceptance")]
    [InlineData("--data-root")]
    [InlineData("--api-url")]
    [InlineData("--pipe-name")]
    [InlineData("--connect-broker-url")]
    [InlineData("--connect-authz-pipe")]
    [InlineData("--connect-control-pipe")]
    [InlineData("--connect-transport")]
    public void Parse_RejectsEveryMissingRequiredOption(string missing)
    {
        var args = Arguments().Where(argument => !argument.Equals(missing, StringComparison.Ordinal) &&
            !argument.StartsWith(missing + "=", StringComparison.Ordinal)).ToArray();
        Assert.Throws<ArgumentException>(() => AgentOptions.Parse(args));
    }

    [Theory]
    [InlineData("--lan")]
    [InlineData("--urls=http://0.0.0.0:18000")]
    [InlineData("--connect-acceptance")]
    [InlineData("--api-url=http://127.0.0.1:18523")]
    public void Parse_RejectsUnknownUnsafeAndDuplicateOptions(string extra) =>
        Assert.Throws<ArgumentException>(() => AgentOptions.Parse([.. Arguments(), extra]));

    [Theory]
    [InlineData("--api-url", "http://0.0.0.0:18000")]
    [InlineData("--api-url", "http://127.0.0.1:5251")]
    [InlineData("--api-url", "http://127.0.0.1:18522")]
    [InlineData("--connect-broker-url", "https://connect.1salem.app")]
    [InlineData("--connect-broker-url", "http://localhost:18000")]
    [InlineData("--connect-broker-url", "http://127.0.0.1:18000/path")]
    [InlineData("--connect-broker-url", "http://127.0.0.1:18000?query=1")]
    [InlineData("--connect-broker-url", "http://user@127.0.0.1:18000")]
    [InlineData("--pipe-name", "1Salem.ServerManager.Agent.v1")]
    [InlineData("--connect-authz-pipe", "1Salem.Connect.HostAuthz.v1")]
    [InlineData("--connect-control-pipe", "1Salem.Connect.Acceptance.test.Authz")]
    [InlineData("--connect-control-pipe", "1Salem.Connect.Acceptance.bad/pipe")]
    [InlineData("--data-root", "relative-root")]
    [InlineData("--data-root", "C:\\ProgramData\\1SalemServerManager.")]
    [InlineData("--data-root", "\\\\?\\C:\\acceptance")]
    [InlineData("--connect-transport", "1Salem.Connect.Host.Transport.exe")]
    public void Parse_RejectsUnsafeValues(string option, string value) =>
        Assert.Throws<ArgumentException>(() => AgentOptions.Parse(Arguments()
            .Select(argument => argument.StartsWith(option + "=", StringComparison.Ordinal) ? option + "=" + value : argument).ToArray()));

    [Theory]
    [InlineData("same")]
    [InlineData("parent")]
    [InlineData("child")]
    public void Parse_RejectsProductionDataRootOverlap(string relation)
    {
        var production = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "1SalemServerManager");
        var path = relation switch { "parent" => Path.GetDirectoryName(production)!, "child" => Path.Combine(production, "acceptance"), _ => production };
        Assert.Throws<ArgumentException>(() => AgentOptions.Parse(Arguments()
            .Select(argument => argument.StartsWith("--data-root=", StringComparison.Ordinal) ? "--data-root=" + path : argument).ToArray()));
    }

    [Fact]
    public void HostedServiceConfiguration_DropsEveryUnrelatedServiceWithoutConstructingIt()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostedService>(_ => throw new InvalidOperationException("Must never be constructed"));
        services.AddHostedService<ApplicationUpdateHostedService>();
        services.AddHostedService<DatabaseInitializationService>();

        ConnectAcceptanceOptions.ConfigureHostedServices(services);

        Assert.Equal(new[] { typeof(ConnectHostService), typeof(NamedPipeAgentServer) },
            services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).Select(descriptor => descriptor.ImplementationType));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DatabaseInitialization_ConfinesServersToOwnMinecraftRoots(bool ownRoot, bool minecraft)
    {
        var database = new FakeDatabase();
        var root = ownRoot ? Path.Combine(_root, "test-server") : Path.GetTempPath();
        var server = new GameServerDefinition(Guid.NewGuid(), minecraft ? GameType.Minecraft : GameType.Palworld,
            "Acceptance", root, 18201, null, DateTimeOffset.UtcNow);
        var services = new ServiceCollection();
        services.AddSingleton<IApplicationDatabase>(database);
        services.AddSingleton<IGameServerStore>(new FakeServerStore(server));
        services.AddSingleton<AgentRuntimeState>();
        using var provider = services.BuildServiceProvider();

        var initialize = () => ConnectAcceptanceOptions.InitializeDatabaseAsync(provider, _root, CancellationToken.None);
        if (ownRoot && minecraft) await initialize();
        else await Assert.ThrowsAsync<InvalidOperationException>(initialize);
        Assert.True(database.Initialized);
    }

    [Fact]
    public async Task Provisioner_RecordsOnlyResourceMetadataBeforeReturningAndObservesDeletes()
    {
        Directory.CreateDirectory(_root);
        var production = new FakeProvisioner();
        using var observer = new ConnectAcceptanceProvisioner(production, new ConnectAcceptanceResourceJournal(_root));
        var host = await observer.CreateHostAuthKeyAsync(CancellationToken.None);
        var friend = await observer.CreateFriendAuthKeyAsync("mem_test", CancellationToken.None);
        var path = Path.Combine(_root, ConnectAcceptanceResourceJournal.FileName);
        var created = File.ReadAllLines(path);
        Assert.Equal(2, created.Length);
        Assert.Equal("keyHost", JsonDocument.Parse(created[0]).RootElement.GetProperty("id").GetString());
        Assert.Equal(TailscaleApiProvisioner.FriendTag, JsonDocument.Parse(created[1]).RootElement.GetProperty("tag").GetString());
        Assert.False(await observer.DeleteAuthKeyAsync(host.KeyId, CancellationToken.None));
        Assert.Equal("nodeFriend", (await observer.GetDeviceAsync("nodeFriend", CancellationToken.None))!.NodeId);
        Assert.True(await observer.DeleteFriendDeviceAsync("nodeFriend", "nodeHost", CancellationToken.None));
        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain(host.AuthKey, content, StringComparison.Ordinal);
        Assert.DoesNotContain(friend.AuthKey, content, StringComparison.Ordinal);
        Assert.Contains("keyDeleted", content, StringComparison.Ordinal);
        Assert.Contains("deviceDeleted", content, StringComparison.Ordinal);
        Assert.Contains("deviceRead", content, StringComparison.Ordinal);
        Assert.Equal("mem_test", production.MembershipId);
        Assert.Equal("nodeFriend", production.DeletedNode);
    }

    [Fact]
    public async Task Provisioner_FailedJournalDoesNotDeliverUntrackedKeyAndAttemptsCleanup()
    {
        Directory.CreateDirectory(Path.Combine(_root, ConnectAcceptanceResourceJournal.FileName));
        var production = new FakeProvisioner();
        using var observer = new ConnectAcceptanceProvisioner(production, new ConnectAcceptanceResourceJournal(_root));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => observer.CreateHostAuthKeyAsync(CancellationToken.None));

        Assert.Equal("keyHost", production.DeletedKey);
    }

    private string[] Arguments()
    {
        Directory.CreateDirectory(_root);
        var executable = Path.Combine(_root, "1Salem.Connect.Host.Transport.exe");
        File.WriteAllText(executable, "not executable; options validation only");
        return ["--connect-acceptance", "--data-root=" + _root, "--api-url=http://127.0.0.1:18521",
            "--pipe-name=1Salem.Connect.Acceptance.test.Agent", "--connect-broker-url=http://127.0.0.1:18522",
            "--connect-authz-pipe=1Salem.Connect.Acceptance.test.Authz", "--connect-control-pipe=1Salem.Connect.Acceptance.test.Control",
            "--connect-transport=" + executable];
    }

    private sealed class FakeDatabase : IApplicationDatabase
    {
        public bool Initialized { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken = default) { Initialized = true; return Task.CompletedTask; }
    }

    private sealed class FakeServerStore(GameServerDefinition server) : IGameServerStore
    {
        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GameServerDefinition>>([server]);
        public Task<GameServerDefinition?> GetAsync(Guid serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertAsync(GameServerDefinition value, ServerState state, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetStateAsync(Guid serverId, ServerState state, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeProvisioner : IConnectProvisioner
    {
        public string? MembershipId { get; private set; }
        public string? DeletedKey { get; private set; }
        public string? DeletedNode { get; private set; }
        public Task<EnrollmentSecret> CreateHostAuthKeyAsync(CancellationToken cancellationToken) => Task.FromResult(new EnrollmentSecret("tskey-auth-host-test", "keyHost"));
        public Task<EnrollmentSecret> CreateFriendAuthKeyAsync(string membershipId, CancellationToken cancellationToken)
        { MembershipId = membershipId; return Task.FromResult(new EnrollmentSecret("tskey-auth-friend-test", "keyFriend")); }
        public Task<bool> DeleteAuthKeyAsync(string keyId, CancellationToken cancellationToken)
        { DeletedKey = keyId; return Task.FromResult(false); }
        public Task<ConnectTailnetDevice?> GetDeviceAsync(string nodeId, CancellationToken cancellationToken) =>
            Task.FromResult<ConnectTailnetDevice?>(new ConnectTailnetDevice(nodeId, [TailscaleApiProvisioner.FriendTag],
                DateTimeOffset.UtcNow, "test", ["100.64.0.1"], false, null));
        public Task<byte[]> GetPolicyAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteFriendDeviceAsync(string nodeId, string hostNodeId, CancellationToken cancellationToken)
        { DeletedNode = nodeId; return Task.FromResult(true); }
        public void Dispose() { }
    }
#else
    [Theory]
    [InlineData("--connect-acceptance")]
    [InlineData("--connect-broker-url=http://127.0.0.1:18522")]
    public void Release_RejectsAllAcceptanceOptions(string argument) =>
        Assert.Throws<ArgumentException>(() => AgentOptions.Parse([argument]));
#endif

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
