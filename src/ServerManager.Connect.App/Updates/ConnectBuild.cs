using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ServerManager.Connect.App.Updates;

/// <summary>
/// A release of 1Salem Connect as the fixed-version policy names it: the visible product version
/// (two parts, such as "1.5") and the build revision that increases with every release. They are
/// compared as a pair, version first and then build, and never folded into one dotted number:
/// "1.5 Build 12" is not "1.5.12".
/// </summary>
public readonly partial record struct ConnectBuild : IComparable<ConnectBuild>
{
    private ConnectBuild(string productVersion, int major, int minor, int buildRevision)
    {
        ProductVersion = productVersion;
        Major = major;
        Minor = minor;
        BuildRevision = buildRevision;
    }

    public string ProductVersion { get; }

    public int Major { get; }

    public int Minor { get; }

    public int BuildRevision { get; }

    /// <summary>The running app, from the attributes Directory.Build.props stamps (VERSION and BUILD_REVISION).</summary>
    public static ConnectBuild Current { get; } = FromAssembly(typeof(ConnectBuild).Assembly);

    public static bool TryCreate(string? productVersion, int buildRevision, out ConnectBuild build)
    {
        build = default;
        if (productVersion is null || buildRevision < 1)
        {
            return false;
        }

        var match = ProductVersionPattern().Match(productVersion);
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        build = new ConnectBuild(productVersion, major, minor, buildRevision);
        return true;
    }

    public static ConnectBuild Create(string productVersion, int buildRevision) =>
        TryCreate(productVersion, buildRevision, out var build)
            ? build
            : throw new ArgumentException($"'{productVersion}' build {buildRevision} is not a valid release.");

    public int CompareTo(ConnectBuild other)
    {
        var byVersion = (Major, Minor).CompareTo((other.Major, other.Minor));
        return byVersion != 0 ? byVersion : BuildRevision.CompareTo(other.BuildRevision);
    }

    public static bool operator >(ConnectBuild left, ConnectBuild right) => left.CompareTo(right) > 0;

    public static bool operator <(ConnectBuild left, ConnectBuild right) => left.CompareTo(right) < 0;

    public static bool operator >=(ConnectBuild left, ConnectBuild right) => left.CompareTo(right) >= 0;

    public static bool operator <=(ConnectBuild left, ConnectBuild right) => left.CompareTo(right) <= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{ProductVersion} (build {BuildRevision})");

    internal static ConnectBuild FromAssembly(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var revision = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildRevision")?.Value;

        // A development build without the stamp still runs; it is simply never offered as current
        // over a real release (build 0 is below every published build).
        return int.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var build) &&
               TryCreate(version, build, out var current)
            ? current
            : new ConnectBuild(version ?? "0.0", 0, 0, 0);
    }

    [GeneratedRegex(@"^(?<major>\d{1,4})\.(?<minor>\d{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductVersionPattern();
}
