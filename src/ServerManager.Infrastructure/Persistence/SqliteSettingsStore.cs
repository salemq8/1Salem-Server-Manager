using System.Text.Json;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Persistence;

public sealed class SqliteSettingsStore(SqliteConnectionFactory connectionFactory) : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT JsonValue FROM Settings WHERE [Key] = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string json
            ? JsonSerializer.Deserialize<T>(json, SerializerOptions)
            : default;
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        var json = JsonSerializer.Serialize(value, SerializerOptions);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Settings ([Key], JsonValue, UpdatedAtUtc)
            VALUES ($key, $json, $updatedAtUtc)
            ON CONFLICT ([Key]) DO UPDATE SET
                JsonValue = excluded.JsonValue,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
        {
            throw new ArgumentException("Setting keys must contain 1 to 200 characters.", nameof(key));
        }
    }
}

