using ServerManager.Infrastructure.Windows;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// Covers the service-state machine the updater depends on. The defect these exist for is that
/// a successful stop *request* was previously treated as proof the service had stopped, so the
/// Agent directory could be replaced while the service was still STOP_PENDING and holding its
/// files open, and the recovery path could issue a start against a service that had not
/// finished stopping (ERROR_SERVICE_ALREADY_RUNNING / 1056).
/// </summary>
public sealed class WindowsServiceTransitionTests
{
    [Fact]
    public async Task Stop_RunningThroughStopPending_WaitsForStopped()
    {
        var control = new FakeServiceControl(
            WindowsServiceState.Running,
            WindowsServiceState.StopPending,
            WindowsServiceState.StopPending,
            WindowsServiceState.Stopped);
        var transition = CreateTransition(control);

        var final = await transition.EnsureStoppedAsync("agent", TimeSpan.FromSeconds(60));

        Assert.Equal(WindowsServiceState.Stopped, final);
        Assert.Equal(1, control.StopRequests);
    }

    [Fact]
    public async Task Stop_AlreadyStopPending_DoesNotIssueASecondStop()
    {
        var control = new FakeServiceControl(
            WindowsServiceState.StopPending,
            WindowsServiceState.StopPending,
            WindowsServiceState.Stopped);
        var transition = CreateTransition(control);

        var final = await transition.EnsureStoppedAsync("agent", TimeSpan.FromSeconds(60));

        Assert.Equal(WindowsServiceState.Stopped, final);
        Assert.Equal(0, control.StopRequests);
    }

    [Fact]
    public async Task Stop_AlreadyStopped_ReturnsImmediatelyWithoutRequesting()
    {
        var control = new FakeServiceControl(WindowsServiceState.Stopped);
        var transition = CreateTransition(control);

        var final = await transition.EnsureStoppedAsync("agent", TimeSpan.FromSeconds(60));

        Assert.Equal(WindowsServiceState.Stopped, final);
        Assert.Equal(0, control.StopRequests);
        Assert.Equal(0, control.StartRequests);
    }

    [Fact]
    public async Task Stop_ServiceNotInstalled_CountsAsStopped()
    {
        var control = new FakeServiceControl(WindowsServiceState.NotInstalled);
        var transition = CreateTransition(control);

        var final = await transition.EnsureStoppedAsync("agent", TimeSpan.FromSeconds(60));

        Assert.Equal(WindowsServiceState.NotInstalled, final);
        Assert.Equal(0, control.StopRequests);
    }

    [Fact]
    public async Task Stop_ThatNeverCompletes_ThrowsWithDiagnosticDetail()
    {
        var control = new FakeServiceControl(WindowsServiceState.Running)
        {
            StickyState = WindowsServiceState.StopPending
        };
        var transition = CreateTransition(control);

        var exception = await Assert.ThrowsAsync<WindowsServiceTransitionException>(
            () => transition.EnsureStoppedAsync("agent", TimeSpan.FromSeconds(60)));

        Assert.Equal("agent", exception.ServiceName);
        Assert.Equal("stop", exception.RequestedAction);
        Assert.Equal(WindowsServiceState.StopPending, exception.LastObservedState);
        Assert.Equal(TimeSpan.FromSeconds(60), exception.Timeout);
        Assert.True(exception.Elapsed >= TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Stop_WhenStateQueryFails_SurfacesTheFailure()
    {
        var control = new FakeServiceControl(WindowsServiceState.Running)
        {
            QueryFailure = new InvalidOperationException("query failed")
        };
        var transition = CreateTransition(control);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transition.EnsureStoppedAsync("agent", TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task Start_StoppedThroughStartPending_WaitsForRunning()
    {
        var control = new FakeServiceControl(
            WindowsServiceState.Stopped,
            WindowsServiceState.StartPending,
            WindowsServiceState.StartPending,
            WindowsServiceState.Running);
        var transition = CreateTransition(control);

        var final = await transition.EnsureRunningAsync("agent", TimeSpan.FromSeconds(60));

        Assert.Equal(WindowsServiceState.Running, final);
        Assert.Equal(1, control.StartRequests);
    }

    [Fact]
    public async Task Start_AlreadyStartPending_DoesNotIssueASecondStart()
    {
        var control = new FakeServiceControl(
            WindowsServiceState.StartPending,
            WindowsServiceState.StartPending,
            WindowsServiceState.Running);
        var transition = CreateTransition(control);

        var final = await transition.EnsureRunningAsync("agent", TimeSpan.FromSeconds(60));

        Assert.Equal(WindowsServiceState.Running, final);
        Assert.Equal(0, control.StartRequests);
    }

    [Fact]
    public async Task Start_AlreadyRunning_IsIdempotentAndIssuesNoStart()
    {
        var control = new FakeServiceControl(WindowsServiceState.Running);
        var transition = CreateTransition(control);

        var final = await transition.EnsureRunningAsync("agent", TimeSpan.FromSeconds(60));

        Assert.Equal(WindowsServiceState.Running, final);
        Assert.Equal(0, control.StartRequests);
    }

    [Fact]
    public async Task Start_ThatNeverCompletes_ThrowsWithDiagnosticDetail()
    {
        var control = new FakeServiceControl(WindowsServiceState.Stopped)
        {
            StickyState = WindowsServiceState.StartPending
        };
        var transition = CreateTransition(control);

        var exception = await Assert.ThrowsAsync<WindowsServiceTransitionException>(
            () => transition.EnsureRunningAsync("agent", TimeSpan.FromSeconds(60)));

        Assert.Equal("start", exception.RequestedAction);
        Assert.Equal(WindowsServiceState.StartPending, exception.LastObservedState);
    }

    private static WindowsServiceTransition CreateTransition(FakeServiceControl control)
    {
        // Deterministic: no real waiting, and the clock only advances when the state machine
        // actually polls, so timeouts are reached by poll count rather than wall time.
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new WindowsServiceTransition(
            control,
            (duration, _) =>
            {
                now = now.Add(duration);
                return Task.CompletedTask;
            },
            () => now);
    }
}

/// <summary>
/// Scripted service states. Each query returns the next scripted state; once the script runs
/// out it keeps returning the last one, or <see cref="StickyState"/> if set (used to model a
/// service that never finishes transitioning).
/// </summary>
internal sealed class FakeServiceControl(params WindowsServiceState[] script) : IWindowsServiceControl
{
    private readonly Queue<WindowsServiceState> _script = new(script);
    private WindowsServiceState _current =
        script.Length > 0 ? script[0] : WindowsServiceState.Stopped;

    public int StopRequests { get; private set; }

    public int StartRequests { get; private set; }

    public List<string> Operations { get; } = [];

    public WindowsServiceState? StickyState { get; init; }

    public Exception? QueryFailure { get; init; }

    public WindowsServiceState GetState(string serviceName)
    {
        if (QueryFailure is not null)
        {
            throw QueryFailure;
        }

        if (_script.Count > 0)
        {
            _current = _script.Dequeue();
        }
        else if (StickyState is { } sticky && (StopRequests > 0 || StartRequests > 0))
        {
            _current = sticky;
        }

        return _current;
    }

    public void RequestStop(string serviceName)
    {
        StopRequests++;
        Operations.Add("stop");
    }

    public void RequestStart(string serviceName)
    {
        StartRequests++;
        Operations.Add("start");
    }
}

/// <summary>
/// Behaves like a healthy service: a stop request lands in Stopped, a start request lands in
/// Running. Used where a test needs the updater's service handling to simply work.
/// </summary>
internal sealed class HealthyFakeServiceControl(
    WindowsServiceState initial = WindowsServiceState.Running) : IWindowsServiceControl
{
    private WindowsServiceState _state = initial;

    public List<string> Operations { get; } = [];

    public List<WindowsServiceState> StatesWhenQueried { get; } = [];

    public WindowsServiceState GetState(string serviceName)
    {
        StatesWhenQueried.Add(_state);
        return _state;
    }

    public void RequestStop(string serviceName)
    {
        Operations.Add("stop");
        _state = WindowsServiceState.Stopped;
    }

    public void RequestStart(string serviceName)
    {
        Operations.Add("start");
        _state = WindowsServiceState.Running;
    }
}
