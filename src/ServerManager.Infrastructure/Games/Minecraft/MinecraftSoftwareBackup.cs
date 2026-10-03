using ServerManager.Core;
using ServerManager.Infrastructure.Backups;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed class MinecraftSoftwareBackup(IBackupService backups) : IMinecraftSoftwareBackup
{
    public async Task<IReadOnlyList<string>> GetExistingBackupPathsAsync(GameServerDefinition server, CancellationToken cancellationToken) =>
        (await backups.ListAsync(server.Id, cancellationToken)).Select(backup => backup.ArchivePath).ToArray();

    public Task<BackupResult> CreateAsync(GameServerDefinition server, CancellationToken cancellationToken) =>
        backups is BackupService implementation
            ? implementation.CreateWithinOperationAsync(new BackupRequest(server.Id,
                BackupDestinationPolicy.GetDefaultRoot(server), false, false, ProtectAfterCreation: true,
                DisplayName: "Before server software migration", Notes: "World restore is never automatic during runtime rollback."), cancellationToken)
            : throw new InvalidOperationException("The verified migration backup implementation is unavailable.");
}
