using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ServerManager.Client.Controls;
using ServerManager.Contracts;

namespace ServerManager.Connect.UiReview;

/// <summary>
/// Drives the real Server Manager Content tab against the fake Agent while the dashboard feed
/// refreshes every 250 ms (faster than the app's own 2 s), and checks the Build 10 search
/// behaviour: no "Loading" takeover, text and focus kept, debounced requests, a slow stale reply
/// that cannot overwrite a newer one, Enter searching at once, and filters that each run one
/// search and survive refreshes.
/// </summary>
internal static class ContentScenarios
{
    private static readonly Guid ServerId = Guid.Parse("7e57c0de-0000-4000-8000-000000000001");

    public static async Task RunAsync(FakeAgent agent, Report report)
    {
        var searches = new List<string>();
        var profiles = 0;
        agent.Extra = async (method, path) =>
        {
            if (method != "GET")
            {
                return null;
            }

            if (path == "/api/v1/dashboard")
            {
                return (200, Dashboard());
            }

            if (path == $"/api/v1/servers/{ServerId}/content/profile")
            {
                Interlocked.Increment(ref profiles);
                return (200, new ServerContentProfile(ServerId, GameType.Minecraft, ServerPlatform.Paper, "1.21.8", "1.21.8-100",
                    @"C:\review\mc", @"C:\review\mc\plugins", SupportsPlugins: true, IsRunning: false));
            }

            if (path.StartsWith($"/api/v1/servers/{ServerId}/content/installed", StringComparison.Ordinal))
            {
                return (200, Array.Empty<InstalledContent>());
            }

            if (path.StartsWith($"/api/v1/servers/{ServerId}/content/search?", StringComparison.Ordinal))
            {
                var query = path[(path.IndexOf('?') + 1)..];
                lock (searches)
                {
                    searches.Add(query);
                }

                var text = Param(query, "query") ?? "discover";
                await Task.Delay(text == "lu" ? 1500 : 150);
                var errors = text == "fail" ? new[] { "Hangar:Offline" } : Array.Empty<string>();
                return (200, new ContentSearchResult(Projects(text, Param(query, "kind"), Param(query, "platform")), 0, 30, 3, errors));
            }

            return null;
        };

        var tab = new ServerContentTab();
        var window = new Window { Content = tab, Width = 1280, Height = 820, Title = "Content review" };
        var search = (TextBox)tab.FindName("SearchBox");
        var hint = (TextBlock)tab.FindName("SearchHint");
        var state = (FrameworkElement)tab.FindName("StatePanel");
        var list = (ItemsControl)tab.FindName("DiscoverList");
        var platform = (ComboBox)tab.FindName("PlatformBox");
        var kind = (ComboBox)tab.FindName("KindBox");
        var notice = (TextBlock)tab.FindName("SearchNotice");
        int SearchCount() { lock (searches) return searches.Count; }
        string LastSearch() { lock (searches) return searches[^1]; }

        // As in the app, the dashboard has the server before its page is opened.
        DashboardFeed.Shared.Start();
        for (var i = 0; i < 40 && DashboardFeed.Shared.Servers.Count == 0; i++)
        {
            await Report.SettleAsync(250);
        }

        ServerDetailContext.Shared.Select(ServerId);
        Report.Show(window);
        for (var i = 0; i < 40 && list.Items.Count == 0; i++)
        {
            await Report.SettleAsync(250);
        }

        report.Check(list.Items.Count > 0 && profiles == 1, $"initial discovery shown ({list.Items.Count} results, {profiles} profile read)");

        // Stress: dashboard refreshes far faster than the app's own, sampled while typing.
        var loadingSeen = 0;
        var blankSeen = 0;
        var sampling = true;
        var stress = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        stress.Tick += (_, _) =>
        {
            _ = DashboardFeed.Shared.RefreshAsync();
            if (sampling && state.Visibility == Visibility.Visible) loadingSeen++;
            if (sampling && list.Items.Count == 0) blankSeen++;
        };
        stress.Start();

        search.Focus();
        var beforeTyping = SearchCount();
        await TypeAsync(search, "lu");
        await Task.Delay(550);              // the pause lets "lu" go out; its reply is slow
        await TypeAsync(search, "ckperms");
        await Task.Delay(2600);             // "luckperms" returns first, then the stale "lu"
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        sampling = false;

        var typed = searches.Skip(beforeTyping).ToArray();
        var first = list.Items.Count > 0 ? ((ContentItemViewModel)list.Items[0]).Name : string.Empty;
        report.Check(search.Text == "luckperms", $"typed text kept: '{search.Text}'");
        report.Check(ReferenceEquals(FocusManager.GetFocusedElement(window), search), "focus stayed in the search box");
        report.Check(loadingSeen == 0, $"no Loading card while typing under refreshes (seen {loadingSeen})");
        report.Check(blankSeen == 0, $"results never blanked while typing (seen {blankSeen})");
        report.Check(typed.Length is >= 1 and <= 2, $"debounced: {typed.Length} request(s) for 9 keystrokes: {string.Join(" | ", typed.Select(q => Param(q, "query")))}");
        report.Check(Param(LastSearch(), "query") == "luckperms", "the last request is the full text");
        report.Check(first.StartsWith("luckperms", StringComparison.Ordinal), $"stale 'lu' reply did not replace newer results (first: '{first}')");
        await report.CaptureAsync("content-01-typed", window, keyboard: false);

        // No periodic reload: several seconds of refreshes, no new searches or profile reads.
        var idleSearches = SearchCount();
        sampling = true;
        await Task.Delay(5000);
        sampling = false;
        report.Check(SearchCount() == idleSearches && profiles == 1, $"no reload loop: {SearchCount() - idleSearches} searches and {profiles} profile read over 5 s of refreshes");
        report.Check(loadingSeen == 0 && search.Text == "luckperms", "idle refreshes kept the page and the text");

        // Enter searches at once, before the debounce would.
        var beforeEnter = SearchCount();
        search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search)!, 0, Key.Enter)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
        await Task.Delay(200);
        report.Check(SearchCount() == beforeEnter + 1, "Enter searched immediately");
        await Report.SettleAsync(400);

        // Platform: one search, combined with the typed query, and kept through refreshes.
        var spigot = IndexOf(platform, "Spigot");
        var automatic = platform.Items.Count > 0 ? (string)platform.Items[0] : string.Empty;
        var beforePlatform = SearchCount();
        platform.SelectedIndex = spigot;
        await Report.SettleAsync(700);
        var platformSearches = searches.Skip(beforePlatform).ToArray();
        report.Check(spigot > 0 && automatic.Contains("Paper", StringComparison.Ordinal), $"plugin platforms offered: {string.Join(", ", platform.Items.Cast<string>())}");
        report.Check(platformSearches.Length == 1 && Param(platformSearches[0], "platform") == "spigot" && Param(platformSearches[0], "query") == "luckperms",
            $"Spigot ran one search with the query: {string.Join(" | ", platformSearches)}");
        await Task.Delay(2500);
        report.Check(platform.SelectedIndex == spigot && search.Text == "luckperms", "platform and query kept through refreshes");
        await report.CaptureAsync("content-02-spigot", window, keyboard: false);

        // Type-aware wording and platforms.
        var beforeKind = SearchCount();
        kind.SelectedIndex = 1;   // Modpack
        await Report.SettleAsync(700);
        var modpackSearches = searches.Skip(beforeKind).ToArray();
        report.Check(hint.Text == ContentLabels.SearchHint(ContentKind.Modpack), $"modpack hint: '{hint.Text}'");
        report.Check(platform.Items.Cast<string>().Contains("Fabric") && !platform.Items.Cast<string>().Contains("Spigot"), $"modpack loaders: {string.Join(", ", platform.Items.Cast<string>())}");
        report.Check(modpackSearches.Length == 1 && Param(modpackSearches[0], "kind") == "Modpack" && Param(modpackSearches[0], "platform") == "all",
            $"type change ran one modpack search: {string.Join(" | ", modpackSearches)}");
        await report.CaptureAsync("content-03-modpacks", window, keyboard: false);

        kind.SelectedIndex = 2;   // Data pack
        await Report.SettleAsync(700);
        report.Check(hint.Text == ContentLabels.SearchHint(ContentKind.DataPack) && platform.Visibility == Visibility.Collapsed && Param(LastSearch(), "platform") is null,
            $"data packs: hint '{hint.Text}', no platform choice");

        // One provider failing keeps the other's results and says so.
        kind.SelectedIndex = 0;
        await Report.SettleAsync(700);
        search.Text = "fail";
        search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search)!, 0, Key.Enter)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
        await Report.SettleAsync(900);
        report.Check(list.Items.Count > 0 && notice.Visibility == Visibility.Visible && state.Visibility != Visibility.Visible,
            $"provider failure kept results and noted: '{notice.Text}'");
        await report.CaptureAsync("content-04-provider-failed", window, keyboard: false);

        stress.Stop();
        DashboardFeed.Shared.Dispose();
        window.Close();
    }

    private static async Task TypeAsync(TextBox box, string text)
    {
        foreach (var character in text)
        {
            box.Text += character;
            box.CaretIndex = box.Text.Length;
            await Task.Delay(90);
        }
    }

    private static int IndexOf(ComboBox box, string item)
    {
        for (var index = 0; index < box.Items.Count; index++)
        {
            if ((string)box.Items[index] == item) return index;
        }

        return -1;
    }

    private static string? Param(string query, string name) =>
        query.Split('&')
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] == name)
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault();

    private static ContentProject[] Projects(string text, string? kind, string? platform) =>
        Enumerable.Range(1, 3).Select(n => new ContentProject(
            ContentProviderId.Modrinth,
            $"p{n}",
            $"p{n}",
            $"{text} {kind} {platform} #{n}",
            "Review result",
            "review",
            null,
            new Uri($"https://modrinth.com/plugin/p{n}"),
            1000 * n,
            ["paper"],
            ["1.21.8"],
            Kind: Enum.TryParse<ContentKind>(kind, out var parsed) ? parsed : ContentKind.Plugin,
            IsCompatible: true)).ToArray();

    private static object Dashboard() => new
    {
        capturedAtUtc = DateTimeOffset.UtcNow,
        warnings = Array.Empty<string>(),
        servers = new[]
        {
            new { serverId = ServerId, game = 1, isInstalled = true, name = "Review Paper", state = 1, installedVersion = "1.21.8", port = 25565 }
        }
    };
}
