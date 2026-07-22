using System.Security.Cryptography;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed class MinecraftJarSwapService
{
    public async Task<JarSwapResult> SwapAsync(
        string serverRoot,
        string downloadedJarPath,
        string expectedSha1,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(serverRoot);
        var active = SafePathPolicy.ResolveWithinRoot(root, "server.jar");
        if (!File.Exists(active))
        {
            throw new FileNotFoundException("The active Minecraft server JAR is missing.", active);
        }

        if (!File.Exists(downloadedJarPath))
        {
            throw new FileNotFoundException("The downloaded Minecraft server JAR is missing.", downloadedJarPath);
        }

        var updateRoot = SafePathPolicy.ResolveWithinRoot(root, Path.Combine(".1salem", "updates"));
        var rollbackRoot = SafePathPolicy.ResolveWithinRoot(root, Path.Combine(".1salem", "rollback"));
        Directory.CreateDirectory(updateRoot);
        Directory.CreateDirectory(rollbackRoot);
        var staged = Path.Combine(updateRoot, $"{Guid.NewGuid():N}.jar");
        var rollback = Path.Combine(
            rollbackRoot,
            $"server-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.jar");
        File.Copy(downloadedJarPath, staged, false);

        await using (var stream = File.OpenRead(staged))
        {
            var hash = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            if (!hash.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(staged);
                throw new InvalidDataException("The staged Minecraft JAR failed hash verification.");
            }
        }

        try
        {
            File.Move(active, rollback, false);
            File.Move(staged, active, false);
            return new JarSwapResult(active, rollback, expectedSha1.ToLowerInvariant());
        }
        catch
        {
            if (!File.Exists(active) && File.Exists(rollback))
            {
                File.Move(rollback, active, false);
            }

            throw;
        }
    }

    public void Rollback(JarSwapResult swap)
    {
        ArgumentNullException.ThrowIfNull(swap);
        if (!File.Exists(swap.RollbackJarPath))
        {
            throw new FileNotFoundException("The rollback Minecraft JAR is missing.", swap.RollbackJarPath);
        }

        var failed = $"{swap.ActiveJarPath}.failed-{Guid.NewGuid():N}";
        if (File.Exists(swap.ActiveJarPath))
        {
            File.Move(swap.ActiveJarPath, failed, false);
        }

        File.Move(swap.RollbackJarPath, swap.ActiveJarPath, false);
    }
}
