using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Backups;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        FindRepositoryRoot(),
        "artifacts",
        "test-data",
        Guid.NewGuid().ToString("N"));
    private string ServerRoot => Path.Combine(_testRoot, "server");
    private string BackupRoot => Path.Combine(_testRoot, "backups");

    [Fact]
    public async Task CreateVerifyAndRestore_RoundTripsMinecraftWorld()
    {
        var world = Path.Combine(ServerRoot, "world");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "original");
        await File.WriteAllTextAsync(Path.Combine(ServerRoot, "server.properties"), "server-port=25565");
        var server = CreateServer();
        var gameStore = new InMemoryGameStore(server);
        var backupStore = new InMemoryBackupStore();
        var service = new BackupService(
            gameStore,
            backupStore,
            new StoppedProcessSupervisor(),
            [new MinecraftServerProvider()]);

        var created = await service.CreateAsync(
            new BackupRequest(
                server.Id,
                BackupRoot,
                false,
                true));
        var verification = await service.VerifyAsync(created.BackupId);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "changed");
        var restored = await service.RestoreAsync(created.BackupId);

        Assert.True(verification.IsValid);
        Assert.True(restored.Success, restored.Message);
        Assert.Equal(
            "original",
            await File.ReadAllTextAsync(Path.Combine(world, "level.dat")));
        Assert.True(File.Exists($"{created.ArchivePath}.sha256"));
    }

    [Fact]
    public async Task VerifyAsync_RejectsModifiedArchive()
    {
        Directory.CreateDirectory(Path.Combine(ServerRoot, "world"));
        await File.WriteAllTextAsync(Path.Combine(ServerRoot, "world", "level.dat"), "world");
        var server = CreateServer();
        var store = new InMemoryBackupStore();
        var service = new BackupService(
            new InMemoryGameStore(server),
            store,
            new StoppedProcessSupervisor(),
            [new MinecraftServerProvider()]);
        var created = await service.CreateAsync(
            new BackupRequest(server.Id, BackupRoot, false, true));

        await File.AppendAllTextAsync(created.ArchivePath, "tamper");
        var verification = await service.VerifyAsync(created.BackupId);

        Assert.False(verification.IsValid);
        Assert.Contains("SHA-256", verification.Error, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    private GameServerDefinition CreateServer() =>
        new(
            Guid.NewGuid(),
            GameType.Minecraft,
            "Test",
            ServerRoot,
            25565,
            "1.21.8",
            DateTimeOffset.UtcNow);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null &&
               !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
        {
            current = current.Parent;
        }

        return current?.FullName ??
               throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class InMemoryGameStore(GameServerDefinition server) : IGameServerStore
    {
        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameServerDefinition>>([server]);

        public Task<GameServerDefinition?> GetAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GameServerDefinition?>(server.Id == serverId ? server : null);

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

    private sealed class InMemoryBackupStore : IBackupStore
    {
        private readonly Dictionary<Guid, BackupRecord> _backups = [];

        public Task UpsertAsync(
            BackupRecord backup,
            CancellationToken cancellationToken = default)
        {
            _backups[backup.Id] = backup;
            return Task.CompletedTask;
        }

        public Task<BackupRecord?> GetAsync(
            Guid backupId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_backups.GetValueOrDefault(backupId));

        public Task<IReadOnlyList<BackupRecord>> ListAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackupRecord>>(
                _backups.Values.Where(backup => backup.ServerId == serverId).ToArray());

        public Task DeleteAsync(
            Guid backupId,
            CancellationToken cancellationToken = default)
        {
            _backups.Remove(backupId);
            return Task.CompletedTask;
        }
    }

    private sealed class StoppedProcessSupervisor : IProcessSupervisor
    {
        public Task<ProcessSnapshot> StartAsync(
            GameServerDefinition server,
            ProcessLaunchSpec launchSpec,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationResult> StopAsync(
            Guid serverId,
            bool force,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());

        public Task<ProcessSnapshot> RestartAsync(
            GameServerDefinition server,
            ProcessLaunchSpec launchSpec,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProcessSnapshot?> GetSnapshotAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProcessSnapshot?>(null);

        public Task<IReadOnlyList<ProcessSnapshot>> GetAllSnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessSnapshot>>([]);

        public void ConfigureRestartPolicy(Guid serverId, RestartPolicy policy)
        {
        }
    }
}
