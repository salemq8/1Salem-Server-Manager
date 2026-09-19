using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ServerManager.Contracts;

namespace ServerManager.Infrastructure.Updates;

public sealed record StableShortcutMigrationResult(
    int Inspected,
    int Updated,
    IReadOnlyList<string> Failures);

public static class StableShortcutMigration
{
    private const string ClientExecutableName = "1Salem.ServerManager.exe";

    public static StableShortcutMigrationResult RetargetInstalledShortcuts(
        string installRoot,
        string stableLauncher,
        IEnumerable<string>? searchRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableLauncher);

        var root = Path.GetFullPath(installRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var launcher = Path.GetFullPath(stableLauncher);
        if (!launcher.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(launcher))
        {
            throw new FileNotFoundException(
                "The permanent launcher must exist inside the installation root.",
                launcher);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new StableShortcutMigrationResult(0, 0, []);
        }

        return RetargetOnWindows(
            root,
            launcher,
            searchRoots ?? GetDefaultSearchRoots());
    }

    public static bool IsVersionedClientTarget(string installRoot, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        var root = Path.GetFullPath(installRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(targetPath);
        if (!target.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(target).Equals(
                ClientExecutableName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, target);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4 &&
            segments[0].Equals("Versions", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("Client", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetDefaultSearchRoots()
    {
        yield return Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(
            Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(
            Environment.SpecialFolder.CommonPrograms);

        var roaming = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roaming))
        {
            yield return Path.Combine(
                roaming,
                "Microsoft",
                "Internet Explorer",
                "Quick Launch",
                "User Pinned",
                "TaskBar");
        }
    }

    [SupportedOSPlatform("windows")]
    private static StableShortcutMigrationResult RetargetOnWindows(
        string installRoot,
        string stableLauncher,
        IEnumerable<string> searchRoots)
    {
        var inspected = 0;
        var updated = 0;
        var failures = new List<string>();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var searchRoot in searchRoots.Where(value =>
                     !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                if (!Directory.Exists(searchRoot))
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(
                             searchRoot,
                             "*.lnk",
                             SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(path).Contains(
                            "1Salem",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(Path.GetFullPath(path));
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{searchRoot}: {exception.Message}");
            }
        }

        foreach (var shortcutPath in candidates)
        {
            inspected++;
            try
            {
                if (RetargetShortcut(
                        shortcutPath,
                        installRoot,
                        stableLauncher))
                {
                    updated++;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    COMException or TargetInvocationException or
                    InvalidOperationException)
            {
                failures.Add($"{shortcutPath}: {exception.GetBaseException().Message}");
            }
        }

        return new StableShortcutMigrationResult(inspected, updated, failures);
    }

    [SupportedOSPlatform("windows")]
    private static bool RetargetShortcut(
        string shortcutPath,
        string installRoot,
        string stableLauncher)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ??
            throw new InvalidOperationException(
                "Windows shortcut service is unavailable.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType) ??
                throw new InvalidOperationException(
                    "Windows shortcut service could not be started.");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath]) ??
                throw new InvalidOperationException(
                    "Windows did not open the shortcut for migration.");
            var shortcutType = shortcut.GetType();
            var target = shortcutType.InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string;
            if (!IsVersionedClientTarget(installRoot, target))
            {
                return false;
            }

            SetProperty(shortcutType, shortcut, "TargetPath", stableLauncher);
            SetProperty(
                shortcutType,
                shortcut,
                "WorkingDirectory",
                Path.GetDirectoryName(stableLauncher)!);
            SetProperty(shortcutType, shortcut, "IconLocation", $"{stableLauncher},0");
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                null,
                shortcut,
                null);

            // This shortcut just had its target retargeted onto the Stable launcher (confirmed
            // by IsVersionedClientTarget above), so it must carry the same fixed Stable
            // AppUserModelID the running Client process sets on itself -- otherwise Windows
            // falls back to a path-derived identity that changes across updates, and a taskbar
            // icon pinned from this shortcut can fail to merge with the running app's button.
            ProductIdentity.StampShortcutAppUserModelId(shortcutPath, ProductIdentity.AppUserModelId);
            return true;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void SetProperty(
        Type type,
        object target,
        string name,
        object value) =>
        type.InvokeMember(
            name,
            BindingFlags.SetProperty,
            null,
            target,
            [value]);

    [SupportedOSPlatform("windows")]
    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }
}
