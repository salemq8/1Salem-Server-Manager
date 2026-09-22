using System.Globalization;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;

namespace ServerManager.Client.Tests;

/// <summary>
/// The three remaining top-level destinations. The backup-health rules get the most attention
/// because summarising a fleet by its newest backup is precisely the dishonesty the
/// foundation work set out to remove.
/// </summary>
public sealed class GlobalPagesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    // --- backup health ------------------------------------------------------------

    [Fact]
    public void ABackupTakenTodayIsHealthy() =>
        Assert.Equal(
            BackupHealth.Healthy,
            ServerPresentation.ClassifyBackup(Now.AddHours(-2), false, Now));

    [Fact]
    public void ABackupOlderThanThreeDaysIsDueSoon() =>
        Assert.Equal(
            BackupHealth.DueSoon,
            ServerPresentation.ClassifyBackup(Now.AddDays(-4), false, Now));

    [Fact]
    public void ABackupOlderThanAWeekIsOverdue() =>
        Assert.Equal(
            BackupHealth.Overdue,
            ServerPresentation.ClassifyBackup(Now.AddDays(-9), false, Now));

    [Fact]
    public void NeverHavingBackedUpIsOverdue_NotUnknown() =>
        Assert.Equal(
            BackupHealth.Overdue,
            ServerPresentation.ClassifyBackup(null, false, Now));

    [Fact]
    public void AFailedAttemptOutranksAFreshBackup() =>
        Assert.Equal(
            BackupHealth.Failed,
            ServerPresentation.ClassifyBackup(Now.AddMinutes(-5), true, Now));

    /// <summary>
    /// The whole point of the global page: one healthy server must not make the fleet look
    /// protected when another has not been backed up in months.
    /// </summary>
    [Fact]
    public void OneHealthyServerNeverMasksAnOverdueOne()
    {
        var overall = ServerPresentation.AggregateBackupHealth(
        [
            BackupHealth.Healthy,
            BackupHealth.Overdue,
            BackupHealth.Healthy
        ]);

        Assert.Equal(BackupHealth.Overdue, overall);
    }

    [Fact]
    public void AFailureOutranksEveryOtherState()
    {
        var overall = ServerPresentation.AggregateBackupHealth(
            [BackupHealth.Healthy, BackupHealth.Overdue, BackupHealth.Failed]);

        Assert.Equal(BackupHealth.Failed, overall);
    }

    [Fact]
    public void AllHealthyAggregatesToHealthy() =>
        Assert.Equal(
            BackupHealth.Healthy,
            ServerPresentation.AggregateBackupHealth([BackupHealth.Healthy, BackupHealth.Healthy]));

    [Fact]
    public void NoServersAggregatesToUnknown_NotHealthy() =>
        Assert.Equal(BackupHealth.Unknown, ServerPresentation.AggregateBackupHealth([]));

    [Fact]
    public void UnknownNeverOutranksARealProblem() =>
        Assert.Equal(
            BackupHealth.Overdue,
            ServerPresentation.AggregateBackupHealth([BackupHealth.Unknown, BackupHealth.Overdue]));

    [Fact]
    public void EveryBackupHealthHasALabelAndTone()
    {
        foreach (var health in Enum.GetValues<BackupHealth>())
        {
            var described = ServerPresentation.DescribeBackupHealth(health);
            Assert.False(
                string.IsNullOrWhiteSpace(described.Label),
                $"{health} has no label.");
            if (health is BackupHealth.Overdue or BackupHealth.DueSoon or BackupHealth.Failed)
            {
                Assert.NotEqual(UiStatusTone.Positive, described.Tone);
            }
        }
    }

    // --- remote access ------------------------------------------------------------

    [Fact]
    public void ALanOnlyServerIsNotReportedAsUnreachable()
    {
        var described = ServerPresentation.DescribeRemoteAccess(
            tunnelOnline: false,
            internetAddress: null);

        Assert.Equal(UiStatusTone.Neutral, described.Tone);
        Assert.Equal(LocalizationService.Get("RemoteAccess.NotSetUp"), described.Label);
    }

    [Fact]
    public void AConfiguredTunnelThatIsDownNeedsAttention()
    {
        var described = ServerPresentation.DescribeRemoteAccess(
            tunnelOnline: false,
            internetAddress: "example.at.ply.gg:7551");

        Assert.Equal(UiStatusTone.Caution, described.Tone);
    }

    // --- settings -----------------------------------------------------------------

    [Fact]
    public void EveryGlobalSettingsCategoryHasALabel()
    {
        foreach (var category in new[]
                 {
                     "General", "Appearance", "Backups", "Network",
                     "Updates", "Advanced", "About"
                 })
        {
            Assert.True(
                LocalizationService.HasKey($"Settings.Category.{category}"),
                $"Settings category '{category}' would render as a raw key.");
        }
    }

    [Fact]
    public void ThePolicyCategoriesExplainWhereTheRealControlsLive()
    {
        foreach (var category in new[] { "Backups", "Network" })
        {
            Assert.True(LocalizationService.HasKey($"Settings.{category}Policy"));
            Assert.True(LocalizationService.HasKey($"Settings.{category}PolicyAction"));
        }
    }

    /// <summary>
    /// The visible product version is 1.5. A four-part or build-suffixed version string must
    /// never reach the UI; the build revision is a diagnostic detail only.
    /// </summary>
    [Fact]
    public void TheVisibleVersionIsTheProductVersion_NotAVersionPlusBuild()
    {
        var shown = LocalizationService.Format("Settings.VersionValue", ProductInfo.Version);

        Assert.Contains(ProductInfo.Version, shown, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"{ProductInfo.Version}.{ProductInfo.BuildRevision}",
            shown,
            StringComparison.Ordinal);
        Assert.Equal(2, ProductInfo.Version.Split('.').Length);
    }

    [Fact]
    public void TheBuildRevisionIsShownSeparatelyFromTheVersion()
    {
        var build = LocalizationService.Format("Settings.BuildValue", ProductInfo.BuildRevision);

        Assert.Contains(
            ProductInfo.BuildRevision.ToString(CultureInfo.InvariantCulture),
            build,
            StringComparison.Ordinal);
    }

    // --- updates ------------------------------------------------------------------

    /// <summary>
    /// The updater is launched with --wait-pid and will not replace the Client while this
    /// process is alive. Build 5's shell exited from ExitForUpdateRequested; when the update
    /// control moved into Settings nothing handled the event any more, so Update Now left the
    /// updater waiting on a Client that never exited. The shell must exit exactly as before.
    /// </summary>
    [Fact]
    public void UpdateNow_ExitsTheClientSoTheUpdaterCanProceed()
    {
        var settingsXaml = ReadSource(
            "src", "ServerManager.Client", "Controls", "SettingsPageControl.xaml");
        var settings = ReadSource(
            "src", "ServerManager.Client", "Controls", "SettingsPageControl.xaml.cs");
        var window = ReadSource("src", "ServerManager.Client", "MainWindow.xaml.cs");

        Assert.Contains(
            "<controls:ApplicationUpdateControl x:Name=\"UpdateInstaller\"",
            settingsXaml,
            StringComparison.Ordinal);
        Assert.Matches(@"UpdateInstaller\.ExitForUpdateRequested\s*\+=", settings);
        Assert.Matches(
            @"SettingsPage\.ExitForUpdateRequested\s*\+=\s*\(_,\s*_\)\s*=>\s*\{\s*" +
            @"_allowClose\s*=\s*true;\s*System\.Windows\.Application\.Current\.Shutdown\(\);",
            window);
    }

    /// <summary>The update control owns a polling timer and an HttpClient, as in Build 5.</summary>
    [Fact]
    public void TheEmbeddedUpdateControlIsDisposedWhenTheShellCloses()
    {
        var settings = ReadSource(
            "src", "ServerManager.Client", "Controls", "SettingsPageControl.xaml.cs");
        var window = ReadSource("src", "ServerManager.Client", "MainWindow.xaml.cs");

        Assert.Matches(@"void Dispose\(\)\s*\{\s*UpdateInstaller\.Dispose\(\);", settings);
        Assert.Matches(@"void OnClosed\([^)]*\)\s*\{\s*SettingsPage\.Dispose\(\);", window);
    }

    // --- tray parity --------------------------------------------------------------

    /// <summary>
    /// Build 5's tray "Create … Server" opened that game's installer. Landing on the server
    /// list instead made the item look broken.
    /// </summary>
    [Fact]
    public void TrayCreateItem_OpensThatGamesInstaller()
    {
        var window = ReadSource("src", "ServerManager.Client", "MainWindow.xaml.cs");

        Assert.Matches(
            @"void OpenServerCreation\(GameType game\)\s*\{[^}]*ServersPage\.OpenInstaller\(game\);",
            window);
    }

    /// <summary>The tray passes Build 5 tab indices: 1 was Console and 2 was Settings.</summary>
    [Fact]
    public void TrayConsoleAndSettingsItems_OpenThoseTabsInServerDetail()
    {
        var window = ReadSource("src", "ServerManager.Client", "MainWindow.xaml.cs");

        Assert.Contains("1 => \"Console\"", window, StringComparison.Ordinal);
        Assert.Contains("2 => \"Settings\"", window, StringComparison.Ordinal);
        Assert.Matches(@"ServerDetailPage\.Show\(serverId,\s*tab\)", window);
    }

    // --- network ------------------------------------------------------------------

    /// <summary>
    /// LAN pairing was never reachable in a released build, the installed service runs
    /// without --lan so no other device can connect, and a pairing code grants full remote
    /// control. It must not be offered from the simple Network page.
    /// </summary>
    [Fact]
    public void NetworkPage_DoesNotOfferLanPairing()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "Controls", "NetworkPageControl.xaml");
        var code = ReadSource(
            "src", "ServerManager.Client", "Controls", "NetworkPageControl.xaml.cs");

        Assert.DoesNotContain("PairDevice", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("new LanPairingWindow", code, StringComparison.Ordinal);
        Assert.Contains("new NetworkStatusWindow", code, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkStringsExistForEveryStateThePageCanShow()
    {
        foreach (var key in new[]
                 {
                     "Network.Destination", "Network.Provider", "Network.ProviderPlayit",
                     "Network.ProviderNone", "Network.LocalNetwork", "Network.ReachableDetail",
                     "Network.UnreachableDetail", "Network.LocalOnlyDetail"
                 })
        {
            Assert.True(LocalizationService.HasKey(key), $"{key} is missing.");
        }
    }

    [Fact]
    public void GlobalPageStringsAlsoResolveInArabic()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");
            foreach (var key in new[]
                     {
                         "BackupHealth.Healthy", "BackupHealth.Overdue", "BackupHealth.Failed",
                         "Backups.ByServer", "Backups.Recent", "Network.Provider",
                         "Settings.Category.About", "Settings.VersionValue"
                     })
            {
                Assert.True(LocalizationService.HasKey(key), $"{key} has no Arabic value.");
            }
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
