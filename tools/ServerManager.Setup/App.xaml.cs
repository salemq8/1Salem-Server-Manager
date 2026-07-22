using System.Windows;
using System.IO;

namespace ServerManager.Setup;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(
                argument => argument.Equals(
                    "--unattended-install",
                    StringComparison.OrdinalIgnoreCase)))
        {
            await RunUnattendedInstallAsync(e.Args);
            return;
        }

        if (e.Args.Any(
                argument => argument.Equals(
                    "--unattended-uninstall",
                    StringComparison.OrdinalIgnoreCase)))
        {
            await RunUnattendedUninstallAsync(e.Args);
            return;
        }

        if (e.Args.Any(
                argument => argument.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            var result = System.Windows.MessageBox.Show(
                "Remove the application, service, firewall rules, and shortcuts? " +
                "All server files, backups, and ProgramData will be preserved.",
                "Uninstall 1Salem Server Manager",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                var message = await InstallerEngine.UninstallAsync(AppContext.BaseDirectory);
                System.Windows.MessageBox.Show(message, "Uninstall", MessageBoxButton.OK);
            }

            Shutdown();
            return;
        }

        new InstallerWindow().Show();
    }

    private async Task RunUnattendedInstallAsync(IReadOnlyList<string> arguments)
    {
        var resultPath = GetArgumentValue(arguments, "--result-file") ??
            Path.Combine(
                Path.GetTempPath(),
                "1SalemServerManager",
                "Installer",
                "unattended-install-result.txt");
        try
        {
            var installRoot = GetArgumentValue(arguments, "--install-root") ??
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "1Salem Server Manager");
            var result = await InstallerEngine.InstallAsync(
                new InstallRequest(
                    "AllInOne",
                    installRoot,
                    DesktopShortcut: true,
                    StartMenuShortcut: true,
                    InstallAgentService: true,
                    AddFirewallRules: true,
                    StartWithWindows: true,
                    LaunchAfterInstall: true),
                new Progress<InstallProgress>());
            WriteResult(
                resultPath,
                $"SUCCESS{Environment.NewLine}" +
                $"Log={result.LogPath}{Environment.NewLine}" +
                $"ServiceHealthVerified={result.ServiceHealthVerified}" +
                $"{Environment.NewLine}Warnings={string.Join(" | ", result.Warnings)}");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            WriteResult(
                resultPath,
                $"FAILED{Environment.NewLine}{exception}{Environment.NewLine}" +
                $"Log={InstallerEngine.LastLogPath}");
            Shutdown(1);
        }
    }

    private async Task RunUnattendedUninstallAsync(IReadOnlyList<string> arguments)
    {
        var resultPath = GetArgumentValue(arguments, "--result-file") ??
            Path.Combine(
                Path.GetTempPath(),
                "1SalemServerManager",
                "Installer",
                "unattended-uninstall-result.txt");
        try
        {
            var message = await InstallerEngine.UninstallAsync(AppContext.BaseDirectory);
            WriteResult(resultPath, $"SUCCESS{Environment.NewLine}{message}");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            WriteResult(
                resultPath,
                $"FAILED{Environment.NewLine}{exception}{Environment.NewLine}" +
                $"Log={InstallerEngine.LastLogPath}");
            Shutdown(1);
        }
    }

    private static string? GetArgumentValue(
        IReadOnlyList<string> arguments,
        string name)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith(
                    $"{name}=",
                    StringComparison.OrdinalIgnoreCase))
            {
                return argument[(name.Length + 1)..];
            }

            if (argument.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count)
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static void WriteResult(string path, string value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, value);
    }
}
