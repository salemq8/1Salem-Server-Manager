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
}
