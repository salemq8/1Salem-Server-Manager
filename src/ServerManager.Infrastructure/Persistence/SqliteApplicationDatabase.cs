using Microsoft.Data.Sqlite;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Persistence;

public sealed class SqliteApplicationDatabase(
    SqliteStorageOptions options,
    SqliteConnectionFactory connectionFactory) : IApplicationDatabase
{
    private const int SchemaVersion = 6;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        Directory.CreateDirectory(options.LogsRoot);

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA busy_timeout = 10000;", cancellationToken);
        var existingVersion = await ReadSchemaVersionAsync(connection, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (existingVersion < 2)
        {
            command.CommandText = MigrationV2Sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (existingVersion < 3)
        {
            command.CommandText = MigrationV3Sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (existingVersion < 5)
        {
            command.CommandText = MigrationV5Sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (existingVersion < 6)
        {
            command.CommandText = MigrationV6Sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        command.CommandText = $"PRAGMA user_version = {SchemaVersion};";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS Settings (
            [Key] TEXT NOT NULL PRIMARY KEY,
            JsonValue TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Agents (
            Id TEXT NOT NULL PRIMARY KEY,
            MachineName TEXT NOT NULL,
            CertificateFingerprint TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            LastSeenAtUtc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS Clients (
            Id TEXT NOT NULL PRIMARY KEY,
            Name TEXT NOT NULL,
            CertificateFingerprint TEXT NOT NULL,
            ProtectedCredential TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            LastConnectedAtUtc TEXT NULL,
            RevokedAtUtc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS GameServers (
            Id TEXT NOT NULL PRIMARY KEY,
            GameType INTEGER NOT NULL CHECK (GameType IN (1, 2)),
            Name TEXT NOT NULL,
            RootPath TEXT NOT NULL,
            Port INTEGER NOT NULL CHECK (Port BETWEEN 1 AND 65535),
            InstalledVersion TEXT NULL,
            State INTEGER NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );

        -- A server is identified by its Id, never by which game it runs: several Minecraft
        -- servers can be managed side by side. This index is for lookups only.
        CREATE INDEX IF NOT EXISTS IX_GameServers_Game
            ON GameServers (GameType, Name);

        CREATE TABLE IF NOT EXISTS ServerRuntimeSettings (
            ServerId TEXT NOT NULL PRIMARY KEY,
            JsonValue TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            FOREIGN KEY (ServerId) REFERENCES GameServers (Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS UpdateState (
            ServerId TEXT NOT NULL PRIMARY KEY,
            InstalledVersion TEXT NULL,
            LatestVersion TEXT NULL,
            LastCheckedAtUtc TEXT NULL,
            LastUpdatedAtUtc TEXT NULL,
            AutoUpdateEnabled INTEGER NOT NULL DEFAULT 0,
            LastResult TEXT NULL,
            FOREIGN KEY (ServerId) REFERENCES GameServers (Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS Backups (
            Id TEXT NOT NULL PRIMARY KEY,
            ServerId TEXT NOT NULL,
            ArchivePath TEXT NOT NULL,
            Version TEXT NOT NULL,
            Sha256 TEXT NOT NULL,
            SizeBytes INTEGER NOT NULL,
            FileCount INTEGER NOT NULL,
            Status INTEGER NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            FOREIGN KEY (ServerId) REFERENCES GameServers (Id) ON DELETE RESTRICT
        );

        CREATE INDEX IF NOT EXISTS IX_Backups_ServerId_CreatedAtUtc
            ON Backups (ServerId, CreatedAtUtc DESC);

        CREATE TABLE IF NOT EXISTS Schedules (
            Id TEXT NOT NULL PRIMARY KEY,
            ServerId TEXT NULL,
            Kind TEXT NOT NULL,
            CronExpression TEXT NOT NULL,
            Enabled INTEGER NOT NULL DEFAULT 1,
            NextRunAtUtc TEXT NULL,
            LastRunAtUtc TEXT NULL,
            FOREIGN KEY (ServerId) REFERENCES GameServers (Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS AuditLog (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            TimestampUtc TEXT NOT NULL,
            Actor TEXT NOT NULL,
            Action TEXT NOT NULL,
            Target TEXT NOT NULL,
            Succeeded INTEGER NOT NULL,
            Detail TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_AuditLog_TimestampUtc
            ON AuditLog (TimestampUtc DESC);

        CREATE TABLE IF NOT EXISTS CrashHistory (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ServerId TEXT NOT NULL,
            CrashedAtUtc TEXT NOT NULL,
            ExitCode INTEGER NULL,
            RestartAttempt INTEGER NOT NULL,
            Detail TEXT NULL,
            FOREIGN KEY (ServerId) REFERENCES GameServers (Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_CrashHistory_ServerId_CrashedAtUtc
            ON CrashHistory (ServerId, CrashedAtUtc DESC);

        -- What the Content Hub installed, kept outside the JAR so a plugin cannot rewrite
        -- its own provenance. A file the person added by hand has no row here.
        CREATE TABLE IF NOT EXISTS InstalledContent (
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

        CREATE INDEX IF NOT EXISTS IX_InstalledContent_ServerId
            ON InstalledContent (ServerId);
        """;

    private const string MigrationV2Sql = """
        ALTER TABLE GameServers ADD COLUMN MinimumMemoryMb INTEGER NULL;
        ALTER TABLE GameServers ADD COLUMN MaximumMemoryMb INTEGER NULL;
        ALTER TABLE GameServers ADD COLUMN JavaExecutablePath TEXT NULL;
        ALTER TABLE GameServers ADD COLUMN PreferredAdapterId TEXT NULL;
        ALTER TABLE GameServers ADD COLUMN AutoStart INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE GameServers ADD COLUMN AutoRestart INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE GameServers ADD COLUMN Priority INTEGER NOT NULL DEFAULT 32;
        ALTER TABLE GameServers ADD COLUMN CpuAffinityMask INTEGER NULL;
        ALTER TABLE GameServers ADD COLUMN LastError TEXT NULL;
        ALTER TABLE GameServers ADD COLUMN LastBackupAtUtc TEXT NULL;
        ALTER TABLE GameServers ADD COLUMN UpdateStatus TEXT NULL;
        """;

    private const string MigrationV3Sql = """
        ALTER TABLE Backups ADD COLUMN IsProtected INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE Backups ADD COLUMN DisplayName TEXT NULL;
        ALTER TABLE Backups ADD COLUMN Notes TEXT NULL;
        ALTER TABLE Backups ADD COLUMN IsScheduled INTEGER NOT NULL DEFAULT 0;
        """;

    // Content Hub phase 2: a resource pack is distributed by pointing clients at the
    // provider's own URL, so the URL and its SHA-1 have to be remembered. RelativePath keeps
    // packs that do not live in one flat folder, such as a world's datapacks directory.
    // Build 7: several Minecraft servers may be managed at once. Only the old uniqueness rule
    // is dropped; no row is rewritten, so every server keeps its Id and everything attached to
    // it (backups, schedules, installed content) stays attached.
    private const string MigrationV6Sql = """
        DROP INDEX IF EXISTS IX_GameServers_GameType;
        CREATE INDEX IF NOT EXISTS IX_GameServers_Game ON GameServers (GameType, Name);
        ALTER TABLE InstalledContent ADD COLUMN Loader TEXT NULL;
        ALTER TABLE InstalledContent ADD COLUMN LoaderVersion TEXT NULL;
        """;

    private const string MigrationV5Sql = """
        ALTER TABLE InstalledContent ADD COLUMN DownloadUrl TEXT NULL;
        ALTER TABLE InstalledContent ADD COLUMN ProviderSha1 TEXT NULL;
        ALTER TABLE InstalledContent ADD COLUMN RelativePath TEXT NULL;
        """;
}
