using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class MinecraftPlayerWorldResolutionTests
{
    [Fact]
    public void ExistingUnreadableOrOversizedPropertiesNeverFallBackToADifferentWorld()
    {
        var root = Path.Combine(Path.GetTempPath(), "1salem-player-world-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(Path.Combine(root, "world"), MinecraftPlayerFiles.ResolveWorld(root));
            var properties = Path.Combine(root, "server.properties");
            File.WriteAllText(properties, "level-name=correct-world\n");
            Assert.Equal(Path.Combine(root, "correct-world"), MinecraftPlayerFiles.ResolveWorld(root));
            using (var serverWrite = new FileStream(properties, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Null(MinecraftPlayerFiles.ResolveWorld(root));
            File.WriteAllText(properties, "level-name=correct-world\n#" + new string('x', 1024 * 1024));
            Assert.Null(MinecraftPlayerFiles.ResolveWorld(root));
            Assert.StartsWith("level-name=correct-world\n", File.ReadAllText(properties), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
