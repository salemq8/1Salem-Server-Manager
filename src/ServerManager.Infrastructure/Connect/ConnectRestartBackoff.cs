namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// Capped exponential restart delay: initial, 2×, 4×, … up to the maximum, and back to the
/// initial delay after a healthy run. A sidecar that fails at once (a port in use, a missing
/// file) is retried at a pace that cannot flood the log or the CPU.
/// </summary>
internal sealed class ConnectRestartBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _maximum;
    private TimeSpan _next;

    public ConnectRestartBackoff(TimeSpan initial, TimeSpan maximum)
    {
        if (initial <= TimeSpan.Zero || maximum < initial)
        {
            throw new ArgumentOutOfRangeException(nameof(initial), "The initial delay must be positive and not above the maximum.");
        }

        _initial = initial;
        _maximum = maximum;
        _next = initial;
    }

    public TimeSpan Next()
    {
        var delay = _next;
        _next = _next >= _maximum / 2 ? _maximum : _next * 2;
        return delay;
    }

    public void Reset() => _next = _initial;
}
