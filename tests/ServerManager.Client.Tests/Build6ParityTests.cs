using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;

namespace ServerManager.Client.Tests;

/// <summary>
/// Capability parity and honesty repairs found by the Build 6 audit: things Build 5 could do
/// that Build 6 had quietly lost, and places the new pages said more than they knew.
/// </summary>
public sealed class Build6ParityTests
{
    // --- remote access ------------------------------------------------------------

    /// <summary>
    /// Build 6 removed the Remote Access destination and nothing else hosted its control, so
    /// Playit could not be installed, linked, stopped or disabled, and a server's public
    /// address could not be changed. It is reachable again from the Network page.
    /// </summary>
    [Fact]
    public void RemoteAccessSetupIsReachableFromTheNetworkPage()
    {
        var window = ReadSource("src", "ServerManager.Client", "RemoteAccessWindow.xaml");
        var network = ReadSource("src", "ServerManager.Client", "Controls", "NetworkPageControl.xaml.cs");
        var networkXaml = ReadSource("src", "ServerManager.Client", "Controls", "NetworkPageControl.xaml");

        Assert.Contains("<controls:RemoteAccessControl", window, StringComparison.Ordinal);
        Assert.Contains("RemoteAccessWindow.Open(", network, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RemoteAccessButton\"", networkXaml, StringComparison.Ordinal);
    }

    /// <summary>The legacy editor's "Open Remote Access" had no subscriber and did nothing.</summary>
    [Fact]
    public void TheLegacyEditorsOpenRemoteAccessItemWorks()
    {
        var editor = ReadSource("src", "ServerManager.Client", "LegacyServerEditorWindow.xaml.cs");

        Assert.Matches(
            new Regex(@"LegacyPage\.RemoteAccessRequested\s*\+=[^;]*RemoteAccessWindow\.Open\("),
            editor);
    }

    /// <summary>Setup guidance must not name one particular person's Playit agent.</summary>
    [Fact]
    public void RemoteAccessGuidanceIsNotTiedToOneInstallation()
    {
        var control = ReadSource("src", "ServerManager.Client", "Controls", "RemoteAccessControl.xaml.cs");

        Assert.DoesNotContain("ALSarabeetMC", control, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteAccessButtonLabelsExistInBothLanguages()
    {
        foreach (var culture in new[] { "en-US", "ar-SA" })
        {
            InCulture(culture, () =>
            {
                Assert.True(LocalizationService.HasKey("Network.SetUpRemoteAccess"));
                Assert.True(LocalizationService.HasKey("Network.ManageRemoteAccess"));
            });
        }
    }

    // --- one remote-access answer across pages ------------------------------------

    private sealed record FakeServer(string Name, bool Online, string? Address);

    [Fact]
    public void AnOnlineTunnelIsPreferred()
    {
        var chosen = ServerPresentation.SelectRemoteAccessServer(
            [new FakeServer("mc", false, null), new FakeServer("pal", true, "p.example:1")],
            server => server.Online,
            server => server.Address);

        Assert.Equal("pal", chosen?.Name);
    }

    /// <summary>
    /// The case where Home and Network used to disagree: the first server has no address and
    /// the second has one whose tunnel is down. Both pages now describe the second.
    /// </summary>
    [Fact]
    public void AConfiguredAddressIsNeverHiddenBehindAServerWithout()
    {
        var chosen = ServerPresentation.SelectRemoteAccessServer(
            [new FakeServer("mc", false, null), new FakeServer("pal", false, "p.example:1")],
            server => server.Online,
            server => server.Address);

        Assert.Equal("pal", chosen?.Name);
    }

    [Fact]
    public void WithNoAddressAnywhereTheFirstServerIsDescribed()
    {
        var chosen = ServerPresentation.SelectRemoteAccessServer(
            [new FakeServer("mc", false, null), new FakeServer("pal", false, " ")],
            server => server.Online,
            server => server.Address);

        Assert.Equal("mc", chosen?.Name);
        Assert.Null(ServerPresentation.SelectRemoteAccessServer(
            Array.Empty<FakeServer>(),
            server => server.Online,
            server => server.Address));
    }

    [Fact]
    public void HomeAndNetworkUseTheSameSelector()
    {
        foreach (var page in new[] { "HomePageControl.xaml.cs", "NetworkPageControl.xaml.cs" })
        {
            Assert.Contains(
                "ServerPresentation.SelectRemoteAccessServer(",
                ReadSource("src", "ServerManager.Client", "Controls", page),
                StringComparison.Ordinal);
        }
    }

    // --- start --------------------------------------------------------------------

    /// <summary>
    /// A card labelled Start only opened Server Detail; the server stayed stopped. It now
    /// starts it through Server Detail's own checked start path, as Build 5's Home did.
    /// </summary>
    [Fact]
    public void TheCardStartButtonReallyStartsTheServer()
    {
        var window = ReadSource("src", "ServerManager.Client", "MainWindow.xaml.cs");
        var detail = ReadSource("src", "ServerManager.Client", "Controls", "ServerDetailPageControl.xaml.cs");

        Assert.Contains(
            "HomePage.ServerStartRequested += async (_, serverId) => await StartServerAsync(serverId);",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "ServersPage.ServerStartRequested += async (_, serverId) => await StartServerAsync(serverId);",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "public Task StartAsync() => RunActionAsync(\"start\");",
            detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackupsOpensTheServersBackupsTab()
    {
        var window = ReadSource("src", "ServerManager.Client", "MainWindow.xaml.cs");

        Assert.Contains(
            "BackupsPage.BackupRequested += (_, serverId) => OpenServer(serverId, \"Backups\");",
            window,
            StringComparison.Ordinal);
    }

    // --- connection state (two-poll grace) -----------------------------------------

    /// <summary>
    /// One failed first poll used to drop the skeleton without raising the error state, so
    /// pages rendered with no data and claimed things like "no internet address is set up".
    /// </summary>
    [Fact]
    public void ThePageSettlesUntilDataOrTheErrorThreshold()
    {
        var feed = ReadSource("src", "ServerManager.Client", "Controls", "DashboardFeed.cs");

        Assert.Contains(
            "public bool ShowSkeleton => !HasLoadedOnce && !ShowErrorState;",
            feed,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool ShowEmptyState => HasLoadedOnce && !ShowErrorState && Servers.Count == 0;",
            feed,
            StringComparison.Ordinal);
    }

    /// <summary>The header used to turn red on a single dropped poll.</summary>
    [Fact]
    public void TheHeaderHonoursTheSameGrace()
    {
        var model = ReadSource("src", "ServerManager.Client", "Shell", "MainViewModel.cs");

        Assert.Contains(
            "_agentConnected && !_feed.ShowErrorState;",
            model,
            StringComparison.Ordinal);
        Assert.Contains("PipeFailuresBeforeLost = 2", model, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectingIsNeutral_NotRed()
    {
        var window = ReadSource("src", "ServerManager.Client", "MainWindow.xaml");

        Assert.Matches(
            new Regex(@"Binding=""\{Binding IsConnecting\}""\s+Value=""True"">\s*<Setter Property=""Fill""\s+Value=""\{DynamicResource TextTertiaryBrush\}"""),
            window);
    }

    [Fact]
    public void TheVisibleRefreshButtonRefreshesPageData()
    {
        var model = ReadSource("src", "ServerManager.Client", "Shell", "MainViewModel.cs");

        Assert.Contains("new AsyncRelayCommand(RefreshAllAsync)", model, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"Task RefreshAllAsync\(\)\s*\{\s*await RefreshAsync\(\);\s*await _feed\.RefreshAsync\(\);"),
            model);
    }

    // --- administrator rights ----------------------------------------------------

    /// <summary>
    /// The main shortcuts start the app without elevation, and the Agent's credential is
    /// readable only by Administrators, so every page said "can't reach its background
    /// service" although the service had answered. A refused, non-elevated session now says
    /// what is actually wrong and offers the fix; an elevated session never sees it.
    /// </summary>
    [Fact]
    public void ARefusedNonElevatedSessionIsToldItNeedsAdministrator()
    {
        var feed = ReadSource("src", "ServerManager.Client", "Controls", "DashboardFeed.cs");
        var state = ReadSource("src", "ServerManager.Client", "Controls", "PageStateView.xaml.cs");

        Assert.Contains(
            "public bool NeedsElevation => ShowErrorState && IsAccessDenied && !IsElevated.Value;",
            feed,
            StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"StatusCode: System\.Net\.HttpStatusCode\.Unauthorized or System\.Net\.HttpStatusCode\.Forbidden"),
            feed);
        Assert.Contains("feed.NeedsElevation", state, StringComparison.Ordinal);
        Assert.Contains("RestartAsAdministrator()", state, StringComparison.Ordinal);
    }

    [Fact]
    public void RestartingAsAdministratorReusesTheProvenRelaunch()
    {
        var app = ReadSource("src", "ServerManager.Client", "App.xaml.cs");

        Assert.Matches(
            new Regex(@"void RestartAsAdministrator\(\)[\s\S]*?ElevationService\.RelaunchAsAdministrator\([\s\S]*?NativeErrorCode == 1223[\s\S]*?ExitDashboard\(\);"),
            app);
    }

    // --- backups ------------------------------------------------------------------

    [Fact]
    public void BackupFailuresAreExplained_NotABareStatusCode() =>
        InCulture("en-US", () =>
        {
            foreach (var status in new[]
                     {
                         HttpStatusCode.Conflict, HttpStatusCode.NotFound,
                         HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError
                     })
            {
                var text = BackupsPageControl.DescribeFailure(status);
                Assert.DoesNotMatch(new Regex(@"^\d{3}$"), text);
                Assert.False(string.IsNullOrWhiteSpace(text));
            }

            Assert.Equal(
                LocalizationService.Get("Backups.Error.Busy"),
                BackupsPageControl.DescribeFailure(HttpStatusCode.Conflict));
        });

    [Fact]
    public void BackupRowButtonsSayWhichBackupTheyActOn()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "Controls", "BackupsPageControl.xaml");

        foreach (var name in new[] { "OpenName", "RestoreName", "VerifyName", "DeleteName" })
        {
            Assert.Contains(
                $"AutomationProperties.Name=\"{{Binding {name}}}\"",
                xaml,
                StringComparison.Ordinal);
        }
    }

    /// <summary>A listing failure is not "no backups", and the page covers every server.</summary>
    [Fact]
    public void AFailedBackupListIsNotReportedAsEmpty()
    {
        var code = ReadSource("src", "ServerManager.Client", "Controls", "BackupsPageControl.xaml.cs");

        Assert.Contains("Backups.ListUnavailable", code, StringComparison.Ordinal);
        Assert.Contains("Backups.NoneAnywhere", code, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalizationService.Get(\"Backups.None\")", code, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupToastsNeverShowRawExceptionText()
    {
        var code = ReadSource("src", "ServerManager.Client", "Controls", "BackupsPageControl.xaml.cs");

        Assert.DoesNotContain("exception.Message", code, StringComparison.Ordinal);
    }

    // --- resources ----------------------------------------------------------------

    [Fact]
    public void TrayCustomResourceModeOpensThePolicyEditor()
    {
        var tray = ReadSource("src", "ServerManager.Client", "Shell", "TrayIconService.cs");
        var window = ReadSource("src", "ServerManager.Client", "MainWindow.xaml.cs");

        Assert.Contains("_window.OpenResourcePolicy", tray, StringComparison.Ordinal);
        Assert.Contains("new ResourceGovernorWindow { Owner = this }.ShowDialog();", window, StringComparison.Ordinal);
    }

    /// <summary>An Agent 409 used to throw out of an async void click and end the process.</summary>
    [Fact]
    public void TheResourceGovernorNeverThrowsOutOfAClickHandler()
    {
        var code = ReadSource("src", "ServerManager.Client", "ResourceGovernorWindow.xaml.cs");

        Assert.DoesNotContain("EnsureSuccessStatusCode", code, StringComparison.Ordinal);
    }

    [Fact]
    public void WarningCardsFollowTheTheme()
    {
        foreach (var path in new[]
                 {
                     new[] { "src", "ServerManager.Client", "ResourceGovernorWindow.xaml" },
                     new[] { "src", "ServerManager.Client", "Controls", "PalworldMemoryPerformanceControl.xaml" }
                 })
        {
            var xaml = ReadSource(path);
            Assert.DoesNotContain("#2B2117", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("#342919", xaml, StringComparison.Ordinal);
        }
    }

    // --- accessibility ------------------------------------------------------------

    [Fact]
    public void ToastsAreALiveRegion()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "MainWindow.xaml");
        var code = ReadSource("src", "ServerManager.Client", "MainWindow.xaml.cs");

        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationEvents.LiveRegionChanged", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdvancedDisclosureShowsKeyboardFocus()
    {
        var app = ReadSource("src", "ServerManager.Client", "App.xaml");
        var style = Regex.Match(app, @"x:Key=""AdvancedDisclosureStyle""[\s\S]*?</Style>").Value;

        Assert.Contains("FocusVisualStyle=\"{x:Null}\"", style, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"Property=""IsKeyboardFocused"" Value=""True"">\s*<Setter TargetName=""HeaderBorder""\s+Property=""BorderBrush""\s+Value=""\{DynamicResource FocusBrush\}"""),
            style);
    }

    private static void InCulture(string name, Action action)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static string ReadSource(params string[] parts)
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root!.FullName, .. parts]));
    }
}
