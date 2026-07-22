using ServerManager.Core;

namespace ServerManager.Infrastructure.Persistence;

public sealed class SqliteScheduleStore(SqliteConnectionFactory connectionFactory) : IScheduleStore
{
    public async Task UpsertAsync(
        ScheduleRecord schedule,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Schedules (
                Id, ServerId, Kind, CronExpression, Enabled, NextRunAtUtc, LastRunAtUtc)
            VALUES (
                $id, $serverId, $kind, $expression, $enabled, $nextRunAtUtc, $lastRunAtUtc)
            ON CONFLICT (Id) DO UPDATE SET
                ServerId = excluded.ServerId,
                Kind = excluded.Kind,
                CronExpression = excluded.CronExpression,
                Enabled = excluded.Enabled,
                NextRunAtUtc = excluded.NextRunAtUtc,
                LastRunAtUtc = excluded.LastRunAtUtc;
            """;
        command.Parameters.AddWithValue("$id", schedule.Id.ToString());
        command.Parameters.AddWithValue(
            "$serverId",
            schedule.ServerId is null ? DBNull.Value : schedule.ServerId.Value.ToString());
        command.Parameters.AddWithValue("$kind", schedule.Kind);
        command.Parameters.AddWithValue("$expression", schedule.CronExpression);
        command.Parameters.AddWithValue("$enabled", schedule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$nextRunAtUtc",
            schedule.NextRunAtUtc is null ? DBNull.Value : schedule.NextRunAtUtc.Value.ToString("O"));
        command.Parameters.AddWithValue(
            "$lastRunAtUtc",
            schedule.LastRunAtUtc is null ? DBNull.Value : schedule.LastRunAtUtc.Value.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ScheduleRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var schedules = new List<ScheduleRecord>();
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ServerId, Kind, CronExpression, Enabled, NextRunAtUtc, LastRunAtUtc
            FROM Schedules
            ORDER BY Kind, Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schedules.Add(Read(reader));
        }

        return schedules;
    }

    public async Task<IReadOnlyList<ScheduleRecord>> GetDueAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var schedules = new List<ScheduleRecord>();
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ServerId, Kind, CronExpression, Enabled, NextRunAtUtc, LastRunAtUtc
            FROM Schedules
            WHERE Enabled = 1
              AND NextRunAtUtc IS NOT NULL
              AND NextRunAtUtc <= $nowUtc
            ORDER BY NextRunAtUtc;
            """;
        command.Parameters.AddWithValue("$nowUtc", nowUtc.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schedules.Add(Read(reader));
        }

        return schedules;
    }

    public async Task MarkRunAsync(
        Guid scheduleId,
        DateTimeOffset lastRunUtc,
        DateTimeOffset nextRunUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Schedules
            SET LastRunAtUtc = $lastRunAtUtc, NextRunAtUtc = $nextRunAtUtc
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", scheduleId.ToString());
        command.Parameters.AddWithValue("$lastRunAtUtc", lastRunUtc.ToString("O"));
        command.Parameters.AddWithValue("$nextRunAtUtc", nextRunUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ScheduleRecord Read(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4) == 1,
            reader.IsDBNull(5)
                ? null
                : DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6)
                ? null
                : DateTimeOffset.Parse(reader.GetString(6)));
}
