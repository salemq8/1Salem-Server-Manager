using System.Net;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Backups;
using ServerManager.Infrastructure.Concurrency;
using ServerManager.Infrastructure.Games.Minecraft;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Tests;

public sealed class BackupServiceTests : IDisposable
{
    // Deliberately independent of the repository's own location (not under a "artifacts/"
    // folder inside the checkout): BackupDestinationPolicy.Validate hard-rejects any
    // destination under Path.GetTempPath(), and a checkout exported for clean-checkout
    // verification (git archive into %TEMP%) would otherwise put this entire test root under
    // Temp too, failing every test in this file for a reason that has nothing to do with the
    // behavior under test. LocalApplicationData mirrors where BackupDestinationPolicy.
    // GetDefaultRoot itself points production backups (CommonApplicationData), so it is
    // guaranteed not to collide with any of the policy's own rejection rules.
    private readonly string _testRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "1SalemServerManager.Tests",
        "BackupServiceTests",
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

    // --- P0-01: transactional restore --------------------------------------------------

    [Theory]
    [InlineData("server.properties")] // first manifest path
    [InlineData("whitelist.json")]    // middle manifest path
    [InlineData("ops.json")]          // last manifest path
    public async Task RestoreAsync_LockedFile_LeavesEveryTrackedFileAtExactPreRestoreState(
        string lockedFileName)
    {
        var (service, _, backupId, _, paths) = await CreateThreeFileBackupAsync(
            new StoppedProcessSupervisor());
        Assert.Equal(["server.properties", "whitelist.json", "ops.json"], paths);
        var lockedPath = Path.Combine(ServerRoot, lockedFileName);

        await using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var restored = await service.RestoreAsync(backupId);
            Assert.False(restored.Success);
            Assert.Equal("RestoreFailed", restored.ErrorCode);
        }

        // Whichever file failed, the whole transaction must roll back to the exact
        // pre-restore state -- no tracked file may be left partially restored, missing,
        // or renamed to a `.failed-restore-*` sibling.
        Assert.Equal("changed-a", await File.ReadAllTextAsync(Path.Combine(ServerRoot, "server.properties")));
        Assert.Equal("changed-b", await File.ReadAllTextAsync(Path.Combine(ServerRoot, "whitelist.json")));
        Assert.Equal("changed-c", await File.ReadAllTextAsync(Path.Combine(ServerRoot, "ops.json")));
        AssertNoFailedRestoreArtifacts();
    }

    [Fact]
    public async Task RestoreAsync_FailsAfterEveryFileIsSwapped_StillRollsBackToPreRestoreState()
    {
        // No Java path/JAR is configured for this server, so CreateLaunchSpec throws while
        // restarting the server *after* every tracked file has already been swapped into
        // place -- proving rollback also reverses already-completed swaps, not only ones
        // that never started.
        var (service, _, backupId, _, _) = await CreateThreeFileBackupAsync(new RunningProcessSupervisor());

        var restored = await service.RestoreAsync(backupId);

        Assert.False(restored.Success);
        Assert.Equal("RestoreFailed", restored.ErrorCode);
        Assert.Equal("changed-a", await File.ReadAllTextAsync(Path.Combine(ServerRoot, "server.properties")));
        Assert.Equal("changed-b", await File.ReadAllTextAsync(Path.Combine(ServerRoot, "whitelist.json")));
        Assert.Equal("changed-c", await File.ReadAllTextAsync(Path.Combine(ServerRoot, "ops.json")));
        AssertNoFailedRestoreArtifacts();
    }

    [Fact]
    public async Task RestoreAsync_InsufficientDiskSpace_FailsWithoutTouchingAnyLiveFile()
    {
        var (service, server, backupId, _, _) = await CreateThreeFileBackupAsync(
            new StoppedProcessSupervisor(),
            driveSpaceProbe: _ => new DriveSpaceInfo(true, 1024));

        var restored = await service.RestoreAsync(backupId);

        Assert.False(restored.Success);
        Assert.Equal("InsufficientDiskSpace", restored.ErrorCode);
        Assert.Equal("changed-a", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")));
        Assert.Equal("changed-b", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "whitelist.json")));
        Assert.Equal("changed-c", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "ops.json")));
        Assert.False(Directory.Exists(Path.Combine(server.RootPath, ".1salem")));
    }

    [Fact]
    public async Task RestoreAsync_MissingArchiveFile_FailsCleanly()
    {
        var (service, _, backupId, archivePath, _) = await CreateThreeFileBackupAsync(
            new StoppedProcessSupervisor());

        File.Delete(archivePath);
        var restored = await service.RestoreAsync(backupId);

        Assert.False(restored.Success);
        Assert.Equal("BackupNotFound", restored.ErrorCode);
    }

    [Fact]
    public async Task RecoverInterruptedRestoresAsync_CompletesAJournaledPartialRestore()
    {
        var (service, server, backupId, _, paths) = await CreateThreeFileBackupAsync(
            new StoppedProcessSupervisor());
        Assert.Equal(["server.properties", "whitelist.json", "ops.json"], paths);

        // Hand-build the exact on-disk state a crash would leave after the first tracked
        // path ("server.properties") was swapped but before the transaction committed:
        // the restored value is live, the pre-restore value sits in a rollback folder, and
        // a journal records that only that one path was processed.
        var rollbackDir = Path.Combine(server.RootPath, ".1salem", $"restore-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rollbackDir);
        File.Move(
            Path.Combine(server.RootPath, "server.properties"),
            Path.Combine(rollbackDir, "server.properties"));
        await File.WriteAllTextAsync(Path.Combine(server.RootPath, "server.properties"), "restored-a");
        await File.WriteAllTextAsync(
            Path.Combine(rollbackDir, "journal.json"),
            $$"""{"backupId":"{{backupId}}","serverId":"{{server.Id}}","processedPaths":["server.properties"],"completed":false}""");

        var recovered = await service.RecoverInterruptedRestoresAsync();

        Assert.Equal(1, recovered);
        // The processed path is rolled back to its pre-restore value...
        Assert.Equal("changed-a", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")));
        // ...and the paths the crash never reached were never touched by recovery at all.
        Assert.Equal("changed-b", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "whitelist.json")));
        Assert.Equal("changed-c", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "ops.json")));
        Assert.False(Directory.Exists(rollbackDir));

        // Recovery must not repeat on the next startup.
        Assert.Equal(0, await service.RecoverInterruptedRestoresAsync());
    }

    private async Task<(BackupService Service, GameServerDefinition Server, Guid BackupId, string ArchivePath, IReadOnlyList<string> Paths)>
        CreateThreeFileBackupAsync(
            IProcessSupervisor supervisor,
            Func<string, DriveSpaceInfo>? driveSpaceProbe = null)
    {
        Directory.CreateDirectory(ServerRoot);
        await File.WriteAllTextAsync(Path.Combine(ServerRoot, "server.properties"), "original-a");
        await File.WriteAllTextAsync(Path.Combine(ServerRoot, "whitelist.json"), "original-b");
        await File.WriteAllTextAsync(Path.Combine(ServerRoot, "ops.json"), "original-c");
        var server = CreateServer();
        var service = new BackupService(
            new InMemoryGameStore(server),
            new InMemoryBackupStore(),
            supervisor,
            [new MinecraftServerProvider()],
            driveSpaceProbe: driveSpaceProbe);
        // SafeOffline is false here so backup *creation* never stops/restarts the server
        // regardless of the supervisor a given test passes in; only RestoreAsync's own
        // behavior is under test below.
        var created = await service.CreateAsync(
            new BackupRequest(server.Id, BackupRoot, false, false));
        var verification = await service.VerifyAsync(created.BackupId);

        await File.WriteAllTextAsync(Path.Combine(ServerRoot, "server.properties"), "changed-a");
        await File.WriteAllTextAsync(Path.Combine(ServerRoot, "whitelist.json"), "changed-b");
        await File.WriteAllTextAsync(Path.Combine(ServerRoot, "ops.json"), "changed-c");

        return (service, server, created.BackupId, created.ArchivePath, verification.Manifest!.IncludedPaths);
    }

    private void AssertNoFailedRestoreArtifacts()
    {
        if (!Directory.Exists(ServerRoot))
        {
            return;
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(ServerRoot, "*.failed-restore-*"));
    }

    // --- P1-02: concurrent operations against the same server must serialize ---------------

    [Fact]
    public async Task RestoreAsync_TwoConcurrentCallsForTheSameServer_NeverRunAtTheSameTime()
    {
        Directory.CreateDirectory(Path.Combine(ServerRoot, "world"));
        await File.WriteAllTextAsync(Path.Combine(ServerRoot, "world", "level.dat"), "original");
        var server = CreateServer();
        var sharedCoordinator = new ServerOperationCoordinator();
        var concurrentEntries = 0;
        var maxObservedConcurrency = 0;
        var gate = new object();

        Func<string, DriveSpaceInfo> probe = _ =>
        {
            lock (gate)
            {
                concurrentEntries++;
                maxObservedConcurrency = Math.Max(maxObservedConcurrency, concurrentEntries);
            }

            // Hold this "critical section" open briefly so a second, truly-concurrent call
            // would overlap here if the coordinator were not actually serializing them.
            Thread.Sleep(50);
            lock (gate)
            {
                concurrentEntries--;
            }

            return new DriveSpaceInfo(true, long.MaxValue / 2);
        };

        var service = new BackupService(
            new InMemoryGameStore(server),
            new InMemoryBackupStore(),
            new StoppedProcessSupervisor(),
            [new MinecraftServerProvider()],
            driveSpaceProbe: probe,
            coordinator: sharedCoordinator);
        var created = await service.CreateAsync(
            new BackupRequest(server.Id, BackupRoot, false, false));

        // Double-clicking "Restore" on the same backup: both calls target the same server, so
        // the coordinator must ensure only one is ever inside its critical section at a time.
        await Task.WhenAll(
            service.RestoreAsync(created.BackupId),
            service.RestoreAsync(created.BackupId));

        Assert.Equal(1, maxObservedConcurrency);
    }

    // --- P1-01: scheduled/live Palworld backups must not depend on REST being enabled ------

    [Fact]
    public async Task CreateAsync_PalworldRunning_RestHealthy_SavesViaRestWithoutStoppingTheServer()
    {
        var server = await CreatePalworldServerAsync(restEnabled: true);
        var supervisor = new TrackingPalworldSupervisor();
        var handler = new SaveWorldOkHandler();
        var restClient = new PalworldRestClient(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) },
            new PassthroughSecretStore());
        var service = new BackupService(
            new InMemoryGameStore(server),
            new InMemoryBackupStore(),
            supervisor,
            [new FakePalworldProvider()],
            restClient);

        var result = await service.CreateAsync(
            new BackupRequest(server.Id, BackupRoot, false, false));

        Assert.Equal("Backup succeeded.", result.StatusMessage);
        Assert.Equal(0, supervisor.StopCalls);
        Assert.Equal(0, supervisor.StartCalls);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateAsync_PalworldRunning_RestDisabled_FallsBackToSafeOfflineAndStillSucceeds()
    {
        // REST management left at its default (disabled) -- no HTTP call is even attempted.
        var server = await CreatePalworldServerAsync(restEnabled: false);
        var supervisor = new TrackingPalworldSupervisor();
        var restClient = new PalworldRestClient(
            new HttpClient(new SaveWorldOkHandler()),
            new PassthroughSecretStore());
        var service = new BackupService(
            new InMemoryGameStore(server),
            new InMemoryBackupStore(),
            supervisor,
            [new FakePalworldProvider()],
            restClient);

        var result = await service.CreateAsync(
            new BackupRequest(server.Id, BackupRoot, false, false));

        Assert.Contains("without REST save", result.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, supervisor.StopCalls);
        Assert.Equal(1, supervisor.StartCalls);
    }

    [Fact]
    public async Task CreateAsync_PalworldRunning_NoRestClientConfiguredAtAll_StillSucceeds()
    {
        // The Agent wires PalworldRestClient in as an optional dependency; if it's entirely
        // absent, a scheduled backup must still complete rather than throwing forever.
        var server = await CreatePalworldServerAsync(restEnabled: true);
        var supervisor = new TrackingPalworldSupervisor();
        var service = new BackupService(
            new InMemoryGameStore(server),
            new InMemoryBackupStore(),
            supervisor,
            [new FakePalworldProvider()],
            palworldRestClient: null);

        var result = await service.CreateAsync(
            new BackupRequest(server.Id, BackupRoot, false, false));

        Assert.Contains("without REST save", result.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, supervisor.StopCalls);
        Assert.Equal(1, supervisor.StartCalls);
    }

    [Fact]
    public async Task CreateAsync_PalworldRunning_RestAuthenticationFails_FallsBackAndSucceeds()
    {
        var server = await CreatePalworldServerAsync(restEnabled: true);
        var supervisor = new TrackingPalworldSupervisor();
        var restClient = new PalworldRestClient(
            new HttpClient(new UnauthorizedHandler()),
            new PassthroughSecretStore());
        var service = new BackupService(
            new InMemoryGameStore(server),
            new InMemoryBackupStore(),
            supervisor,
            [new FakePalworldProvider()],
            restClient);

        var result = await service.CreateAsync(
            new BackupRequest(server.Id, BackupRoot, false, false));

        Assert.Contains("without REST save", result.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Unauthorized", result.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, supervisor.StopCalls);
        Assert.Equal(1, supervisor.StartCalls);
    }

    [Fact]
    public async Task CreateAsync_PalworldRunning_RestTimesOut_FallsBackAndSucceeds()
    {
        var server = await CreatePalworldServerAsync(restEnabled: true);
        var supervisor = new TrackingPalworldSupervisor();
        var restClient = new PalworldRestClient(
            new HttpClient(new TimeoutHandler()) { Timeout = TimeSpan.FromMilliseconds(50) },
            new PassthroughSecretStore());
        var service = new BackupService(
            new InMemoryGameStore(server),
            new InMemoryBackupStore(),
            supervisor,
            [new FakePalworldProvider()],
            restClient);

        var result = await service.CreateAsync(
            new BackupRequest(server.Id, BackupRoot, false, false));

        Assert.Contains("without REST save", result.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, supervisor.StopCalls);
        Assert.Equal(1, supervisor.StartCalls);
    }

    private async Task<GameServerDefinition> CreatePalworldServerAsync(bool restEnabled)
    {
        Directory.CreateDirectory(Path.Combine(ServerRoot, ".1salem"));
        Directory.CreateDirectory(Path.Combine(ServerRoot, "Pal", "Saved", "SaveGames"));
        await File.WriteAllTextAsync(
            Path.Combine(ServerRoot, "Pal", "Saved", "SaveGames", "level.sav"),
            "save-data");
        var metadata = new PalworldServerMetadata(
            "PalworldVanilla",
            2_394_010,
            "build",
            "protected-server-password",
            "protected-admin-password",
            new PalworldServerSettingsTemplate(
                "1Salem Palworld",
                "Test",
                32,
                8211,
                false,
                false,
                25575,
                restEnabled,
                8212),
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            Path.Combine(ServerRoot, ".1salem", "metadata.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return new GameServerDefinition(
            Guid.NewGuid(),
            GameType.Palworld,
            "1Salem Palworld",
            ServerRoot,
            8211,
            "build",
            DateTimeOffset.UtcNow);
    }

    private sealed class PassthroughSecretStore : ISecretStore
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class SaveWorldOkHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class UnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class FakePalworldProvider : IGameServerProvider
    {
        public GameType Game => GameType.Palworld;

        public Task<InstallationDetection> DetectInstallationAsync(
            string rootPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ProcessLaunchSpec CreateLaunchSpec(GameServerDefinition server) =>
            new("PalServer.exe", string.Empty, server.RootPath, new Dictionary<string, string>());
    }

    private sealed class TrackingPalworldSupervisor : IProcessSupervisor
    {
        private bool _running = true;

        public int StopCalls { get; private set; }
        public int StartCalls { get; private set; }

        public Task<ProcessSnapshot> StartAsync(
            GameServerDefinition server,
            ProcessLaunchSpec launchSpec,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            _running = true;
            return Task.FromResult(new ProcessSnapshot(
                server.Id, 4321, ServerState.Running, DateTimeOffset.UtcNow, 0, 0, null));
        }

        public Task<OperationResult> StopAsync(
            Guid serverId,
            bool force,
            CancellationToken cancellationToken = default)
        {
            StopCalls++;
            _running = false;
            return Task.FromResult(OperationResult.Ok());
        }

        public Task<ProcessSnapshot> RestartAsync(
            GameServerDefinition server,
            ProcessLaunchSpec launchSpec,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProcessSnapshot?> GetSnapshotAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProcessSnapshot?>(_running
                ? new ProcessSnapshot(
                    serverId, 4321, ServerState.Running, DateTimeOffset.UtcNow, 0, 0, null)
                : null);

        public Task<IReadOnlyList<ProcessSnapshot>> GetAllSnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessSnapshot>>([]);

        public void ConfigureRestartPolicy(Guid serverId, RestartPolicy policy)
        {
        }
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

    private sealed class RunningProcessSupervisor : IProcessSupervisor
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
            Task.FromResult<ProcessSnapshot?>(new ProcessSnapshot(
                serverId,
                1234,
                ServerState.Running,
                DateTimeOffset.UtcNow,
                0,
                0,
                null));

        public Task<IReadOnlyList<ProcessSnapshot>> GetAllSnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessSnapshot>>([]);

        public void ConfigureRestartPolicy(Guid serverId, RestartPolicy policy)
        {
        }
    }
}
