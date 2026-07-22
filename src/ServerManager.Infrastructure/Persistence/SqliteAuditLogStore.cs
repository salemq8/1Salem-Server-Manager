using ServerManager.Core;

namespace ServerManager.Infrastructure.Persistence;

public sealed class SqliteAuditLogStore(SqliteConnectionFactory connectionFactory) : IAuditLogStore
{
    public async Task WriteAsync(
        string actor,
        string action,
        string target,
        bool succeeded,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AuditLog (TimestampUtc, Actor, Action, Target, Succeeded, Detail)
            VALUES ($timestampUtc, $actor, $action, $target, $succeeded, $detail);
            """;
        command.Parameters.AddWithValue("$timestampUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$succeeded", succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogRecord>> ListRecentAsync(
        string targetPrefix,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPrefix);
        var boundedLimit = Math.Clamp(limit, 1, 50);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TimestampUtc, Actor, Action, Target, Succeeded, Detail
            FROM AuditLog
            WHERE Target = $target OR Target LIKE $targetPrefix
            ORDER BY TimestampUtc DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$target", targetPrefix);
        command.Parameters.AddWithValue("$targetPrefix", targetPrefix + ":%");
        command.Parameters.AddWithValue("$limit", boundedLimit);

        var items = new List<AuditLogRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AuditLogRecord(
                reader.GetInt64(0),
                DateTimeOffset.Parse(
                    reader.GetString(1),
                    System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return items;
    }
}
