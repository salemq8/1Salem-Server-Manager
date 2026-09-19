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

    /// <summary>
    /// Stamps PKEY_AppUserModel_ID onto an already-saved .lnk shortcut file by reopening it via
    /// IPersistFile/IPropertyStore. Every shortcut-creation code path in this product (the Setup
    /// installer, the in-app update's shortcut retargeting, and the Admin Tools manual shortcut
    /// creator) needs a shortcut a real user receives to carry the same fixed AppUserModelID as
    /// the running application, or Windows falls back to a path-derived identity and a pinned
    /// taskbar icon can fail to merge with the running app's button. No-op on non-Windows
    /// platforms. Best-effort: a shortcut is still useful without this property, so a failure
    /// here (e.g. an unexpected shell COM configuration) must not block shortcut creation.
    /// </summary>
    public static void StampShortcutAppUserModelId(string shortcutPath, string appUserModelId)
    {
        if (OperatingSystem.IsWindows())
        {
            StampShortcutAppUserModelIdCore(shortcutPath, appUserModelId);
        }
    }

    /// <summary>Test-only accessor: reads back PKEY_AppUserModel_ID from a saved shortcut.</summary>
    public static string? TryReadShortcutAppUserModelId(string shortcutPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return TryReadShortcutAppUserModelIdCore(shortcutPath);
    }

    [SupportedOSPlatform("windows")]
    private static string? TryReadShortcutAppUserModelIdCore(string shortcutPath)
    {
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!;
        var shellLink = Activator.CreateInstance(type)!;
        try
        {
            ((IPersistFile)shellLink).Load(shortcutPath, 0);
            var propertyStore = (IPropertyStore)shellLink;
            var key = AppUserModelIdKey;
            propertyStore.GetValue(ref key, out var propVariant);
            try
            {
                const ushort VtLpwstr = 31;
                return propVariant.VarType == VtLpwstr && propVariant.PointerValue != nint.Zero
                    ? Marshal.PtrToStringUni(propVariant.PointerValue)
                    : null;
            }
            finally
            {
                PropVariantClear(ref propVariant);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLink);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void StampShortcutAppUserModelIdCore(string shortcutPath, string appUserModelId)
    {
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!;
        var shellLink = Activator.CreateInstance(type)!;
        try
        {
            // STGM_READWRITE (2), not STGM_READ (0): a shortcut opened read-only via
            // IPersistFile.Load cannot later be written back to the same path via Save --
            // doing so fails with STG_E_ACCESSDENIED. The read-only accessor
            // (TryReadShortcutAppUserModelId) intentionally loads with 0 since it never saves.
            const uint StgmReadWrite = 2;
            ((IPersistFile)shellLink).Load(shortcutPath, StgmReadWrite);

            try
            {
                var propertyStore = (IPropertyStore)shellLink;
                var key = AppUserModelIdKey;
                // Built by hand rather than via the propsys.dll InitPropVariantFromString
                // helper, which is a C++-only inline function with no stable DLL export to
                // P/Invoke. VT_LPWSTR = 31; PropVariantClear frees this CoTaskMem string
                // correctly for that variant type.
                var propVariant = new PropVariant
                {
                    VarType = 31,
                    PointerValue = Marshal.StringToCoTaskMemUni(appUserModelId)
                };
                try
                {
                    propertyStore.SetValue(ref key, ref propVariant);
                    propertyStore.Commit();
                }
                finally
                {
                    PropVariantClear(ref propVariant);
                }

                ((IPersistFile)shellLink).Save(shortcutPath, true);
            }
            catch (COMException)
            {
            }
            catch (InvalidCastException)
            {
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLink);
        }
    }

    // PKEY_AppUserModel_ID: {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant propVariant);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, int propertyId)
    {
        public Guid FormatId = formatId;
        public int PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort VarType;

        [FieldOffset(8)]
        public nint PointerValue;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

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
