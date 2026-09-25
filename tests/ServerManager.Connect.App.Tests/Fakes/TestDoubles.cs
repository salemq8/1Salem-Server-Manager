using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Transport;

namespace ServerManager.Connect.App.Tests.Fakes;

/// <summary>
/// A clock the test sets by hand. Its delays never finish on their own (only when cancelled), so a
/// page's background loop parks and the test drives each step itself.
/// </summary>
internal sealed class ManualClock : IAppClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
}

internal sealed class FakeTransportProcess : ITransportProcess
{
    public int EnsureCalls { get; private set; }

    public int StopCalls { get; private set; }

    public bool UsedTransportsExited { get; set; }

    /// <summary>Runs inside <see cref="Stop"/>, so a test can see what had happened by then.</summary>
    public Action? Stopping { get; set; }

    public Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        EnsureCalls++;
        return Task.CompletedTask;
    }

    public void Stop()
    {
        StopCalls++;
        Stopping?.Invoke();
    }
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public bool TrySetText(string text)
    {
        Text = text;
        return true;
    }
}
