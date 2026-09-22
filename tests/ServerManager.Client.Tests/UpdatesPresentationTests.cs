using System.Globalization;
using System.Text.RegularExpressions;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Tests;

/// <summary>
/// Settings > Updates in Build 6: one simple card over the proven update flow. The rules here
/// are about honesty — the card must never claim more than the agent actually reported.
/// </summary>
public sealed class UpdatesPresentationTests
{
    private static readonly DateTimeOffset Checked = new(2026, 9, 21, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AFailedCheckIsNeverReportedAsUpToDate() =>
        InEnglish(() =>
        {
            var failedStage = Status(stage: ApplicationUpdateStage.Failed, lastError: "feed unreachable");
            var failedIdle = Status(lastError: "feed unreachable");

            Assert.Equal(
                LocalizationService.Get("Updates.LastStepFailed"),
                ApplicationUpdateControl.DescribeSimpleStatus(failedStage, refreshFailed: false));
            Assert.Equal(
                LocalizationService.Get("Updates.LastStepFailed"),
                ApplicationUpdateControl.DescribeSimpleStatus(failedIdle, refreshFailed: false));
        });

    [Fact]
    public void AnUnreachableAgentIsUnavailable_NotUpToDate() =>
        InEnglish(() => Assert.Equal(
            LocalizationService.Get("Updates.Unavailable"),
            ApplicationUpdateControl.DescribeSimpleStatus(null, refreshFailed: true)));

    [Fact]
    public void NeverCheckedSaysSo() =>
        InEnglish(() => Assert.Equal(
            LocalizationService.Get("Updates.NotCheckedYet"),
            ApplicationUpdateControl.DescribeSimpleStatus(Status(), refreshFailed: false)));

    [Fact]
    public void UpToDateOnlyAfterARealCheck_AndSaysWhen() =>
        InEnglish(() =>
        {
            var text = ApplicationUpdateControl.DescribeSimpleStatus(
                Status(lastChecked: Checked),
                refreshFailed: false);

            Assert.StartsWith("1Salem is up to date.", text, StringComparison.Ordinal);
            Assert.Contains(
                Checked.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                text,
                StringComparison.Ordinal);
        });

    /// <summary>
    /// The visible version stays 1.5 across builds, so "Version 1.5 is available" directly
    /// under "Version 1.5" reads as nonsense. The card says an update exists; the build that
    /// distinguishes it lives in Update details.
    /// </summary>
    [Fact]
    public void AnAvailableUpdateNeverRepeatsTheSameLookingVersion() =>
        InEnglish(() =>
        {
            var text = ApplicationUpdateControl.DescribeSimpleStatus(
                Status(stage: ApplicationUpdateStage.Available, available: true, latest: "1.5", lastChecked: Checked),
                refreshFailed: false);

            Assert.Equal(LocalizationService.Get("Updates.Available"), text);
            Assert.DoesNotContain("1.5", text, StringComparison.Ordinal);
        });

    [Fact]
    public void EveryUpdateStageHasAHumanLabelInBothLanguages()
    {
        foreach (var culture in new[] { "en-US", "ar-SA" })
        {
            InCulture(culture, () =>
            {
                foreach (var stage in Enum.GetValues<ApplicationUpdateStage>())
                {
                    Assert.True(
                        LocalizationService.HasKey($"Updates.Stage.{stage}"),
                        $"{stage} has no {culture} label and would show the raw enum name.");
                }
            });
        }
    }

    /// <summary>
    /// A ScrollViewer inside the Settings page's own ScrollViewer takes the mouse wheel even
    /// when it has nothing to scroll, so the page stopped scrolling under the pointer.
    /// </summary>
    [Fact]
    public void TheEmbeddedUpdateControlDoesNotScrollOnItsOwn()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "Controls", "ApplicationUpdateControl.xaml");

        Assert.DoesNotContain("<ScrollViewer", xaml, StringComparison.Ordinal);
    }

    /// <summary>Every visible string comes from the localization tables, not the XAML.</summary>
    [Fact]
    public void TheEmbeddedUpdateControlHasNoHardcodedText()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "Controls", "ApplicationUpdateControl.xaml");

        Assert.DoesNotMatch(new Regex(@"\b(Text|Content|Header|ToolTip)=""(?!\{)[^""]+"""), xaml);
    }

    [Fact]
    public void TheUpdateFlowSitsInsideTheUpdatesCard_NotASecondCard()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "Controls", "SettingsPageControl.xaml");

        Assert.Matches(
            new Regex(@"<Expander x:Name=""UpdateDetails""[\s\S]*?<controls:ApplicationUpdateControl x:Name=""UpdateInstaller"""),
            xaml);
    }

    /// <summary>The General category's only control used to be a checkbox that could never change.</summary>
    [Fact]
    public void GeneralOffersARealLabelledStartupToggle()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "Controls", "SettingsPageControl.xaml");

        Assert.DoesNotContain("MinimiseToTrayToggle", xaml, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"x:Name=""StartWithWindowsToggle""[\s\S]*?AutomationProperties\.LabeledBy=""\{Binding ElementName=StartWithWindowsLabel\}"""),
            xaml);
    }

    [Fact]
    public void TheSafeFileManagerButtonHasALabel()
    {
        var code = ReadSource("src", "ServerManager.Client", "Controls", "ServerSettingsTab.xaml.cs");

        Assert.Contains(
            "FileManagerButton.Content = LocalizationService.Get(\"ServerSettings.Files\")",
            code,
            StringComparison.Ordinal);
    }

    /// <summary>In-app links and the tray both call ShowDashboard; it must not un-maximise.</summary>
    [Fact]
    public void BringingTheWindowForwardKeepsItMaximised()
    {
        var window = ReadSource("src", "ServerManager.Client", "MainWindow.xaml.cs");

        Assert.Matches(
            new Regex(@"void ShowDashboard\(\)\s*\{\s*Show\(\);[\s\S]*?if \(WindowState == WindowState\.Minimized\)"),
            window);
    }

    [Fact]
    public void ALanguageOrThemeChangeDoesNotMoveTheSidebar()
    {
        var window = ReadSource("src", "ServerManager.Client", "MainWindow.xaml.cs");

        Assert.Contains(
            "preferences with { SidebarCollapsed = _viewModel.SidebarCollapsed }",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AboutShowsACopyrightLine_NotTheProductNameTwice() =>
        InEnglish(() => Assert.NotEqual(
            LocalizationService.Get("AppTitle"),
            LocalizationService.Get("Settings.Copyright")));

    private static ApplicationUpdateStatusResponse Status(
        ApplicationUpdateStage stage = ApplicationUpdateStage.Idle,
        bool available = false,
        string? latest = null,
        DateTimeOffset? lastChecked = null,
        string? lastError = null) =>
        new(
            CurrentVersion: "1.5",
            LatestVersion: latest,
            Channel: ApplicationUpdateChannel.Stable,
            Stage: stage,
            IsUpdateAvailable: available,
            AutomaticChecksEnabled: true,
            DownloadPercent: 0,
            DownloadedBytes: 0,
            PackageSize: null,
            ReleaseNotes: null,
            LastCheckedAtUtc: lastChecked,
            StagedPackagePath: null,
            RollbackStatus: null,
            LastError: lastError,
            CurrentBuildSigned: false,
            GameServerBusy: false,
            History: [],
            CurrentBuildRevision: 5,
            LatestBuildRevision: available ? 6 : null);

    private static void InEnglish(Action action) => InCulture("en-US", action);

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
