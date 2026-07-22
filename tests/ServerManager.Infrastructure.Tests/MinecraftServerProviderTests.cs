using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class MinecraftServerProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DetectAndCreateLaunchSpec_UsesManagedFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".1salem"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "server.jar"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(_root, "server.properties"), "server-port=25565");
        var java = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        await File.WriteAllTextAsync(Path.Combine(_root, ".1salem", "java-path.txt"), java);
        var provider = new MinecraftServerProvider();

        var detection = await provider.DetectInstallationAsync(_root);
        var spec = provider.CreateLaunchSpec(
            new GameServerDefinition(
                Guid.NewGuid(),
                GameType.Minecraft,
                "Test",
                _root,
                25565,
                null,
                DateTimeOffset.UtcNow));

        Assert.True(detection.IsInstalled);
        Assert.Equal(java, spec.FileName);
        Assert.Equal("@user_jvm_args.txt -jar server.jar nogui", spec.Arguments);
        Assert.Equal(_root, spec.WorkingDirectory);
        Assert.Equal(
            ["-Xms1024M", "-Xmx4096M", "-jar", "server.jar", "nogui"],
            spec.ArgumentList);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
