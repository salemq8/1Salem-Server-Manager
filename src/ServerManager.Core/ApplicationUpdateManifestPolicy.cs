using ServerManager.Contracts;

namespace ServerManager.Core;

public static class ApplicationUpdateManifestPolicy
{
    public static void Validate(
        ApplicationUpdateManifest manifest,
        ApplicationUpdateChannel expectedChannel,
        bool allowLoopbackTestSources = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.MinimumSupportedVersion) ||
            string.IsNullOrWhiteSpace(manifest.ReleaseChannel) ||
            string.IsNullOrWhiteSpace(manifest.PackageUrl) ||
            string.IsNullOrWhiteSpace(manifest.ReleaseNotesUrl) ||
            string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            throw new InvalidDataException(
                "The update manifest is missing a required field.");
        }

        _ = SemanticVersion.Parse(manifest.Version);
        _ = SemanticVersion.Parse(manifest.MinimumSupportedVersion);
        if (SemanticVersion.Parse(manifest.Version).CompareTo(
                SemanticVersion.Parse("1.5")) >= 0 &&
            manifest.BuildRevision <= 0)
        {
            throw new InvalidDataException(
                "The update manifest build revision must be a positive integer.");
        }

        var channel = expectedChannel.ToString();
        if (!manifest.ReleaseChannel.Equals(channel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The manifest channel '{manifest.ReleaseChannel}' does not match '{channel}'.");
        }

        ValidateUri(manifest.PackageUrl, allowLoopbackTestSources, "package");
        ValidateUri(manifest.ReleaseNotesUrl, allowLoopbackTestSources, "release notes");
        if (manifest.PackageSize <= 0)
        {
            throw new InvalidDataException("The update package size must be positive.");
        }

        if (manifest.Sha256.Length != 64 ||
            !manifest.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The update package SHA-256 is invalid.");
        }

        if (manifest.PublishedAt == default)
        {
            throw new InvalidDataException("The manifest publication time is missing.");
        }

        if (!manifest.RequiresServiceRestart &&
            !string.Equals(
                manifest.AgentUpdateMode,
                "StageIfBusy",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                manifest.AgentUpdateMode,
                "SafeRestartAndReadopt",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "An Agent package must restart the service or declare StageIfBusy.");
        }
    }

    private static void ValidateUri(
        string value,
        bool allowLoopbackTestSources,
        string purpose)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException($"The {purpose} URL is invalid.");
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (allowLoopbackTestSources &&
            uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            uri.IsLoopback)
        {
            return;
        }

        throw new InvalidDataException($"The {purpose} URL must use HTTPS.");
    }
}
