using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Windows;

public sealed class WindowsShortcutManager
{
    public OperationResult CreateApplicationShortcuts(string clientExecutablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult.Fail("WindowsRequired", "Shortcuts require Windows.");
        }

        if (!Path.IsPathFullyQualified(clientExecutablePath) ||
            !File.Exists(clientExecutablePath))
        {
            throw new FileNotFoundException(
                "Shortcut creation requires an existing absolute client executable.",
                clientExecutablePath);
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "1Salem Server Manager");
        Directory.CreateDirectory(startMenu);
        Create(
            Path.Combine(desktop, "1Salem Server Manager.lnk"),
            clientExecutablePath,
            string.Empty);
        Create(
            Path.Combine(startMenu, "1Salem Server Manager.lnk"),
            clientExecutablePath,
            string.Empty);
        Create(
            Path.Combine(startMenu, "1Salem Server Manager (Administrator).lnk"),
            clientExecutablePath,
            "--admin");
        return OperationResult.Ok();
    }

    public static string TaskbarPinInstructions =>
        "Open 1Salem Server Manager, right-click its taskbar icon, then choose " +
        "'Pin to taskbar'. Windows does not provide a supported silent pin API.";

    /// <summary>Test-only accessor: reads back PKEY_AppUserModel_ID from a saved shortcut.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? TryReadAppUserModelId(string linkPath)
    {
        var type = Type.GetTypeFromCLSID(
            new Guid("00021401-0000-0000-C000-000000000046"),
            true)!;
        var shellLink = (IShellLinkW)Activator.CreateInstance(type)!;
        try
        {
            ((IPersistFile)shellLink).Load(linkPath, 0);
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
    internal static void Create(string linkPath, string targetPath, string arguments)
    {
        var type = Type.GetTypeFromCLSID(
            new Guid("00021401-0000-0000-C000-000000000046"),
            true)!;
        var shellLink = (IShellLinkW)Activator.CreateInstance(type)!;
        shellLink.SetPath(Path.GetFullPath(targetPath));
        shellLink.SetArguments(arguments);
        shellLink.SetWorkingDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        shellLink.SetIconLocation(Path.GetFullPath(targetPath), 0);
        shellLink.SetDescription(
            string.IsNullOrEmpty(arguments)
                ? "1Salem Server Manager"
                : "1Salem Server Manager administrative tools");

        // Stamp the fixed Stable AppUserModelID onto the shortcut itself, independent of the
        // executable path it points at. Without this, Windows derives the pinned shortcut's
        // identity from the *target path* -- which changes across updates (the Stable launcher
        // resolves to a different Versions\<v>\Builds\<n> folder each time) -- so a taskbar
        // icon pinned from this shortcut could fail to merge with the running app's button.
        // Best-effort: a shortcut is still useful without this property, so a failure here
        // (e.g. an unexpected shell COM configuration) must not block shortcut creation.
        try
        {
            var propertyStore = (IPropertyStore)shellLink;
            var key = AppUserModelIdKey;
            // Built by hand rather than via the propsys.dll InitPropVariantFromString helper,
            // which is a C++-only inline function with no stable DLL export to P/Invoke.
            // VT_LPWSTR = 31; PropVariantClear (below) frees this CoTaskMem string correctly
            // for that variant type.
            var propVariant = new PropVariant
            {
                VarType = 31,
                PointerValue = Marshal.StringToCoTaskMemUni(ProductIdentity.AppUserModelId)
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
        }
        catch (COMException)
        {
        }
        catch (InvalidCastException)
        {
        }

        ((IPersistFile)shellLink).Save(linkPath, true);
        Marshal.FinalReleaseComObject(shellLink);
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
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(nint file, int maximumPath, nint findData, uint flags);
        void GetIDList(out nint itemIdList);
        void SetIDList(nint itemIdList);
        void GetDescription(nint name, int maximumName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(nint directory, int maximumPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(nint arguments, int maximumPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(nint iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
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
}
