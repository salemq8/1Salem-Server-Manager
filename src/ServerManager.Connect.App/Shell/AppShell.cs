using System.Diagnostics;
using System.Windows;
using ServerManager.Connect.App.Updates;

namespace ServerManager.Connect.App.Shell;

/// <summary>What view models may ask of the running application; faked in tests.</summary>
public interface IAppShell
{
    int ProcessId { get; }

    /// <summary>Closes the app the normal way (sessions closed, transport stopped), for an update.</summary>
    void Shutdown();

    /// <summary>Opens an official release address in the browser. False for anything else.</summary>
    bool OpenReleaseLink(Uri address);

    void ApplyTheme(ThemeChoice choice);
}

public sealed class WpfAppShell : IAppShell
{
    public int ProcessId => Environment.ProcessId;

    // Posted, so the command that asked for it finishes before the window closes.
    public void Shutdown() =>
        Application.Current?.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());

    public bool OpenReleaseLink(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!ConnectUpdateSource.IsAllowedHost(address))
        {
            return false;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public void ApplyTheme(ThemeChoice choice)
    {
        if (Application.Current is { } application)
        {
            ConnectTheme.Apply(application.Resources, choice);
        }
    }
}
