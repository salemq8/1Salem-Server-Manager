using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Files;

namespace ServerManager.Infrastructure.Tests;

public sealed class RegisteredFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1Salem-Files-{Guid.NewGuid():N}");
    private readonly Guid _serverId = Guid.NewGuid();

    public RegisteredFileServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task WriteText_CreatesSafetyCopyBeforeReplacingConfiguration()
    {
        var path = Path.Combine(_root, "server.properties");
        await File.WriteAllTextAsync(path, "motd=old");
        var service = CreateService();

        var result = await service.WriteTextAsync(
            _serverId,
            "server.properties",
            "motd=new");

        Assert.True(result.Success);
        Assert.Equal("motd=new", await File.ReadAllTextAsync(path));
        var backup = Assert.Single(Directory.GetFiles(
            Path.Combine(_root, "backups", "config-edits"),
            "server.properties",
            SearchOption.AllDirectories));
        Assert.Equal("motd=old", await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task Delete_RequiresExactTypedConfirmation_AndBacksUp()
    {
        var path = Path.Combine(_root, "ops.json");
        await File.WriteAllTextAsync(path, "[]");
        var service = CreateService();

        var rejected = await service.DeleteAsync(
            _serverId,
            "ops.json",
            "delete ops.json");
        var accepted = await service.DeleteAsync(
            _serverId,
            "ops.json",
            "DELETE ops.json");

        Assert.False(rejected.Success);
        Assert.True(accepted.Success);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(
            Path.Combine(_root, "backups", "config-edits"),
            "ops.json",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReadText_RejectsPathEscape()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ReadTextAsync(_serverId, @"..\outside.txt"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private RegisteredFileService CreateService() =>
        new(
            new SingleServerStore(new GameServerDefinition(
                _serverId,
                GameType.Minecraft,
                "Test",
                _root,
                25565,
                "1.21",
                DateTimeOffset.UtcNow)),
            new NoOpAuditStore());

    private sealed class SingleServerStore(GameServerDefinition server) : IGameServerStore
    {
        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameServerDefinition>>([server]);

        public Task<GameServerDefinition?> GetAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GameServerDefinition?>(
                server.Id == serverId ? server : null);

        public Task UpsertAsync(
            GameServerDefinition value,
            ServerState state,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetStateAsync(
            Guid serverId,
            ServerState state,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpAuditStore : IAuditLogStore
    {
        public Task WriteAsync(
            string actor,
            string action,
            string target,
            bool succeeded,
            string? detail = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
