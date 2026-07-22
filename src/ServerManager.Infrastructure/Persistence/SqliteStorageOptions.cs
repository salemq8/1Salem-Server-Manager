namespace ServerManager.Infrastructure.Persistence;

public sealed record SqliteStorageOptions
{
    public SqliteStorageOptions(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("A data root is required.", nameof(dataRoot));
        }

        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DataRoot { get; }

    public string DatabasePath => Path.Combine(DataRoot, "data", "server-manager.db");

    public string LogsRoot => Path.Combine(DataRoot, "logs");
}

