using System.Globalization;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Tests;

/// <summary>
/// Content Discover search (Build 10): typing is debounced, the newest search always wins,
/// filters and the query are carried into every request, and the dashboard's regular
/// refreshes no longer restart the search or put the page back into "Loading".
/// </summary>
public sealed class ContentSearchSessionTests
{
    private static readonly Guid Server = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Query_CarriesTheTextAndEveryFilter()
    {
        var query = ContentSearchSession.BuildQuery(new ContentSearchInputs(
            Server, ContentKind.Plugin, "  luck perms ", ContentSortOrder.Downloads, "Modrinth", CompatibleOnly: false, Platform: "spigot"));

        Assert.Equal(
            "sort=Downloads&kind=Plugin&compatibleOnly=false&limit=30&query=luck%20perms&provider=Modrinth&platform=spigot",
            query);
    }

    [Fact]
    public void Query_UsesTheTypesDefaultPlatform_AndOmitsItWhereThereIsNone()
    {
        var plugin = ContentSearchSession.BuildQuery(new ContentSearchInputs(Server, ContentKind.Plugin, null, ContentSortOrder.Relevance, null, true, null));
        var modpack = ContentSearchSession.BuildQuery(new ContentSearchInputs(Server, ContentKind.Modpack, "", ContentSortOrder.Relevance, null, true, "forge"));
        var dataPack = ContentSearchSession.BuildQuery(new ContentSearchInputs(Server, ContentKind.DataPack, "x", ContentSortOrder.Relevance, null, true, "paper"));

        Assert.EndsWith("platform=auto", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("query=", plugin, StringComparison.Ordinal);
        Assert.EndsWith("platform=forge", modpack, StringComparison.Ordinal);
        Assert.DoesNotContain("platform=", dataPack, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedDashboardRefreshes_DoNotReloadTheSameServer()
    {
        var session = new ContentSearchSession();

        Assert.True(session.ContextChanged($"{Server}|Minecraft|True|1.21.8"));
        for (var refresh = 0; refresh < 20; refresh++)
        {
            Assert.False(session.ContextChanged($"{Server}|Minecraft|True|1.21.8"));
        }

        Assert.True(session.ContextChanged($"{Guid.NewGuid()}|Minecraft|True|1.21.8"));
    }

    [Fact]
    public void TheSameSearch_IsNotRepeated_UnlessForced()
    {
        var session = new ContentSearchSession();

        Assert.True(session.TryBegin("q=luck", force: false, out _));
        Assert.False(session.TryBegin("q=luck", force: false, out _));
        Assert.True(session.TryBegin("q=luck", force: true, out _));
        Assert.True(session.TryBegin("q=luckp", force: false, out _));
    }

    [Fact]
    public void AStaleReply_IsRecognised_AfterANewerSearchStarts()
    {
        var session = new ContentSearchSession();
        session.TryBegin("q=lu", force: false, out var older);

        session.TryBegin("q=luck", force: false, out var newer);

        Assert.False(session.IsCurrent(older));
        Assert.True(session.IsCurrent(newer));
    }

    [Fact]
    public void AFailedSearch_CanBeAskedAgain_ButAnOvertakenFailureChangesNothing()
    {
        var session = new ContentSearchSession();
        session.TryBegin("q=a", force: false, out var first);
        session.TryBegin("q=b", force: false, out var second);

        session.Forget(first);
        Assert.False(session.TryBegin("q=b", force: false, out _));

        session.Forget(second);
        Assert.True(session.TryBegin("q=b", force: false, out _));
    }

    [Fact]
    public void ChangingType_MakesAnyReplyOnItsWayStale()
    {
        var session = new ContentSearchSession();
        session.TryBegin("kind=Plugin", force: false, out var plugin);

        session.Invalidate();

        Assert.False(session.IsCurrent(plugin));
        Assert.True(session.TryBegin("kind=Plugin", force: false, out _));
    }

    [Fact]
    public async Task Debouncer_RunsOnce_WithTheLastValue_AfterTypingPauses()
    {
        var debouncer = new Debouncer(TimeSpan.FromMilliseconds(120));
        var runs = new List<string>();
        var typed = string.Empty;
        var pending = new List<Task>();

        foreach (var character in "luckperms")
        {
            typed += character;
            var snapshot = typed;
            pending.Add(debouncer.RunAsync(() =>
            {
                runs.Add(snapshot);
                return Task.CompletedTask;
            }));
            await Task.Delay(20);
        }

        await Task.WhenAll(pending);

        Assert.Equal(["luckperms"], runs);
    }

    [Fact]
    public async Task Debouncer_Cancel_DropsThePendingRun()
    {
        var debouncer = new Debouncer(TimeSpan.FromMilliseconds(80));
        var ran = false;

        var pending = debouncer.RunAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        debouncer.Cancel();
        await pending;

        Assert.False(ran);
    }

    [Theory]
    [InlineData(ContentKind.Plugin, "Search plugins")]
    [InlineData(ContentKind.Modpack, "Search modpacks")]
    [InlineData(ContentKind.DataPack, "Search data packs")]
    [InlineData(ContentKind.ResourcePack, "Search resource packs")]
    public void SearchHint_FollowsTheSelectedType(ContentKind kind, string expected)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            Assert.Equal(expected, ContentLabels.SearchHint(kind));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void AutomaticPlatform_NamesWhatThisServerRuns()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            Assert.Equal("Automatic (Paper)", ContentLabels.Platform("auto", ContentKind.Plugin, ServerPlatform.Paper));
            Assert.Equal("Spigot", ContentLabels.Platform("spigot", ContentKind.Plugin, ServerPlatform.Paper));
            Assert.Equal("All loaders", ContentLabels.Platform("all", ContentKind.Modpack, ServerPlatform.Paper));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void ContentTab_NoLongerReloadsOnEveryFeedRefresh_OrBlanksTheResultsWhileSearching()
    {
        var code = ReadSource("src", "ServerManager.Client", "Controls", "ServerContentTab.xaml.cs");
        var search = code[code.IndexOf("private async Task SearchAsync(", StringComparison.Ordinal)..];
        search = search[..search.IndexOf("private static string DescribeProviderFailures", StringComparison.Ordinal)];

        Assert.Contains("if (!_session.ContextChanged(ContextKey()))", code, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Content.Loading\"", search, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchBox.Text =", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchBox.Clear", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".Focus()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentStrings_ExistInEnglishAndArabic()
    {
        foreach (var key in new[]
                 {
                     "Content.Search.Plugin", "Content.Search.Modpack", "Content.Search.DataPack",
                     "Content.Search.ResourcePack", "Content.PlatformLabel", "Content.Platform.Auto",
                     "Content.Platform.AllPlugins", "Content.Platform.AllLoaders",
                     "Content.Notice.ProviderFailed", "Content.Notice.Unreachable"
                 })
        {
            var original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                var english = LocalizationService.Get(key);
                CultureInfo.CurrentUICulture = new CultureInfo("ar-SA");
                var arabic = LocalizationService.Get(key);

                Assert.NotEqual(key, english);
                Assert.NotEqual(key, arabic);
                Assert.NotEqual(english, arabic);
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
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
