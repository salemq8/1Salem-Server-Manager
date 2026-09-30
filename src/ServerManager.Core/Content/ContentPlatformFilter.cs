using ServerManager.Contracts;

namespace ServerManager.Core.Content;

/// <summary>
/// The optional server-software filter on Discover. It only narrows what is asked of the
/// providers; whether a result can run on this server is still decided per card from the
/// server's own platform (<see cref="PluginPlatformPolicy"/>), so choosing "Folia" on a Paper
/// server shows Folia plugins marked as not fitting rather than pretending they do.
/// </summary>
public static class ContentPlatformFilter
{
    /// <summary>What this server itself can run (the default).</summary>
    public const string Automatic = "auto";

    /// <summary>Every platform of the type, each card still saying whether it fits.</summary>
    public const string All = "all";

    private static readonly string[] PluginPlatforms = ["paper", "purpur", "spigot", "bukkit", "folia"];
    private static readonly string[] ModpackLoaders = ["fabric", "forge", "neoforge", "quilt"];

    /// <summary>The plugin platforms the providers publish for, in menu order.</summary>
    public static IReadOnlyList<string> PluginPlatformNames => PluginPlatforms;

    /// <summary>
    /// The choices for a type on this server. A server that runs no plugin platform (Vanilla)
    /// has no "Automatic" for plugins, because nothing would match; it starts at "All".
    /// </summary>
    public static IReadOnlyList<string> Options(ContentKind kind, ServerPlatform? platform) =>
        kind == ContentKind.Plugin && platform is { } known && !PluginPlatformPolicy.SupportsPlugins(known)
            ? [All, .. PluginPlatforms]
            : Options(kind);

    /// <summary>
    /// The choices that mean something for a content type, in menu order. Data packs and
    /// resource packs have one fixed Modrinth loader each, so they offer no choice at all.
    /// </summary>
    public static IReadOnlyList<string> Options(ContentKind kind) =>
        kind switch
        {
            ContentKind.Plugin => [Automatic, .. PluginPlatforms, All],
            ContentKind.Modpack => [All, .. ModpackLoaders],
            _ => []
        };

    /// <summary>A known choice for this type, or the type's default for anything else.</summary>
    public static string Normalize(ContentKind kind, string? filter)
    {
        var options = Options(kind);
        if (options.Count == 0)
        {
            return Automatic;
        }

        var value = filter?.Trim().ToLowerInvariant();
        return value is not null && options.Contains(value) ? value : options[0];
    }

    /// <summary>
    /// The Modrinth loaders to search for this type, server and filter. With
    /// <paramref name="compatibleOnly"/> ("Works with this server"), a plugin filter is narrowed to
    /// what this server can run, so that list never consists of plugins that do not work here;
    /// an empty result means nothing chosen can run on this server.
    /// </summary>
    public static IReadOnlyList<string> ModrinthLoaders(
        ContentKind kind,
        ServerPlatform platform,
        string? filter,
        bool compatibleOnly = false)
    {
        var chosen = Normalize(kind, filter);
        IReadOnlyList<string> loaders = kind switch
        {
            ContentKind.Plugin => chosen switch
            {
                // A server that runs no plugin platform browses them all instead of nothing.
                Automatic => PluginPlatformPolicy.ModrinthLoaders(platform) is { Count: > 0 } own ? own : PluginPlatforms,
                All => PluginPlatforms,
                _ => [chosen]
            },
            ContentKind.Modpack => chosen == All ? ModpackLoaders : [chosen],
            _ => ContentTypePolicy.ModrinthLoaders(kind, platform)
        };

        var runnable = PluginPlatformPolicy.ModrinthLoaders(platform);
        if (compatibleOnly && kind == ContentKind.Plugin && runnable.Count > 0)
        {
            // Where nothing can run (Vanilla) this is browsing only; each card says so.
            loaders = loaders.Where(loader => runnable.Contains(loader, StringComparer.OrdinalIgnoreCase)).ToArray();
        }

        return loaders;
    }

    /// <summary>
    /// Whether Hangar belongs in the search. Hangar only publishes for PAPER: that answers
    /// Paper and its fork Purpur, but proves nothing for Spigot, Bukkit or Folia (a plugin has
    /// to opt into Folia's threading). So those filters leave Hangar out, and so does a Folia
    /// server whatever the filter, because its PAPER files would be shown as fitting.
    /// </summary>
    public static bool IncludesHangar(ContentKind kind, string? filter, ServerPlatform platform)
    {
        if (kind != ContentKind.Plugin || platform == ServerPlatform.Folia)
        {
            return false;
        }

        var chosen = Normalize(kind, filter);
        return chosen is Automatic or All or "paper" or "purpur";
    }

    /// <summary>The name shown for a choice, for example "NeoForge".</summary>
    public static string DisplayName(string option) =>
        option switch
        {
            "paper" => "Paper",
            "purpur" => "Purpur",
            "spigot" => "Spigot",
            "bukkit" => "Bukkit",
            "folia" => "Folia",
            "fabric" => "Fabric",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "quilt" => "Quilt",
            _ => option
        };
}
