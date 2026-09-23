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

    /// <summary>The order the type selector offers, with plugins first.</summary>
    public static IReadOnlyList<ContentKind> SelectableKinds { get; } =
    [
        ContentKind.Plugin,
        ContentKind.Modpack,
        ContentKind.DataPack,
        ContentKind.ResourcePack
    ];
}
