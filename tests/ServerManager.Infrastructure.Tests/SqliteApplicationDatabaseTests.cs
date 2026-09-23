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
            "CrashHistory",
            "InstalledContent"
        };
        Assert.All(required, table => Assert.Contains(table, tables));

        await reader.DisposeAsync();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(6, version);
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

    [Fact]
    public async Task AuditLog_ListRecentAsync_FiltersSortsAndBoundsServerActivity()
    {
        var options = new SqliteStorageOptions(_root);
        var factory = new SqliteConnectionFactory(options);
        var database = new SqliteApplicationDatabase(options, factory);
        await database.InitializeAsync();
        var store = new SqliteAuditLogStore(factory);
        var serverId = Guid.NewGuid();

        for (var index = 0; index < 60; index++)
        {
            await store.WriteAsync(
                "Agent",
                $"Action{index:00}",
                index % 2 == 0
                    ? $"Server:{serverId}"
                    : $"Server:{serverId}:Backup",
                true,
                $"Detail {index}");
        }

        await store.WriteAsync(
            "Agent",
            "OtherServerAction",
            $"Server:{Guid.NewGuid()}",
            true);
        var items = await store.ListRecentAsync($"Server:{serverId}", 100);

        Assert.Equal(50, items.Count);
        Assert.DoesNotContain(items, item => item.Action == "OtherServerAction");
        Assert.True(items.Zip(items.Skip(1), (left, right) => left.Id > right.Id).All(value => value));
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
