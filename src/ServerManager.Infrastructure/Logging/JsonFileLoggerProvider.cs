using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ServerManager.Infrastructure.Logging;

public sealed class JsonFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly JsonFileLoggerOptions _options;
    private readonly object _sync = new();
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private bool _disposed;

    public JsonFileLoggerProvider(JsonFileLoggerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.FilePath));
        Directory.CreateDirectory(directory!);
    }

    public ILogger CreateLogger(string categoryName) =>
        new JsonFileLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private void Write<TState>(
        string category,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var (key, value) in values)
            {
                if (key != "{OriginalFormat}")
                {
                    properties[key] = value;
                }
            }
        }

        var scopes = new List<object?>();
        _scopeProvider.ForEachScope((scope, target) => target.Add(scope), scopes);

        var entry = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            level = logLevel.ToString(),
            category,
            eventId = eventId.Id,
            eventName = eventId.Name,
            message = formatter(state, exception),
            exception = exception?.ToString(),
            properties,
            scopes
        };
        var json = JsonSerializer.Serialize(entry);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RollIfRequired();
            File.AppendAllText(_options.FilePath, json + Environment.NewLine);
        }
    }

    private void RollIfRequired()
    {
        var file = new FileInfo(_options.FilePath);
        if (!file.Exists || file.Length < _options.MaximumFileBytes)
        {
            return;
        }

        var overflow = $"{_options.FilePath}.{_options.RetainedFileCount}";
        if (File.Exists(overflow))
        {
            File.Delete(overflow);
        }

        for (var index = _options.RetainedFileCount - 1; index >= 1; index--)
        {
            var source = $"{_options.FilePath}.{index}";
            var destination = $"{_options.FilePath}.{index + 1}";
            if (File.Exists(source))
            {
                File.Move(source, destination, true);
            }
        }

        File.Move(_options.FilePath, $"{_options.FilePath}.1", true);
    }

    private sealed class JsonFileLogger(
        string category,
        JsonFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            provider._scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, eventId, state, exception, formatter);
            }
        }
    }
}

