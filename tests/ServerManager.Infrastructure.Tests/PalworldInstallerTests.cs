using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Tests;

public sealed class PalworldInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallAsync_StagesSteamCmdOutputAndProtectsSecrets()
    {
        Directory.CreateDirectory(_root);
        var store = new InMemoryStore();
        var installer = new PalworldInstaller(
            new FakeSteamCmdService(),
            new EncodingSecretStore(),
            store);
        var destination = Path.Combine(_root, "Palworld");

        var result = await installer.InstallAsync(
            new PalworldInstallRequest(
                destination,
                new PalworldServerSettings(
                    "Test Palworld",
                    "Description",
                    "join-password",
                    "admin-password",
                    32,
                    8211,
                    false)));

        Assert.Equal("123456", result.BuildId);
        Assert.True(File.Exists(Path.Combine(destination, "PalServer.exe")));
        var metadataText = await File.ReadAllTextAsync(
            Path.Combine(destination, ".1salem", "metadata.json"));
        var metadata = JsonSerializer.Deserialize<PalworldServerMetadata>(
            metadataText,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(metadata);
        Assert.NotEqual("join-password", metadata.ProtectedServerPassword);
        Assert.NotEqual("admin-password", metadata.ProtectedAdminPassword);
        Assert.False(File.Exists(
            Path.Combine(
                destination,
                "Pal",
                "Saved",
                "Config",
                "WindowsServer",
                "PalWorldSettings.ini")));
        Assert.Single(store.Servers);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class FakeSteamCmdService : ISteamCmdService
    {
        public Task<string> EnsureInstalledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("steamcmd.exe");

        public Task<SteamCmdInstallResult> InstallOrUpdateAsync(
            string destinationPath,
            int appId,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.Combine(destinationPath, "Pal"));
            File.WriteAllText(Path.Combine(destinationPath, "PalServer.exe"), "exe");
            return Task.FromResult(
                new SteamCmdInstallResult(destinationPath, "123456", "Success"));
        }

        public Task<string?> GetLatestBuildIdAsync(
            int appId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("123456");
    }

    private sealed class EncodingSecretStore : ISecretStore
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
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
}

