using System.Globalization;
using System.IO;
using System.Windows;

namespace ServerManager.Connect.Setup;

/// <summary>
/// 1SalemConnect-Setup.exe. No arguments: the install window. <c>--uninstall</c>: the remove
/// window (Windows' Installed apps runs this). <c>--quiet</c> with either: no window, exit code
/// 0 on success, 2 when blocked (for example the app is running), 1 on failure.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var uninstall = args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        var quiet = args.Any(arg => arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        if (quiet)
        {
            return RunQuiet(uninstall);
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return app.Run(new SetupWindow(uninstall ? SetupMode.Uninstall : ConnectInstaller.HasPayload ? SetupMode.Install : SetupMode.NoPayload));
    }

    private static int RunQuiet(bool uninstall)
    {
        var log = Path.Combine(Path.GetTempPath(), "1SalemConnect-Setup.log");
        void Write(string line) => File.AppendAllText(log, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");

        // Synchronous on purpose: Progress<T> would post to the thread pool here, where two log
        // writes could collide and the exception would escape the try below.
        var progress = new SynchronousProgress(Write);
        try
        {
            Write(uninstall ? "uninstall start" : "install start");
            if (uninstall)
            {
                ConnectInstaller.Uninstall(progress);
            }
            else
            {
                ConnectInstaller.Install(progress);
            }

            Write("done");
            return 0;
        }
        catch (InstallerBlockedException exception)
        {
            Write("blocked: " + exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Write("failed: " + exception);
            return 1;
        }
    }

    public static bool Arabic => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft &&
                                 CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        private readonly object _gate = new();

        public void Report(string value)
        {
            lock (_gate)
            {
                report(value);
            }
        }
    }
}
