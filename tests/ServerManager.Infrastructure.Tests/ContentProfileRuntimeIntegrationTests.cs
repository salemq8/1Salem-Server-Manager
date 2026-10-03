using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Content;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

/// <summary>Build 12 runtime-inspector → Content profile integration; disposable files only.</summary>
public sealed class ContentProfileRuntimeIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "1salem-content-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly GameServerDefinition _server;
    private readonly ContentProfileService _profiles;

    public ContentProfileRuntimeIntegrationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "SalemWorld", "players", "data"));
        Directory.CreateDirectory(Path.Combine(_root, "SalemWorld", "region"));
        File.WriteAllText(Path.Combine(_root, "server.properties"), "level-name=SalemWorld\nmax-players=20\n");
        File.WriteAllText(Path.Combine(_root, "SalemWorld", "level.dat"), "fixture gamerules / world identity");
        File.WriteAllText(Path.Combine(_root, "SalemWorld", "region", "r.0.0.mca"), "fixture blocks and entities");
        File.WriteAllText(Path.Combine(_root, "SalemWorld", "players", "data", "11111111-2222-3333-4444-555555555555.dat"), "fixture inventory");
        _server = new(Guid.NewGuid(), GameType.Minecraft, "Fixture", _root, 25565, "1.21.11", DateTimeOffset.UtcNow);
        _profiles = new(new Servers(_server), new ReadOnlySupervisor());
    }

    [Fact]
    public async Task ActiveVanillaOverridesStalePaperHistoryAndSpareJar()
    {
        WriteJar("server.jar", "Main-Class: net.minecraft.bundler.Main\n", "26.3");
        WriteJar("paper-unused.jar", "Main-Class: io.papermc.paperclip.Main\nImplementation-Title: Paper\n", "1.21.11");
        File.WriteAllText(Path.Combine(_root, "version_history.json"), "{\"currentVersion\":\"git-Paper-11 (MC: 1.21.11)\"}");
        var before = Fingerprints();

        var profile = await _profiles.GetAsync(_server.Id);

        Assert.NotNull(profile);
        Assert.Equal(ServerPlatform.Vanilla, profile.Platform);
        Assert.Equal("26.3", profile.MinecraftVersion);
        Assert.False(profile.SupportsPlugins);
        Assert.Equal(ContentProfileService.VanillaReason, profile.UnsupportedReason);
        Assert.Null(profile.PlatformVersion);
        Assert.Equal(before, Fingerprints());
    }

    [Fact]
    public async Task HashBoundMigratedPurpurRuntimeRefreshesPluginCapabilityAndExactVersionWithoutWorldWrites()
    {
        WriteJar("server.jar", "Main-Class: net.minecraft.bundler.Main\n", "26.3");
        Assert.False((await _profiles.GetAsync(_server.Id))!.SupportsPlugins);
        var worldBefore = Fingerprints(worldOnly: true);
        var propertiesBefore = File.ReadAllBytes(Path.Combine(_root, "server.properties"));

        // Simulate only the output of the separately tested runtime swap: a new active JAR
        // and the migration's SHA-bound identity marker. No server is launched here.
        WriteJar("server.jar", "Main-Class: io.papermc.paperclip.Main\n", null);
        Directory.CreateDirectory(Path.Combine(_root, ".1salem"));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "server.jar"))));
        File.WriteAllText(Path.Combine(_root, ".1salem", "software.json"), JsonSerializer.Serialize(
            new MinecraftSoftwareSafety.InstalledSoftware(ServerPlatform.Purpur, "26.3", "42", hash), MinecraftSoftwareSafety.Json));
        File.WriteAllText(Path.Combine(_root, "version_history.json"), "{\"currentVersion\":\"git-Paper-10 (MC: 1.20.4)\"}");
        var beforeRead = Fingerprints();

        var profile = await _profiles.GetAsync(_server.Id);

        Assert.NotNull(profile);
        Assert.Equal(ServerPlatform.Purpur, profile.Platform);
        Assert.Equal("26.3", profile.MinecraftVersion);
        Assert.True(profile.SupportsPlugins);
        Assert.Null(profile.UnsupportedReason);
        Assert.Equal(Path.Combine(_root, "plugins"), profile.PluginsDirectory);
        Assert.Equal(worldBefore, Fingerprints(worldOnly: true));
        Assert.Equal(propertiesBefore, File.ReadAllBytes(Path.Combine(_root, "server.properties")));
        Assert.Equal(beforeRead, Fingerprints());
    }

    private string[] Fingerprints(bool worldOnly = false) => Directory.GetFiles(worldOnly ? Path.Combine(_root, "SalemWorld") : _root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(_root, path) + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private void WriteJar(string name, string manifest, string? version)
    {
        using var file = new FileStream(Path.Combine(_root, name), FileMode.Create);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open())) writer.Write(manifest);
        if (version is not null)
            using (var writer = new StreamWriter(zip.CreateEntry("version.json").Open())) writer.Write(JsonSerializer.Serialize(new { id = version }));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Servers(GameServerDefinition server) : IGameServerStore
    {
        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GameServerDefinition>>([server]);
        public Task<GameServerDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(id == server.Id ? server : null);
        public Task UpsertAsync(GameServerDefinition value, ServerState state, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Profile reads must not change registered state.");
        public Task SetStateAsync(Guid id, ServerState state, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Profile reads must not change registered state.");
    }

    private sealed class ReadOnlySupervisor : IProcessSupervisor
    {
        public Task<ProcessSnapshot?> GetSnapshotAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.FromResult<ProcessSnapshot?>(null);
        public Task<IReadOnlyList<ProcessSnapshot>> GetAllSnapshotsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProcessSnapshot>>([]);
        public Task<ProcessSnapshot> StartAsync(GameServerDefinition server, ProcessLaunchSpec launchSpec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected start.");
        public Task<OperationResult> StopAsync(Guid serverId, bool force, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected stop.");
        public Task<ProcessSnapshot> RestartAsync(GameServerDefinition server, ProcessLaunchSpec launchSpec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected restart.");
        public void ConfigureRestartPolicy(Guid serverId, RestartPolicy policy) => throw new InvalidOperationException("Unexpected policy change.");
    }
}
