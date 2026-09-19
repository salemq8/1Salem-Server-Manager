using System.Security.Cryptography;

namespace ServerManager.Setup.Tests;

public sealed class FixedVersionPolicyTests
{
    [Fact]
    public void VisibleVersionAndBuildIdentity_AreSeparate()
    {
        var root = FindRepositoryRoot();

        Assert.Equal("1.5", File.ReadAllText(Path.Combine(root, "VERSION")).Trim());
        Assert.Equal("4", File.ReadAllText(Path.Combine(root, "BUILD_REVISION")).Trim());
        var info = File.ReadAllText(Path.Combine(root, "build-info.json"));
        Assert.Contains("\"productVersion\": \"1.5\"", info, StringComparison.Ordinal);
        Assert.Contains("\"buildRevision\": 4", info, StringComparison.Ordinal);
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
    public void ProductVersionTool_ProducesTwoPartOutputAcceptedByTheBuildValidator()
    {
        // Directory.Build.props's ValidateProductVersion target requires VERSION to match
        // `^\d+\.\d+$` (exactly two parts). next-version.ps1 previously computed a three-part
        // Major.Minor.Build value (including a "Patch" option that has no meaning under the
        // fixed-version policy, where patch-level change is BUILD_REVISION's job) -- a value
        // that this tool itself would happily write to VERSION, but that would then fail the
        // very next build. This asserts the generator and the validator agree.
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "tools", "next-version.ps1"));
        var propsPath = Path.Combine(root, "Directory.Build.props");
        var props = File.ReadAllText(propsPath);

        Assert.DoesNotContain("'Patch'", script, StringComparison.Ordinal);
        Assert.Contains("ValidateSet('Minor', 'Major')", script, StringComparison.Ordinal);
        Assert.Contains("'{0}.{1}' -f $next.Major, $next.Minor", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$next.Build", script, StringComparison.Ordinal);

        // The regex the two must agree on: pull it directly from Directory.Build.props rather
        // than hardcoding a copy here, so this test breaks if the validator itself ever changes.
        var match = System.Text.RegularExpressions.Regex.Match(
            props,
            @"IsMatch\('\$\(ProductVersion\)', '(?<pattern>[^']+)'\)");
        Assert.True(match.Success, "Could not find ValidateProductVersion's regex in Directory.Build.props.");
        var validator = new System.Text.RegularExpressions.Regex(match.Groups["pattern"].Value);

        Assert.Matches(validator, "1.5");
        Assert.Matches(validator, "1.6");
        Assert.Matches(validator, "2.0");
        Assert.DoesNotMatch(validator, "1.5.1");
        Assert.DoesNotMatch(validator, "1.5.0");
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
        // This artifact lives under the git-ignored `artifacts/` tree, so it only exists on a
        // machine that has already built the 1.3.2 release locally -- never on a clean
        // checkout or a fresh CI runner. Skip rather than fail when it's absent, matching the
        // sibling check in Version132ReleaseWorkflowTests.ImportantPreviousUpdatePackages_
        // RemainByteForByteUnchanged, which already uses this pattern.
        var root = FindRepositoryRoot();
        var package = Path.Combine(
            root,
            "artifacts",
            "release",
            "1.3.2",
            "1SalemServerManager-Update-1.3.2.zip");
        if (!File.Exists(package))
        {
            return;
        }

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
