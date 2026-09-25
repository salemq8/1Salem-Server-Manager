using System.Globalization;
using ServerManager.Connect.Core.Diagnostics;

namespace ServerManager.Connect.App.Services;

/// <summary>
/// The most recent errors, for the Diagnostics page. Lines are redacted as they are recorded, so
/// a secret that reaches an exception message by mistake is never held here in the clear.
/// </summary>
public sealed class DiagnosticsLog
{
    private const int Capacity = 50;
    private const int MaxInnerExceptions = 3;

    private readonly IAppClock _clock;
    private readonly object _gate = new();
    private readonly Queue<string> _entries = new();

    public DiagnosticsLog(IAppClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public void Record(string area, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = $"{exception.GetType().Name}: {exception.Message}";
        var inner = exception.InnerException;
        for (var depth = 0; inner is not null && depth < MaxInnerExceptions; depth++, inner = inner.InnerException)
        {
            message += $" <- {inner.GetType().Name}: {inner.Message}";
        }

        Record(area, message);
    }

    public void Record(string area, string message)
    {
        var line = SecretRedactor.Redact(
            $"{_clock.UtcNow.ToString("u", CultureInfo.InvariantCulture)} {area}: {message}");
        lock (_gate)
        {
            _entries.Enqueue(line);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }
}
