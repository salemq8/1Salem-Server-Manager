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

    // --- network ------------------------------------------------------------------

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
}
