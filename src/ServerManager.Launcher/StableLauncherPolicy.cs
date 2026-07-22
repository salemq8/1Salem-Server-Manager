using ServerManager.Contracts;

namespace ServerManager.Launcher;

public static class StableLauncherPolicy
{
    public static string ResolveClientPath(
        string installRoot,
        InstalledApplicationManifest manifest,
        Func<string, string?>? versionReader = null,
        Func<string, int>? buildRevisionReader = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.ReleaseChannel.Equals(
                ProductIdentity.StableChannel,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The Stable launcher will not start a Preview or Development channel build.");
        }

        if (manifest.ActiveVersion.Contains('-', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Stable launcher will not start a prerelease build.");
        }

        var root = Path.GetFullPath(installRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(manifest.ClientExecutablePath);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(candidate))
        {
            throw new InvalidDataException(
                "The selected Client path is missing or outside the installation root.");
        }

        var detected = (versionReader ?? InstalledVersionDetector.ReadProductVersion)(candidate);
        if (!string.Equals(detected, manifest.ActiveVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The selected Client reports {detected ?? "no version"}, expected {manifest.ActiveVersion}.");
        }

        if (manifest.ActiveBuildRevision > 0)
        {
            var detectedBuild = (buildRevisionReader ??
                InstalledVersionDetector.ReadBuildRevision)(candidate);
            if (detectedBuild != manifest.ActiveBuildRevision)
            {
                throw new InvalidDataException(
                    $"The selected Client reports Build {detectedBuild}, expected Build {manifest.ActiveBuildRevision}.");
            }
        }

        return candidate;
    }
}
