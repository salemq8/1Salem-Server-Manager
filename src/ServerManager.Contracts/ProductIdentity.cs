using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ServerManager.Contracts;

public static class ProductIdentity
{
    public const string ProductName = "1Salem Server Manager";
    public const string StableLauncherRelativePath = @"Client\1Salem.ServerManager.exe";
    public const string ServiceName = "1SalemServerManagerAgent";
    public const string StableChannel = "Stable";

    /// <summary>
    /// The one AppUserModelID every Stable-channel process and shortcut must use. Explorer
    /// groups taskbar buttons, jump lists, and pinned-shortcut identity by this ID rather than
    /// by executable path -- without it, Windows falls back to a path-derived identity that
    /// changes across updates (the Stable launcher's target moves between Versions\&lt;v&gt;\
    /// Builds\&lt;n&gt; folders), which can make a pinned taskbar icon fail to merge with the
    /// running app's button after an update.
    /// </summary>
    public const string AppUserModelId = "1Salem.ServerManager.Client";

    /// <summary>
    /// A distinct AppUserModelID for the developer-only Preview/design harness, so it can never
    /// be mistaken for (or visually merge with) the real Stable application in the taskbar.
    /// </summary>
    public const string PreviewAppUserModelId = "1Salem.ServerManager.Client.Preview";

    /// <summary>
    /// Sets the current process's explicit AppUserModelID. Must be called once, early in
    /// startup, before the process creates its first top-level window -- Windows freezes the
    /// AUMID at first-window-creation time. No-op (does not throw) on non-Windows platforms.
    /// </summary>
    public static void ApplyExplicitAppUserModelId(string appUserModelId)
    {
        if (OperatingSystem.IsWindows())
        {
            SetCurrentProcessExplicitAppUserModelID(appUserModelId);
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    public static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+', 2)[0]
        ?? assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static int BuildRevisionOf(Assembly assembly)
    {
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals(
                "BuildRevision",
                StringComparison.Ordinal))
            ?.Value;
        return int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var revision)
            ? revision
            : 0;
    }
}
