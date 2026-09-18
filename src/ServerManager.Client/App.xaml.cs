using System.Windows;
using System.ComponentModel;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client;

public partial class App : System.Windows.Application
{
    private TrayIconService? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Must happen before the first top-level window is created (Windows freezes the
        // AppUserModelID at that point) so the taskbar button for this running process merges
        // with a shortcut pinned via WindowsShortcutManager, which stamps the same ID onto the
        // .lnk itself -- neither alone is sufficient, since the two are matched by this ID, not
        // by the (version-dependent) executable path.
        ProductIdentity.ApplyExplicitAppUserModelId(ProductIdentity.AppUserModelId);
        base.OnStartup(e);
        var preferences = new UiPreferencesStore().Load();
        LocalizationService.Apply(preferences.Language);
        ThemeService.Apply(preferences.Theme);
        var launchMode = ClientLaunchModeParser.Parse(e.Args);
        if (launchMode == ClientLaunchMode.Administrator &&
            !ElevationService.IsAdministrator())
        {
            try
            {
                ElevationService.RelaunchAsAdministrator(e.Args);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                System.Windows.MessageBox.Show(
                    "Administrator mode was cancelled. No changes were made.",
                    "1Salem Server Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        var window = new MainWindow(new MainViewModel(launchMode));
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow = window;
        _trayIcon = new TrayIconService(window, ExitDashboard);
        if (e.Args.Any(
                argument => argument.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            window.Hide();
        }
        else
        {
            window.Show();
        }
    }

    private void ExitDashboard()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (MainWindow is MainWindow window)
        {
            window.PrepareForExit();
            window.Close();
        }

        Shutdown();
    }
}
