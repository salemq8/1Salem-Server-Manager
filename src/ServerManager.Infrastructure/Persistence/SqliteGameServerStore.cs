using Microsoft.Data.Sqlite;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Persistence;

public sealed class SqliteGameServerStore(SqliteConnectionFactory connectionFactory) : IGameServerStore
{
    public async Task<IReadOnlyList<GameServerDefinition>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var servers = new List<GameServerDefinition>();
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, GameType, Name, RootPath, Port, InstalledVersion, CreatedAtUtc,
                   State, MinimumMemoryMb, MaximumMemoryMb, JavaExecutablePath,
                   PreferredAdapterId, AutoStart, AutoRestart, Priority, CpuAffinityMask,
                   LastError, LastBackupAtUtc, UpdateStatus
            FROM GameServers
            ORDER BY GameType;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            servers.Add(ReadServer(reader));
        }

        return servers;
    }

    public async Task<GameServerDefinition?> GetAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, GameType, Name, RootPath, Port, InstalledVersion, CreatedAtUtc,
                   State, MinimumMemoryMb, MaximumMemoryMb, JavaExecutablePath,
                   PreferredAdapterId, AutoStart, AutoRestart, Priority, CpuAffinityMask,
                   LastError, LastBackupAtUtc, UpdateStatus
            FROM GameServers
            WHERE Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", serverId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadServer(reader) : null;
    }

    public async Task UpsertAsync(
        GameServerDefinition server,
        ServerState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO GameServers (
                Id, GameType, Name, RootPath, Port, InstalledVersion, State,
                CreatedAtUtc, UpdatedAtUtc, MinimumMemoryMb, MaximumMemoryMb,
                JavaExecutablePath, PreferredAdapterId, AutoStart, AutoRestart,
                Priority, CpuAffinityMask, LastError, LastBackupAtUtc, UpdateStatus)
            VALUES (
                $id, $gameType, $name, $rootPath, $port, $installedVersion, $state,
                $createdAtUtc, $updatedAtUtc, $minimumMemoryMb, $maximumMemoryMb,
                $javaExecutablePath, $preferredAdapterId, $autoStart, $autoRestart,
                $priority, $cpuAffinityMask, $lastError, $lastBackupAtUtc, $updateStatus)
            ON CONFLICT (Id) DO UPDATE SET
                GameType = excluded.GameType,
                Name = excluded.Name,
                RootPath = excluded.RootPath,
                Port = excluded.Port,
                InstalledVersion = excluded.InstalledVersion,
                State = excluded.State,
                MinimumMemoryMb = excluded.MinimumMemoryMb,
                MaximumMemoryMb = excluded.MaximumMemoryMb,
                JavaExecutablePath = excluded.JavaExecutablePath,
                PreferredAdapterId = excluded.PreferredAdapterId,
                AutoStart = excluded.AutoStart,
                AutoRestart = excluded.AutoRestart,
                Priority = excluded.Priority,
                CpuAffinityMask = excluded.CpuAffinityMask,
                LastError = excluded.LastError,
                LastBackupAtUtc = excluded.LastBackupAtUtc,
                UpdateStatus = excluded.UpdateStatus,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$id", server.Id.ToString());
        command.Parameters.AddWithValue("$gameType", (int)server.Game);
        command.Parameters.AddWithValue("$name", server.Name);
        command.Parameters.AddWithValue("$rootPath", server.RootPath);
        command.Parameters.AddWithValue("$port", server.Port);
        command.Parameters.AddWithValue(
            "$installedVersion",
            (object?)server.InstalledVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$createdAtUtc", server.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue(
            "$minimumMemoryMb",
            (object?)server.MinimumMemoryMb ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$maximumMemoryMb",
            (object?)server.MaximumMemoryMb ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$javaExecutablePath",
            (object?)server.JavaExecutablePath ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$preferredAdapterId",
            (object?)server.PreferredAdapterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$autoStart", server.AutoStart ? 1 : 0);
        command.Parameters.AddWithValue("$autoRestart", server.AutoRestart ? 1 : 0);
        command.Parameters.AddWithValue("$priority", (int)server.Priority);
        command.Parameters.AddWithValue(
            "$cpuAffinityMask",
            (object?)server.CpuAffinityMask ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastError", (object?)server.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$lastBackupAtUtc",
            server.LastBackupAtUtc is { } lastBackup
                ? lastBackup.ToString("O")
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$updateStatus",
            (object?)server.UpdateStatus ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetStateAsync(
        Guid serverId,
        ServerState state,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE GameServers
            SET State = $state, UpdatedAtUtc = $updatedAtUtc
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", serverId.ToString());
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetStateWithErrorAsync(
        Guid serverId,
        ServerState state,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE GameServers
            SET State = $state,
                LastError = $lastError,
                UpdatedAtUtc = $updatedAtUtc
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", serverId.ToString());
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$lastError", (object?)lastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static GameServerDefinition ReadServer(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            (GameType)reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            DateTimeOffset.Parse(
                reader.GetString(6),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            (ServerState)reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetInt32(12) != 0,
            reader.GetInt32(13) != 0,
            (System.Diagnostics.ProcessPriorityClass)reader.GetInt32(14),
            reader.IsDBNull(15) ? null : reader.GetInt64(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17)
                ? null
                : DateTimeOffset.Parse(
                    reader.GetString(17),
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
            reader.IsDBNull(18) ? null : reader.GetString(18));
}
