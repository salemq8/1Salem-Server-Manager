using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Content;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Builds the normalized view of what a server can accept. Everything downstream, including
/// the install target, is derived from this, and all of it comes from the registered server
/// root rather than from anything a provider said.
/// </summary>
public sealed class ContentProfileService(
    IGameServerStore servers,
    IProcessSupervisor processSupervisor)
{
    /// <summary>Reason codes; the client localizes them rather than showing English from here.</summary>
    public const string NotMinecraftReason = "NotMinecraft";

    public const string VanillaReason = "Vanilla";

    public const string UnknownPlatformReason = "UnknownPlatform";

    public const string UnknownVersionReason = "UnknownVersion";

    public const string MissingRootReason = "MissingRoot";

    public async Task<ServerContentProfile?> GetAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await servers.GetAsync(serverId, cancellationToken);
        if (server is null)
        {
            return null;
        }

        var running = false;
        var snapshot = await processSupervisor.GetSnapshotAsync(serverId, cancellationToken);
        if (snapshot is not null)
        {
            running = snapshot.State is ServerState.Running or ServerState.Starting or
                ServerState.Stopping or ServerState.Restarting;
        }

        running |= server.State is ServerState.Running or ServerState.Starting or
            ServerState.Stopping or ServerState.Restarting;

        if (server.Game != GameType.Minecraft)
        {
            return Unsupported(server, ServerPlatform.Unknown, null, running, NotMinecraftReason);
        }

        if (!Directory.Exists(server.RootPath))
        {
            return Unsupported(server, ServerPlatform.Unknown, null, running, MissingRootReason);
        }

        var (platform, detectedVersion, platformVersion) =
            MinecraftPlatformDetector.Detect(server.RootPath);
        // A migrated runtime has a hash-bound marker. Inspect the active JAR as well so a
        // stale version_history.json cannot describe a runtime no longer installed.
        var active = MinecraftSoftwareSafety.Inspect(server.RootPath, server.InstalledVersion);
        if (active.Platform != ServerPlatform.Unknown)
        {
            platform = active.Platform;
            detectedVersion = active.Version;
            platformVersion = null;
        }

        // The registered version is the fallback: it is what the manager installed, and the
        // fork's own version_history.json wins over it when present.
        var minecraftVersion = detectedVersion ?? server.InstalledVersion;

        if (!PluginPlatformPolicy.SupportsPlugins(platform))
        {
            return Unsupported(
                server,
                platform,
                minecraftVersion,
                running,
                platform == ServerPlatform.Vanilla ? VanillaReason : UnknownPlatformReason);
        }

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return Unsupported(server, platform, null, running, UnknownVersionReason);
        }

        return new ServerContentProfile(
            server.Id,
            server.Game,
            platform,
            minecraftVersion,
            platformVersion,
            Path.GetFullPath(server.RootPath),
            ContentPathPolicy.ResolvePluginsDirectory(server.RootPath),
            SupportsPlugins: true,
            IsRunning: running);
    }

    private static ServerContentProfile Unsupported(
        GameServerDefinition server,
        ServerPlatform platform,
        string? minecraftVersion,
        bool running,
        string reason)
    {
        // Still report a plugins path so the UI can offer "open folder", but nothing is
        // installable while SupportsPlugins is false.
        string pluginsDirectory;
        try
        {
            pluginsDirectory = ContentPathPolicy.ResolvePluginsDirectory(server.RootPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            pluginsDirectory = string.Empty;
        }

        return new ServerContentProfile(
            server.Id,
            server.Game,
            platform,
            minecraftVersion,
            null,
            server.RootPath,
            pluginsDirectory,
            SupportsPlugins: false,
            IsRunning: running,
            UnsupportedReason: reason);
    }
}
