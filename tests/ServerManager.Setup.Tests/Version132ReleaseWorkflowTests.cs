using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ServerManager.Setup.Tests;

public sealed partial class Version132ReleaseWorkflowTests
{
    [Fact]
    public void VersionFile_IsTheOnlyActiveProductVersionSource()
    {
        var root = FindRepositoryRoot();
        var version = File.ReadAllText(Path.Combine(root, "VERSION")).Trim();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));

        Assert.Equal("1.5", version);
        Assert.Equal("5", File.ReadAllText(Path.Combine(root, "BUILD_REVISION")).Trim());
        Assert.Contains("ReadAllText('$(VersionFile)').Trim()", props, StringComparison.Ordinal);
        Assert.Contains("<Version>$(ProductVersion)</Version>", props, StringComparison.Ordinal);
        Assert.Contains("<AssemblyVersion>$(ProductVersion).0.0</AssemblyVersion>", props, StringComparison.Ordinal);
        Assert.Contains("ReadAllText('$(BuildRevisionFile)').Trim()", props, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"<Version>\d+\.\d+(?:\.\d+)?</Version>", props);
    }

    [Fact]
    public void ActiveSource_HasNoUnexpectedHardCodedProductVersion()
    {
        var root = FindRepositoryRoot();
        var matches = new List<string>();
        foreach (var folder in new[] { "src", "tools" })
        {
            foreach (var file in Directory.EnumerateFiles(
                         Path.Combine(root, folder),
                         "*",
                         SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                foreach (Match match in ActiveProductVersion().Matches(text))
                {
                    var contextEnd = Math.Min(
                        text.Length,
                        match.Index + match.Length + 80);
                    var context = text[Math.Max(0, match.Index - 80)..contextEnd];
                    if (context.Contains("rollbackCompatibility", StringComparison.Ordinal) ||
                        context.Contains("new Oid", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matches.Add($"{Path.GetRelativePath(root, file)}: {match.Value}");
                }
            }
        }

        Assert.Empty(matches);
    }

    [Fact]
    public void BuiltProductAssemblies_MatchVersionFile()
    {
        var root = FindRepositoryRoot();
        var version = File.ReadAllText(Path.Combine(root, "VERSION")).Trim();
        var projects = new[]
        {
            (@"src\ServerManager.Client", "1Salem.ServerManager"),
            (@"src\ServerManager.Agent", "1Salem.ServerManager.Agent"),
            (@"src\ServerManager.Updater", "1Salem.ServerManager.Updater"),
            (@"src\ServerManager.Launcher", "1Salem.ServerManager.Launcher"),
            (@"tools\ServerManager.Setup", "1Salem.ServerManager.Setup")
        };
        foreach (var (projectDirectoryRelative, baseFileName) in projects)
        {
            var outputDirectory = ResolveReleaseOutputDirectory(root, projectDirectoryRelative);
            var path = Path.Combine(outputDirectory, $"{baseFileName}.exe");
            Assert.True(File.Exists(path), path);
            var info = FileVersionInfo.GetVersionInfo(path);
            Assert.Equal(version, info.ProductVersion);
            Assert.Equal($"{version}.0.0", info.FileVersion);
            Assert.Equal(
                new Version($"{version}.0.0"),
                System.Reflection.AssemblyName.GetAssemblyName(
                    Path.Combine(outputDirectory, $"{baseFileName}.dll")).Version);
        }
    }

    // Resolves the actual Release output directory from the project's own <TargetFramework>
    // instead of a hard-coded TFM directory, so a future TargetFramework change cannot
    // silently desynchronize this test from the real build output.
    private static string ResolveReleaseOutputDirectory(string root, string projectDirectoryRelative)
    {
        var projectDirectory = Path.Combine(root, projectDirectoryRelative);
        var csprojFile = Directory.EnumerateFiles(projectDirectory, "*.csproj").Single();
        var match = TargetFrameworkPattern().Match(File.ReadAllText(csprojFile));
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Could not resolve <TargetFramework> from {csprojFile}.");
        }

        return Path.Combine(projectDirectory, "bin", "Release", match.Groups["tfm"].Value);
    }

    [Fact]
    public void SetupAndUpdater_ReadAssemblyMetadataInsteadOfConstants()
    {
        var root = FindRepositoryRoot();
        var setup = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "ServerManager.Setup",
            "InstallerEngine.cs"));
        var updater = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ServerManager.Updater",
            "Program.cs"));
        var setupWindow = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "ServerManager.Setup",
            "InstallerWindow.xaml"));

        Assert.Contains("ProductIdentity.VersionOf", setup, StringComparison.Ordinal);
        Assert.Contains("ProductIdentity.VersionOf", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("Version 1.3.", setupWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledUpdater_UsesWindowsAdministratorMaintenanceContext()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ServerManager.Updater",
            "ServerManager.Updater.csproj"));
        var manifest = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ServerManager.Updater",
            "app.manifest"));

        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", project, StringComparison.Ordinal);
        Assert.Contains("requestedExecutionLevel level=\"requireAdministrator\"", manifest, StringComparison.Ordinal);
        Assert.Contains("uiAccess=\"false\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseAndValidationPaths_AreDerivedFromVersion()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "tools", "build-release.ps1"));

        Assert.Contains("Get-Content -LiteralPath (Join-Path $root 'VERSION')", script, StringComparison.Ordinal);
        Assert.Contains(
            "$canonicalReleaseRoot = Join-Path $releaseBase $releaseVersion",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "validation\\$releaseVersion\\build-$buildRevision\\palworld-overview",
            script,
            StringComparison.Ordinal);
        Assert.Contains("release-candidates", script, StringComparison.Ordinal);
        Assert.Contains("Rolling Stable release promoted", File.ReadAllText(Path.Combine(root, "tools", "promote-release.ps1")), StringComparison.Ordinal);
        Assert.DoesNotContain("$releaseVersion = '1.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Reset-ArtifactDirectory -Path $releaseRoot", script, StringComparison.Ordinal);
    }

    [Fact]
    public void StableLauncherAndShortcutIdentity_AreVersionIndependent()
    {
        var root = FindRepositoryRoot();
        // Normalized to \n regardless of how this checkout happens to have materialized the
        // file on disk: `git archive` (used by tools/verify-clean-checkout.ps1) re-applies
        // core.autocrlf on export, which can produce \r\n here even where the working tree
        // (and the git blob) has bare \n, and this assertion's needle spans a line break.
        var identity = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ServerManager.Contracts",
            "ProductIdentity.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var installer = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "ServerManager.Setup",
            "InstallerEngine.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "StableLauncherRelativePath = @\"Client\\1Salem.ServerManager.exe\"",
            identity,
            StringComparison.Ordinal);
        // The Stable AppUserModelID is a single fixed constant (not derived from a version or
        // build path), and the raw Win32 call is only ever reached from behind an
        // OperatingSystem.IsWindows() guard -- never called unconditionally from this
        // cross-platform contracts file.
        Assert.Contains(
            "public const string AppUserModelId = \"1Salem.ServerManager.Client\";",
            identity,
            StringComparison.Ordinal);
        Assert.Contains("OperatingSystem.IsWindows()", identity, StringComparison.Ordinal);
        Assert.Contains("\"Client\",\n                        \"1Salem.ServerManager.exe\"", installer, StringComparison.Ordinal);
        Assert.Contains("VersionBuildPath(installRoot, \"Client\")", installer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.3.0", "0DCB8B9F89F9CB81058461BEFFEA9422C9DEC719ED307C901E28BBD551CACEB7")]
    [InlineData("1.3.1", "93C86F8A62BA10A1329FF860468E09385501D36A19C61E196F34A616F1E0E818")]
    public void ImportantPreviousUpdatePackages_RemainByteForByteUnchanged(
        string version,
        string expectedSha256)
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "artifacts",
            "release",
            version,
            $"1SalemServerManager-Update-{version}.zip");
        if (!File.Exists(path))
        {
            return;
        }

        Assert.Equal(
            expectedSha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
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

    [GeneratedRegex(@"(?<!\d)1\.3\.\d+(?!\d)")]
    private static partial Regex ActiveProductVersion();

    [GeneratedRegex(@"<TargetFramework>(?<tfm>[^<]+)</TargetFramework>")]
    private static partial Regex TargetFrameworkPattern();
}
