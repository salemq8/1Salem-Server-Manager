using System.Globalization;

namespace ServerManager.Core;

public readonly record struct SemanticVersion(
    int Major,
    int Minor,
    int Patch,
    string? Prerelease = null,
    int CoreComponents = 3) : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new FormatException($"'{value}' is not a valid semantic version.");
        }

        return version;
    }

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v'))
        {
            normalized = normalized[1..];
        }

        var buildIndex = normalized.IndexOf('+');
        if (buildIndex >= 0)
        {
            normalized = normalized[..buildIndex];
        }

        var prereleaseIndex = normalized.IndexOf('-');
        var prerelease = prereleaseIndex >= 0
            ? normalized[(prereleaseIndex + 1)..]
            : null;
        var core = prereleaseIndex >= 0 ? normalized[..prereleaseIndex] : normalized;
        var parts = core.Split('.');
        var patch = 0;
        if (parts.Length is not (2 or 3) ||
            !TryPart(parts[0], out var major) ||
            !TryPart(parts[1], out var minor) ||
            (parts.Length == 3 && !TryPart(parts[2], out patch)) ||
            (prerelease is not null && !IsValidPrerelease(prerelease)))
        {
            return false;
        }

        version = new SemanticVersion(
            major,
            minor,
            parts.Length == 3 ? patch : 0,
            prerelease,
            parts.Length);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core == 0)
        {
            core = Minor.CompareTo(other.Minor);
        }

        if (core == 0)
        {
            core = Patch.CompareTo(other.Patch);
        }

        if (core != 0)
        {
            return core;
        }

        if (Prerelease is null)
        {
            return other.Prerelease is null ? 0 : 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            if (index >= left.Length)
            {
                return -1;
            }

            if (index >= right.Length)
            {
                return 1;
            }

            var comparison = CompareIdentifier(left[index], right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    public override string ToString() =>
        (CoreComponents == 2 && Patch == 0
            ? $"{Major}.{Minor}"
            : $"{Major}.{Minor}.{Patch}") +
        (Prerelease is null ? string.Empty : $"-{Prerelease}");

    private static bool TryPart(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) &&
        result >= 0 &&
        (value == "0" || !value.StartsWith('0'));

    private static bool IsValidPrerelease(string value) =>
        value.Length > 0 &&
        value.Split('.').All(identifier =>
            identifier.Length > 0 &&
            identifier.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '-'));

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = int.TryParse(
            left,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var leftNumber);
        var rightNumeric = int.TryParse(
            right,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var rightNumber);
        if (leftNumeric && rightNumeric)
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }
}
