using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Controls;

/// <summary>
/// The words the Content Hub uses for each content type. One place, so a badge, a row and a
/// dialog never disagree about what something is called.
/// </summary>
public static class ContentLabels
{
    public static string Kind(ContentKind kind) =>
        LocalizationService.Get(kind switch
        {
            ContentKind.Modpack => "Content.Kind.Modpack",
            ContentKind.DataPack => "Content.Kind.DataPack",
            ContentKind.ResourcePack => "Content.Kind.ResourcePack",
            _ => "Content.Kind.Plugin"
        });

    /// <summary>The search box hint for a type: "Search plugins", "Search modpacks", …</summary>
    public static string SearchHint(ContentKind kind) =>
        LocalizationService.Get(kind switch
        {
            ContentKind.Modpack => "Content.Search.Modpack",
            ContentKind.DataPack => "Content.Search.DataPack",
            ContentKind.ResourcePack => "Content.Search.ResourcePack",
            _ => "Content.Search.Plugin"
        });

    /// <summary>
    /// A platform choice as shown: "Automatic (Paper)" names what this server runs, "All" is
    /// worded for the type, and the rest are the software's own names.
    /// </summary>
    public static string Platform(string option, ContentKind kind, ServerPlatform? serverPlatform) =>
        option switch
        {
            ServerManager.Core.Content.ContentPlatformFilter.Automatic => LocalizationService.Format(
                "Content.Platform.Auto",
                ServerManager.Core.Content.PluginPlatformPolicy.DisplayName(serverPlatform ?? ServerPlatform.Unknown)),
            ServerManager.Core.Content.ContentPlatformFilter.All => LocalizationService.Get(
                kind == ContentKind.Modpack ? "Content.Platform.AllLoaders" : "Content.Platform.AllPlugins"),
            _ => ServerManager.Core.Content.ContentPlatformFilter.DisplayName(option)
        };

    /// <summary>The order the type selector offers, with plugins first.</summary>
    public static IReadOnlyList<ContentKind> SelectableKinds { get; } =
    [
        ContentKind.Plugin,
        ContentKind.Modpack,
        ContentKind.DataPack,
        ContentKind.ResourcePack
    ];
}
