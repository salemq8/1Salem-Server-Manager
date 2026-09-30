using ServerManager.Contracts;

namespace ServerManager.Core.Content;

/// <summary>
/// What each content type is, in one place: who serves it, whether this server can take it,
/// what the file looks like and what has to happen before it is live. Install behaviour is
/// decided from the type, never from which provider an item came from.
/// </summary>
public static class ContentTypePolicy
{
    /// <summary>
    /// Only Modrinth carries packs. Hangar is a plugin repository, so offering a Hangar
    /// section for a data pack would be an empty promise.
    /// </summary>
    public static bool IsServedBy(ContentKind kind, ContentProviderId provider) =>
        kind switch
        {
            ContentKind.Plugin => true,
            _ => provider == ContentProviderId.Modrinth
        };

    /// <summary>
    /// Whether this server can take this kind at all. Plugins need a Bukkit-family platform;
    /// data and resource packs are vanilla Minecraft features that any Minecraft server has;
    /// a modpack is never applied to an existing server, so it is not offered per-server.
    /// </summary>
    public static bool IsSupportedBy(ContentKind kind, ServerContentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Game != GameType.Minecraft)
        {
            return false;
        }

        return kind switch
        {
            ContentKind.Plugin => profile.SupportsPlugins,
            ContentKind.DataPack or ContentKind.ResourcePack =>
                !string.IsNullOrWhiteSpace(profile.MinecraftVersion),

            // A modpack can be browsed from any Minecraft server because installing one
            // always builds a NEW server: it is never applied over this one, so this
            // server's own version does not limit what can be looked at.
            ContentKind.Modpack => true,
            _ => false
        };
    }

    /// <summary>
    /// Whether this type can be browsed for this server. Plugins can be looked at on any
    /// Minecraft server, so the type is never missing from the selector; on a server that cannot
    /// load plugins (Vanilla) every plugin card says it cannot be installed here, and
    /// <see cref="IsSupportedBy"/> still refuses the install. Everything else is browsed where it
    /// can be used.
    /// </summary>
    public static bool IsBrowsableBy(ContentKind kind, ServerContentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return kind == ContentKind.Plugin
            ? profile.Game == GameType.Minecraft
            : IsSupportedBy(kind, profile);
    }

    /// <summary>The file extension the manager writes for this kind.</summary>
    public static string FileExtension(ContentKind kind) =>
        kind switch
        {
            ContentKind.Plugin => ".jar",
            ContentKind.Modpack => ".mrpack",
            _ => ".zip"
        };

    /// <summary>
    /// What has to happen before installed content actually takes effect. A data pack needs
    /// the world to reload; a plugin needs the server restarted; a resource pack takes effect
    /// for clients the next time they connect.
    /// </summary>
    public static InstalledContentState PendingState(ContentKind kind, bool serverRunning) =>
        kind switch
        {
            ContentKind.DataPack => serverRunning
                ? InstalledContentState.ReloadRequired
                : InstalledContentState.UpToDate,
            ContentKind.ResourcePack => InstalledContentState.NotDistributed,
            _ => serverRunning
                ? InstalledContentState.RestartRequired
                : InstalledContentState.UpToDate
        };

    /// <summary>
    /// The Modrinth search facet for a kind. Plugins and data packs are both reported as
    /// "mod" by the API and are only distinguishable by their loaders, which is why the facet
    /// is used rather than the returned type.
    /// </summary>
    public static string ModrinthProjectType(ContentKind kind) =>
        kind switch
        {
            ContentKind.Modpack => "modpack",
            ContentKind.DataPack => "datapack",
            ContentKind.ResourcePack => "resourcepack",
            _ => "plugin"
        };

    /// <summary>
    /// The loaders to ask Modrinth for. A data pack is published under the `datapack` loader
    /// and a resource pack under `minecraft`; plugins use the server's own platform.
    /// </summary>
    public static IReadOnlyList<string> ModrinthLoaders(ContentKind kind, ServerPlatform platform) =>
        kind switch
        {
            ContentKind.DataPack => ["datapack"],
            ContentKind.ResourcePack => ["minecraft"],
            ContentKind.Modpack => ["fabric", "forge", "neoforge", "quilt"],
            _ => PluginPlatformPolicy.ModrinthLoaders(platform)
        };

    /// <summary>
    /// Mod loaders this app can set up on its own. Fabric publishes a server launcher jar
    /// through its metadata API; Forge, NeoForge and Quilt require running their own
    /// installer programs, which this app does not do.
    /// </summary>
    public static bool CanInstallLoader(string? loader) =>
        string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase);

    /// <summary>The loader a modpack's dependencies name, normalized.</summary>
    public static string? ReadLoader(IReadOnlyDictionary<string, string>? dependencies)
    {
        if (dependencies is null)
        {
            return null;
        }

        foreach (var (key, _) in dependencies)
        {
            var loader = key.ToLowerInvariant() switch
            {
                "fabric-loader" => "fabric",
                "quilt-loader" => "quilt",
                "forge" => "forge",
                "neoforge" => "neoforge",
                _ => null
            };
            if (loader is not null)
            {
                return loader;
            }
        }

        return null;
    }
}

