using System.Net;
using System.Security.Cryptography;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class MinecraftInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallAsync_StagesVerifiesAndGeneratesManagedServer()
    {
        Directory.CreateDirectory(_root);
        var jar = "fake minecraft server jar"u8.ToArray();
        var hash = Convert.ToHexString(SHA1.HashData(jar)).ToLowerInvariant();
        var version = new MinecraftVersionDescriptor(
            "1.21.8",
            "release",
            new Uri("https://example.test/version.json"),
            new Uri("https://example.test/server.jar"),
            hash,
            jar.Length,
            21);
        var store = new InMemoryGameServerStore();
        var installer = new MinecraftInstaller(
            new HttpClient(new StaticContentHandler(jar)),
            new StaticVersionCatalog(version),
            new StaticJavaLocator(),
            store);
        var destination = Path.Combine(_root, "Minecraft");

        var result = await installer.InstallAsync(CreateRequest(destination, true));

        Assert.Equal("1.21.8", result.Version);
        Assert.True(File.Exists(Path.Combine(destination, "server.jar")));
        Assert.Contains(
            "eula=true",
            await File.ReadAllTextAsync(Path.Combine(destination, "eula.txt")),
            StringComparison.Ordinal);
        Assert.Contains(
            "-Xmx2048M",
            await File.ReadAllTextAsync(Path.Combine(destination, "user_jvm_args.txt")),
            StringComparison.Ordinal);
        Assert.Single(store.Servers);
    }

    [Fact]
    public async Task InstallAsync_RequiresExplicitEulaAcceptanceWithoutCreatingDestination()
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, "Minecraft");
        var version = new MinecraftVersionDescriptor(
            "1.21.8",
            "release",
            new Uri("https://example.test/version.json"),
            new Uri("https://example.test/server.jar"),
            "00",
            1,
            21);
        var installer = new MinecraftInstaller(
            new HttpClient(new StaticContentHandler([0])),
            new StaticVersionCatalog(version),
            new StaticJavaLocator(),
            new InMemoryGameServerStore());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(CreateRequest(destination, false)));
        Assert.False(Directory.Exists(destination));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static MinecraftInstallRequest CreateRequest(string destination, bool accepted) =>
        new(
            "Test Minecraft",
            destination,
            "1.21.8",
            25565,
            1024,
            2048,
            new MinecraftServerSettings(
                "Test",
                20,
                "normal",
                "survival",
                true,
                10,
                10,
                false),
            accepted);

    private sealed class StaticContentHandler(byte[] content) : HttpMessageHandler
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

    private sealed class StaticVersionCatalog(MinecraftVersionDescriptor version) :
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

    private sealed class StaticJavaLocator : IJavaRuntimeLocator
    {
        public Task<JavaRuntimeInfo?> FindAsync(
            int minimumMajorVersion,
            CancellationToken cancellationToken = default)
        {
            var executable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            return Task.FromResult<JavaRuntimeInfo?>(
                new JavaRuntimeInfo(executable, 21, "test"));
        }
    }

    private sealed class InMemoryGameServerStore : IGameServerStore
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
            Servers.RemoveAll(existing => existing.Id == server.Id);
            Servers.Add(server);
            return Task.CompletedTask;
        }

        public Task SetStateAsync(
            Guid serverId,
            ServerState state,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

