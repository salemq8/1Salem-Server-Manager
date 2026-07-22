using ServerManager.Agent;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Agent.IntegrationTests;

public sealed class ConfigurationRestorePointTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1Salem-RestorePointTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RestorePoint_ProtectsContentRedactsSecretsAndRestoresOnlyConfiguration()
    {
        Directory.CreateDirectory(_root);
        var configurationPath = Path.Combine(_root, "server.properties");
        var worldPath = Path.Combine(_root, "world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(worldPath)!);
        await File.WriteAllTextAsync(
            configurationPath,
            "motd=Before\nserver-password=TopSecretValue\n");
        await File.WriteAllTextAsync(worldPath, "world-is-untouched");
        var server = CreateServer();
        var service = CreateService(server);

        var restorePoint = await service.CreateAsync(
            server,
            "minecraft-settings",
            ["server.properties"],
            new Dictionary<string, string>
            {
                ["MOTD"] = "Before → After",
                ["Server password"] = "TopSecretValue"
            });
        await File.WriteAllTextAsync(configurationPath, "motd=After\n");
        await service.CompleteAsync(server.Id, restorePoint.Id, false);

        var storedManifest = await File.ReadAllTextAsync(
            Directory.EnumerateFiles(
                    Path.Combine(
                        _root,
                        "backups",
                        "configuration-restore-points"),
                    "*.json")
                .Single());
        Assert.DoesNotContain(
            "TopSecretValue",
            storedManifest,
            StringComparison.Ordinal);
        var history = await service.ListAsync(server.Id);
        Assert.Single(history);
        Assert.Contains("[protected]", history[0].Summary, StringComparison.Ordinal);
        Assert.NotEqual(
            history[0].OriginalFileHashes["server.properties"],
            history[0].AppliedFileHashes["server.properties"]);

        var result = await service.RestoreAsync(
            server.Id,
            new ConfigurationRestorePointRestoreRequest(
                restorePoint.Id,
                ConfirmRestart: true));

        Assert.True(result.Success, result.Message);
        Assert.Contains(
            "motd=Before",
            await File.ReadAllTextAsync(configurationPath),
            StringComparison.Ordinal);
        Assert.Equal(
            "world-is-untouched",
            await File.ReadAllTextAsync(worldPath));
    }

    [Fact]
    public async Task RestorePoint_RetentionKeepsLatestTenPerServer()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "server.properties"),
            "motd=retention\n");
        var server = CreateServer();
        var service = CreateService(server);

        for (var index = 0; index < 12; index++)
        {
            _ = await service.CreateAsync(
                server,
                $"change-{index}",
                ["server.properties"]);
        }

        var history = await service.ListAsync(server.Id);
        Assert.Equal(
            ConfigurationRestorePointService.DefaultRetentionCount,
            history.Count);
        Assert.DoesNotContain(
            history,
            item => item.Reason == "change-0");
        Assert.Contains(
            history,
            item => item.Reason == "change-11");
    }

    [Fact]
    public void MinecraftMemoryMerge_PreservesCustomJvmArguments()
    {
        var merged = ServerConfigurationService.MergeMinecraftMemoryArguments(
            "-XX:+UseG1GC\n-Dfile.encoding=UTF-8\n-Xms512M\n-Xmx1024M\n",
            2048,
            4096);

        Assert.Contains("-Xms2048M", merged, StringComparison.Ordinal);
        Assert.Contains("-Xmx4096M", merged, StringComparison.Ordinal);
        Assert.Contains("-XX:+UseG1GC", merged, StringComparison.Ordinal);
        Assert.Contains("-Dfile.encoding=UTF-8", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("-Xms512M", merged, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private GameServerDefinition CreateServer() =>
        new(
            Guid.NewGuid(),
            GameType.Minecraft,
            "Test",
            _root,
            25565,
            "1.21.8",
            DateTimeOffset.UtcNow);

    private static ConfigurationRestorePointService CreateService(
        GameServerDefinition server) =>
        new(
            new MemoryServerStore(server),
            new TestSecretStore(),
            new NullAuditLogStore(),
            new StoppedProcessSupervisor(),
            null!);

    private sealed class TestSecretStore : ISecretStore
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue) =>
            System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(protectedValue));
    }

    private sealed class NullAuditLogStore : IAuditLogStore
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

    private sealed class MemoryServerStore(
        GameServerDefinition server) : IGameServerStore
    {
        private GameServerDefinition _server = server;

        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameServerDefinition>>([_server]);

        public Task<GameServerDefinition?> GetAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GameServerDefinition?>(
                serverId == _server.Id ? _server : null);

        public Task UpsertAsync(
            GameServerDefinition updated,
            ServerState state,
            CancellationToken cancellationToken = default)
        {
            _server = updated with { State = state };
            return Task.CompletedTask;
        }

        public Task SetStateAsync(
            Guid serverId,
            ServerState state,
            CancellationToken cancellationToken = default)
        {
            _server = _server with { State = state };
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

        public void ConfigureRestartPolicy(
            Guid serverId,
            RestartPolicy policy)
        {
        }
    }
}
