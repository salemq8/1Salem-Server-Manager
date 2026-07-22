using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

    [SupportedOSPlatform("windows")]
    private static void Create(string linkPath, string targetPath, string arguments)
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
        ((IPersistFile)shellLink).Save(linkPath, true);
        Marshal.FinalReleaseComObject(shellLink);
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
