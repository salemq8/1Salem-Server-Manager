using System.Security.Cryptography;

namespace ServerManager.Setup.Tests;

public sealed class FixedVersionPolicyTests
{
    [Fact]
    public void VisibleVersionAndBuildIdentity_AreSeparate()
    {
        var root = FindRepositoryRoot();

        Assert.Equal("1.5", File.ReadAllText(Path.Combine(root, "VERSION")).Trim());
        Assert.Equal("1", File.ReadAllText(Path.Combine(root, "BUILD_REVISION")).Trim());
        var info = File.ReadAllText(Path.Combine(root, "build-info.json"));
        Assert.Contains("\"productVersion\": \"1.5\"", info, StringComparison.Ordinal);
        Assert.Contains("\"buildRevision\": 1", info, StringComparison.Ordinal);
        Assert.DoesNotContain("1.5.1", info, StringComparison.Ordinal);
    }

    [Fact]
    public void NextBuild_ChangesBuildRevisionOnly()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "tools", "next-build.ps1"));

        Assert.Contains("Next internal build", script, StringComparison.Ordinal);
        Assert.Contains("Join-Path $root 'BUILD_REVISION'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content -LiteralPath (Join-Path $root 'VERSION')", script, StringComparison.Ordinal);
        Assert.DoesNotContain("1.5.1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductVersionTool_RequiresSalemAuthorizationPhrase()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "tools", "next-version.ps1"));

        Assert.Contains("Change the product version.", script, StringComparison.Ordinal);
        Assert.Contains("VERSION is locked", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RollingReleasePath_DoesNotInventPatchDirectories()
    {
        var root = FindRepositoryRoot();
        var build = File.ReadAllText(Path.Combine(root, "tools", "build-release.ps1"));
        var promote = File.ReadAllText(Path.Combine(root, "tools", "promote-release.ps1"));

        Assert.Contains("$canonicalReleaseRoot = Join-Path $releaseBase $releaseVersion", build, StringComparison.Ordinal);
        Assert.Contains("release-candidates", build, StringComparison.Ordinal);
        Assert.Contains("release\\$version", promote, StringComparison.Ordinal);
        Assert.DoesNotContain("1.5.1", build, StringComparison.Ordinal);
        Assert.DoesNotContain("1.5.1", promote, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalVersion132Package_RemainsUnchanged()
    {
        var root = FindRepositoryRoot();
        var package = Path.Combine(
            root,
            "artifacts",
            "release",
            "1.3.2",
            "1SalemServerManager-Update-1.3.2.zip");

        Assert.True(File.Exists(package), package);
        Assert.Equal(
            "63EE0470B75768B63C8D277FC9FA0F5C3627C760B221717A3D37234862CCB562",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))));
    }

    [Fact]
    public void StableUpdatesRemainSetupFreeAndDataSafe()
    {
        var root = FindRepositoryRoot();
        var release = File.ReadAllText(Path.Combine(root, "tools", "build-release.ps1"));
        var updater = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ServerManager.Infrastructure",
            "Updates",
            "VersionedUpdateInstaller.cs"));

        Assert.Contains("requiresFullSetup = $false", release, StringComparison.Ordinal);
        Assert.Contains("EnsureSeparateRoots", updater, StringComparison.Ordinal);
        Assert.Contains("SnapshotDirectory", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveGames", updater, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("playit.toml", updater, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "VERSION")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
