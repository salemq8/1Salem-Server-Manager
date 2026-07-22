using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Persistence;

public sealed class SqliteBackupStore(SqliteConnectionFactory connectionFactory) : IBackupStore
{
    public async Task UpsertAsync(
        BackupRecord backup,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Backups (
                Id, ServerId, ArchivePath, Version, Sha256, SizeBytes, FileCount,
                Status, CreatedAtUtc, IsProtected, DisplayName, Notes, IsScheduled)
            VALUES (
                $id, $serverId, $archivePath, $version, $sha256, $sizeBytes,
                $fileCount, $status, $createdAtUtc, $isProtected, $displayName,
                $notes, $isScheduled)
            ON CONFLICT (Id) DO UPDATE SET
                ArchivePath = excluded.ArchivePath,
                Version = excluded.Version,
                Sha256 = excluded.Sha256,
                SizeBytes = excluded.SizeBytes,
                FileCount = excluded.FileCount,
                Status = excluded.Status,
                IsProtected = excluded.IsProtected,
                DisplayName = excluded.DisplayName,
                Notes = excluded.Notes,
                IsScheduled = excluded.IsScheduled;
            """;
        command.Parameters.AddWithValue("$id", backup.Id.ToString());
        command.Parameters.AddWithValue("$serverId", backup.ServerId.ToString());
        command.Parameters.AddWithValue("$archivePath", backup.ArchivePath);
        command.Parameters.AddWithValue("$version", backup.Version);
        command.Parameters.AddWithValue("$sha256", backup.Sha256);
        command.Parameters.AddWithValue("$sizeBytes", backup.SizeBytes);
        command.Parameters.AddWithValue("$fileCount", backup.FileCount);
        command.Parameters.AddWithValue("$status", (int)backup.Status);
        command.Parameters.AddWithValue("$createdAtUtc", backup.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$isProtected", backup.IsProtected ? 1 : 0);
        command.Parameters.AddWithValue(
            "$displayName",
            (object?)backup.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$notes",
            (object?)backup.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$isScheduled", backup.IsScheduled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<BackupRecord?> GetAsync(
        Guid backupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ServerId, ArchivePath, Version, Sha256, SizeBytes,
                   FileCount, Status, CreatedAtUtc, IsProtected, DisplayName,
                   Notes, IsScheduled
            FROM Backups
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", backupId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<BackupRecord>> ListAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var backups = new List<BackupRecord>();
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ServerId, ArchivePath, Version, Sha256, SizeBytes,
                   FileCount, Status, CreatedAtUtc, IsProtected, DisplayName,
                   Notes, IsScheduled
            FROM Backups
            WHERE ServerId = $serverId
            ORDER BY CreatedAtUtc DESC;
            """;
        command.Parameters.AddWithValue("$serverId", serverId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            backups.Add(Read(reader));
        }

        return backups;
    }

    public async Task DeleteAsync(
        Guid backupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Backups WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", backupId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static BackupRecord Read(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetInt32(6),
            (BackupStatus)reader.GetInt32(7),
            DateTimeOffset.Parse(
                reader.GetString(8),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            reader.GetInt32(9) != 0,
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetInt32(12) != 0);
}
