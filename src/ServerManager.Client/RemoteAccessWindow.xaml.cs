using ServerManager.Client.Shell;
using Window = System.Windows.Window;

namespace ServerManager.Client;

/// <summary>
/// Remote-access setup and management, reachable from the Network page and from the legacy
/// server editor's "Open Remote Access". Build 6 dropped the Remote Access destination from
/// the sidebar; without this window nothing could install, link, stop or disable Playit, or
/// change the public address a server advertises.
/// </summary>
public partial class RemoteAccessWindow : Window
{
    public RemoteAccessWindow()
    {
        InitializeComponent();
        Title = $"{LocalizationService.Get("RemoteAccess")} - {LocalizationService.Get("AppTitle")}";
        Closed += (_, _) => RemoteAccess.Dispose();
    }

    /// <summary>Opens remote access as a modal owned by the calling window.</summary>
    public static void Open(Window? owner) =>
        new RemoteAccessWindow { Owner = owner }.ShowDialog();
}
