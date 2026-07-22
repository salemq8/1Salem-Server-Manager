namespace ServerManager.Core;

public sealed record ReleaseVersionDecision(
    bool Allowed,
    SemanticVersion Target,
    SemanticVersion? HighestKnown,
    string Message);

public static class ReleaseVersionPolicy
{
    public static ReleaseVersionDecision Evaluate(
        string targetVersion,
        IEnumerable<string> knownVersions,
        bool rebuildSameVersion = false)
    {
        var target = SemanticVersion.Parse(targetVersion);
        var parsed = knownVersions
            .Select(value => SemanticVersion.TryParse(value, out var version)
                ? (SemanticVersion?)version
                : null)
            .Where(version => version.HasValue)
            .Select(version => version!.Value)
            .ToArray();
        var highest = parsed.Length == 0 ? (SemanticVersion?)null : parsed.Max();
        if (highest is null)
        {
            return new ReleaseVersionDecision(
                true,
                target,
                null,
                $"Release {target} is newer than every detected installed/released version.");
        }

        var comparison = target.CompareTo(highest.Value);
        if (comparison > 0 || (comparison == 0 && rebuildSameVersion))
        {
            return new ReleaseVersionDecision(
                true,
                target,
                highest,
                comparison == 0
                    ? $"Developer same-version rebuild explicitly enabled for {target}."
                    : $"Release {target} is newer than highest known version {highest}.");
        }

        var next = new SemanticVersion(
            highest.Value.Major,
            highest.Value.Minor,
            highest.Value.Patch + 1);
        return new ReleaseVersionDecision(
            false,
            target,
            highest,
            $"Release blocked:{Environment.NewLine}" +
            $"Target {target} is not newer than installed/released version {highest}. " +
            $"Choose at least {next}.");
    }

    public static SemanticVersion Next(string highestVersion, string part)
    {
        var highest = SemanticVersion.Parse(highestVersion);
        return part.ToUpperInvariant() switch
        {
            "PATCH" => new SemanticVersion(highest.Major, highest.Minor, highest.Patch + 1),
            "MINOR" => new SemanticVersion(highest.Major, highest.Minor + 1, 0),
            "MAJOR" => new SemanticVersion(highest.Major + 1, 0, 0),
            _ => throw new ArgumentOutOfRangeException(
                nameof(part),
                part,
                "Part must be Patch, Minor, or Major.")
        };
    }
}
