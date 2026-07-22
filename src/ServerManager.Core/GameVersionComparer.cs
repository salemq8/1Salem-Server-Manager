using System.Text.RegularExpressions;

namespace ServerManager.Core;

public static partial class GameVersionComparer
{
    public static int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var leftParts = VersionPartRegex().Matches(left);
        var rightParts = VersionPartRegex().Matches(right);
        var count = Math.Max(leftParts.Count, rightParts.Count);
        for (var index = 0; index < count; index++)
        {
            if (index >= leftParts.Count)
            {
                return -1;
            }

            if (index >= rightParts.Count)
            {
                return 1;
            }

            var leftPart = leftParts[index].Value;
            var rightPart = rightParts[index].Value;
            var leftNumeric = long.TryParse(leftPart, out var leftNumber);
            var rightNumeric = long.TryParse(rightPart, out var rightNumber);
            var comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    [GeneratedRegex("\\d+|[A-Za-z]+")]
    private static partial Regex VersionPartRegex();
}

