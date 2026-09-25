using System.Windows;
using System.Windows.Threading;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Shell;
using ServerManager.Connect.App.ViewModels;

namespace ServerManager.Connect.App;

/// <summary>Startup and shutdown only. All behaviour lives in the view models and services.</summary>
public partial class App : Application
{
    private ConnectAppComposition? _composition;
    private MainViewModel? _main;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _composition = ConnectAppComposition.Create();
        _main = new MainViewModel(_composition.Context);
        var window = new MainWindow { DataContext = _main };
        MainWindow = window;
        window.Show();
        await _main.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Shutdown();
        _composition?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Recorded, redacted, for Diagnostics. The friend gets one plain sentence rather than a
        // crash that would also drop every open session.
        _composition?.Context.Log.Record("unhandled", e.Exception);
        MessageBox.Show(Text.ErrorUnexpected, Text.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
