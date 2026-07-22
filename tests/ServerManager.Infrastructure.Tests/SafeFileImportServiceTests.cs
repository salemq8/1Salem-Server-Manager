using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Minecraft;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Tests;

public sealed class SafeFileImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CopyImport_VerifiesDestinationAndLeavesSourceUntouched()
    {
        var source = Path.Combine(_root, "Existing");
        var destination = Path.Combine(_root, "Managed");
        Directory.CreateDirectory(Path.Combine(source, "world"));
        await File.WriteAllTextAsync(Path.Combine(source, "server.jar"), "jar");
        await File.WriteAllTextAsync(Path.Combine(source, "server.properties"), "server-port=25566");
        await File.WriteAllTextAsync(Path.Combine(source, "world", "level.dat"), "world");
        var store = new InMemoryStore();
        var secrets = new PassthroughSecretStore();
        var service = new SafeFileImportService(
            new MinecraftServerProvider(),
            new PalworldServerProvider(secrets),
            secrets,
            store);

        var plan = await service.PlanAsync(
            new ImportRequest(GameType.Minecraft, ImportMode.Copy, source, destination));
        var result = await service.ExecuteAsync(plan);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(source, "world", "level.dat")));
        Assert.True(File.Exists(Path.Combine(destination, "world", "level.dat")));
        Assert.True(File.Exists(Path.Combine(destination, ".1salem", "import-manifest.json")));
        Assert.Single(store.Servers);
        Assert.Equal(25566, store.Servers[0].Port);
    }

    [Fact]
    public async Task MoveImport_RequiresExactTypedConfirmation()
    {
        var source = Path.Combine(_root, "Existing");
        var destination = Path.Combine(_root, "Managed");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "server.jar"), "jar");
        await File.WriteAllTextAsync(Path.Combine(source, "server.properties"), "server-port=25565");
        var secrets = new PassthroughSecretStore();
        var service = new SafeFileImportService(
            new MinecraftServerProvider(),
            new PalworldServerProvider(secrets),
            secrets,
            new InMemoryStore());
        var plan = await service.PlanAsync(
            new ImportRequest(GameType.Minecraft, ImportMode.Move, source, destination));

        var result = await service.ExecuteAsync(plan);

        Assert.False(result.Success);
        Assert.Equal("MoveConfirmationRequired", result.ErrorCode);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task PalworldCopyImport_PreservesSavedDataAndCreatesProtectedMetadata()
    {
        var source = Path.Combine(_root, "ExistingPalworld");
        var destination = Path.Combine(_root, "ManagedPalworld");
        Directory.CreateDirectory(Path.Combine(source, "Pal", "Saved", "SaveGames"));
        await File.WriteAllTextAsync(Path.Combine(source, "PalServer.exe"), "exe");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Pal", "Saved", "SaveGames", "Level.sav"),
            "save");
        var secrets = new PassthroughSecretStore();
        var store = new InMemoryStore();
        var service = new SafeFileImportService(
            new MinecraftServerProvider(),
            new PalworldServerProvider(secrets),
            secrets,
            store);

        var plan = await service.PlanAsync(
            new ImportRequest(GameType.Palworld, ImportMode.Copy, source, destination));
        var result = await service.ExecuteAsync(plan);

        Assert.True(result.Success);
        Assert.True(File.Exists(
            Path.Combine(source, "Pal", "Saved", "SaveGames", "Level.sav")));
        Assert.True(File.Exists(
            Path.Combine(destination, "Pal", "Saved", "SaveGames", "Level.sav")));
        Assert.True(File.Exists(Path.Combine(destination, ".1salem", "metadata.json")));
        Assert.Single(store.Servers);
        Assert.Equal(GameType.Palworld, store.Servers[0].Game);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class InMemoryStore : IGameServerStore
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

    private sealed class PassthroughSecretStore : ISecretStore
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";

        public string Unprotect(string protectedValue) =>
            protectedValue.StartsWith("protected:", StringComparison.Ordinal)
                ? protectedValue["protected:".Length..]
                : protectedValue;
    }
}
