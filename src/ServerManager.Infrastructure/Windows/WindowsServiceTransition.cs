namespace ServerManager.Infrastructure.Windows;

/// <summary>
/// Thrown when a service does not reach the requested state within its timeout. Callers treat
/// this as "do not proceed": in the updater it specifically means the Agent installation
/// directory must be left alone, because the service may still be holding files in it.
/// </summary>
public sealed class WindowsServiceTransitionException(
    string serviceName,
    string requestedAction,
    WindowsServiceState lastObservedState,
    TimeSpan elapsed,
    TimeSpan timeout)
    : Exception(
        $"The {serviceName} service did not reach the state required by '{requestedAction}'. " +
        $"Last observed state: {lastObservedState}; waited {elapsed.TotalSeconds:F1}s of a " +
        $"{timeout.TotalSeconds:F0}s timeout.")
{
    public string ServiceName { get; } = serviceName;

    public string RequestedAction { get; } = requestedAction;

    public WindowsServiceState LastObservedState { get; } = lastObservedState;

    public TimeSpan Elapsed { get; } = elapsed;

    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>
/// Drives a Windows service to a requested terminal state and waits for the Service Control
/// Manager to actually report it. Both operations are idempotent: a service already in the
/// requested state returns immediately, and one already transitioning toward it is waited on
/// rather than issued a second, redundant control.
/// </summary>
public sealed class WindowsServiceTransition(
    IWindowsServiceControl control,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<DateTimeOffset>? utcNow = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        delay ?? ((duration, token) => Task.Delay(duration, token));
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Ensures the service is genuinely Stopped, requesting a stop only if it is not already
    /// stopping. A service that is absent counts as stopped -- there is nothing holding files.
    /// </summary>
    public Task<WindowsServiceState> EnsureStoppedAsync(
        string serviceName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            serviceName,
            "stop",
            timeout,
            state => state is WindowsServiceState.Stopped or WindowsServiceState.NotInstalled,
            state => state is WindowsServiceState.StopPending,
            control.RequestStop,
            cancellationToken);

    /// <summary>
    /// Ensures the service is genuinely Running, requesting a start only if it is not already
    /// starting. A service already Running is left alone rather than started again.
    /// </summary>
    public Task<WindowsServiceState> EnsureRunningAsync(
        string serviceName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            serviceName,
            "start",
            timeout,
            state => state is WindowsServiceState.Running,
            state => state is WindowsServiceState.StartPending or WindowsServiceState.ContinuePending,
            control.RequestStart,
            cancellationToken);

    private async Task<WindowsServiceState> TransitionAsync(
        string serviceName,
        string requestedAction,
        TimeSpan timeout,
        Func<WindowsServiceState, bool> isSatisfied,
        Func<WindowsServiceState, bool> isAlreadyTransitioning,
        Action<string> requestChange,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        var started = _utcNow();
        var deadline = started + timeout;
        var requested = false;
        var state = control.GetState(serviceName);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isSatisfied(state))
            {
                return state;
            }

            if (!requested && !isAlreadyTransitioning(state))
            {
                requestChange(serviceName);
                requested = true;
            }

            if (_utcNow() >= deadline)
            {
                throw new WindowsServiceTransitionException(
                    serviceName,
                    requestedAction,
                    state,
                    _utcNow() - started,
                    timeout);
            }

            await _delay(PollInterval, cancellationToken);
            state = control.GetState(serviceName);
        }
    }
}
