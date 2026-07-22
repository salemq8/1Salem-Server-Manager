using ServerManager.Contracts;
using ServerManager.Launcher;

namespace ServerManager.Client.Tests;

public sealed class StableLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-launcher-{Guid.NewGuid():N}");

    [Fact]
    public void StableLauncher_ResolvesOnlyMatchingStableClient()
    {
        var client = CreateClient("1.3.2");
        var manifest = CreateManifest(client, "Stable", "1.3.2");

        var resolved = StableLauncherPolicy.ResolveClientPath(
            _root,
            manifest,
            _ => "1.3.2");

        Assert.Equal(Path.GetFullPath(client), resolved);
    }

    [Theory]
    [InlineData("Preview", "1.3.2-preview.1")]
    [InlineData("Development", "1.3.3-dev.1")]
    [InlineData("Stable", "1.3.2-preview.1")]
    public void StableLauncher_NeverOpensPreviewOrDevelopmentBuild(
        string channel,
        string version)
    {
        var client = CreateClient(version);
        var manifest = CreateManifest(client, channel, version);

        Assert.Throws<InvalidDataException>(() =>
            StableLauncherPolicy.ResolveClientPath(
                _root,
                manifest,
                _ => version));
    }

    [Fact]
    public void StableLauncher_RejectsPathOutsideInstallRoot()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.exe");
        File.WriteAllText(outside, "outside");
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                StableLauncherPolicy.ResolveClientPath(
                    _root,
                    CreateManifest(outside, "Stable", "1.3.2"),
                    _ => "1.3.2"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void StableLauncherPath_IsStableAndVersionIndependent()
    {
        Assert.Equal(@"Client\1Salem.ServerManager.exe", ProductIdentity.StableLauncherRelativePath);
        Assert.DoesNotContain("1.3", ProductIdentity.StableLauncherRelativePath, StringComparison.Ordinal);
    }

    private string CreateClient(string version)
    {
        var path = Path.Combine(
            _root,
            "Versions",
            version,
            "Client",
            "1Salem.ServerManager.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, version);
        return path;
    }

    private InstalledApplicationManifest CreateManifest(
        string client,
        string channel,
        string version) =>
        new(
            1,
            version,
            null,
            null,
            channel,
            Path.Combine(_root, "Client", "1Salem.ServerManager.exe"),
            client,
            Path.Combine(_root, "Agent", "1Salem.ServerManager.Agent.exe"),
            Path.Combine(_root, "Client", "Updater", "1Salem.ServerManager.Updater.exe"),
            null,
            "Succeeded",
            DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
