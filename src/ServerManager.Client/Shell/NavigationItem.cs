namespace ServerManager.Client.Shell;

/// <summary>
/// One top-level destination in the sidebar. <paramref name="Glyph"/> is a Segoe MDL2 Assets
/// code point so the whole app draws from one icon family rather than mixing emoji and
/// ad-hoc vector art. It is optional so a destination can still be created from a key alone.
/// </summary>
public sealed record NavigationItem(
    string Key,
    string Title,
    string Description,
    string Glyph = "");
