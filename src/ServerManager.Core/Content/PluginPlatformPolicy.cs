using ServerManager.Contracts;

namespace ServerManager.Core.Content;

/// <summary>
/// Maps the server software in front of us onto what each provider actually models. The
/// mapping is deliberately conservative: where a provider cannot prove a file will run on
/// this server, the server is not offered that provider's files at all.
/// </summary>
public static class PluginPlatformPolicy
{
    /// <summary>Vanilla has no plugin API, so nothing is installable there.</summary>
    public static bool SupportsPlugins(ServerPlatform platform) =>
        platform is ServerPlatform.Paper or ServerPlatform.Purpur or ServerPlatform.Spigot or
            ServerPlatform.Bukkit or ServerPlatform.Folia;

    /// <summary>
    /// The Modrinth loaders this server can run. Ordered widest-first so a Paper server can
    /// still use a plugin published only for Bukkit. Folia is not included for the others:
    /// a plugin has to opt into Folia's threading, so Paper does not imply Folia and Folia
    /// does not imply Paper.
    /// </summary>
    public static IReadOnlyList<string> ModrinthLoaders(ServerPlatform platform) =>
        platform switch
        {
            ServerPlatform.Purpur => ["purpur", "paper", "spigot", "bukkit"],
            ServerPlatform.Paper => ["paper", "spigot", "bukkit"],
            ServerPlatform.Spigot => ["spigot", "bukkit"],
            ServerPlatform.Bukkit => ["bukkit"],
            ServerPlatform.Folia => ["folia"],
            _ => []
        };

    /// <summary>
    /// Hangar's Platform enum is only PAPER, WATERFALL and VELOCITY, and the last two are
    /// proxies rather than game servers. Purpur is a Paper fork, so it takes PAPER files.
    /// Spigot and Bukkit get null: a Hangar PAPER artifact may call Paper-only API, and
    /// nothing in the response proves otherwise, so we do not offer it. Those servers are
    /// served by Modrinth, which models spigot and bukkit explicitly.
    /// </summary>
    public static string? HangarPlatform(ServerPlatform platform) =>
        platform switch
        {
            ServerPlatform.Paper or ServerPlatform.Purpur or ServerPlatform.Folia => "PAPER",
            _ => null
        };

    public static bool IsServedBy(ContentProviderId provider, ServerPlatform platform) =>
        provider switch
        {
            ContentProviderId.Modrinth => ModrinthLoaders(platform).Count > 0,
            ContentProviderId.Hangar => HangarPlatform(platform) is not null,
            _ => false
        };

    /// <summary>The platform names shown on a card, for example "Paper".</summary>
    public static string DisplayName(ServerPlatform platform) =>
        platform switch
        {
            ServerPlatform.Paper => "Paper",
            ServerPlatform.Purpur => "Purpur",
            ServerPlatform.Spigot => "Spigot",
            ServerPlatform.Bukkit => "Bukkit",
            ServerPlatform.Folia => "Folia",
            ServerPlatform.Vanilla => "Vanilla",
            _ => "Unknown"
        };

    /// <summary>
    /// Reads a platform back from a stored or detected name. Unknown input stays Unknown
    /// rather than being guessed into something installable.
    /// </summary>
    public static ServerPlatform Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "paper" => ServerPlatform.Paper,
            "purpur" => ServerPlatform.Purpur,
            "spigot" => ServerPlatform.Spigot,
            "bukkit" or "craftbukkit" => ServerPlatform.Bukkit,
            "folia" => ServerPlatform.Folia,
            "vanilla" => ServerPlatform.Vanilla,
            _ => ServerPlatform.Unknown
        };
}
