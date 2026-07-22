using Microsoft.Data.Sqlite;
using ServerManager.Infrastructure.Persistence;

namespace ServerManager.Infrastructure.Tests;

public sealed class SqliteApplicationDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InitializeAsync_CreatesRequiredTablesAndSchemaVersion()
    {
        var options = new SqliteStorageOptions(_root);
        var factory = new SqliteConnectionFactory(options);
        var database = new SqliteApplicationDatabase(options, factory);

        await database.InitializeAsync();

        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%';
            """;

        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        var required = new[]
        {
            "Settings",
            "Agents",
            "Clients",
            "GameServers",
            "ServerRuntimeSettings",
            "UpdateState",
            "Backups",
            "Schedules",
            "AuditLog",
            "CrashHistory"
        };
        Assert.All(required, table => Assert.Contains(table, tables));

        await reader.DisposeAsync();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(3, version);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var options = new SqliteStorageOptions(_root);
        var factory = new SqliteConnectionFactory(options);
        var database = new SqliteApplicationDatabase(options, factory);

        await database.InitializeAsync();
        await database.InitializeAsync();

        Assert.True(File.Exists(options.DatabasePath));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
