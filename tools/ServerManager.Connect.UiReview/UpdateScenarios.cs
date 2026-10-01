using System.IO;
using System.Windows;
using System.Windows.Media;
using ServerManager.Connect.App;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Shell;
using ServerManager.Connect.App.Tests.Fakes;
using ServerManager.Connect.App.Updates;
using ServerManager.Connect.App.ViewModels;

namespace ServerManager.Connect.UiReview;

/// <summary>
/// The real 1Salem Connect window and Settings page with the updater wired to fakes: the update
/// notice, an update available, the disconnect-first prompt, the light theme, a declined
/// administrator prompt, a portable copy, and the notice after an update. Nothing is downloaded
/// or installed, and the app never closes.
/// </summary>
internal static class UpdateScenarios
{
    private const string Label = "Salem's world";

    public static async Task RunAsync(Report report)
    {
        var root = Path.Combine(Path.GetTempPath(), "1salem-uireview-updates-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            await RunCoreAsync(report, root);
        }
        finally
        {
            ConnectTheme.Apply(Application.Current.Resources, ThemeChoice.Dark);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task RunCoreAsync(Report report, string root)
    {
        var clock = new ManualClock();
        var broker = new FakeBroker { SessionExpiresAt = clock.UtcNow.AddMinutes(10) };
        var transport = new FakeTransport();
        using var identity = TestIds.NewDevice();
        var ownerId = TestIds.NewOwnerId();
        var http = new FakeUpdateHttp();
        var launcher = new FakeInstallerLauncher { Outcome = InstallerLaunchOutcome.Cancelled };
        var handshake = new FakeHandshake();
        var shell = new ReviewShell();
        var log = new DiagnosticsLog(clock);
        var member = new Membership("mem_" + new string('a', 26), ownerId, "5b0f7f2e-3c1a-4d57-9a7e-2f1d8c0b6a41", Label,
            MembershipState.Approved, "nFAKE1CNTRL", MembershipNodeState.Confirmed);
        broker.Memberships.Add(member);

        ConnectUpdater Updater(InstallationKind kind, ConnectBuild current, UpdateStateStore? store = null) =>
            new(http, store ?? new UpdateStateStore(null), launcher, handshake, clock, log, current, kind, Path.Combine(root, "downloads"));

        ConnectAppContext Context(ConnectUpdater updater) => new()
        {
            Settings = new ConnectAppSettings
            {
                BrokerUrl = new Uri("https://broker.test/"),
                TransportExecutablePath = @"C:\1salem-connect-review-missing\1Salem.Connect.Transport.exe"
            },
            Services = new ConnectServices(broker, identity, new FakeTransportProcess()),
            Transport = transport,
            Clock = clock,
            Log = log,
            Clipboard = new FakeClipboard(),
            Updater = updater,
            Preferences = new ConnectPreferencesStore(null),
            Shell = shell
        };

        // Installed copy, dark.
        var main = new MainViewModel(Context(Updater(InstallationKind.Installed, ConnectBuild.Create("1.5", 11))));
        var window = new MainWindow { DataContext = main };
        Report.Show(window);

        main.ShowServers();
        await Call(main, "CheckForUpdateAutomaticallyAsync", CancellationToken.None);
        await report.CaptureAsync("01-update-notice-dark", window);
        report.Check(main.NoticeText == Text.Format(Text.NoticeUpdateAvailable, "12") && main.HasNoticeAction,
            $"non-blocking notice offers the update: {main.NoticeText}");

        main.NoticeActionCommand.Execute(null);
        var settings = (SettingsViewModel)main.CurrentPage!;
        await Report.SettleAsync(400);
        await report.CaptureAsync("02-settings-update-available-dark", window);
        report.Check(settings.ShowUpdateNow && settings.StatusText == Text.UpdatesStatusAvailable &&
                     settings.VersionText == "1.5" && settings.BuildText == "11" &&
                     settings.AvailableBuildText == Text.Format(Text.UpdatesAvailableBuild, "12"),
            "Settings shows version 1.5, build 11, Update available, Build 12 and Update now");
        report.Check(!main.HasNotice, "opening Settings from the notice clears it");

        // Connected: never updated out from under the game.
        main.ShowConnection(member);
        var connection = (ConnectionViewModel)main.CurrentPage!;
        await connection.ConnectAsync();
        main.ShowSettings();
        settings = (SettingsViewModel)main.CurrentPage!;
        await Call(settings, "UpdateNowAsync");
        await report.CaptureAsync("03-settings-disconnect-first-dark", window);
        report.Check(settings.NeedsDisconnect && launcher.Launches.Count == 0 && connection.State == ConnectionState.Connected,
            "connected: Disconnect before updating, nothing launched, still connected");

        // Light theme, then the administrator prompt declined.
        settings.SelectedTheme = settings.ThemeOptions.Single(option => option.Choice == ThemeChoice.Light);
        await Report.SettleAsync(300);
        var surface = ((SolidColorBrush)Application.Current.Resources["SurfaceBrush"]).Color;
        report.Check(surface == (Color)ColorConverter.ConvertFromString("#F4F6F9"), $"light theme applied (surface {surface})");
        await Call(settings, "DisconnectAndUpdateAsync");
        await report.CaptureAsync("04-settings-update-declined-light", window);
        report.Check(connection.State == ConnectionState.Disconnected && launcher.Launches.Count == 1 &&
                     shell.ShutdownCalls == 0 && settings.ProblemText == Text.UpdatesProblemCancelled,
            "disconnected first, then a declined prompt changed nothing");
        window.Close();
        main.Shutdown();

        // Portable copy, light.
        var portable = new MainViewModel(Context(Updater(InstallationKind.Portable, ConnectBuild.Create("1.5", 11))));
        var portableWindow = new MainWindow { DataContext = portable };
        Report.Show(portableWindow);
        portable.ShowSettings();
        await Report.SettleAsync(400);
        var portableSettings = (SettingsViewModel)portable.CurrentPage!;
        await report.CaptureAsync("05-settings-portable-light", portableWindow);
        report.Check(!portableSettings.ShowUpdateNow && portableSettings.ShowPortableDownload,
            "portable copy: newer portable package offered, no in-place update");
        portableWindow.Close();
        portable.Shutdown();

        // After the update: the new build says so.
        ConnectTheme.Apply(Application.Current.Resources, ThemeChoice.Dark);
        var store = new UpdateStateStore(null);
        store.Save(new UpdateState(null, new PendingUpdate(UpdateProtocol.NewToken(), "1.5", 11, "1.5", 12, clock.UtcNow)));
        var updated = new MainViewModel(Context(Updater(InstallationKind.Installed, ConnectBuild.Create("1.5", 12), store)));
        var updatedWindow = new MainWindow { DataContext = updated };
        Report.Show(updatedWindow);
        await updated.InitializeAsync();
        await report.CaptureAsync("06-updated-notice-dark", updatedWindow, keyboard: false);
        report.Check(updated.NoticeText == Text.Format(Text.NoticeUpdated, "12") && handshake.Signals.Count == 1,
            "the new build confirms it started and says so");
        updatedWindow.Close();
        updated.Shutdown();
    }

    /// <summary>The view models' internal steps, as FriendScenarios drives them.</summary>
    private static Task Call(object target, string method, params object[] arguments) =>
        (Task)target.GetType().GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .Invoke(target, arguments)!;

    private sealed class ReviewShell : IAppShell
    {
        public int ProcessId => 4242;

        public int ShutdownCalls { get; private set; }

        public void Shutdown() => ShutdownCalls++;

        public bool OpenReleaseLink(Uri address) => ConnectUpdateSource.IsAllowedHost(address);

        public void ApplyTheme(ThemeChoice choice) => ConnectTheme.Apply(Application.Current.Resources, choice);
    }
}
