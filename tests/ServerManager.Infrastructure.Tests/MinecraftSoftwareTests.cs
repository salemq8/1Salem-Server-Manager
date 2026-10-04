using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Concurrency;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class MinecraftSoftwareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "1SalemSoftwareTests", Guid.NewGuid().ToString("N"));
    public MinecraftSoftwareTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "MyWorld", "region"));
        Directory.CreateDirectory(Path.Combine(_root, ".1salem"));
        File.WriteAllText(Path.Combine(_root, "server.properties"), "level-name=MyWorld\nmax-players=12\n");
        File.WriteAllText(Path.Combine(_root, "MyWorld", "level.dat"), "gamerules-and-level-data");
        File.WriteAllText(Path.Combine(_root, "MyWorld", "region", "r.0.0.mca"), "world-must-not-roll-back");
        File.WriteAllText(Path.Combine(_root, "java.exe"), "disposable-fixture-not-an-executable");
        WriteJar(Path.Combine(_root, "server.jar"), "Paper");
    }

    [Theory]
    [InlineData(ServerPlatform.Vanilla, ServerPlatform.Paper, "1.21.11")]
    [InlineData(ServerPlatform.Vanilla, ServerPlatform.Purpur, "26.3")]
    [InlineData(ServerPlatform.Paper, ServerPlatform.Vanilla, "26.3")]
    [InlineData(ServerPlatform.Spigot, ServerPlatform.Paper, "26.3")]
    public void IncompatibleWorldLayoutsAreBlockedWithoutMovingAnyFile(ServerPlatform current, ServerPlatform target, string version)
    {
        // The preserving path still refuses conversion. Explicit reset is a separate path,
        // available only for Vanilla -> Paper/Purpur after post-stop confirmation.
        Assert.Equal("WorldLayoutChangeRequired", MinecraftSoftwareSafety.MigrationBlock(current, target, version, _root, "MyWorld"));
        Assert.Equal(current == ServerPlatform.Vanilla && target is ServerPlatform.Paper or ServerPlatform.Purpur,
            MinecraftSoftwareSafety.RequiresWorldReset(current, target));
        Assert.Equal("gamerules-and-level-data", File.ReadAllText(Path.Combine(_root, "MyWorld", "level.dat")));
        Assert.False(Directory.Exists(Path.Combine(_root, "MyWorld_nether")));
    }

    [Fact]
    public void SameVersionPaperPurpurWithExistingLayoutIsPermitted()
    {
        Assert.Null(MinecraftSoftwareSafety.MigrationBlock(ServerPlatform.Paper, ServerPlatform.Purpur, "26.3", _root, "MyWorld"));
        Directory.CreateDirectory(Path.Combine(_root, "MyWorld_nether"));
        Assert.Equal("WorldLayoutChangeRequired", MinecraftSoftwareSafety.MigrationBlock(ServerPlatform.Paper, ServerPlatform.Purpur, "26.3", _root, "MyWorld"));
    }

    [Fact]
    public void InspectorUsesActiveJarNotStaleHistoryOrSpareJar()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        WriteJar(Path.Combine(_root, "purpur-old.jar"), "Purpur");
        File.WriteAllText(Path.Combine(_root, "version_history.json"), "{\"currentVersion\":\"git-Purpur-10 (MC: 1.21)\"}");
        var result = MinecraftSoftwareSafety.Inspect(_root, "26.3");
        Assert.Equal(ServerPlatform.Vanilla, result.Platform);
        Assert.Equal("26.3", result.Version);
    }

    [Fact]
    public void ChangedJarInvalidatesRecordedSoftware()
    {
        var record = new MinecraftSoftwareSafety.InstalledSoftware(ServerPlatform.Purpur, "26.3", "10", "wrong-hash");
        File.WriteAllText(Path.Combine(_root, ".1salem", "software.json"), JsonSerializer.Serialize(record, MinecraftSoftwareSafety.Json));
        Assert.Equal(ServerPlatform.Paper, MinecraftSoftwareSafety.Inspect(_root, "26.3").Platform);
    }

    [Fact]
    public void SharedPaperclipBootstrapIsNotEnoughToIdentifyPaper()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "UnidentifiedFork");
        Assert.Equal(ServerPlatform.Unknown, MinecraftSoftwareSafety.Inspect(_root, "26.3").Platform);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("C:\\other")]
    [InlineData("world\\u002fother")]
    [InlineData("world:alias")]
    public void UnsafeLevelNamesRejected(string name)
    {
        File.WriteAllText(Path.Combine(_root, "server.properties"), "level-name=" + name);
        Assert.Throws<InvalidDataException>(() => MinecraftSoftwareSafety.LevelName(_root));
    }

    [Theory]
    [InlineData("26.2")]
    [InlineData("26.4")]
    public async Task MigrationRejectsEveryVersionChangeBeforeStopping(string targetVersion)
    {
        var fixture = Fixture();
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, targetVersion));
        Assert.Equal("VersionChangeRejected", result.Code);
        Assert.Equal(0, fixture.Runtime.Starts);
        Assert.Equal(0, fixture.Backup.Count);
    }

    [Fact]
    public async Task SuccessfulMigrationPreservesWorldAndPropertiesAndVerifiesStartup()
    {
        var fixture = Fixture();
        var original = File.ReadAllBytes(Path.Combine(_root, "server.properties"));
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3"));
        Assert.True(result.Success, result.Message);
        Assert.True(result.StartupVerified);
        Assert.Equal(1, fixture.Backup.Count);
        Assert.Equal(1, fixture.Runtime.Starts);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root, "server.properties")));
        Assert.Equal("world-must-not-roll-back", File.ReadAllText(Path.Combine(_root, "MyWorld", "region", "r.0.0.mca")));
        Assert.Equal(ServerPlatform.Purpur, MinecraftSoftwareSafety.Inspect(_root, "26.3").Platform);
        Assert.False(MinecraftSoftwareService.HasPendingMigration(_root));
        Assert.False(fixture.Runtime.Running); // An initially stopped server is stopped again after verification.
    }

    [Fact]
    public async Task FailedStartupRollsBackRuntimeAndConfigButNeverWorld()
    {
        var fixture = Fixture();
        fixture.Runtime.FailFirstStart = true;
        var jar = File.ReadAllBytes(Path.Combine(_root, "server.jar"));
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3"));
        Assert.False(result.Success);
        Assert.True(result.RuntimeRolledBack);
        Assert.Equal(jar, File.ReadAllBytes(Path.Combine(_root, "server.jar")));
        Assert.Equal("startup-world-write-preserved", File.ReadAllText(Path.Combine(_root, "MyWorld", "level.dat")));
        Assert.Contains("max-players=12", File.ReadAllText(Path.Combine(_root, "server.properties")));
        Assert.False(MinecraftSoftwareService.HasPendingMigration(_root));
    }

    [Fact]
    public async Task FailedBackupNeverDownloadsOrSwapsRuntime()
    {
        var fixture = Fixture();
        fixture.Backup.Fail = true;
        var jar = File.ReadAllBytes(Path.Combine(_root, "server.jar"));
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3"));
        Assert.False(result.Success);
        Assert.Equal(0, fixture.Catalog.Downloads);
        Assert.Equal(jar, File.ReadAllBytes(Path.Combine(_root, "server.jar")));
    }

    [Fact]
    public async Task RunningServerIsGracefullyStoppedAndReturnsRunningAfterSuccessfulMigration()
    {
        var fixture = Fixture();
        fixture.Runtime.Running = true;
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3"));
        Assert.True(result.Success, result.Message);
        Assert.True(fixture.Runtime.Running);
        Assert.Equal(1, fixture.Runtime.Starts);
    }

    [Fact]
    public async Task ReadoptedProcessWithoutConsoleIsNotStopped()
    {
        var fixture = Fixture();
        fixture.Runtime.Running = true;
        fixture.Runtime.NoConsole = true;
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3"));
        Assert.Equal("GracefulControlUnavailable", result.Code);
        Assert.True(fixture.Runtime.Running);
        Assert.Equal(0, fixture.Backup.Count);
    }

    [Fact]
    public void InterruptedMigrationBlocksOrdinaryServerStart()
    {
        File.WriteAllText(Path.Combine(_root, ".1salem", "software-migration.pending.json"), "{}");
        Assert.Throws<InvalidOperationException>(() => new MinecraftServerProvider().CreateLaunchSpec(Fixture().Server));
    }

    [Fact]
    public async Task PaperCatalogUsesOnlyStableExactVersionAndNeverFallsBack()
    {
        var handler = new MetadataHandler("[{\"id\":4,\"channel\":\"EXPERIMENTAL\"},{\"id\":3,\"channel\":\"STABLE\",\"downloads\":{\"server:default\":{\"url\":\"https://fill-data.papermc.io/paper.jar\",\"checksums\":{\"sha256\":\"" + new string('a',64) + "\"}}}}]");
        var catalog = new MinecraftSoftwareCatalog(new HttpClient(handler), new VanillaCatalog());
        var result = await catalog.ResolveAsync(ServerPlatform.Paper, "26.3");
        Assert.Equal("3", result?.Build);
        Assert.Single(handler.Paths);
        Assert.EndsWith("/versions/26.3/builds", handler.Paths[0]);
        handler.Response = "[]";
        Assert.Null(await catalog.ResolveAsync(ServerPlatform.Paper, "26.3"));
        Assert.Equal(2, handler.Paths.Count);
    }

    [Theory]
    [InlineData("http://fill-data.papermc.io/paper.jar")]
    [InlineData("https://evil.example/paper.jar")]
    [InlineData("https://fill-data.papermc.io:8443/paper.jar")]
    public void RuntimeDownloadsRejectNonOfficialDestinations(string url) =>
        Assert.Throws<InvalidDataException>(() => MinecraftSoftwareCatalog.ValidateDownloadUrl(ServerPlatform.Paper, new Uri(url)));

    [Fact]
    public async Task DownloadChecksumFailureDeletesOnlyStagedArtifact()
    {
        var handler = new MetadataHandler("not the expected artifact");
        var catalog = new MinecraftSoftwareCatalog(new HttpClient(handler), new VanillaCatalog());
        var stage = Path.Combine(_root, ".1salem", "checksum-fixture");
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.DownloadAsync(new MinecraftSoftwareArtifact(
            ServerPlatform.Paper, "26.3", "1", new Uri("https://fill-data.papermc.io/test.jar"), "SHA256", new string('a', 64)), stage));
        Assert.Empty(Directory.EnumerateFiles(stage));
        Assert.True(File.Exists(Path.Combine(_root, "server.jar")));
        Assert.True(File.Exists(Path.Combine(_root, "MyWorld", "level.dat")));
    }

    [Fact]
    public async Task FreshWorldReset_StatusOffersExplicitResetForAvailableVanillaTargets()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        var fixture = Fixture();
        var status = await fixture.Service.GetAsync(fixture.Server.Id);
        foreach (var target in new[] { ServerPlatform.Paper, ServerPlatform.Purpur })
        {
            var option = Assert.Single(status.Options, option => option.Platform == target);
            Assert.True(option.Available);
            Assert.True(option.RequiresWorldReset);
            Assert.Equal("WorldResetRequired", option.Code);
        }
    }

    [Fact]
    public async Task FreshWorldReset_WithoutConfirmationDoesNotDeleteOrInstall()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        var fixture = Fixture();
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3"));
        Assert.Equal("WorldDeletionConfirmationRequired", result.Code);
        Assert.True(File.Exists(Path.Combine(_root, "MyWorld", "level.dat")));
        Assert.Equal(0, fixture.Catalog.Downloads);
        Assert.Equal(0, fixture.Runtime.Starts);
    }

    [Fact]
    public async Task FreshWorldReset_ConfirmedButRunningIsRefusedWithoutImplicitStop()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        var fixture = Fixture();
        fixture.Runtime.Running = true;
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3", true));
        Assert.Equal("ServerMustBeStopped", result.Code);
        Assert.True(fixture.Runtime.Running);
        Assert.True(File.Exists(Path.Combine(_root, "MyWorld", "level.dat")));
    }

    [Fact]
    public async Task FreshWorldReset_PrepareOnlyStopsAndNeverDeletesOrRestarts()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        var fixture = Fixture();
        fixture.Runtime.Running = true;
        var result = await fixture.Service.PrepareAsync(fixture.Server.Id);
        Assert.True(result.Success);
        Assert.Equal("StoppedForSoftwareChange", result.Code);
        Assert.False(fixture.Runtime.Running);
        Assert.Equal(0, fixture.Runtime.Starts);
        Assert.True(File.Exists(Path.Combine(_root, "MyWorld", "level.dat")));
    }

    [Fact]
    public async Task FreshWorldReset_ReadoptedNoConsolePrepareNeverKillsOrRestarts()
    {
        var fixture = Fixture();
        fixture.Runtime.Running = true;
        fixture.Runtime.NoConsole = true;
        var result = await fixture.Service.PrepareAsync(fixture.Server.Id);
        Assert.False(result.Success);
        Assert.Equal("GracefulControlUnavailable", result.Code);
        Assert.True(fixture.Runtime.Running);
        Assert.Equal(0, fixture.Runtime.Starts);
    }

    [Fact]
    public async Task FreshWorldReset_DeletesOnlyNamedWorldsPreservesSettingsBackupsAndStartsFresh()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        foreach (var name in new[] { "MyWorld", "MyWorld_nether", "MyWorld_the_end" })
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
            File.WriteAllText(Path.Combine(_root, name, "old-world-marker.txt"), "old");
        }
        Directory.CreateDirectory(Path.Combine(_root, "backups"));
        File.WriteAllText(Path.Combine(_root, "backups", "existing.zip"), "preserved-backup");
        File.WriteAllText(Path.Combine(_root, "ops.json"), "preserved-operators");
        File.WriteAllText(Path.Combine(_root, "whitelist.json"), "preserved-whitelist");
        File.WriteAllText(Path.Combine(_root, "banned-players.json"), "preserved-bans");
        var properties = File.ReadAllText(Path.Combine(_root, "server.properties"));
        var fixture = Fixture();
        fixture.Runtime.GenerateFreshWorld = true;
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3", true));
        Assert.True(result.Success, result.Message);
        Assert.True(result.StartupVerified);
        Assert.Equal("WorldResetAndMigrated", result.Code);
        foreach (var name in new[] { "MyWorld", "MyWorld_nether", "MyWorld_the_end" })
            Assert.False(File.Exists(Path.Combine(_root, name, "old-world-marker.txt")));
        Assert.Equal("fresh-world", File.ReadAllText(Path.Combine(_root, "MyWorld", "level.dat")));
        Assert.Equal("preserved-backup", File.ReadAllText(Path.Combine(_root, "backups", "existing.zip")));
        Assert.Equal("preserved-operators", File.ReadAllText(Path.Combine(_root, "ops.json")));
        Assert.Equal("preserved-whitelist", File.ReadAllText(Path.Combine(_root, "whitelist.json")));
        Assert.Equal("preserved-bans", File.ReadAllText(Path.Combine(_root, "banned-players.json")));
        Assert.Equal(properties, File.ReadAllText(Path.Combine(_root, "server.properties")));
        Assert.Equal(0, fixture.Backup.Count); // No additional world backup is silently created.
        Assert.True(fixture.Runtime.Running);
        Assert.False(MinecraftSoftwareService.HasPendingMigration(_root));
    }

    [Fact]
    public async Task FreshWorldReset_FailedStagingPreservesWorldAndStaysStopped()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        var fixture = Fixture();
        fixture.Catalog.FailDownload = true;
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3", true));
        Assert.False(result.Success);
        Assert.True(File.Exists(Path.Combine(_root, "MyWorld", "level.dat")));
        Assert.Equal(0, fixture.Runtime.Starts);
        Assert.False(fixture.Runtime.Running);
    }

    [Fact]
    public async Task FreshWorldReset_PreStartInstallFailureRestoresRuntimeButDoesNotRegenerateDeletedWorld()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        var oldJar = File.ReadAllBytes(Path.Combine(_root, "server.jar"));
        File.Delete(Path.Combine(_root, "java.exe"));
        var fixture = Fixture();
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3", true));
        Assert.Equal("FreshWorldInstallationFailed", result.Code);
        Assert.True(result.RuntimeRolledBack);
        Assert.Equal(oldJar, File.ReadAllBytes(Path.Combine(_root, "server.jar")));
        Assert.False(Directory.Exists(Path.Combine(_root, "MyWorld")));
        Assert.Equal(0, fixture.Runtime.Starts);
        Assert.False(fixture.Runtime.Running);
    }

    [Fact]
    public async Task FreshWorldReset_StartupFailureNeverReversesRuntimeOrRestoresWorld()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        var oldJar = File.ReadAllBytes(Path.Combine(_root, "server.jar"));
        var fixture = Fixture();
        fixture.Runtime.GenerateFreshWorld = true;
        fixture.Runtime.FailFirstStart = true;
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3", true));
        Assert.Equal("FreshWorldStartupFailed", result.Code);
        Assert.False(result.RuntimeRolledBack);
        Assert.NotEqual(oldJar, File.ReadAllBytes(Path.Combine(_root, "server.jar")));
        Assert.Equal("startup-world-write-preserved", File.ReadAllText(Path.Combine(_root, "MyWorld", "level.dat")));
        Assert.True(MinecraftSoftwareService.HasPendingMigration(_root));
        Assert.Equal(1, fixture.Runtime.Starts);
        Assert.False(fixture.Runtime.Running);
    }

    [Fact]
    public async Task StartupConfigurationLock_FreshWorldSuccessRetainsRuntimeFilesAndCompletesJournal()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        File.WriteAllText(Path.Combine(_root, "ops.json"), "preserved-operators");
        Directory.CreateDirectory(Path.Combine(_root, "backups"));
        File.WriteAllText(Path.Combine(_root, "backups", "existing.zip"), "preserved-backup");
        var fixture = Fixture();
        using var runtime = fixture.Runtime;
        runtime.GenerateFreshWorld = true;
        runtime.LockStartupConfiguration = true;

        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3", true));

        Assert.True(result.Success, result.Message);
        Assert.True(result.StartupVerified);
        Assert.True(runtime.Running);
        Assert.Equal(1, runtime.Starts);
        Assert.False(MinecraftSoftwareService.HasPendingMigration(_root));
        Assert.Equal(ServerPlatform.Purpur, MinecraftSoftwareSafety.Inspect(_root, "26.3").Platform);
        Assert.True(File.Exists(Path.Combine(_root, ".1salem", "software.json")));
        Assert.Contains("max-players=12", File.ReadAllText(Path.Combine(_root, "server.properties")));
        Assert.Contains("runtime-added-setting=true", File.ReadAllText(Path.Combine(_root, "server.properties")));
        Assert.Equal("runtime-generated-configuration", File.ReadAllText(Path.Combine(_root, "purpur.yml")));
        Assert.Equal("preserved-operators", File.ReadAllText(Path.Combine(_root, "ops.json")));
        Assert.Equal("preserved-backup", File.ReadAllText(Path.Combine(_root, "backups", "existing.zip")));
        Assert.Equal("fresh-world", File.ReadAllText(Path.Combine(_root, "MyWorld", "level.dat")));
    }

    [Fact]
    public async Task StartupConfigurationLock_FreshWorldTimeoutReturnsFailureWithoutTouchingLiveFiles()
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        var oldJar = File.ReadAllBytes(Path.Combine(_root, "server.jar"));
        var fixture = Fixture();
        using var runtime = fixture.Runtime;
        runtime.GenerateFreshWorld = true;
        runtime.LockStartupConfiguration = true;
        runtime.NoConsole = true;

        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3", true));

        Assert.False(result.Success);
        Assert.Equal("FreshWorldStartupFailed", result.Code);
        Assert.Contains("TimeoutException", result.Message);
        Assert.False(result.StartupVerified);
        Assert.False(result.RuntimeRolledBack);
        Assert.True(runtime.Running);
        Assert.Equal(1, runtime.Starts);
        Assert.True(MinecraftSoftwareService.HasPendingMigration(_root));
        Assert.False(File.Exists(Path.Combine(_root, ".1salem", "software.json")));
        Assert.NotEqual(oldJar, File.ReadAllBytes(Path.Combine(_root, "server.jar")));
        Assert.Contains("max-players=12", File.ReadAllText(Path.Combine(_root, "server.properties")));
        Assert.Contains("runtime-added-setting=true", File.ReadAllText(Path.Combine(_root, "server.properties")));
        Assert.Equal("runtime-generated-configuration", File.ReadAllText(Path.Combine(_root, "purpur.yml")));
        Assert.Equal("fresh-world", File.ReadAllText(Path.Combine(_root, "MyWorld", "level.dat")));
        Assert.Throws<InvalidOperationException>(() => new MinecraftServerProvider().CreateLaunchSpec(fixture.Server));
    }

    [Fact]
    public async Task StartupConfigurationLock_CompatibleRunningChangePreservesRuntimeFiles()
    {
        var fixture = Fixture();
        using var runtime = fixture.Runtime;
        runtime.Running = true;
        runtime.LockStartupConfiguration = true;

        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3"));

        Assert.True(result.Success, result.Message);
        Assert.True(result.StartupVerified);
        Assert.True(runtime.Running);
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(1, fixture.Backup.Count);
        Assert.False(MinecraftSoftwareService.HasPendingMigration(_root));
        Assert.Equal(ServerPlatform.Purpur, MinecraftSoftwareSafety.Inspect(_root, "26.3").Platform);
        Assert.Contains("max-players=12", File.ReadAllText(Path.Combine(_root, "server.properties")));
        Assert.Contains("runtime-added-setting=true", File.ReadAllText(Path.Combine(_root, "server.properties")));
        Assert.Equal("runtime-generated-configuration", File.ReadAllText(Path.Combine(_root, "purpur.yml")));
        Assert.Equal("world-must-not-roll-back", File.ReadAllText(Path.Combine(_root, "MyWorld", "region", "r.0.0.mca")));
    }

    [Theory]
    [InlineData("backups")]
    [InlineData(".1salem")]
    [InlineData("plugins")]
    [InlineData("../other")]
    [InlineData("C:\\outside")]
    [InlineData("world:stream")]
    [InlineData("CON")]
    public void FreshWorldReset_ReservedOrEscapingDeletionRootIsRefused(string level) =>
        Assert.Throws<InvalidDataException>(() => MinecraftSoftwareService.ResolveWorldDeletionRoots(_root, level));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FreshWorldReset_BackupInsideWorldPreventsAnyDeletion(bool registered)
    {
        WriteJar(Path.Combine(_root, "server.jar"), "Vanilla");
        var fixture = Fixture();
        var backupPath = Path.Combine(_root, "MyWorld", registered ? "saved-data.bin" : "existing.zip");
        File.WriteAllText(backupPath, "preserve-me");
        if (registered) fixture.Backup.Paths.Add(backupPath);
        var result = await fixture.Service.MigrateAsync(fixture.Server.Id, new(ServerPlatform.Purpur, "26.3", true));
        Assert.Equal("WorldDeletionUnsafe", result.Code);
        Assert.Equal("preserve-me", File.ReadAllText(backupPath));
        Assert.True(File.Exists(Path.Combine(_root, "MyWorld", "level.dat")));
        Assert.Equal(0, fixture.Catalog.Downloads);
    }

    private (MinecraftSoftwareService Service, GameServerDefinition Server, FakeRuntime Runtime, FakeBackup Backup, FakeCatalog Catalog) Fixture()
    {
        var server = new GameServerDefinition(Guid.NewGuid(), GameType.Minecraft, "Disposable", _root, 25566, "26.3", DateTimeOffset.UtcNow,
            JavaExecutablePath: Path.Combine(_root, "java.exe"));
        var runtime = new FakeRuntime(_root);
        var backup = new FakeBackup();
        var catalog = new FakeCatalog();
        var service = new MinecraftSoftwareService(new FakeStore(server), new ServerOperationCoordinator(), catalog, backup,
            new MinecraftJarSwapService(), new MinecraftServerProvider(), runtime, runtime,
            new MinecraftSoftwareTimeouts(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(1)));
        return (service, server, runtime, backup, catalog);
    }

    private static void WriteJar(string path, string brand)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open()))
            writer.Write("Manifest-Version: 1.0\nMain-Class: " + (brand == "Vanilla" ? "net.minecraft.bundler.Main" : "io.papermc.paperclip.Main") + "\nImplementation-Title: " + brand + "\n");
        using var version = new StreamWriter(zip.CreateEntry("version.json").Open());
        version.Write("{\"id\":\"26.3\"}");
    }

    private sealed class FakeStore(GameServerDefinition server) : IGameServerStore
    {
        public Task<GameServerDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<GameServerDefinition?>(server);
        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GameServerDefinition>>([server]);
        public Task UpsertAsync(GameServerDefinition value, ServerState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetStateAsync(Guid id, ServerState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class FakeBackup : IMinecraftSoftwareBackup
    {
        public int Count; public bool Fail;
        public List<string> Paths { get; } = [];
        public Task<IReadOnlyList<string>> GetExistingBackupPathsAsync(GameServerDefinition server, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Paths);
        public Task<BackupResult> CreateAsync(GameServerDefinition server, CancellationToken cancellationToken)
        {
            Count++;
            if (Fail) throw new IOException("fixture backup failed");
            return Task.FromResult(new BackupResult(Guid.NewGuid(), "verified-fixture.zip", "hash", 1, 1, DateTimeOffset.UtcNow));
        }
    }
    private sealed class FakeCatalog : IMinecraftSoftwareCatalog
    {
        public int Downloads;
        public bool FailDownload;
        public Task<MinecraftSoftwareArtifact?> ResolveAsync(ServerPlatform platform, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<MinecraftSoftwareArtifact?>(new(platform, version, "1", new Uri("https://api.purpurmc.org/test"), "SHA256", new string('a', 64)));
        public Task<string> DownloadAsync(MinecraftSoftwareArtifact artifact, string stagingDirectory, CancellationToken cancellationToken = default)
        {
            Downloads++;
            if (FailDownload) throw new IOException("Staging failed before deletion.");
            Directory.CreateDirectory(stagingDirectory);
            var path = Path.Combine(stagingDirectory, "new.jar");
            WriteJar(path, "Purpur");
            return Task.FromResult(path);
        }
    }
    private sealed class FakeRuntime(string root) : IProcessSupervisor, IMinecraftConsoleChannel, IDisposable
    {
        public bool Running; public bool NoConsole; public bool FailFirstStart; public bool GenerateFreshWorld; public int Starts;
        public bool LockStartupConfiguration;
        private FileStream? _configurationLock;
        public event EventHandler<Guid>? ServerReady { add { } remove { } }
        public MinecraftConsoleState GetState(Guid id) => !Running ? MinecraftConsoleState.NotRunning : NoConsole ? MinecraftConsoleState.NoConsole : MinecraftConsoleState.Ready;
        public Task<ConsoleExchangeResult> ExchangeAsync(Guid id, string command, Func<string, bool> isAnswer, TimeSpan timeout, CancellationToken cancellationToken = default)
        { Assert.Equal("stop", command); Dispose(); Running = false; return Task.FromResult(new ConsoleExchangeResult(OperationResult.Ok(), "Stopping server", [])); }
        public Task<ProcessSnapshot> StartAsync(GameServerDefinition server, ProcessLaunchSpec launchSpec, CancellationToken cancellationToken = default)
        {
            Starts++;
            Running = !(FailFirstStart && Starts == 1);
            if (GenerateFreshWorld)
            {
                Directory.CreateDirectory(Path.Combine(root, "MyWorld"));
                File.WriteAllText(Path.Combine(root, "MyWorld", "level.dat"), "fresh-world");
            }
            if (LockStartupConfiguration)
            {
                File.AppendAllText(Path.Combine(root, "server.properties"), "runtime-added-setting=true\n");
                File.WriteAllText(Path.Combine(root, "purpur.yml"), "runtime-generated-configuration");
                _configurationLock = new FileStream(Path.Combine(root, "server.properties"), FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            if (!Running)
            {
                File.WriteAllText(Path.Combine(root, "server.properties"), "level-name=MyWorld\nmax-players=99");
                File.WriteAllText(Path.Combine(root, "MyWorld", "level.dat"), "startup-world-write-preserved");
            }
            return Task.FromResult(Snapshot(server.Id));
        }
        private ProcessSnapshot Snapshot(Guid id) => new(id, 123, Running ? ServerState.Running : ServerState.Stopped, DateTimeOffset.UtcNow, 0, 0, Running ? null : 1);
        public Task<ProcessSnapshot?> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ProcessSnapshot?>(Snapshot(id));
        public Task<IReadOnlyList<ProcessSnapshot>> GetAllSnapshotsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProcessSnapshot>>([]);
        public Task<OperationResult> StopAsync(Guid id, bool force, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Never use force-kill-capable StopAsync during migration.");
        public Task<ProcessSnapshot> RestartAsync(GameServerDefinition server, ProcessLaunchSpec launchSpec, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void ConfigureRestartPolicy(Guid id, RestartPolicy policy) { }
        public void Dispose() { _configurationLock?.Dispose(); _configurationLock = null; }
    }
    private sealed class MetadataHandler(string response) : HttpMessageHandler
    {
        public string Response = response; public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Paths.Add(request.RequestUri!.AbsolutePath); Assert.Contains("1Salem", request.Headers.UserAgent.ToString()); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(Response, Encoding.UTF8, "application/json") }); }
    }
    private sealed class VanillaCatalog : IMinecraftVersionCatalog
    {
        public Task<IReadOnlyList<MinecraftVersionDescriptor>> GetReleasesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MinecraftVersionDescriptor> GetVersionAsync(string version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
