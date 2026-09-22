using ServerManager.Contracts;

namespace ServerManager.Core.Content;

/// <summary>
/// The record of what the manager installed. It lives outside the plugin files, so a plugin
/// cannot claim to be something it is not, and a file added by hand simply has no record
/// rather than a made-up one.
/// </summary>
public interface IInstalledContentStore
{
    Task<IReadOnlyList<InstalledContent>> ListAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<InstalledContent?> GetAsync(
        Guid serverId,
        string fileName,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(InstalledContent record, CancellationToken cancellationToken = default);

    Task RemoveAsync(
        Guid serverId,
        string fileName,
        CancellationToken cancellationToken = default);
}
