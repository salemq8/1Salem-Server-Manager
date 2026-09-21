using System.Globalization;
using System.Text.RegularExpressions;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;

namespace ServerManager.Client.Tests;

/// <summary>
/// The product rule these cover: never display a value the app cannot actually know. Each of
/// these locks in a specific way the UI was previously stating something untrue.
/// </summary>
public sealed class PresentationHonestyTests
{
    [Fact]
    public void FormatPlayers_ReportsUnknownRatherThanZero_WhenTheServerIsNotReporting()
    {
        // null means "not reporting", which is not the same as an empty server.
        Assert.Equal("—", ServerPresentation.FormatPlayers(null, 32));
        Assert.Equal("—", ServerPresentation.FormatPlayers(null, null));
    }

    [Fact]
    public void FormatPlayers_KeepsOnlineBeforeMaximum()
    {
        // Culture is pinned because another test class switches the process to Arabic and
        // xUnit runs classes in parallel; under RTL this string carries bidi isolates.
        InCulture("en-US", () =>
            Assert.Equal("3 / 32", ServerPresentation.FormatPlayers(3, 32)));
    }

    [Fact]
    public void FormatPlayers_IsIdenticalUnderRtl_AndCarriesNoInvisibleControlCharacters()
    {
        // Reading order is pinned by the view (MetricValueStyle sets FlowDirection), because
        // WPF ignores Unicode isolates. The string itself must stay clean so it does not leak
        // control characters into accessible names or the clipboard.
        var ltr = InCultureResult("en-US", () => ServerPresentation.FormatPlayers(3, 32));
        var rtl = InCultureResult("ar-SA", () => ServerPresentation.FormatPlayers(3, 32));

        Assert.Equal("3 / 32", ltr);
        Assert.Equal(ltr, rtl);
        Assert.DoesNotContain(rtl, c => char.GetUnicodeCategory(c) == UnicodeCategory.Format);
    }

    /// <summary>
    /// The RTL reading-order guarantee lives in the view, so it is asserted there: the metric
    /// style must pin FlowDirection or "0 / 32" renders as "32 / 0" in Arabic.
    /// </summary>
    [Fact]
    public void MetricValueStyle_PinsLeftToRight_SoNumericPairsDoNotReverseUnderRtl()
    {
        var app = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "ServerManager.Client", "App.xaml"));

        var styleStart = app.IndexOf("x:Key=\"MetricValueStyle\"", StringComparison.Ordinal);
        Assert.True(styleStart >= 0, "MetricValueStyle is missing.");
        var styleEnd = app.IndexOf("</Style>", styleStart, StringComparison.Ordinal);
        var style = app[styleStart..styleEnd];

        Assert.Contains("FlowDirection", style, StringComparison.Ordinal);
        Assert.Contains("LeftToRight", style, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatPlayers_OmitsTheMaximum_WhenTheServerDoesNotPublishOne()
    {
        InCulture("en-US", () =>
        {
            Assert.Equal("3", ServerPresentation.FormatPlayers(3, 0));
            Assert.Equal("3", ServerPresentation.FormatPlayers(3, null));
        });
    }

    /// <summary>
    /// Brushes declared in XAML are frozen, so applying a theme replaces the resource instead
    /// of recolouring it. Anything that resolved a brush in code keeps the old object, which
    /// left the "Running" badge dark green on a white card after switching to Light. The
    /// signal that tells those consumers to re-resolve must exist and fire.
    /// </summary>
    [Fact]
    public void ApplyingATheme_RaisesThemeChanged_SoCodeResolvedBrushesReResolve()
    {
        var fired = 0;
        EventHandler handler = (_, _) => fired++;
        ThemeService.ThemeChanged += handler;
        try
        {
            ThemeService.Apply(AppTheme.Light);
            ThemeService.Apply(AppTheme.Dark);
        }
        finally
        {
            ThemeService.ThemeChanged -= handler;
        }

        Assert.Equal(2, fired);
    }

    private static void InCulture(string culture, Action action) =>
        InCultureResult(culture, () => { action(); return 0; });

    private static T InCultureResult<T>(string culture, Func<T> action)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void FormatUptimeAndMemory_ReportUnknownRatherThanZero()
    {
        Assert.Equal("—", ServerPresentation.FormatUptime(null));
        Assert.Equal("—", ServerPresentation.FormatUptime(TimeSpan.Zero));
        Assert.Equal("—", ServerPresentation.FormatMemory(0));
    }

    [Fact]
    public void SummarizeHealth_DoesNotWarn_WhenRemoteAccessWasNeverSetUp()
    {
        // A LAN-only server has no tunnel by design. Warning about it forever would train
        // the person to ignore the one line on Home that is supposed to mean something.
        var health = ServerPresentation.SummarizeHealth(
            agentConnected: true,
            serverStatuses: [UiStatus.Running],
            remoteAccessOnline: false,
            anyServerRunning: true,
            connectionAttempted: true,
            remoteAccessConfigured: false);

        Assert.Equal(UiStatusTone.Positive, health.Tone);
    }

    [Fact]
    public void SummarizeHealth_Warns_WhenRemoteAccessWasSetUpButIsDown()
    {
        var health = ServerPresentation.SummarizeHealth(
            agentConnected: true,
            serverStatuses: [UiStatus.Running],
            remoteAccessOnline: false,
            anyServerRunning: true,
            connectionAttempted: true,
            remoteAccessConfigured: true);

        Assert.Equal(UiStatusTone.Caution, health.Tone);
    }

    [Fact]
    public void SummarizeHealth_SaysConnecting_OnlyBeforeAPollHasComeBack()
    {
        var beforeFirstPoll = ServerPresentation.SummarizeHealth(
            agentConnected: false,
            serverStatuses: [],
            remoteAccessOnline: false,
            anyServerRunning: false,
            connectionAttempted: false);

        var afterAFailedPoll = ServerPresentation.SummarizeHealth(
            agentConnected: false,
            serverStatuses: [],
            remoteAccessOnline: false,
            anyServerRunning: false,
            connectionAttempted: true);

        // Repeating "connecting" after a failure leaves the person waiting on nothing.
        Assert.NotEqual(beforeFirstPoll.Headline, afterAFailedPoll.Headline);
        Assert.Equal(UiStatusTone.Negative, afterAFailedPoll.Tone);
    }

    [Fact]
    public void DescribeBackup_SurfacesNeverBackedUpAsSomethingToActOn()
    {
        var described = ServerPresentation.DescribeBackup(
            lastBackupUtc: null,
            nowUtc: DateTimeOffset.UnixEpoch);

        Assert.Equal(UiStatusTone.Caution, described.Tone);
        Assert.False(string.IsNullOrWhiteSpace(described.Label));
    }

    [Fact]
    public void DescribeBackup_TreatsAWeekOldBackupAsSomethingToActOn()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(30);

        var fresh = ServerPresentation.DescribeBackup(now.AddHours(-2), now);
        var stale = ServerPresentation.DescribeBackup(now.AddDays(-9), now);

        Assert.Equal(UiStatusTone.Positive, fresh.Tone);
        Assert.Equal(UiStatusTone.Caution, stale.Tone);
    }

    /// <summary>
    /// Every user-visible phrase must come from the localization tables. These were hardcoded
    /// English sentences built by concatenation, which can never be translated and reorder
    /// wrongly under RTL.
    /// </summary>
    [Fact]
    public void BackupAndGameDescriptions_ComeFromTheLocalizationTables()
    {
        Assert.Equal(
            LocalizationService.Get("Backup.None"),
            ServerPresentation.DescribeBackup(null, DateTimeOffset.UnixEpoch).Label);

        Assert.Equal(
            LocalizationService.Get("Minecraft"),
            ServerPresentation.DescribeGame(ServerManager.Contracts.GameType.Minecraft));
    }

    /// <summary>
    /// LocalizationService.Get returns the key itself when a key is missing, so a typo ships
    /// the raw key to the screen — which is exactly what "Appearance.Saved" did. This walks
    /// every key the client asks for and fails on any that does not resolve.
    /// </summary>
    [Fact]
    public void EveryLocalizationKeyUsedInCode_ExistsInTheTables()
    {
        var root = RepositoryRoot();
        var clientRoot = Path.Combine(root, "src", "ServerManager.Client");
        var pattern = new Regex(
            @"LocalizationService\.(?:Get|Format)\(\s*""([^""]+)""",
            RegexOptions.Compiled);

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        InCulture("en-US", () =>
        {
            foreach (var file in Directory.EnumerateFiles(
                         clientRoot,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                foreach (Match match in pattern.Matches(File.ReadAllText(file)))
                {
                    var key = match.Groups[1].Value;
                    if (!LocalizationService.HasKey(key))
                    {
                        missing.Add($"{key}  ({Path.GetFileName(file)})");
                    }
                }
            }
        });

        Assert.True(
            missing.Count == 0,
            "Localization keys used in code but absent from the tables:\n  " +
            string.Join("\n  ", missing));
    }

    [Fact]
    public void EveryLocalizationKey_ResolvesInArabicToo()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            var root = RepositoryRoot();
            var clientRoot = Path.Combine(root, "src", "ServerManager.Client");
            var pattern = new Regex(
                @"LocalizationService\.(?:Get|Format)\(\s*""([^""]+)""",
                RegexOptions.Compiled);

            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(
                         clientRoot,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                foreach (Match match in pattern.Matches(File.ReadAllText(file)))
                {
                    keys.Add(match.Groups[1].Value);
                }
            }

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");
            var missing = keys.Where(key => !LocalizationService.HasKey(key)).ToArray();

            Assert.True(
                missing.Length == 0,
                "Keys with no Arabic translation:\n  " + string.Join("\n  ", missing));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root!.FullName;
    }
}
