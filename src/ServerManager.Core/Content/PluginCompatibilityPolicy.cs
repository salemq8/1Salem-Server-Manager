using ServerManager.Contracts;

namespace ServerManager.Core.Content;

/// <summary>
/// Decides which release actually fits the server in front of us. The rule that matters:
/// install the newest release compatible with this server, which is not the same as the
/// project's newest release.
/// </summary>
public static class PluginCompatibilityPolicy
{
    /// <summary>
    /// A version fits when the server's platform is among its loaders, the server's exact
    /// Minecraft version is among its game versions, and the provider hosts the file itself.
    /// Both providers list exact version strings, so this is a membership test, not a range.
    /// </summary>
    public static bool IsCompatible(
        ContentVersion version,
        ServerContentProfile profile,
        ContentKind kind = ContentKind.Plugin)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(profile);
        if (version.File is null)
        {
            return false;
        }

        // Plugins need a plugin-capable platform; data and resource packs are vanilla
        // features, so they only need a known Minecraft version.
        if (kind == ContentKind.Plugin && !profile.SupportsPlugins)
        {
            return false;
        }

        return MatchesPlatform(version, profile.Platform, kind) &&
               MatchesGameVersion(version, profile.MinecraftVersion);
    }

    public static bool MatchesPlatform(
        ContentVersion version,
        ServerPlatform platform,
        ContentKind kind = ContentKind.Plugin)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Platforms.Count == 0)
        {
            // A version that names no platform proves nothing, so it is not offered.
            return false;
        }

        var accepted = version.Provider == ContentProviderId.Hangar
            ? Single(PluginPlatformPolicy.HangarPlatform(platform))
            : ContentTypePolicy.ModrinthLoaders(kind, platform);
        return accepted.Any(name => version.Platforms.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An unknown server Minecraft version cannot be matched honestly, so nothing is
    /// considered compatible rather than installing something that may not load.
    /// </summary>
    public static bool MatchesGameVersion(ContentVersion version, string? minecraftVersion)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (string.IsNullOrWhiteSpace(minecraftVersion) || version.GameVersions.Count == 0)
        {
            return false;
        }

        return version.GameVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The newest compatible release. Stable releases win over beta and alpha even when a
    /// pre-release is newer; pre-releases are only considered when explicitly allowed.
    /// </summary>
    public static ContentVersion? SelectBest(
        IEnumerable<ContentVersion> versions,
        ServerContentProfile profile,
        bool allowPrerelease = false,
        ContentKind kind = ContentKind.Plugin)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(profile);
        var compatible = versions
            .Where(version => IsCompatible(version, profile, kind))
            .Where(version => allowPrerelease || version.Channel == ContentReleaseChannel.Release)
            .ToArray();
        if (compatible.Length == 0)
        {
            return null;
        }

        return compatible
            .OrderBy(version => version.Channel)
            .ThenByDescending(version => version.PublishedAtUtc ?? DateTimeOffset.MinValue)
            .First();
    }

    /// <summary>
    /// True when the candidate is a different release from what is installed. Version numbers
    /// across providers have no shared ordering, so identity is by version id, and the
    /// candidate has already been filtered for compatibility before it gets here.
    /// </summary>
    public static bool IsUpdate(ContentVersion candidate, InstalledContent installed)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(installed);
        if (installed.VersionId is null || installed.Provider != candidate.Provider)
        {
            return false;
        }

        if (string.Equals(installed.VersionId, candidate.VersionId, StringComparison.Ordinal))
        {
            return false;
        }

        return candidate.PublishedAtUtc is null ||
               installed.InstalledAtUtc is null ||
               candidate.PublishedAtUtc > installed.InstalledAtUtc.Value.AddSeconds(-1);
    }

    private static IReadOnlyList<string> Single(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [value];
}
