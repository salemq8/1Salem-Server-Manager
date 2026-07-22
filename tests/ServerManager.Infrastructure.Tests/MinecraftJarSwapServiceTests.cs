using System.Security.Cryptography;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class MinecraftJarSwapServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SwapAndRollback_PreservesBothJarVersions()
    {
        Directory.CreateDirectory(_root);
        var active = Path.Combine(_root, "server.jar");
        var downloaded = Path.Combine(_root, "downloaded.jar");
        await File.WriteAllTextAsync(active, "old");
        await File.WriteAllTextAsync(downloaded, "new");
        var hash = Convert.ToHexString(SHA1.HashData("new"u8.ToArray())).ToLowerInvariant();
        var service = new MinecraftJarSwapService();

        var swap = await service.SwapAsync(_root, downloaded, hash);
        Assert.Equal("new", await File.ReadAllTextAsync(active));
        Assert.Equal("old", await File.ReadAllTextAsync(swap.RollbackJarPath));

        service.Rollback(swap);
        Assert.Equal("old", await File.ReadAllTextAsync(active));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
