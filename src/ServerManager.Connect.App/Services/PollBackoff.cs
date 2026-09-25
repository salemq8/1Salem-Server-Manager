namespace ServerManager.Connect.App.Services;

/// <summary>
/// Doubling poll interval with a ceiling. Approval can take the owner minutes or hours, so the
/// app asks often at first and then settles at a rate the broker's per-device limits tolerate.
/// </summary>
public sealed class PollBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _maximum;
    private TimeSpan _next;

    public PollBackoff(TimeSpan initial, TimeSpan maximum)
    {
        if (initial <= TimeSpan.Zero || maximum < initial)
        {
            throw new ArgumentOutOfRangeException(nameof(initial), "Expected 0 < initial <= maximum.");
        }

        _initial = initial;
        _maximum = maximum;
        _next = initial;
    }

    public TimeSpan Next()
    {
        var current = _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, _maximum.Ticks));
        return current;
    }

    public void Reset() => _next = _initial;
}
