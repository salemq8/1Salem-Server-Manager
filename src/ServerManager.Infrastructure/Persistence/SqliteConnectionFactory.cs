using Microsoft.Data.Sqlite;

namespace ServerManager.Infrastructure.Persistence;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 10
        }.ToString();
    }

    public SqliteConnection Create() => new(_connectionString);
}

