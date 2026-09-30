using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace ServerManager.Connect.Setup;

/// <summary>A reason to stop that the person can fix, for example 1Salem Connect still running.</summary>
internal sealed class InstallerBlockedException(string message) : Exception(message);

/// <summary>
/// Installs 1Salem Connect into Program Files from the payload embedded in this exe, registers
/// it with Windows (Start Menu, desktop, Installed apps) and removes it again. The app's own
/// state stays in each user's %LOCALAPPDATA%\1Salem Connect and is never touched here.
/// </summary>
internal static class ConnectInstaller
{
    public const string DisplayName = "1Salem Connect";
    public const string AppExecutable = "1Salem.Connect.exe";
    public const string TransportExecutable = "1Salem.Connect.Transport.exe";
    public const string SettingsFile = "1Salem.Connect.settings.json";
    public const string UninstallerExecutable = "1Salem.Connect.Setup.exe";
    public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\1SalemConnect";
    private const string PayloadResource = "1Salem.Connect.Payload.zip";

    public static string InstallRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), DisplayName);

    public static string StartMenuShortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), DisplayName + ".lnk");

    public static string DesktopShortcut { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), DisplayName + ".lnk");

    public static string InstalledApp => Path.Combine(InstallRoot, AppExecutable);

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo(PayloadResource) is not null;

    public static void Install(IProgress<string>? progress = null)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource) ??
            throw new InstallerBlockedException("This copy of Setup does not contain 1Salem Connect. Download 1SalemConnect-Setup.exe again.");
        ThrowIfRunning();

        var parent = Path.GetDirectoryName(InstallRoot)!;

        // Leftovers of an install that was stopped half way.
        foreach (var stale in Directory.EnumerateDirectories(parent, DisplayName + ".new-*")
                     .Concat(Directory.EnumerateDirectories(parent, DisplayName + ".old-*")))
        {
            TryDelete(stale);
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var staging = Path.Combine(parent, $"{DisplayName}.new-{suffix}");
        var previous = Path.Combine(parent, $"{DisplayName}.old-{suffix}");

        progress?.Report("Copying files");
        try
        {
            Extract(payload, staging);
            foreach (var required in new[] { AppExecutable, TransportExecutable, SettingsFile, UninstallerExecutable })
            {
                if (!File.Exists(Path.Combine(staging, required)))
                {
                    throw new InvalidOperationException($"The installer payload is incomplete: {required} is missing.");
                }
            }

            // Swap folders so an interrupted install never leaves half an app behind.
            if (Directory.Exists(InstallRoot))
            {
                Directory.Move(InstallRoot, previous);
            }

            try
            {
                Directory.Move(staging, InstallRoot);
            }
            catch
            {
                if (!Directory.Exists(InstallRoot) && Directory.Exists(previous))
                {
                    Directory.Move(previous, InstallRoot);
                }

                throw;
            }
        }
        finally
        {
            TryDelete(staging);
            TryDelete(previous);
        }

        progress?.Report("Adding shortcuts");
        CreateShortcut(StartMenuShortcut, InstalledApp);
        CreateShortcut(DesktopShortcut, InstalledApp);

        progress?.Report("Registering with Windows");
        Register();
    }

    public static void Uninstall(IProgress<string>? progress = null)
    {
        ThrowIfRunning();
        progress?.Report("Removing shortcuts");
        foreach (var shortcut in new[] { StartMenuShortcut, DesktopShortcut })
        {
            if (File.Exists(shortcut))
            {
                File.Delete(shortcut);
            }
        }

        progress?.Report("Removing the uninstall entry");
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);

        progress?.Report("Removing files");
        if (!Directory.Exists(InstallRoot))
        {
            return;
        }

        var running = Path.GetFullPath(Environment.ProcessPath ?? string.Empty);
        if (running.StartsWith(InstallRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            // This uninstaller runs from that folder and has its runtime loaded, so a helper
            // waits for this process to exit (however long its window stays open) and then
            // removes the folder.
            var root = InstallRoot.Replace("'", "''", StringComparison.Ordinal);
            var script = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; " +
                         "Start-Sleep -Milliseconds 500; " +
                         $"Remove-Item -LiteralPath '{root}' -Recurse -Force -ErrorAction SilentlyContinue";
            var helper = new ProcessStartInfo("powershell.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script })
            {
                helper.ArgumentList.Add(argument);
            }

            Process.Start(helper);
        }
        else
        {
            Directory.Delete(InstallRoot, recursive: true);
        }
    }

    /// <summary>
    /// Starts the installed app as the signed-in user rather than elevated: this installer runs as
    /// administrator, and 1Salem Connect must not.
    /// </summary>
    public static void LaunchInstalledApp() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{InstalledApp}\"") { UseShellExecute = false });

    /// <summary>Processes started from the install folder, found by their real path, never by name.</summary>
    public static IReadOnlyList<string> RunningFromInstallRoot()
    {
        var prefix = InstallRoot + Path.DirectorySeparatorChar;
        var self = Environment.ProcessId;
        var found = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id != self &&
                    process.MainModule?.FileName is { } path &&
                    path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add($"{Path.GetFileName(path)} ({process.Id})");
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited, or not ours to inspect.
            }
            finally
            {
                process.Dispose();
            }
        }

        return found;
    }

    private static void ThrowIfRunning()
    {
        if (RunningFromInstallRoot().Count > 0)
        {
            throw new InstallerBlockedException("1Salem Connect is running. Close its window, then try again.");
        }
    }

    /// <summary>Unpacks the payload, refusing any entry that would land outside the target folder.</summary>
    private static void Extract(Stream payload, string target)
    {
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The installer payload contains an unsafe path: {entry.FullName}");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static void Register()
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKeyPath, writable: true);
        var uninstaller = Path.Combine(InstallRoot, UninstallerExecutable);
        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", DisplayVersion());
        key.SetValue("Publisher", "1Salem");
        key.SetValue("DisplayIcon", $"{InstalledApp},0");
        key.SetValue("InstallLocation", InstallRoot);
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --quiet");
        key.SetValue("URLInfoAbout", "https://github.com/salemq8/1Salem-Server-Manager");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimatedSizeKb(), RegistryValueKind.DWord);
    }

    private static string DisplayVersion()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(InstallRoot, "build-info.json")));
            var root = document.RootElement;
            return $"{root.GetProperty("productVersion").GetString()} (Build {root.GetProperty("buildRevision").GetInt32()})";
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return FileVersionInfo.GetVersionInfo(InstalledApp).ProductVersion ?? "1.5";
        }
    }

    private static int EstimatedSizeKb() =>
        (int)Math.Min(int.MaxValue, new DirectoryInfo(InstallRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length) / 1024);

    private static void CreateShortcut(string path, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ??
            throw new InvalidOperationException("The Windows shortcut service is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target);
        shortcut.IconLocation = $"{target},0";
        shortcut.Description = DisplayName;
        shortcut.Save();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
