namespace ServerManager.Setup.Tests;

/// <summary>
/// From Build 12 on, every release carries the metadata 1Salem Connect updates itself from, and
/// the normal release flow publishes it without any per-release edit.
/// </summary>
public sealed class ConnectSelfUpdateReleaseTests
{
    [Fact]
    public void EveryCandidate_GeneratesChecksumsAndRequiresTheConnectUpdateMetadata()
    {
        var script = Read("tools", "build-release.ps1");

        Assert.Contains("New-ConnectUpdateManifest.ps1", script, StringComparison.Ordinal);
        Assert.Contains("$connectUpdateName = '1SalemConnect-update.json'", script, StringComparison.Ordinal);

        // In SHA256SUMS.txt and the required list, and checked against the files beside it.
        Assert.Equal(2, Count(script, "    $connectUpdateName,"));
        Assert.Contains("'1SalemConnect-update.json',", script, StringComparison.Ordinal);
        Assert.Contains("$connectUpdate.installer.sha256 -ne (Get-FileHash -LiteralPath $connectSetupPath -Algorithm SHA256).Hash", script, StringComparison.Ordinal);
        Assert.Contains("[int]$connectUpdate.buildRevision -ne $BuildRevision", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Promotion_RequiresTheConnectUpdateMetadata() =>
        Assert.Contains("'1SalemConnect-update.json',", Read("tools", "promote-release.ps1"), StringComparison.Ordinal);

    [Fact]
    public void TheGenerator_NamesThisReleasesOwnAssetsByVersionAndBuild()
    {
        var script = Read("tools", "New-ConnectUpdateManifest.ps1");

        Assert.Contains("$tag = \"v$Version-build-$BuildRevision\"", script, StringComparison.Ordinal);
        Assert.Contains("https://github.com/$Repository/releases/download/$tag/$Name", script, StringComparison.Ordinal);
        Assert.Contains("[System.Text.UTF8Encoding]::new($false)", script, StringComparison.Ordinal);
        Assert.Contains("Get-ReleaseFile '1SalemConnect-Setup.exe'", script, StringComparison.Ordinal);
        Assert.Contains("Get-ReleaseFile '1SalemConnect-Portable.zip'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("1.5.1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Publishing_UploadsEveryChecksummedFile_VerifiesThem_AndNeverTouchesOldReleases()
    {
        var script = Read("tools", "publish-github-release.ps1");

        // Assets come from SHA256SUMS.txt, so new release files need no edit here.
        Assert.Contains("foreach ($line in Get-Content -LiteralPath (Join-Path $canonical 'SHA256SUMS.txt'))", script, StringComparison.Ordinal);
        Assert.Contains("$assets = @($expected.Keys)", script, StringComparison.Ordinal);
        Assert.Contains("1SalemConnect-update.json does not describe $tag.", script, StringComparison.Ordinal);
        Assert.Contains("already exists and is left untouched", script, StringComparison.Ordinal);
        Assert.Contains("draft = $true", script, StringComparison.Ordinal);
        Assert.Contains("Hash verification failed; the release stays a draft", script, StringComparison.Ordinal);
        Assert.Contains("make_latest = 'true'", script, StringComparison.Ordinal);
        Assert.Contains("$tag = \"v$version-build-$buildRevision\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host $token", script, StringComparison.OrdinalIgnoreCase);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts])).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BUILD_REVISION")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
