using ServerManager.Contracts;

namespace ServerManager.Core;

public sealed record MinecraftSoftwareArtifact(
    ServerPlatform Platform, string MinecraftVersion, string Build, Uri DownloadUrl,
    string HashAlgorithm, string Hash, long? SizeBytes = null);

public interface IMinecraftSoftwareCatalog
{
    Task<MinecraftSoftwareArtifact?> ResolveAsync(ServerPlatform platform, string exactVersion,
        CancellationToken cancellationToken = default);
    Task<string> DownloadAsync(MinecraftSoftwareArtifact artifact, string stagingDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>Called only while the shared server operation lease is held and the server is stopped.</summary>
public interface IMinecraftSoftwareBackup
{
    Task<BackupResult> CreateAsync(GameServerDefinition server, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetExistingBackupPathsAsync(GameServerDefinition server, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Existing backup paths cannot be verified safely.");
}
