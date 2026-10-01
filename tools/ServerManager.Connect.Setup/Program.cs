using System.Globalization;
using System.IO;
using System.Windows;

namespace ServerManager.Connect.Setup;

/// <summary>
/// 1SalemConnect-Setup.exe. No arguments: the install window. <c>--uninstall</c>: the remove
/// window (Windows' Installed apps runs this). <c>--update --wait-pid N --expected-version V
/// --expected-build B --token T</c>: the update the app started (it has verified this exe and
/// closed itself). <c>--quiet</c> with any of them: no window. Exit codes: 0 success, 2 blocked
/// (for example the app is still running), 3 an update that was rolled back, 1 failure.
/// </summary>
internal static class Program
{
    private const string SetupMutexName = @"Global\1SalemConnect.Setup";

    [STAThread]
    public static int Main(string[] args)
    {
        var uninstall = args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        var quiet = args.Any(arg => arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        var updating = args.Any(arg => arg.Equals(UpdateProtocol.UpdateArgument, StringComparison.OrdinalIgnoreCase));
        var update = UpdateRequest.Parse(args);
        if (updating && update is null)
        {
            Log("update refused: the request is not valid");
            return (int)UpdateOutcome.Failed;
        }

        // One Setup at a time: two could otherwise swap the same folder at once.
        using var mutex = new Mutex(false, SetupMutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.FromMinutes(2));
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            Log("blocked: another 1Salem Connect Setup is running");
            return (int)UpdateOutcome.Blocked;
        }

        try
        {
            if (update is not null)
            {
                return quiet ? (int)RunUpdate(update, new SynchronousProgress(Log)).Outcome : RunWindow(SetupMode.Update, update);
            }

            if (quiet)
            {
                return RunQuiet(uninstall);
            }

            return RunWindow(uninstall ? SetupMode.Uninstall : ConnectInstaller.HasPayload ? SetupMode.Install : SetupMode.NoPayload, null);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>The update itself, shared by the window and quiet mode.</summary>
    internal static UpdateRunResult RunUpdate(UpdateRequest request, IProgress<string>? progress)
    {
        Log($"update start: build {request.ExpectedBuild}, app process {request.WaitPid}");
        using var host = new WindowsUpdateHost(ConnectInstaller.InstallRoot);
        var result = new ConnectUpdate(new ConnectInstallation(ConnectInstaller.InstallRoot), host)
            .Run(request, ConnectInstaller.OpenPayload, progress);
        Log($"update {result.Outcome}: {result.Message}");
        return result;
    }

    private static int RunWindow(SetupMode mode, UpdateRequest? update)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return app.Run(new SetupWindow(mode, update));
    }

    private static int RunQuiet(bool uninstall)
    {
        // Synchronous on purpose: Progress<T> would post to the thread pool here, where two log
        // writes could collide and the exception would escape the try below.
        var progress = new SynchronousProgress(Log);
        try
        {
            Log(uninstall ? "uninstall start" : "install start");
            if (uninstall)
            {
                ConnectInstaller.Uninstall(progress);
            }
            else
            {
                ConnectInstaller.Install(progress);
            }

            Log("done");
            return 0;
        }
        catch (InstallerBlockedException exception)
        {
            Log("blocked: " + exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Log("failed: " + exception);
            return 1;
        }
    }

    private static readonly object LogGate = new();

    internal static void Log(string line)
    {
        try
        {
            lock (LogGate)
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "1SalemConnect-Setup.log"),
                    $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
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
