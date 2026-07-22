using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class ScheduleExpressionTests
{
    [Theory]
    [InlineData("@hourly", 60)]
    [InlineData("every:02:00:00", 120)]
    public void GetNextRun_ParsesSafeIntervals(string expression, int minutes)
    {
        var from = DateTimeOffset.UtcNow;

        Assert.Equal(from.AddMinutes(minutes), ScheduleExpression.GetNextRun(expression, from));
    }

    [Fact]
    public void GetNextRun_RejectsSubMinuteLoop()
    {
        Assert.Throws<FormatException>(
            () => ScheduleExpression.GetNextRun("every:00:00:05", DateTimeOffset.UtcNow));
    }
}

