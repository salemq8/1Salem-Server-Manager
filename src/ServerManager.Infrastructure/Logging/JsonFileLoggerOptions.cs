namespace ServerManager.Infrastructure.Logging;

public sealed record JsonFileLoggerOptions(
    string FilePath,
    long MaximumFileBytes = 10 * 1024 * 1024,
    int RetainedFileCount = 5);

