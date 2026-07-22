using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace ServerManager.Client.Shell;

public static class ElevationService
{
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RelaunchAsAdministrator(IEnumerable<string> originalArguments)
    {
        ArgumentNullException.ThrowIfNull(originalArguments);
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "1Salem.ServerManager.exe");
        if (!File.Exists(executable))
        {
            executable = Environment.ProcessPath ??
                throw new InvalidOperationException("The client executable path is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in originalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!originalArguments.Any(
                argument => argument.Equals("--admin", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.ArgumentList.Add("--admin");
        }

        Process.Start(startInfo);
    }
}
