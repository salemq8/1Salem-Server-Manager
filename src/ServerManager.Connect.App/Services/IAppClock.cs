namespace ServerManager.Connect.App.Services;

/// <summary>
/// Time for polling and ticket refresh. View models wait through this instead of
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> so tests control time and can drive one
/// step at a time.
/// </summary>
public interface IAppClock
{
    DateTimeOffset UtcNow { get; }

    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemAppClock : IAppClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
