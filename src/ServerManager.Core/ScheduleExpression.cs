using System.Globalization;

namespace ServerManager.Core;

public static class ScheduleExpression
{
    public static DateTimeOffset GetNextRun(string expression, DateTimeOffset fromUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        if (expression.Equals("@hourly", StringComparison.OrdinalIgnoreCase))
        {
            return fromUtc.AddHours(1);
        }

        if (expression.Equals("@daily", StringComparison.OrdinalIgnoreCase))
        {
            return fromUtc.AddDays(1);
        }

        const string prefix = "every:";
        if (expression.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            TimeSpan.TryParse(
                expression[prefix.Length..],
                CultureInfo.InvariantCulture,
                out var interval) &&
            interval >= TimeSpan.FromMinutes(1))
        {
            return fromUtc.Add(interval);
        }

        throw new FormatException(
            "Schedule expressions must be @hourly, @daily, or every:HH:mm:ss with at least one minute.");
    }
}
