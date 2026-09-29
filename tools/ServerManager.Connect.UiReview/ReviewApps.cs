using System.Windows;

namespace ServerManager.Connect.UiReview;

// WPF's Application constructor queues OnStartup on the dispatcher, so each application is
// subclassed with a startup that does nothing, and its own App.xaml resources are loaded from
// source (AppResources). The real window, tray icon, composition, identity and transport never
// start, and nothing of the user's is read or written.

internal sealed class OwnerReviewApp : ServerManager.Client.App
{
    protected override void OnStartup(StartupEventArgs e)
    {
    }
}

internal sealed class FriendReviewApp : ServerManager.Connect.App.App
{
    protected override void OnStartup(StartupEventArgs e)
    {
    }

    protected override void OnExit(ExitEventArgs e)
    {
    }
}
