using ServerManager.Infrastructure.Updates;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// P1-05: focused tests for VersionedUpdateInstaller.ReplaceDirectory/VerifyDirectoryCopy --
/// the internal stage-then-atomic-swap primitive used to replace the live Agent and
/// self-updater directories during an application update. These call the internal methods
/// directly (via InternalsVisibleTo) rather than driving the full ApplyAsync package/manifest
/// pipeline, so each failure mode below is isolated and unambiguous.
/// </summary>
public sealed class VersionedUpdateInstallerAtomicityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-replace-directory-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReplaceDirectory_NormalCase_SwapsAtomicallyWithNoLeftoverStaging()
    {
        var live = CreateDirectoryWithFile("live", "agent.exe", "old-build");
        var source = CreateDirectoryWithFile("source", "agent.exe", "new-build");
        var old = Path.Combine(_root, "old-agent");

        VersionedUpdateInstaller.ReplaceDirectory(live, source, old);

        Assert.Equal("new-build", await File.ReadAllTextAsync(Path.Combine(live, "agent.exe")));
        Assert.Equal("old-build", await File.ReadAllTextAsync(Path.Combine(old, "agent.exe")));
        Assert.Empty(Directory.GetDirectories(_root, "live.staged-*"));
    }

    [Fact]
    public async Task ReplaceDirectory_LiveDoesNotExistYet_StillActivatesStagedContent()
    {
        // Simulates the recovery path after a crash left `live` missing: a later call (a
        // fresh update attempt, or a repair) must still succeed rather than requiring `live`
        // to already exist.
        var live = Path.Combine(_root, "live");
        var source = CreateDirectoryWithFile("source", "agent.exe", "new-build");
        var old = Path.Combine(_root, "old-agent");

        VersionedUpdateInstaller.ReplaceDirectory(live, source, old);

        Assert.Equal("new-build", await File.ReadAllTextAsync(Path.Combine(live, "agent.exe")));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void VerifyDirectoryCopy_DetectsATruncatedOrCorruptedFile()
    {
        var source = CreateDirectoryWithFile("source", "agent.exe", "0123456789");
        var staged = CreateDirectoryWithFile("staged", "agent.exe", "01234");

        var exception = Assert.Throws<IOException>(
            () => VersionedUpdateInstaller.VerifyDirectoryCopy(source, staged));
        Assert.Contains("wrong size", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyDirectoryCopy_DetectsAFileCountMismatch()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "agent.exe"), "content");
        File.WriteAllText(Path.Combine(source, "extra.dll"), "content");
        var staged = CreateDirectoryWithFile("staged", "agent.exe", "content");

        var exception = Assert.Throws<IOException>(
            () => VersionedUpdateInstaller.VerifyDirectoryCopy(source, staged));
        Assert.Contains("incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyDirectoryCopy_DetectsASpecificMissingFile_WhenCountsHappenToMatch()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "agent.exe"), "content");
        File.WriteAllText(Path.Combine(source, "extra.dll"), "content");
        var staged = Path.Combine(_root, "staged");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "agent.exe"), "content");
        // Same file count as source, but "extra.dll" was replaced by an unrelated file --
        // simulates a copy that dropped one file and, coincidentally, left another stray one.
        File.WriteAllText(Path.Combine(staged, "unexpected.dll"), "content");

        var exception = Assert.Throws<IOException>(
            () => VersionedUpdateInstaller.VerifyDirectoryCopy(source, staged));
        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("extra.dll", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyDirectoryCopy_IdenticalContent_DoesNotThrow()
    {
        var source = CreateDirectoryWithFile("source", "agent.exe", "matching-content");
        var staged = CreateDirectoryWithFile("staged", "agent.exe", "matching-content");

        VersionedUpdateInstaller.VerifyDirectoryCopy(source, staged);
    }

    [Fact]
    public async Task ReplaceDirectory_OldPathAlreadyExists_ThrowsWithoutTouchingLiveOrSource()
    {
        var live = CreateDirectoryWithFile("live", "agent.exe", "old-build");
        var source = CreateDirectoryWithFile("source", "agent.exe", "new-build");
        var old = CreateDirectoryWithFile("old-agent", "leftover.txt", "stale");

        Assert.Throws<IOException>(() => VersionedUpdateInstaller.ReplaceDirectory(live, source, old));

        Assert.Equal("old-build", await File.ReadAllTextAsync(Path.Combine(live, "agent.exe")));
        Assert.True(File.Exists(Path.Combine(source, "agent.exe")));
    }

    private string CreateDirectoryWithFile(string name, string fileName, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, fileName), content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
