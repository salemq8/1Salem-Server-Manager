using System.Net;
using System.Security.Cryptography;
using System.Text;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Shell;
using ServerManager.Connect.App.Tests.Fakes;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.App.Updates;
using ServerManager.Connect.App.ViewModels;

namespace ServerManager.Connect.App.Tests;

/// <summary>
/// 1Salem Connect's built-in updater: version-then-build decisions, the release metadata it
/// trusts, verified downloads, the installed and portable flows, never updating under a
/// connected game, and how the next start reports the update.
/// </summary>
public sealed class UpdateTests : IDisposable
{
    private static readonly ConnectBuild Build11 = ConnectBuild.Create("1.5", 11);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "1salem-connect-app-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeUpdateHttp _http = new();
    private readonly FakeInstallerLauncher _launcher = new();
    private readonly FakeHandshake _handshake = new();
    private readonly ManualClock _clock = new();

    public UpdateTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- Version and build ------------------------------------------------------------------

    [Theory]
    [InlineData("1.5", 12, UpdateDecision.UpdateAvailable)]
    [InlineData("1.5", 11, UpdateDecision.UpToDate)]
    [InlineData("1.5", 10, UpdateDecision.OlderRejected)]
    [InlineData("1.6", 1, UpdateDecision.UpdateAvailable)]
    [InlineData("1.4", 99, UpdateDecision.OlderRejected)]
    public void Releases_are_compared_by_version_then_build(string version, int build, UpdateDecision expected) =>
        Assert.Equal(expected, ConnectUpdatePolicy.Decide(Build11, ConnectBuild.Create(version, build)));

    [Theory]
    [InlineData("1.5.12", 12)]
    [InlineData("1.5", 0)]
    [InlineData("v1.5", 12)]
    [InlineData("1.5-build-12", 12)]
    public void A_build_is_never_folded_into_a_dotted_version(string version, int build) =>
        Assert.False(ConnectBuild.TryCreate(version, build, out _));

    [Fact]
    public void Build_12_is_newer_than_build_9_even_though_1_5_9_sorts_after_1_5_12_as_text() =>
        Assert.True(ConnectBuild.Create("1.5", 12) > ConnectBuild.Create("1.5", 9));

    // ---- Release metadata -------------------------------------------------------------------

    [Fact]
    public void The_metadata_the_release_pipeline_writes_is_accepted()
    {
        foreach (var layout in new[] { false, true })
        {
            var manifest = ConnectUpdateManifestReader.Read(Encoding.UTF8.GetBytes(ReleaseFixtures.Manifest(powerShellLayout: layout)));

            Assert.Equal(ConnectBuild.Create("1.5", 12), manifest.Build);
            Assert.Equal("v1.5-build-12", manifest.ReleaseTag);
            Assert.Equal(new Uri("https://github.com/salemq8/1Salem-Server-Manager/releases/download/v1.5-build-12/1SalemConnect-Setup.exe"), manifest.Installer.Url);
            Assert.Equal(ReleaseFixtures.Sha256(ReleaseFixtures.Installer), manifest.Installer.Sha256);
            Assert.Equal("1SalemConnect-Portable.zip", manifest.Portable.FileName);
        }
    }

    [Fact]
    public void A_byte_order_mark_is_tolerated()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ReleaseFixtures.Manifest())).ToArray();
        Assert.Equal(12, ConnectUpdateManifestReader.Read(bytes).Build.BuildRevision);
    }

    public static TheoryData<string> UntrustedManifests() => new()
    {
        ReleaseFixtures.Manifest(repository: "someone-else/1Salem-Server-Manager"),
        ReleaseFixtures.Manifest(installerSha256: "not-a-hash"),
        ReleaseFixtures.Manifest(installerSize: 0),
        ReleaseFixtures.Manifest().Replace("\"releaseTag\": \"v1.5-build-12\"", "\"releaseTag\": \"v1.5-build-13\"", StringComparison.Ordinal),
        ReleaseFixtures.Manifest().Replace("https://github.com/salemq8/1Salem-Server-Manager/releases/download/v1.5-build-12/1SalemConnect-Setup.exe", "http://github.com/salemq8/1Salem-Server-Manager/releases/download/v1.5-build-12/1SalemConnect-Setup.exe", StringComparison.Ordinal),
        ReleaseFixtures.Manifest().Replace("https://github.com/salemq8/1Salem-Server-Manager/releases/download/v1.5-build-12/1SalemConnect-Setup.exe", "https://example.com/1SalemConnect-Setup.exe", StringComparison.Ordinal),
        ReleaseFixtures.Manifest().Replace("\"schema\": 1,", "\"schema\": 1, \"schema\": 1,", StringComparison.Ordinal),
        ReleaseFixtures.Manifest().Replace("\"product\": \"1Salem Connect\"", "\"product\": \"Something else\"", StringComparison.Ordinal),
        ReleaseFixtures.Manifest(version: "1.5.12"),
        "{ not json"
    };

    [Theory]
    [MemberData(nameof(UntrustedManifests))]
    public void Metadata_that_is_not_exactly_the_official_release_is_refused(string json) =>
        Assert.Throws<ConnectUpdateManifestException>(() => ConnectUpdateManifestReader.Read(Encoding.UTF8.GetBytes(json)));

    // ---- Downloads --------------------------------------------------------------------------

    [Fact]
    public async Task Release_assets_follow_GitHub_redirects_and_are_verified_before_use()
    {
        var manifest = Manifest();
        using var handler = new RoutedHandler(uri => uri.Host == "github.com"
            ? RoutedHandler.Redirect("https://release-assets.githubusercontent.com/asset/1")
            : RoutedHandler.Bytes(ReleaseFixtures.Installer));
        using var http = new GitHubUpdateHttp(Build11, handler);
        var target = Path.Combine(_root, "1SalemConnect-Setup.exe");

        await http.DownloadAsync(manifest.Installer, target, null, CancellationToken.None);

        Assert.Equal(ReleaseFixtures.Installer, File.ReadAllBytes(target));
        Assert.Equal(["github.com", "release-assets.githubusercontent.com"], handler.Requests.Select(uri => uri.Host));
    }

    [Fact]
    public async Task A_wrong_SHA256_is_rejected_and_nothing_is_kept()
    {
        var manifest = Manifest();
        var tampered = ReleaseFixtures.Installer.ToArray();
        tampered[^1] ^= 0xFF;
        using var handler = new RoutedHandler(_ => RoutedHandler.Bytes(tampered));
        using var http = new GitHubUpdateHttp(Build11, handler);
        var target = Path.Combine(_root, "1SalemConnect-Setup.exe");

        var failure = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => http.DownloadAsync(manifest.Installer, target, null, CancellationToken.None));

        Assert.Equal(UpdateDownloadFailure.HashMismatch, failure.Failure);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task An_incomplete_download_is_rejected_and_nothing_is_kept()
    {
        var manifest = Manifest();
        using var handler = new RoutedHandler(_ => RoutedHandler.Bytes(ReleaseFixtures.Installer[..^5]));
        using var http = new GitHubUpdateHttp(Build11, handler);

        var failure = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => http.DownloadAsync(manifest.Installer, Path.Combine(_root, "setup.exe"), null, CancellationToken.None));

        Assert.Equal(UpdateDownloadFailure.Incomplete, failure.Failure);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task A_redirect_away_from_GitHub_is_refused_before_it_is_contacted()
    {
        using var handler = new RoutedHandler(uri => uri.Host == "github.com"
            ? RoutedHandler.Redirect("https://downloads.example.com/1SalemConnect-Setup.exe")
            : RoutedHandler.Bytes(ReleaseFixtures.Installer));
        using var http = new GitHubUpdateHttp(Build11, handler);

        var failure = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => http.DownloadAsync(Manifest().Installer, Path.Combine(_root, "setup.exe"), null, CancellationToken.None));

        Assert.Equal(UpdateDownloadFailure.Refused, failure.Failure);
        Assert.DoesNotContain(handler.Requests, uri => uri.Host == "downloads.example.com");
    }

    [Fact]
    public async Task A_release_without_update_information_is_reported_as_such()
    {
        using var handler = new RoutedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new GitHubUpdateHttp(Build11, handler);

        Assert.Null(await http.GetManifestAsync(CancellationToken.None));
        Assert.Equal(ConnectUpdateSource.LatestManifestUrl, Assert.Single(handler.Requests));
    }

    // ---- Checking ---------------------------------------------------------------------------

    [Fact]
    public async Task A_newer_build_is_offered()
    {
        var updater = Updater();

        await updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.Available, updater.Status);
        Assert.Equal(12, updater.Offer!.Build.BuildRevision);
        Assert.Equal(_clock.UtcNow, updater.LastCheckedUtc);
    }

    [Fact]
    public async Task The_same_build_is_up_to_date()
    {
        _http.ManifestBytes = Encoding.UTF8.GetBytes(ReleaseFixtures.Manifest(build: 11));
        var updater = Updater();

        await updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, updater.Status);
        Assert.Null(updater.Offer);
    }

    [Fact]
    public async Task An_older_build_is_never_offered()
    {
        _http.ManifestBytes = Encoding.UTF8.GetBytes(ReleaseFixtures.Manifest(build: 10));
        var updater = Updater();

        await updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, updater.Status);
        Assert.Null(updater.Offer);
        Assert.Null(await updater.DownloadInstallerAsync(CancellationToken.None));
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task An_unreachable_GitHub_is_never_reported_as_up_to_date()
    {
        _http.ManifestFailure = new UpdateDownloadException(UpdateDownloadFailure.Unreachable, "offline");
        var updater = Updater();

        await updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.Unavailable, updater.Status);
        Assert.Equal(UpdateProblem.Unreachable, updater.Problem);
        Assert.Null(updater.LastCheckedUtc);
    }

    [Fact]
    public async Task Automatic_checks_happen_at_most_once_a_day()
    {
        using var app = Harness();

        await app.Main.CheckForUpdateAutomaticallyAsync(CancellationToken.None);
        await app.Main.CheckForUpdateAutomaticallyAsync(CancellationToken.None);
        app.Clock.UtcNow += TimeSpan.FromHours(23);
        await app.Main.CheckForUpdateAutomaticallyAsync(CancellationToken.None);

        Assert.Equal(1, _http.ManifestRequests);
        Assert.Equal("1Salem Connect update available — Build 12", app.Main.NoticeText);
        Assert.Empty(_http.Downloads);
        Assert.Empty(_launcher.Launches);

        app.Clock.UtcNow += TimeSpan.FromHours(2);
        await app.Main.CheckForUpdateAutomaticallyAsync(CancellationToken.None);
        Assert.Equal(2, _http.ManifestRequests);
    }

    // ---- Installed update ---------------------------------------------------------------------

    [Fact]
    public async Task Installed_copy_downloads_verifies_and_hands_the_update_to_Setup_then_closes()
    {
        var shell = new FakeAppShell();
        using var app = Harness(shell: shell);
        await app.Context.Updater!.CheckAsync(CancellationToken.None);
        app.Main.ShowSettings();
        var settings = Assert.IsType<SettingsViewModel>(app.Main.CurrentPage);

        await settings.UpdateNowAsync();

        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal(Path.Combine(_root, "downloads", "build-12", "1SalemConnect-Setup.exe"), launch.Path);
        Assert.Equal(ReleaseFixtures.Sha256(ReleaseFixtures.Installer), launch.Sha256);
        Assert.Matches(@"^--update --wait-pid 4242 --expected-version 1\.5 --expected-build 12 --token [0-9a-f]{32}$", launch.Arguments);
        Assert.Equal(1, shell.ShutdownCalls);
        var pending = new UpdateStateStore(StatePath).Load().Pending!;
        Assert.Equal((11, 12), (pending.FromBuild, pending.ToBuild));
        Assert.EndsWith(pending.Token, launch.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_download_is_never_run()
    {
        var tampered = ReleaseFixtures.Installer.ToArray();
        tampered[^1] ^= 0xFF;
        _http.DownloadBytes = tampered;
        var shell = new FakeAppShell();
        using var app = Harness(shell: shell);
        await app.Context.Updater!.CheckAsync(CancellationToken.None);
        app.Main.ShowSettings();
        var settings = Assert.IsType<SettingsViewModel>(app.Main.CurrentPage);

        await settings.UpdateNowAsync();

        Assert.Empty(_launcher.Launches);
        Assert.Equal(0, shell.ShutdownCalls);
        Assert.Equal(UpdateProblem.HashMismatch, app.Context.Updater.Problem);
        Assert.Equal("The downloaded file did not match the official release, so it was deleted and not run.", settings.ProblemText);
    }

    [Fact]
    public async Task Declining_the_administrator_prompt_changes_nothing()
    {
        _launcher.Outcome = InstallerLaunchOutcome.Cancelled;
        var shell = new FakeAppShell();
        using var app = Harness(shell: shell);
        await app.Context.Updater!.CheckAsync(CancellationToken.None);
        app.Main.ShowSettings();
        var settings = Assert.IsType<SettingsViewModel>(app.Main.CurrentPage);

        await settings.UpdateNowAsync();

        Assert.Equal(0, shell.ShutdownCalls);
        Assert.Null(new UpdateStateStore(StatePath).Load().Pending);
        Assert.Equal(UpdateStatus.Available, app.Context.Updater.Status);
        Assert.True(settings.ShowUpdateNow);
    }

    [Fact]
    public async Task A_connected_game_is_never_cut_off_by_an_update()
    {
        var shell = new FakeAppShell();
        using var app = Harness(shell: shell);
        var connection = await ConnectAsync(app);
        await app.Context.Updater!.CheckAsync(CancellationToken.None);
        app.Main.ShowSettings();
        var settings = Assert.IsType<SettingsViewModel>(app.Main.CurrentPage);

        await settings.UpdateNowAsync();

        Assert.True(settings.NeedsDisconnect);
        Assert.Empty(_launcher.Launches);
        Assert.Equal(ConnectionState.Connected, connection.State);

        settings.LaterCommand.Execute(null);
        Assert.False(settings.NeedsDisconnect);
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task Disconnect_and_update_closes_the_session_first()
    {
        var shell = new FakeAppShell();
        using var app = Harness(shell: shell);
        var connection = await ConnectAsync(app);
        await app.Context.Updater!.CheckAsync(CancellationToken.None);
        app.Main.ShowSettings();
        var settings = Assert.IsType<SettingsViewModel>(app.Main.CurrentPage);
        await settings.UpdateNowAsync();

        await settings.DisconnectAndUpdateAsync();

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal("ses_test1", Assert.Single(app.Transport.Closed));
        Assert.Single(_launcher.Launches);
        Assert.Equal(1, shell.ShutdownCalls);
    }

    [Fact]
    public async Task A_session_that_cannot_be_closed_stops_the_update()
    {
        var shell = new FakeAppShell();
        using var app = Harness(shell: shell);
        await ConnectAsync(app);
        await app.Context.Updater!.CheckAsync(CancellationToken.None);
        app.Main.ShowSettings();
        var settings = Assert.IsType<SettingsViewModel>(app.Main.CurrentPage);
        await settings.UpdateNowAsync();
        // The close may not have happened, and the transport cannot say either.
        app.Transport.CloseFailures.Enqueue(new TransportException(TransportErrorCodes.NoAnswer));
        app.Transport.StatusFailure = new TransportException(TransportErrorCodes.NoAnswer);

        await settings.DisconnectAndUpdateAsync();

        Assert.Empty(_launcher.Launches);
        Assert.Equal(0, shell.ShutdownCalls);
        Assert.Equal("The connection could not be closed yet. Try again in a moment.", settings.ProblemText);
    }

    // ---- Portable ---------------------------------------------------------------------------

    [Fact]
    public async Task A_portable_copy_is_told_about_the_new_package_and_never_overwritten()
    {
        var shell = new FakeAppShell();
        using var app = Harness(shell: shell, kind: InstallationKind.Portable);
        await app.Context.Updater!.CheckAsync(CancellationToken.None);
        app.Main.ShowSettings();
        var settings = Assert.IsType<SettingsViewModel>(app.Main.CurrentPage);

        Assert.False(settings.ShowUpdateNow);
        Assert.True(settings.ShowPortableDownload);
        await settings.UpdateNowAsync();
        settings.DownloadPortableCommand.Execute(null);

        Assert.Empty(_http.Downloads);
        Assert.Empty(_launcher.Launches);
        Assert.Equal(0, shell.ShutdownCalls);
        Assert.Equal(
            new Uri("https://github.com/salemq8/1Salem-Server-Manager/releases/download/v1.5-build-12/1SalemConnect-Portable.zip"),
            Assert.Single(shell.OpenedLinks));
    }

    [Fact]
    public void Only_the_registered_Program_Files_copy_counts_as_installed()
    {
        var root = Path.Combine(_root, "Program Files", "1Salem Connect");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, InstallationDetector.UninstallerFileName), "x");
        var elsewhere = Path.Combine(_root, "Downloads", "1SalemConnect-Portable");

        Assert.Equal(InstallationKind.Installed, InstallationDetector.Detect(root, root, () => root));
        Assert.Equal(InstallationKind.Installed, InstallationDetector.Detect(root + "\\", root, () => "\"" + root + "\""));
        Assert.Equal(InstallationKind.Portable, InstallationDetector.Detect(elsewhere, root, () => root));
        Assert.Equal(InstallationKind.Portable, InstallationDetector.Detect(root, root, () => null));
        File.Delete(Path.Combine(root, InstallationDetector.UninstallerFileName));
        Assert.Equal(InstallationKind.Portable, InstallationDetector.Detect(root, root, () => root));
    }

    // ---- After the update -------------------------------------------------------------------

    [Fact]
    public void The_new_build_confirms_it_started_and_says_so()
    {
        var token = UpdateProtocol.NewToken();
        new UpdateStateStore(StatePath).Save(new UpdateState(null, new PendingUpdate(token, "1.5", 11, "1.5", 12, _clock.UtcNow)));
        using var app = Harness(current: ConnectBuild.Create("1.5", 12));

        app.Main.ReviewLastUpdate();

        Assert.Equal([token], _handshake.Signals);
        Assert.Equal("1Salem Connect was updated to Build 12.", app.Main.NoticeText);
        Assert.False(app.Main.NoticeIsWarning);
        Assert.Null(new UpdateStateStore(StatePath).Load().Pending);
    }

    [Fact]
    public void An_update_that_was_rolled_back_leaves_the_working_app_and_offers_retry()
    {
        var token = UpdateProtocol.NewToken();
        new UpdateStateStore(StatePath).Save(new UpdateState(null, new PendingUpdate(token, "1.5", 11, "1.5", 12, _clock.UtcNow)));
        _handshake.Results[token] = new InstallerResult(InstallerResult.RolledBack, 11, 12, "did not start");
        using var app = Harness();

        app.Main.ReviewLastUpdate();

        Assert.Empty(_handshake.Signals);
        Assert.True(app.Main.NoticeIsWarning);
        Assert.Equal("The update to Build 12 did not finish. 1Salem Connect is unchanged and still works.", app.Main.NoticeText);
        Assert.Equal("Try again", app.Main.NoticeActionText);
        app.Main.NoticeActionCommand.Execute(null);
        Assert.IsType<SettingsViewModel>(app.Main.CurrentPage);
        Assert.Null(new UpdateStateStore(StatePath).Load().Pending);
    }

    [Fact]
    public void A_copy_opened_while_Setup_still_runs_does_not_end_the_update()
    {
        var token = UpdateProtocol.NewToken();
        new UpdateStateStore(StatePath).Save(new UpdateState(null, new PendingUpdate(token, "1.5", 11, "1.5", 12, _clock.UtcNow)));
        using var app = Harness();

        app.Main.ReviewLastUpdate();

        Assert.Equal("An update is being installed. 1Salem Connect will open again when it finishes.", app.Main.NoticeText);
        Assert.NotNull(new UpdateStateStore(StatePath).Load().Pending);
    }

    [Fact]
    public async Task The_friends_own_files_are_untouched_by_checking_downloading_and_handing_over()
    {
        var userData = Path.Combine(_root, "LocalAppData", "1Salem Connect");
        var files = new Dictionary<string, byte[]>
        {
            [Path.Combine(userData, "identity", "identity.v1.json")] = Encoding.UTF8.GetBytes("{\"protected\":\"device key\"}"),
            [Path.Combine(userData, "consumed-enrollments.json")] = Encoding.UTF8.GetBytes("{\"version\":2,\"memberships\":[\"mem_a\"]}"),
            [Path.Combine(userData, "transport", "ticket-keys.json")] = Encoding.UTF8.GetBytes("{}"),
            [Path.Combine(userData, "preferences.json")] = Encoding.UTF8.GetBytes("{\"theme\":\"Light\"}")
        };
        foreach (var (path, bytes) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        var updater = new ConnectUpdater(
            _http,
            new UpdateStateStore(Path.Combine(userData, "updates", UpdateStateStore.FileName)),
            _launcher,
            _handshake,
            _clock,
            new DiagnosticsLog(_clock),
            Build11,
            InstallationKind.Installed,
            Path.Combine(userData, "updates", "downloads"));

        await updater.CheckAsync(CancellationToken.None);
        var installer = await updater.DownloadInstallerAsync(CancellationToken.None);
        updater.StartInstaller(installer!, 4242);

        foreach (var (path, bytes) in files)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        Assert.All(
            Directory.EnumerateFiles(userData, "*", SearchOption.AllDirectories).Where(path => !files.ContainsKey(path)),
            path => Assert.StartsWith(Path.Combine(userData, "updates"), path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_protocol_arguments_need_no_quoting()
    {
        var arguments = UpdateProtocol.Arguments(4242, ConnectBuild.Create("1.5", 12), UpdateProtocol.NewToken());

        Assert.DoesNotContain('"', arguments);
        Assert.Throws<ArgumentException>(() => UpdateProtocol.Arguments(1, Build11, "../../evil"));
        Assert.Throws<ArgumentException>(() => UpdateProtocol.ResultPath("..\\..\\x"));
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private string StatePath => Path.Combine(_root, UpdateStateStore.FileName);

    private ConnectUpdater Updater(InstallationKind kind = InstallationKind.Installed, ConnectBuild? current = null, IAppClock? clock = null) =>
        new(
            _http,
            new UpdateStateStore(StatePath),
            _launcher,
            _handshake,
            clock ?? _clock,
            new DiagnosticsLog(_clock),
            current ?? Build11,
            kind,
            Path.Combine(_root, "downloads"));

    private AppHarness Harness(IAppShell? shell = null, InstallationKind kind = InstallationKind.Installed, ConnectBuild? current = null) =>
        new(updater: harness => Updater(kind, current, harness.Clock), shell: shell);

    private static ConnectUpdateManifest Manifest() =>
        ConnectUpdateManifestReader.Read(Encoding.UTF8.GetBytes(ReleaseFixtures.Manifest()));

    private static async Task<ConnectionViewModel> ConnectAsync(AppHarness app)
    {
        var membership = app.AddMembership(MembershipState.Approved, "nFAKE1CNTRL");
        app.Main.ShowConnection(membership);
        var connection = Assert.IsType<ConnectionViewModel>(app.Main.CurrentPage);
        await connection.ConnectAsync();
        Assert.Equal(ConnectionState.Connected, connection.State);
        return connection;
    }
}
