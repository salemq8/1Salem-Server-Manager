using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace ServerManager.Connect.UiReview;

/// <summary>
/// One process per application: WPF allows a single Application, and each app's own App.xaml
/// resources are loaded without running its startup (so nothing real is composed or contacted).
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("owner" or "friend" or "content" or "gameplay"))
        {
            Console.Error.WriteLine("usage: ServerManager.Connect.UiReview owner|friend|content|gameplay <output directory> <culture>");
            return 2;
        }

        var culture = CultureInfo.GetCultureInfo(args[2]);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        var report = new Report(Path.GetFullPath(args[1]), args[0], culture.Name);

        Func<Task> scenarios;
        FakeAgent? agent = null;
        if (args[0] is "content" or "gameplay")
        {
            // The real Server Manager Content tab against the in-process fake Agent.
            agent = new FakeAgent();
            Environment.SetEnvironmentVariable("ONE_SALEM_AGENT_URL", agent.Url);
            Environment.SetEnvironmentVariable("ONE_SALEM_AGENT_DATA_ROOT", agent.DataRoot);
            Environment.SetEnvironmentVariable("ONE_SALEM_AGENT_PIPE", "1Salem.UiReview." + Guid.NewGuid().ToString("N"));
            var app = new OwnerReviewApp { Resources = AppResources.Load(@"src\ServerManager.Client\App.xaml") };
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ServerManager.Client.Shell.LocalizationService.Apply(culture.Name);
            ServerManager.Client.Shell.ThemeService.Apply(ServerManager.Client.Shell.AppTheme.Dark);
            scenarios = args[0] == "content"
                ? () => ContentScenarios.RunAsync(agent, report)
                : () => GameplayScenarios.RunAsync(agent, report);
        }
        else if (args[0] == "owner")
        {
            // Point every owner client at the in-process fake before any client type is touched,
            // so no request can reach an installed Agent.
            agent = new FakeAgent();
            Environment.SetEnvironmentVariable("ONE_SALEM_AGENT_URL", agent.Url);
            Environment.SetEnvironmentVariable("ONE_SALEM_AGENT_DATA_ROOT", agent.DataRoot);
            Environment.SetEnvironmentVariable("ONE_SALEM_AGENT_PIPE", "1Salem.UiReview." + Guid.NewGuid().ToString("N"));
            var app = new OwnerReviewApp { Resources = AppResources.Load(@"src\ServerManager.Client\App.xaml") };
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ServerManager.Client.Shell.LocalizationService.Apply(culture.Name);
            ServerManager.Client.Shell.ThemeService.Apply(ServerManager.Client.Shell.AppTheme.Dark);
            scenarios = () => OwnerScenarios.RunAsync(agent, report);
        }
        else
        {
            var app = new FriendReviewApp { Resources = AppResources.Load(@"src\ServerManager.Connect.App\App.xaml") };
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            scenarios = () => FriendScenarios.RunAsync(report);
        }

        var exit = 1;
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                await scenarios();
                exit = report.Failures == 0 ? 0 : 1;
            }
            catch (Exception exception)
            {
                report.Note("RUN FAILED: " + exception);
            }
            finally
            {
                report.Write();
                agent?.Dispose();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        return exit;
    }
}
