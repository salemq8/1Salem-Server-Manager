using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Minecraft;
using ServerManager.Infrastructure.Persistence;
using ServerManager.Infrastructure.Windows;

namespace ServerManager.Infrastructure.Tests;

public sealed class V110FunctionalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.V110.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("openjdk version \"21.0.8\" 2025-07-15", 21)]
    [InlineData("java version \"1.8.0_441\"", 8)]
    [InlineData("openjdk version \"25.0.1\" 2026-01-20", 25)]
    public void JavaDetection_ParsesModernAndLegacyVersions(
        string output,
        int expected) =>
        Assert.Equal(expected, JavaRuntimeLocator.ParseMajorVersion(output));

    [Fact]
    public async Task PortConflictDetection_ReportsBoundTcpPort()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var result = await new WindowsNetworkService().TestPortAsync(port, "TCP");

            Assert.False(result.IsAvailable);
            Assert.Equal(port, result.Port);
            Assert.Equal("TCP", result.Protocol);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task LocalIpv4Detection_ExcludesLoopbackAndLinkLocal()
    {
        var snapshot = await new WindowsNetworkService().GetSnapshotAsync();

        Assert.All(
            snapshot.Adapters ?? [],
            adapter =>
            {
                Assert.False(adapter.Ipv4.StartsWith("127.", StringComparison.Ordinal));
                Assert.False(adapter.Ipv4.StartsWith("169.254.", StringComparison.Ordinal));
            });
        if (snapshot.LocalIpv4 is not null)
        {
            Assert.True(IPAddress.TryParse(snapshot.LocalIpv4, out _));
        }
    }

    [Fact]
    public async Task PreferredAdapter_PersistsAndSelectsKnownAdapter()
    {
        var settings = new MemorySettingsStore();
        var network = new WindowsNetworkService(settings);
        var first = await network.GetSnapshotAsync();
        var chosen = first.Adapters?.LastOrDefault();
        if (chosen is null)
        {
            return;
        }

        var result = await network.SetPreferredAdapterAsync(chosen.Id);
        var refreshed = await network.GetSnapshotAsync();

        Assert.True(result.Success);
        Assert.Equal(chosen.Id, refreshed.PreferredAdapterId);
        Assert.Equal(chosen.Ipv4, refreshed.LocalIpv4);
    }

    [Fact]
    public async Task ServerRegistration_PersistsAllVersion110Fields()
    {
        Directory.CreateDirectory(_root);
        var options = new SqliteStorageOptions(_root);
        var factory = new SqliteConnectionFactory(options);
        await new SqliteApplicationDatabase(options, factory).InitializeAsync();
        var store = new SqliteGameServerStore(factory);
        var id = Guid.NewGuid();
        var backup = DateTimeOffset.UtcNow.AddMinutes(-5);
        var server = new GameServerDefinition(
            id,
            GameType.Minecraft,
            "Persistent",
            Path.Combine(_root, "Server With Spaces"),
            25570,
            "1.21.8",
            DateTimeOffset.UtcNow,
            ServerState.Error,
            2048,
            6144,
            @"C:\Java 21\bin\java.exe",
            "adapter-1",
            true,
            false,
            System.Diagnostics.ProcessPriorityClass.AboveNormal,
            3,
            "test error",
            backup,
            "available");

        await store.UpsertAsync(server, server.State);
        var restored = await store.GetAsync(id);

        Assert.NotNull(restored);
        Assert.Equal(server.Name, restored.Name);
        Assert.Equal(server.MinimumMemoryMb, restored.MinimumMemoryMb);
        Assert.Equal(server.MaximumMemoryMb, restored.MaximumMemoryMb);
        Assert.Equal(server.JavaExecutablePath, restored.JavaExecutablePath);
        Assert.Equal(server.PreferredAdapterId, restored.PreferredAdapterId);
        Assert.True(restored.AutoStart);
        Assert.False(restored.AutoRestart);
        Assert.Equal(server.Priority, restored.Priority);
        Assert.Equal(server.CpuAffinityMask, restored.CpuAffinityMask);
        Assert.Equal(server.LastError, restored.LastError);
        Assert.Equal(server.UpdateStatus, restored.UpdateStatus);
    }

    [Fact]
    public void RealLaunchArguments_UseArgumentListAndPathsWithSpaces()
    {
        var serverRoot = Path.Combine(_root, "Minecraft Server With Spaces");
        Directory.CreateDirectory(serverRoot);
        Directory.CreateDirectory(Path.Combine(serverRoot, ".1salem"));
        File.WriteAllBytes(Path.Combine(serverRoot, "server.jar"), [1]);
        var java = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var server = new GameServerDefinition(
            Guid.NewGuid(),
            GameType.Minecraft,
            "Spaces",
            serverRoot,
            25565,
            "1.21.8",
            DateTimeOffset.UtcNow,
            MinimumMemoryMb: 2048,
            MaximumMemoryMb: 4096,
            JavaExecutablePath: java);

        var spec = new MinecraftServerProvider().CreateLaunchSpec(server);

        Assert.Equal(java, spec.FileName);
        Assert.Equal(serverRoot, spec.WorkingDirectory);
        Assert.Equal(
            ["-Xms2048M", "-Xmx4096M", "-jar", "server.jar", "nogui"],
            spec.ArgumentList);
    }

    [Fact]
    public void ServerPropertiesMerge_PreservesUnmanagedSettings()
    {
        const string existing = "resource-pack=https\\://example.test/pack.zip\nmax-players=10\n";
        var settings = CreateSettings() with { MaxPlayers = 30, Hardcore = true, Pvp = false };

        var merged = MinecraftPropertiesSerializer.Merge(existing, settings, 25570);
        var parsed = MinecraftPropertiesSerializer.Parse(merged);

        Assert.Equal("https://example.test/pack.zip", parsed["resource-pack"]);
        Assert.Equal("30", parsed["max-players"]);
        Assert.Equal("true", parsed["hardcore"]);
        Assert.Equal("false", parsed["pvp"]);
        Assert.Equal("25570", parsed["server-port"]);
    }

    [Fact]
    public async Task Installer_SupportsFreshDestinationContainingSpaces()
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, "Fresh Server With Spaces");
        var jar = "verified jar"u8.ToArray();
        var installer = CreateInstaller(jar, SHA1.HashData(jar));

        var result = await installer.InstallAsync(CreateRequest(destination, true));

        Assert.Equal(destination, result.RootPath);
        Assert.True(File.Exists(Path.Combine(destination, "server.jar")));
        Assert.Contains(
            "eula=true",
            await File.ReadAllTextAsync(Path.Combine(destination, "eula.txt")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreationRollback_RemovesOnlyOwnedStagingDirectory()
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, "Failed Server");
        var installer = CreateInstaller("bad jar"u8.ToArray(), [0, 1, 2]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.InstallAsync(CreateRequest(destination, true)));

        Assert.False(Directory.Exists(destination));
        Assert.Empty(
            Directory.EnumerateDirectories(
                _root,
                ".1salem-minecraft-staging-*",
                SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ExistingWorld_IsNeverDeletedWhenCreationIsRejected()
    {
        var destination = Path.Combine(_root, "Existing");
        var world = Path.Combine(destination, "world");
        Directory.CreateDirectory(world);
        var level = Path.Combine(world, "level.dat");
        await File.WriteAllTextAsync(level, "preserve me");
        var jar = "jar"u8.ToArray();
        var installer = CreateInstaller(jar, SHA1.HashData(jar));

        await Assert.ThrowsAsync<IOException>(
            () => installer.InstallAsync(CreateRequest(destination, true)));

        Assert.Equal("preserve me", await File.ReadAllTextAsync(level));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static MinecraftInstaller CreateInstaller(
        byte[] response,
        byte[] expectedHash)
    {
        var java = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var descriptor = new MinecraftVersionDescriptor(
            "1.21.8",
            "release",
            new Uri("https://example.test/version.json"),
            new Uri("https://example.test/server.jar"),
            Convert.ToHexString(expectedHash).ToLowerInvariant(),
            response.Length,
            21);
        return new MinecraftInstaller(
            new HttpClient(new StaticHandler(response)),
            new StaticCatalog(descriptor),
            new StaticJavaLocator(java),
            new MemoryServerStore());
    }

    private static MinecraftInstallRequest CreateRequest(
        string destination,
        bool eulaAccepted) =>
        new(
            "Test",
            destination,
            "1.21.8",
            25565,
            2048,
            4096,
            CreateSettings(),
            eulaAccepted);

    private static MinecraftServerSettings CreateSettings() =>
        new(
            "Functional Test",
            20,
            "normal",
            "survival",
            true,
            10,
            10,
            false);

    private sealed class StaticHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content)
                });
    }

    private sealed class StaticCatalog(MinecraftVersionDescriptor version) :
        IMinecraftVersionCatalog
    {
        public Task<IReadOnlyList<MinecraftVersionDescriptor>> GetReleasesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MinecraftVersionDescriptor>>([version]);

        public Task<MinecraftVersionDescriptor> GetVersionAsync(
            string requestedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(version);
    }

    private sealed class StaticJavaLocator(string executable) : IJavaRuntimeLocator
    {
        public Task<JavaRuntimeInfo?> FindAsync(
            int minimumMajorVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaRuntimeInfo?>(
                new JavaRuntimeInfo(executable, 21, "openjdk version \"21\""));
    }

    private sealed class MemoryServerStore : IGameServerStore
    {
        public List<GameServerDefinition> Servers { get; } = [];

        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameServerDefinition>>(Servers);

        public Task<GameServerDefinition?> GetAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Servers.FirstOrDefault(server => server.Id == serverId));

        public Task UpsertAsync(
            GameServerDefinition server,
            ServerState state,
            CancellationToken cancellationToken = default)
        {
            Servers.Add(server);
            return Task.CompletedTask;
        }

        public Task SetStateAsync(
            Guid serverId,
            ServerState state,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, object?> _values =
            new(StringComparer.Ordinal);

        public Task<T?> GetAsync<T>(
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _values.TryGetValue(key, out var value) ? (T?)value : default);

        public Task SetAsync<T>(
            string key,
            T value,
            CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }
}
