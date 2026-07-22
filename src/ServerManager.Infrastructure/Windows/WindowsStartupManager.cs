using Microsoft.Win32;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Windows;

public sealed class WindowsStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "1Salem Server Manager";

    public OperationResult SetEnabled(string clientExecutablePath, bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult.Fail("WindowsRequired", "Startup registration requires Windows.");
        }

        if (!Path.IsPathFullyQualified(clientExecutablePath) ||
            !File.Exists(clientExecutablePath))
        {
            throw new FileNotFoundException(
                "Startup registration requires an existing absolute client executable.",
                clientExecutablePath);
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Path.GetFullPath(clientExecutablePath)}\" --minimized");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }

        return OperationResult.Ok();
    }

    public bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string;
    }
}
