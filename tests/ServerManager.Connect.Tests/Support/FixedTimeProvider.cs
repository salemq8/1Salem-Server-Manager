namespace ServerManager.Connect.Tests.Support;

/// <summary>A clock the test moves by hand, so time-window checks are exact and repeatable.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FixedTimeProvider(long unixSeconds)
    {
        _now = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    public long UnixSeconds => _now.ToUnixTimeSeconds();

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
