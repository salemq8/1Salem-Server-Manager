using Microsoft.Data.Sqlite;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Content;
using ServerManager.Infrastructure.Persistence;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// Moving a real Build 6 database to the Build 7 shape, where a server is identified by its
/// id rather than by which game it runs. The point of these tests is that nothing anybody
/// already has is lost or re-pointed on the way.
/// </summary>
public sealed class MultiServerMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-migrate-{Guid.NewGuid():N}");

    private readonly Guid _minecraftId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private readonly Guid _palworldId = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");
    private readonly Guid _backupId = Guid.Parse("cccccccc-1111-2222-3333-444444444444");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ABuild6DatabaseKeepsItsServersAndEverythingAttachedToThem()
    {
        var options = new SqliteStorageOptions(_root);
        var factory = new SqliteConnectionFactory(options);
        await WriteBuild6DatabaseAsync(options, factory);

        await new SqliteApplicationDatabase(options, factory).InitializeAsync();

        var servers = new SqliteGameServerStore(factory);
        var all = await servers.ListAsync();

        // Both servers survive, with the ids and roots they already had.
        var minecraft = Assert.Single(all, server => server.Id == _minecraftId);
        var palworld = Assert.Single(all, server => server.Id == _palworldId);
        Assert.Equal("1Salem Minecraft", minecraft.Name);
        Assert.Equal(@"C:\servers\minecraft", minecraft.RootPath);
        Assert.Equal(25565, minecraft.Port);
        Assert.Equal(GameType.Palworld, palworld.Game);

        // The backup and the installed content still belong to the same server.
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ServerId FROM Backups WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", _backupId.ToString());
        Assert.Equal(_minecraftId.ToString(), (string?)await command.ExecuteScalarAsync());

        var content = new SqliteInstalledContentStore(factory);
        var installed = Assert.Single(await content.ListAsync(_minecraftId));
        Assert.Equal("LuckPerms.jar", installed.FileName);

        command.CommandText = "SELECT COUNT(*) FROM Schedules WHERE ServerId = $server;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$server", _minecraftId.ToString());
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ASecondMinecraftServerCanBeRegisteredAfterMigrating()
    {
        var options = new SqliteStorageOptions(_root);
        var factory = new SqliteConnectionFactory(options);
        await WriteBuild6DatabaseAsync(options, factory);
        await new SqliteApplicationDatabase(options, factory).InitializeAsync();

        var servers = new SqliteGameServerStore(factory);
        var second = Guid.NewGuid();
        await servers.UpsertAsync(
            new GameServerDefinition(
                second,
                GameType.Minecraft,
                "Modpack server",
                @"C:\servers\modpack",
                25566,
                "1.21.8",
                DateTimeOffset.UtcNow),
            ServerState.Stopped);

        var all = await servers.ListAsync();
        var minecraftServers = all.Where(server => server.Game == GameType.Minecraft).ToArray();

        Assert.Equal(2, minecraftServers.Length);
        Assert.Equal(2, minecraftServers.Select(server => server.Id).Distinct().Count());
        Assert.Contains(minecraftServers, server => server.Id == _minecraftId);
        Assert.Contains(minecraftServers, server => server.Id == second);

        // The old rule is gone: game type is no longer an identity.
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_GameServers_GameType';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ContentStaysWithTheServerItBelongsTo()
    {
        var options = new SqliteStorageOptions(_root);
        var factory = new SqliteConnectionFactory(options);
        await WriteBuild6DatabaseAsync(options, factory);
        await new SqliteApplicationDatabase(options, factory).InitializeAsync();

        var servers = new SqliteGameServerStore(factory);
        var second = Guid.NewGuid();
        await servers.UpsertAsync(
            new GameServerDefinition(
                second,
                GameType.Minecraft,
                "Modpack server",
                @"C:\servers\modpack",
                25566,
                "1.21.8",
                DateTimeOffset.UtcNow),
            ServerState.Stopped);

        var content = new SqliteInstalledContentStore(factory);
        await content.UpsertAsync(new InstalledContent(
            second,
            "Fabulously.mrpack",
            InstalledContentState.UpToDate,
            ContentKind.Modpack,
            ContentProviderId.Modrinth,
            "1KVo5zza",
            "version-1",
            "Fabulously Optimized",
            "14.1.0",
            "1.21.8",
            ServerPlatform.Unknown,
            InstalledAtUtc: DateTimeOffset.UtcNow,
            ManagedByManager: true,
            Loader: "fabric",
            LoaderVersion: "0.16.9"));

        // Each server sees only its own content.
        var first = await content.ListAsync(_minecraftId);
        var other = await content.ListAsync(second);
        Assert.Equal("LuckPerms.jar", Assert.Single(first).FileName);
        var pack = Assert.Single(other);
        Assert.Equal(ContentKind.Modpack, pack.Kind);
        Assert.Equal("fabric", pack.Loader);
        Assert.Equal("0.16.9", pack.LoaderVersion);
    }

    [Fact]
    public async Task DeletingOneServerLeavesTheOther()
    {
        var options = new SqliteStorageOptions(_root);
        var factory = new SqliteConnectionFactory(options);
        await WriteBuild6DatabaseAsync(options, factory);
        await new SqliteApplicationDatabase(options, factory).InitializeAsync();

        var servers = new SqliteGameServerStore(factory);
        var second = Guid.NewGuid();
        await servers.UpsertAsync(
            new GameServerDefinition(
                second,
                GameType.Minecraft,
                "Disposable",
                @"C:\servers\disposable",
                25566,
                "1.21.8",
                DateTimeOffset.UtcNow),
            ServerState.Stopped);
        var content = new SqliteInstalledContentStore(factory);
        await content.UpsertAsync(new InstalledContent(
            second,
            "Pack.mrpack",
            InstalledContentState.UpToDate,
            ContentKind.Modpack,
            InstalledAtUtc: DateTimeOffset.UtcNow));

        Assert.True(await servers.DeleteRegistrationAsync(second));

        var remaining = await servers.ListAsync();
        Assert.Contains(remaining, server => server.Id == _minecraftId);
        Assert.Contains(remaining, server => server.Id == _palworldId);
        Assert.DoesNotContain(remaining, server => server.Id == second);

        // The other server's content is untouched; the deleted server's went with it.
        Assert.Single(await content.ListAsync(_minecraftId));
        Assert.Empty(await content.ListAsync(second));
    }

    /// <summary>
    /// A database in the shape Build 6 shipped: the old unique index on GameType, one
    /// Minecraft server, one Palworld server, and rows hanging off them.
    /// </summary>
    private async Task WriteBuild6DatabaseAsync(
        SqliteStorageOptions options,
        SqliteConnectionFactory factory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE GameServers (
                Id TEXT NOT NULL PRIMARY KEY,
                GameType INTEGER NOT NULL CHECK (GameType IN (1, 2)),
                Name TEXT NOT NULL,
                RootPath TEXT NOT NULL,
                Port INTEGER NOT NULL CHECK (Port BETWEEN 1 AND 65535),
                InstalledVersion TEXT NULL,
                State INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                MinimumMemoryMb INTEGER NULL,
                MaximumMemoryMb INTEGER NULL,
                JavaExecutablePath TEXT NULL,
                PreferredAdapterId TEXT NULL,
                AutoStart INTEGER NOT NULL DEFAULT 0,
                AutoRestart INTEGER NOT NULL DEFAULT 1,
                Priority INTEGER NOT NULL DEFAULT 32,
                CpuAffinityMask INTEGER NULL,
                LastError TEXT NULL,
                LastBackupAtUtc TEXT NULL,
                UpdateStatus TEXT NULL
            );

            CREATE UNIQUE INDEX IX_GameServers_GameType ON GameServers (GameType);

            CREATE TABLE Backups (
                Id TEXT NOT NULL PRIMARY KEY,
                ServerId TEXT NOT NULL,
                ArchivePath TEXT NOT NULL,
                Version TEXT NOT NULL,
                Sha256 TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                FileCount INTEGER NOT NULL,
                Status INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                IsProtected INTEGER NOT NULL DEFAULT 0,
                DisplayName TEXT NULL,
                Notes TEXT NULL,
                IsScheduled INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (ServerId) REFERENCES GameServers (Id) ON DELETE RESTRICT
            );

            CREATE TABLE Schedules (
                Id TEXT NOT NULL PRIMARY KEY,
                ServerId TEXT NULL,
                Kind TEXT NOT NULL,
                CronExpression TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1,
                NextRunAtUtc TEXT NULL,
                LastRunAtUtc TEXT NULL,
                FOREIGN KEY (ServerId) REFERENCES GameServers (Id) ON DELETE CASCADE
            );

            CREATE TABLE InstalledContent (
                ServerId TEXT NOT NULL,
                FileName TEXT NOT NULL,
                Kind INTEGER NOT NULL DEFAULT 1,
                Provider INTEGER NULL,
                ProjectId TEXT NULL,
                VersionId TEXT NULL,
                ProjectName TEXT NULL,
                InstalledVersion TEXT NULL,
                MinecraftVersionAtInstall TEXT NULL,
                PlatformAtInstall INTEGER NOT NULL DEFAULT 0,
                ProjectUrl TEXT NULL,
                ProviderSha512 TEXT NULL,
                ProviderSha256 TEXT NULL,
                LocalSha256 TEXT NULL,
                SizeBytes INTEGER NOT NULL DEFAULT 0,
                InstalledAtUtc TEXT NOT NULL,
                RestartRequired INTEGER NOT NULL DEFAULT 0,
                PreviousVersionId TEXT NULL,
                PreviousFileName TEXT NULL,
                PRIMARY KEY (ServerId, FileName),
                FOREIGN KEY (ServerId) REFERENCES GameServers (Id) ON DELETE CASCADE
            );

            PRAGMA user_version = 4;
            """;
        await command.ExecuteNonQueryAsync();

        command.CommandText = """
            INSERT INTO GameServers (Id, GameType, Name, RootPath, Port, InstalledVersion, State,
                CreatedAtUtc, UpdatedAtUtc, AutoStart)
            VALUES ($minecraft, 1, '1Salem Minecraft', 'C:\servers\minecraft', 25565, '1.21.8', 1,
                $now, $now, 0),
                ($palworld, 2, '1Salem Palworld', 'C:\servers\palworld', 8211, '1.0', 1,
                $now, $now, 1);

            INSERT INTO Backups (Id, ServerId, ArchivePath, Version, Sha256, SizeBytes, FileCount,
                Status, CreatedAtUtc)
            VALUES ($backup, $minecraft, 'C:\backups\a.zip', '1.21.8', 'abc', 10, 2, 1, $now);

            INSERT INTO Schedules (Id, ServerId, Kind, CronExpression)
            VALUES ($schedule, $minecraft, 'Backup', '0 3 * * *');

            INSERT INTO InstalledContent (ServerId, FileName, Kind, Provider, ProjectId,
                InstalledAtUtc)
            VALUES ($minecraft, 'LuckPerms.jar', 1, 1, 'Vebnzrzj', $now);
            """;
        command.Parameters.AddWithValue("$minecraft", _minecraftId.ToString());
        command.Parameters.AddWithValue("$palworld", _palworldId.ToString());
        command.Parameters.AddWithValue("$backup", _backupId.ToString());
        command.Parameters.AddWithValue("$schedule", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        await command.ExecuteNonQueryAsync();
    }
}
