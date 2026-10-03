using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core.Content;
using AutomationProperties = System.Windows.Automation.AutomationProperties;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

/// <summary>
/// The Content Hub for one Minecraft server: browse Hangar and Modrinth, install into this
/// server's plugins folder, and manage what is already there. The app hosts nothing; every
/// card names the site it came from and links back to it.
/// </summary>
public partial class ServerContentTab : UserControl
{
    private readonly ServerDetailContext _context = ServerDetailContext.Shared;
    private readonly ObservableCollection<ContentItemViewModel> _discovered = [];
    private readonly ObservableCollection<InstalledItemViewModel> _installed = [];

    // Providers are asked once typing pauses, not on every keystroke.
    private readonly Debouncer _searchDebounce = new(TimeSpan.FromMilliseconds(400));
    private readonly ContentSearchSession _session = new();

    private ServerContentProfile? _profile;
    private CancellationTokenSource? _inFlight;
    private bool _loaded;
    private bool _busy;
    private bool? _compatibleBeforeBrowseOnly;

    // Set while code rebuilds the selectors, so their selection events do not start searches.
    private bool _updatingControls;

    // A newly selected server starts on its default content type once its profile is known.
    private bool _chooseDefaultKind = true;

    /// <summary>The content types this server can actually use, in selector order.</summary>
    private IReadOnlyList<ContentKind> _kinds = [ContentKind.Plugin];

    /// <summary>The platform choices offered for the selected type, in selector order.</summary>
    private IReadOnlyList<string> _platformOptions = [];

    /// <summary>The platform last chosen for each content type.</summary>
    private readonly Dictionary<ContentKind, string> _platformByKind = [];

    public ServerContentTab()
    {
        InitializeComponent();
        DiscoverList.ItemsSource = _discovered;
        InstalledList.ItemsSource = _installed;

        _context.Changed += (_, _) => Dispatcher.Invoke(() =>
        {
            // The dashboard feed raises this on every refresh, every few seconds. Only a
            // different server, or a change in what it runs, reloads the content; anything
            // else would restart the search under the user's hands.
            if (!_session.ContextChanged(ContextKey()))
            {
                return;
            }

            // Nothing still on its way for the previous server may land on this one.
            _searchDebounce.Cancel();
            _inFlight?.Cancel();
            SetSearching(false);
            SetNotice(null);
            _loaded = false;
            _chooseDefaultKind = true;

            // Platform choices are about this server's software, so a new server starts fresh.
            _platformByKind.Clear();
            _discovered.Clear();
            _installed.Clear();
            Localize();
            if (IsVisible)
            {
                _ = ReloadAsync();
            }
        });
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Localize);
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !_loaded)
            {
                await ReloadAsync();
            }
        };
        Loaded += (_, _) => Localize();
    }

    /// <summary>Re-reads everything for the selected server.</summary>
    public async Task ReloadAsync()
    {
        _loaded = true;
        await LoadProfileAsync();
        if (_profile is null || _kinds.Count == 0)
        {
            return;
        }

        if (TabDiscover.IsChecked == true)
        {
            await SearchAsync(force: true);
        }
        else
        {
            await LoadInstalledAsync();
        }
    }

    private string ContextKey() =>
        $"{_context.ServerId}|{_context.Source?.Game}|{_context.Source?.IsInstalled}|{_context.Source?.InstalledVersion}";

    private void Localize()
    {
        if (Heading is null)
        {
            return;
        }

        _updatingControls = true;
        try
        {
            LocalizeControls();
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void LocalizeControls()
    {
        Heading.Text = LocalizationService.Get("ServerTab.Content");
        Subheading.Text = _context.Source?.Game switch
        {
            GameType.Minecraft => LocalizationService.Get("Content.MinecraftSubtitle"),
            GameType.Palworld => LocalizationService.Get("Content.PalworldSubtitle"),
            _ => LocalizationService.Get("Content.Subtitle")
        };

        TabDiscover.Content = LocalizationService.Get("Content.Discover");
        TabInstalled.Content = LocalizationService.Get("Content.Installed");
        TabUpdates.Content = LocalizationService.Get("Content.Updates");
        CompatibleOnlyBox.Content = LocalizationService.Get("Content.CompatibleOnly");
        DiscoverList.Tag = LocalizationService.Get("Content.ResultsList");
        AutomationProperties.SetName(SortBox, LocalizationService.Get("Content.SortLabel"));
        AutomationProperties.SetName(ProviderBox, LocalizationService.Get("Content.ProviderLabel"));

        var sortIndex = SortBox.SelectedIndex < 0 ? 0 : SortBox.SelectedIndex;
        SortBox.ItemsSource = new[]
        {
            LocalizationService.Get("Content.Sort.Relevance"),
            LocalizationService.Get("Content.Sort.Downloads"),
            LocalizationService.Get("Content.Sort.Updated"),
            LocalizationService.Get("Content.Sort.Newest")
        };
        SortBox.SelectedIndex = sortIndex;

        var providerIndex = ProviderBox.SelectedIndex < 0 ? 0 : ProviderBox.SelectedIndex;
        ProviderBox.ItemsSource = new[]
        {
            LocalizationService.Get("Content.Provider.All"),
            "Modrinth",
            "Hangar"
        };
        ProviderBox.SelectedIndex = providerIndex;

        AutomationProperties.SetName(KindBox, LocalizationService.Get("Content.KindLabel"));
        var kindIndex = KindBox.SelectedIndex < 0 ? 0 : KindBox.SelectedIndex;
        KindBox.ItemsSource = _kinds.Select(ContentLabels.Kind).ToArray();
        KindBox.SelectedIndex = Math.Min(kindIndex, Math.Max(0, _kinds.Count - 1));
        RefreshKindControls();
    }

    /// <summary>
    /// The search hint and the platform choices follow the selected type: "Search modpacks"
    /// with Fabric/Forge/NeoForge/Quilt, "Search plugins" with Paper/Purpur/Spigot/Bukkit/Folia,
    /// and no platform choice for data packs or resource packs. A still-offered choice is kept.
    /// </summary>
    private void RefreshKindControls()
    {
        var kind = SelectedKind;
        SearchHint.Text = ContentLabels.SearchHint(kind);
        AutomationProperties.SetName(SearchBox, ContentLabels.SearchHint(kind));
        AutomationProperties.SetName(PlatformBox, LocalizationService.Get("Content.PlatformLabel"));

        // Each type remembers its own choice: plugins and modpacks both have an "all", and a trip
        // through modpacks must not turn the plugin filter into "All plugin platforms".
        var previous = _platformByKind.TryGetValue(kind, out var remembered) ? remembered : null;
        _platformOptions = ContentPlatformFilter.Options(kind, _profile?.Platform);
        PlatformBox.ItemsSource = _platformOptions
            .Select(option => ContentLabels.Platform(option, kind, _profile?.Platform))
            .ToArray();
        var kept = previous is null ? -1 : IndexOf(_platformOptions, previous);
        PlatformBox.SelectedIndex = _platformOptions.Count == 0 ? -1 : Math.Max(0, kept);
        PlatformBox.Visibility = _platformOptions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // The page says what it is showing: plugins, modpacks, data packs or resource packs.
        if (_context.Source?.Game is GameType.Minecraft || _profile?.Game is GameType.Minecraft)
        {
            Subheading.Text = ContentLabels.Subtitle(kind);
        }

        // Plugins on a server that cannot load them: browsable, never installable, and said so.
        var pluginsUnavailable = kind == ContentKind.Plugin && _profile is { SupportsPlugins: false };
        if (pluginsUnavailable)
        {
            _compatibleBeforeBrowseOnly ??= CompatibleOnlyBox.IsChecked == true;
            CompatibleOnlyBox.IsChecked = false;
        }
        else if (_compatibleBeforeBrowseOnly is { } rememberedCompatible)
        {
            CompatibleOnlyBox.IsChecked = rememberedCompatible;
            _compatibleBeforeBrowseOnly = null;
        }
        CompatibleOnlyBox.IsEnabled = !pluginsUnavailable;
        CompatibleOnlyBox.Content = pluginsUnavailable ? PluginSoftwarePresentation.BrowseOnlyLabel : LocalizationService.Get("Content.CompatibleOnly");
        KindNotice.Text = pluginsUnavailable
            ? PluginSoftwarePresentation.RequiresLabel
            : string.Empty;
        KindNotice.Visibility = pluginsUnavailable ? Visibility.Visible : Visibility.Collapsed;
    }

    private int IndexOfKind(ContentKind kind)
    {
        for (var index = 0; index < _kinds.Count; index++)
        {
            if (_kinds[index] == kind)
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOf(IReadOnlyList<string> options, string value)
    {
        for (var index = 0; index < options.Count; index++)
        {
            if (options[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    private ContentKind SelectedKind =>
        KindBox.SelectedIndex >= 0 && KindBox.SelectedIndex < _kinds.Count
            ? _kinds[KindBox.SelectedIndex]
            : _kinds.FirstOrDefault();

    private string? SelectedPlatform =>
        PlatformBox.SelectedIndex >= 0 && PlatformBox.SelectedIndex < _platformOptions.Count
            ? _platformOptions[PlatformBox.SelectedIndex]
            : null;

    private async Task LoadProfileAsync()
    {
        if (_context.ServerId == Guid.Empty)
        {
            return;
        }

        try
        {
            using var client = _context.CreateClient(TimeSpan.FromSeconds(20));
            _profile = await client.GetFromJsonAsync<ServerContentProfile>(
                $"/api/v1/servers/{_context.ServerId}/content/profile");
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            _profile = null;
            ShowState("Content.Error.AgentTitle", "Content.Error.AgentMessage", retry: true);
            return;
        }

        if (_profile is null)
        {
            ShowState("Content.Error.AgentTitle", "Content.Error.AgentMessage", retry: true);
            return;
        }

        // Every Minecraft server lists all four types. A Vanilla server can browse plugins but
        // not install them (each card and a notice say so), so Plugin is never missing.
        _kinds = ContentLabels.SelectableKinds
            .Where(kind => ContentTypePolicy.IsBrowsableBy(kind, _profile))
            .ToArray();
        Localize();
        if (_chooseDefaultKind && _kinds.Count > 0)
        {
            // A newly opened server starts on Plugin where it can run plugins, otherwise on the
            // first type it can actually install (data packs on Vanilla).
            _chooseDefaultKind = false;
            var preferred = _profile.SupportsPlugins
                ? ContentKind.Plugin
                : _kinds.FirstOrDefault(kind => kind != ContentKind.Plugin && kind != ContentKind.Modpack &&
                                                ContentTypePolicy.IsSupportedBy(kind, _profile));
            var index = IndexOfKind(preferred);
            _updatingControls = true;
            try
            {
                KindBox.SelectedIndex = index < 0 ? 0 : index;
                RefreshKindControls();
            }
            finally
            {
                _updatingControls = false;
            }
        }

        if (_kinds.Count == 0)
        {
            // Say plainly why, rather than showing an empty catalogue that never works.
            var reasonKey = _profile.UnsupportedReason switch
            {
                "Vanilla" => "Content.Unsupported.Vanilla",
                "UnknownPlatform" => "Content.Unsupported.UnknownPlatform",
                "UnknownVersion" => "Content.Unsupported.UnknownVersion",
                "MissingRoot" => "Content.Unsupported.MissingRoot",
                _ => "Content.Unsupported.NotMinecraft"
            };
            ControlsPanel.Visibility = Visibility.Collapsed;
            ShowState("Content.Unsupported.Title", reasonKey);
            return;
        }

        ControlsPanel.Visibility = Visibility.Visible;
        HideState();
    }

    /// <summary>
    /// One search for the current filters. The results on screen stay until the reply is in,
    /// the box keeps its text and focus, and a reply that a newer search has overtaken is
    /// dropped. <paramref name="force"/> repeats a search that is already current (Enter, Retry).
    /// </summary>
    private async Task SearchAsync(bool force = false)
    {
        if (_profile is null || _kinds.Count == 0 || TabDiscover.IsChecked != true)
        {
            return;
        }

        var inputs = new ContentSearchInputs(
            _context.ServerId,
            SelectedKind,
            SearchBox.Text,
            SortBox.SelectedIndex switch
            {
                1 => ContentSortOrder.Downloads,
                2 => ContentSortOrder.Updated,
                3 => ContentSortOrder.Newest,
                _ => ContentSortOrder.Relevance
            },
            ProviderBox.SelectedIndex switch
            {
                1 => "Modrinth",
                2 => "Hangar",
                _ => null
            },
            CompatibleOnlyBox.IsChecked == true,
            SelectedPlatform);
        var query = ContentSearchSession.BuildQuery(inputs);
        if (!_session.TryBegin($"{inputs.ServerId}?{query}", force, out var generation))
        {
            return;
        }

        var token = BeginRequest();
        SetSearching(true);
        try
        {
            using var client = _context.CreateClient(TimeSpan.FromSeconds(30));
            var result = await client.GetFromJsonAsync<ContentSearchResult>(
                $"/api/v1/servers/{_context.ServerId}/content/search?{query}",
                token);
            if (!_session.IsCurrent(generation))
            {
                return;
            }

            var installedNames = _installed.Select(item => item.Record.ProjectId).ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            _discovered.Clear();
            foreach (var project in result?.Projects ?? [])
            {
                var item = new ContentItemViewModel(Describe(project), _profile)
                {
                    IsInstalled = project.ProjectId is { Length: > 0 } id && installedNames.Contains(id)
                };
                _discovered.Add(item);
            }

            var errors = result is null ? [] : ContentProviderPresentation.FailedProviders(result)
                .Select(provider => provider.ToString()).ToArray();
            if (_discovered.Count == 0)
            {
                var bothUnavailable = result is not null && ContentProviderPresentation.BothUnavailable(result);
                SetNotice(errors.Length > 0 && !bothUnavailable ? DescribeProviderFailures(errors) : null);
                ShowState(
                    bothUnavailable ? "Content.Error.ProvidersTitle" : "Content.Empty.Title",
                    bothUnavailable ? "Content.Error.ProvidersMessage" : "Content.Empty.Message",
                    retry: errors.Length > 0);
                if (errors.Length > 0)
                {
                    _session.Forget(generation);
                }
            }
            else
            {
                HideState();
                SetNotice(errors.Length > 0 ? DescribeProviderFailures(errors) : null);
                DiscoverScroller.ScrollToTop();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Replaced by a newer search, a tab switch or a server switch: never "done".
            _session.Forget(generation);
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            if (!_session.IsCurrent(generation))
            {
                return;
            }

            _session.Forget(generation);
            if (_discovered.Count > 0)
            {
                // Keep what is on screen rather than trading it for an error card.
                SetNotice(LocalizationService.Get("Content.Notice.Unreachable"));
            }
            else
            {
                ShowState("Content.Error.AgentTitle", "Content.Error.AgentMessage", retry: true);
            }
        }
        finally
        {
            if (_session.IsCurrent(generation))
            {
                SetSearching(false);
            }
        }
    }

    /// <summary>"Hangar could not be reached…", naming only the sites that failed.</summary>
    private static string DescribeProviderFailures(IReadOnlyList<string> errors)
    {
        var names = errors
            .Select(error => error.Split(':')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return LocalizationService.Format("Content.Notice.ProviderFailed", string.Join(", ", names));
    }

    private void SetSearching(bool searching) =>
        SearchProgress.Visibility = searching ? Visibility.Visible : Visibility.Hidden;

    private void SetNotice(string? text)
    {
        SearchNotice.Text = text ?? string.Empty;
        SearchNotice.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Fills in the one line a card shows about fit, from what the provider returned for this
    /// exact server.
    /// </summary>
    private ContentProject Describe(ContentProject project)
    {
        var platform = _profile is null
            ? string.Empty
            : project.Platforms.Count > 0
                ? Capitalize(project.Platforms[0])
                : string.Empty;
        var summary = string.IsNullOrEmpty(platform)
            ? LocalizationService.Format("Content.MinecraftVersion", _profile?.MinecraftVersion ?? string.Empty)
            : $"{platform} · {LocalizationService.Format("Content.MinecraftVersion", _profile?.MinecraftVersion ?? string.Empty)}";
        return project with { CompatibilitySummary = summary };
    }

    private async Task LoadInstalledAsync()
    {
        if (_profile is null || _kinds.Count == 0)
        {
            return;
        }

        var token = BeginRequest();
        ShowState("Content.Loading", "Content.LoadingMessage");
        try
        {
            using var client = _context.CreateClient(TimeSpan.FromMinutes(2));
            var records = await client.GetFromJsonAsync<InstalledContent[]>(
                $"/api/v1/servers/{_context.ServerId}/content/installed?identify=true&updates=true",
                token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var onlyUpdates = TabUpdates.IsChecked == true;
            var visible = (records ?? [])
                .Where(record => !onlyUpdates || record.State == InstalledContentState.UpdateAvailable)
                .ToArray();

            _installed.Clear();
            foreach (var record in visible)
            {
                _installed.Add(new InstalledItemViewModel(record));
            }

            if (_installed.Count == 0)
            {
                ShowState(
                    onlyUpdates ? "Content.NoUpdates.Title" : "Content.NoInstalled.Title",
                    onlyUpdates ? "Content.NoUpdates.Message" : "Content.NoInstalled.Message");
            }
            else
            {
                HideState();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            ShowState("Content.Error.AgentTitle", "Content.Error.AgentMessage", retry: true);
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ContentItemViewModel item })
        {
            await InstallItemAsync(item);
        }
    }

    private async Task InstallItemAsync(ContentItemViewModel item)
    {
        if (_busy)
        {
            return;
        }

        if (PluginSoftwarePresentation.RequiresSoftware(item.Project, _profile))
        {
            await ShowProjectAsync(item);
            return;
        }

        if (item.Project.Kind == ContentKind.Modpack)
        {
            await InstallModpackAsync(item);
            return;
        }

        var request = new ContentInstallRequest(
            _context.ServerId,
            item.Provider,
            item.ProjectId,
            Kind: item.Project.Kind);

        item.IsBusy = true;
        _busy = true;
        try
        {
            using var client = _context.CreateClient(TimeSpan.FromMinutes(10));

            // Show what will be installed before anything is downloaded, so a plugin never
            // quietly brings in several others.
            var plan = await PostAsync<ContentInstallPlan>(
                client,
                $"/api/v1/servers/{_context.ServerId}/content/plan",
                request);
            if (plan is null)
            {
                Notify(NotificationKind.Error, "Content.Error.ProvidersTitle", "Content.Error.ProviderRequestMessage");
                return;
            }

            if (plan.Blocked)
            {
                Notify(
                    NotificationKind.Error,
                    "Content.Install.BlockedTitle",
                    DescribeBlocked(plan.BlockedReason));
                return;
            }

            var dependencies = plan.Items.Where(entry => entry.IsDependency).ToArray();
            if (dependencies.Length > 0 && !ConfirmDependencies(item.Name, dependencies))
            {
                return;
            }

            ShowProgress("Content.Stage.Downloading");
            var result = await PostAsync<ContentOperationResult>(
                client,
                $"/api/v1/servers/{_context.ServerId}/content/install",
                request);
            HideProgress();

            if (result is { Success: true })
            {
                item.IsInstalled = true;
                Notify(
                    NotificationKind.Success,
                    "Content.Installed.Title",
                    result.RestartRequired ? "Content.RestartRequired" : "Content.Installed.Message",
                    item.Name);
            }
            else
            {
                Notify(
                    NotificationKind.Error,
                    "Content.Install.FailedTitle",
                    DescribeError(result?.ErrorCode));
            }
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            HideProgress();
            Notify(NotificationKind.Error, "Content.Error.AgentTitle", "Content.Error.AgentMessage");
        }
        finally
        {
            item.IsBusy = false;
            _busy = false;
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e) =>
        await RunFileActionAsync(
            sender,
            "update",
            "Content.Updated.Title",
            "Content.Update.FailedTitle",
            confirm: false);

    private async void Rollback_Click(object sender, RoutedEventArgs e) =>
        await RunFileActionAsync(
            sender,
            "rollback",
            "Content.RolledBack.Title",
            "Content.Rollback.FailedTitle",
            confirm: false);

    private async void Uninstall_Click(object sender, RoutedEventArgs e) =>
        await RunFileActionAsync(
            sender,
            "uninstall",
            "Content.Uninstalled.Title",
            "Content.Uninstall.FailedTitle",
            confirm: true);

    private async Task RunFileActionAsync(
        object sender,
        string action,
        string successTitleKey,
        string failureTitleKey,
        bool confirm)
    {
        if (_busy || sender is not Button { DataContext: InstalledItemViewModel item })
        {
            return;
        }

        if (confirm && !ConfirmUninstall(item))
        {
            return;
        }

        item.IsBusy = true;
        _busy = true;
        try
        {
            ShowProgress(action == "uninstall" ? "Content.Stage.Removing" : "Content.Stage.Downloading");
            using var client = _context.CreateClient(TimeSpan.FromMinutes(10));
            var result = await PostAsync<ContentOperationResult>(
                client,
                $"/api/v1/servers/{_context.ServerId}/content/{action}",
                new ContentFileRequest(item.FileName));
            HideProgress();

            if (result is { Success: true })
            {
                Notify(
                    NotificationKind.Success,
                    successTitleKey,
                    result.RestartRequired ? "Content.RestartRequired" : "Content.Done",
                    item.DisplayName);
                await LoadInstalledAsync();
            }
            else
            {
                Notify(NotificationKind.Error, failureTitleKey, DescribeError(result?.ErrorCode));
            }
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            HideProgress();
            Notify(NotificationKind.Error, "Content.Error.AgentTitle", "Content.Error.AgentMessage");
        }
        finally
        {
            item.IsBusy = false;
            _busy = false;
        }
    }

    private void ViewProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: InstalledItemViewModel item } &&
            item.Record.ProjectUrl is { } url)
        {
            OpenExternal(url);
        }
    }

    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ContentItemViewModel item }) await ShowProjectAsync(item);
    }

    private async Task ShowProjectAsync(ContentItemViewModel item)
    {
        if (_profile is null || _busy)
        {
            return;
        }
        var serverId = _context.ServerId;
        var profile = _profile;
        item.IsBusy = true;
        _busy = true;
        try
        {
            using var client = _context.CreateClient(TimeSpan.FromSeconds(30));
            var detail = await client.GetFromJsonAsync<ContentProjectDetail>(
                $"/api/v1/servers/{serverId}/content/projects/{item.Provider}/" +
                $"{Uri.EscapeDataString(item.ProjectId)}?kind={item.Project.Kind}");
            if (_context.ServerId != serverId) return;
            if (detail is null)
            {
                Notify(NotificationKind.Error, "Content.Error.ProvidersTitle", "Content.Error.ProviderRequestMessage");
                return;
            }

            IReadOnlyList<PluginSoftwareChoice> choices = [];
            if (PluginSoftwarePresentation.CanChooseSoftware(detail.Project, profile))
            {
                var software = await client.GetFromJsonAsync<MinecraftSoftwareStatus>($"/api/v1/servers/{serverId}/minecraft/software");
                if (_context.ServerId != serverId) return;
                if (software is not null) choices = PluginSoftwarePresentation.Choices(detail, profile, software);
            }
            var action = ContentProjectWindow.ShowAction(Window.GetWindow(this), detail, profile, choices);
            if (action.ChangeSoftware is { } target && choices.Any(c => c.Software.Platform == target && c.Software.Available))
            {
                var owner = Window.GetWindow(this);
                if (owner is null || !ServerSoftwareWindow.Open(owner, serverId, _context.Source?.Name ?? string.Empty, target)) return;
                if (_context.ServerId != serverId) return;
                // Refresh only the profile and selected project. Keep the current query, filters,
                // provider results and scroll position; installation is a separate explicit choice.
                _chooseDefaultKind = false;
                _compatibleBeforeBrowseOnly = null;
                CompatibleOnlyBox.IsChecked = false;
                await LoadProfileAsync();
                if (_context.ServerId != serverId || _profile is null) return;
                var refreshed = await client.GetFromJsonAsync<ContentProjectDetail>(
                    $"/api/v1/servers/{serverId}/content/projects/{item.Provider}/{Uri.EscapeDataString(item.ProjectId)}?kind={item.Project.Kind}");
                if (_context.ServerId != serverId || refreshed is null) return;
                foreach (var card in _discovered) card.Update(card.Project, _profile);
                item.Update(Describe(refreshed.Project with { IsCompatible = refreshed.LatestCompatible is not null }), _profile);
                var afterMigration = ContentProjectWindow.ShowAction(owner, refreshed, _profile, []);
                if (afterMigration.Install)
                {
                    item.IsBusy = false;
                    _busy = false;
                    await InstallItemAsync(item);
                }
                return;
            }
            if (action.Install)
            {
                item.IsBusy = false;
                _busy = false;
                await InstallItemAsync(item);
            }
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            Notify(NotificationKind.Error, "Content.Error.ProvidersTitle", "Content.Error.ProviderRequestMessage");
        }
        finally { item.IsBusy = false; _busy = false; }
    }

    private bool ConfirmDependencies(string name, IReadOnlyList<ContentInstallPlanItem> dependencies) =>
        ConfirmationDialog.Confirm(
            Window.GetWindow(this),
            LocalizationService.Get("Content.Dependencies.Title"),
            LocalizationService.Format("Content.Dependencies.Heading", name),
            LocalizationService.Get("Content.Dependencies.Body"),
            string.Join(
                Environment.NewLine,
                dependencies.Select(dependency => $"• {dependency.ProjectName} {dependency.VersionNumber}")),
            LocalizationService.Get("Content.Dependencies.Confirm"));

    private bool ConfirmUninstall(InstalledItemViewModel item) =>
        ConfirmationDialog.Confirm(
            Window.GetWindow(this),
            LocalizationService.Get("Content.Uninstall.Title"),
            LocalizationService.Format("Content.Uninstall.Heading", item.DisplayName),
            LocalizationService.Get("Content.Uninstall.Body"),
            item.FileName,
            LocalizationService.Get("Content.Uninstall.Confirm"));

    private void SubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (DiscoverScroller is null)
        {
            return;
        }

        var discovering = TabDiscover.IsChecked == true;
        ShowLists(StatePanel.Visibility != Visibility.Visible);
        FilterRow.Visibility = discovering ? Visibility.Visible : Visibility.Collapsed;
        if (!_loaded)
        {
            return;
        }

        // Coming back to Discover always refreshes it: the Installed/Updates state card and any
        // search that tab switch cancelled must not be left behind.
        _ = discovering ? SearchAsync(force: true) : LoadInstalledAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!_loaded)
        {
            return;
        }

        // Clearing the box also lands here and brings back the discovery list.
        _ = _searchDebounce.RunAsync(() => SearchAsync());
    }

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || !_loaded)
        {
            return;
        }

        e.Handled = true;
        _searchDebounce.Cancel();
        _ = SearchAsync(force: true);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _updatingControls || sender is not (ComboBox or CheckBox))
        {
            return;
        }

        if (ReferenceEquals(sender, PlatformBox) && SelectedPlatform is { } platform)
        {
            _platformByKind[SelectedKind] = platform;
        }

        // One search per change; the pending typed search is folded into it.
        _searchDebounce.Cancel();
        _ = SearchAsync();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) =>
        Filter_Changed(sender, (RoutedEventArgs)e);

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || PlatformBox is null)
        {
            return;
        }

        _updatingControls = true;
        try
        {
            RefreshKindControls();
        }
        finally
        {
            _updatingControls = false;
        }

        if (!_loaded)
        {
            return;
        }

        // Switching type changes what "compatible" even means, so the list is rebuilt rather
        // than filtered in place, and a reply for the old type is ignored.
        _searchDebounce.Cancel();
        _session.Invalidate();
        _discovered.Clear();
        SetNotice(null);
        HideState();
        _ = TabDiscover.IsChecked == true ? SearchAsync(force: true) : LoadInstalledAsync();
    }

    private async void Distribute_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: InstalledItemViewModel item })
        {
            return;
        }

        item.IsBusy = true;
        _busy = true;
        try
        {
            using var client = _context.CreateClient(TimeSpan.FromSeconds(30));
            var result = await PostAsync<ContentOperationResult>(
                client,
                $"/api/v1/servers/{_context.ServerId}/content/resource-pack/distribute",
                new ResourcePackDistributionRequest(item.FileName));
            if (result is { Success: true })
            {
                Notify(
                    NotificationKind.Success,
                    "Content.Distributed.Title",
                    result.RestartRequired ? "Content.RestartRequired" : "Content.Distributed.Message",
                    item.DisplayName);
                await LoadInstalledAsync();
            }
            else
            {
                Notify(NotificationKind.Error, "Content.Distribute.FailedTitle", DescribeError(result?.ErrorCode));
            }
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            Notify(NotificationKind.Error, "Content.Error.AgentTitle", "Content.Error.AgentMessage");
        }
        finally
        {
            item.IsBusy = false;
            _busy = false;
        }
    }

    private async void Withdraw_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: InstalledItemViewModel item })
        {
            return;
        }

        item.IsBusy = true;
        _busy = true;
        try
        {
            using var client = _context.CreateClient(TimeSpan.FromSeconds(30));
            var result = await PostAsync<ContentOperationResult>(
                client,
                $"/api/v1/servers/{_context.ServerId}/content/resource-pack/withdraw",
                new { });
            if (result is { Success: true })
            {
                Notify(NotificationKind.Success, "Content.Withdrawn.Title", "Content.Done", item.DisplayName);
                await LoadInstalledAsync();
            }
            else
            {
                Notify(NotificationKind.Error, "Content.Distribute.FailedTitle", DescribeError(result?.ErrorCode));
            }
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            Notify(NotificationKind.Error, "Content.Error.AgentTitle", "Content.Error.AgentMessage");
        }
        finally
        {
            item.IsBusy = false;
            _busy = false;
        }
    }

    /// <summary>
    /// A modpack never changes this server: it shows what would be built, asks where, and
    /// creates a new server.
    /// </summary>
    private async Task InstallModpackAsync(ContentItemViewModel item)
    {
        item.IsBusy = true;
        _busy = true;
        try
        {
            using var client = _context.CreateClient(TimeSpan.FromMinutes(30));
            ShowProgress("Content.Stage.ResolvingPack");
            var plan = await client.GetFromJsonAsync<ModpackPlan>(
                $"/api/v1/servers/{_context.ServerId}/content/modpacks/plan" +
                $"?provider={item.Provider}&projectId={Uri.EscapeDataString(item.ProjectId)}");
            HideProgress();
            if (plan is null)
            {
                Notify(NotificationKind.Error, "Content.Error.ProvidersTitle", "Content.Error.ProviderRequestMessage");
                return;
            }

            var choices = ModpackInstallWindow.Ask(Window.GetWindow(this), plan);
            if (choices is null)
            {
                return;
            }

            ShowProgress("Content.Stage.BuildingServer");
            var result = await PostAsync<ModpackInstallResult>(
                client,
                $"/api/v1/servers/{_context.ServerId}/content/modpacks/install",
                choices with
                {
                    Provider = plan.Provider,
                    ProjectId = plan.ProjectId,
                    VersionId = plan.VersionId
                });
            HideProgress();

            if (result is { Success: true })
            {
                NotificationService.Publish(
                    NotificationKind.Success,
                    LocalizationService.Get("Content.Modpack.CreatedTitle"),
                    LocalizationService.Format(
                        "Content.Modpack.CreatedMessage",
                        choices.ServerName,
                        result.MinecraftVersion ?? string.Empty));
            }
            else
            {
                Notify(
                    NotificationKind.Error,
                    "Content.Modpack.FailedTitle",
                    DescribeError(result?.ErrorCode));
            }
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            HideProgress();
            Notify(NotificationKind.Error, "Content.Error.AgentTitle", "Content.Error.AgentMessage");
        }
        finally
        {
            item.IsBusy = false;
            _busy = false;
        }
    }

    private async void StateAction_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private CancellationToken BeginRequest()
    {
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = new CancellationTokenSource();
        return _inFlight.Token;
    }

    private static async Task<T?> PostAsync<T>(HttpClient client, string url, object payload)
    {
        var response = await client.PostAsJsonAsync(url, payload);
        return await response.Content.ReadFromJsonAsync<T>();
    }

    private void ShowState(string titleKey, string messageKey, bool retry = false)
    {
        StateTitle.Text = LocalizationService.Get(titleKey);
        StateMessage.Text = LocalizationService.Get(messageKey);
        StateActionButton.Content = LocalizationService.Get("Action.Retry");
        StateActionButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        StatePanel.Visibility = Visibility.Visible;

        // Hide the list behind it: a "Loading" card floating over the previous results would
        // suggest those results are current.
        ShowLists(false);
    }

    private void HideState()
    {
        StatePanel.Visibility = Visibility.Collapsed;
        ShowLists(true);
    }

    private void ShowLists(bool visible)
    {
        var discovering = TabDiscover.IsChecked == true;
        DiscoverScroller.Visibility = visible && discovering
            ? Visibility.Visible
            : Visibility.Collapsed;
        InstalledScroller.Visibility = visible && !discovering
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowProgress(string stageKey)
    {
        ProgressText.Text = LocalizationService.Get(stageKey);
        ProgressPanel.Visibility = Visibility.Visible;
    }

    private void HideProgress() => ProgressPanel.Visibility = Visibility.Collapsed;

    private static void Notify(
        NotificationKind kind,
        string titleKey,
        string messageKey,
        string? subject = null)
    {
        var message = subject is null
            ? LocalizationService.Get(messageKey)
            : $"{subject} — {LocalizationService.Get(messageKey)}";
        NotificationService.Publish(kind, LocalizationService.Get(titleKey), message);
    }

    /// <summary>Turns a provider or install error code into a sentence, never raw text.</summary>
    private static string DescribeError(string? code) =>
        code switch
        {
            "Offline" => "Content.Error.Offline",
            "RateLimited" => "Content.Error.RateLimited",
            "ProviderApiRetired" => "Content.Error.ApiRetired",
            "ProviderUnavailable" or "ProviderSchema" => "Content.Error.ProviderRequestMessage",
            "HashMismatch" => "Content.Error.HashMismatch",
            "InvalidJar" => "Content.Error.InvalidJar",
            "ServerBusy" => "Content.Error.ServerBusy",
            "FileInUse" => "Content.Error.FileInUse",
            "AccessDenied" => "Content.Error.AccessDenied",
            "NoCompatibleVersion" => "Content.Error.NoCompatibleVersion",
            "NoRollback" or "RollbackMissing" => "Content.Error.NoRollback",
            "AlreadyCurrent" => "Content.Error.AlreadyCurrent",

            // Phase 2 content types bring their own ways of not working.
            "NeedsReachableUrl" => "Content.Error.NeedsReachableUrl",
            "NoSha1" => "Content.Error.NoSha1",
            "PackTooLarge" => "Content.Error.PackTooLarge",
            "NoWorld" or "NoLevelName" or "NoServerProperties" or "UnsafeLevelName"
                or "PropertiesUnreadable" => "Content.Error.NoWorld",
            "InvalidArchive" => "Content.Error.InvalidArchive",
            "DestinationNotEmpty" => "Content.Error.DestinationNotEmpty",
            "EulaRequired" => "Content.Error.EulaRequired",
            "JavaMissing" => "Content.Error.JavaMissing",
            "FileUnavailable" => "Content.Error.FileUnavailable",
            "UnsupportedServer" or "UnsupportedKind" => "Content.Error.UnsupportedServer",
            "PortInUse" => "Content.Error.PortInUse",
            "RegistrationFailed" => "Content.Error.RegistrationFailed",
            _ => "Content.Error.Generic"
        };

    private static string DescribeBlocked(string? reason)
    {
        if (reason is null)
        {
            return "Content.Error.Generic";
        }

        if (reason.StartsWith("DependencyIncompatible", StringComparison.Ordinal))
        {
            return "Content.Error.DependencyIncompatible";
        }

        return reason.StartsWith("DependencyUnresolvable", StringComparison.Ordinal)
            ? "Content.Error.DependencyUnresolvable"
            : DescribeError(reason);
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static void OpenExternal(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get("Content.Error.LinkTitle"),
                LocalizationService.Get("Content.Error.LinkMessage"));
        }
    }

    private static bool IsTransport(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or
            System.Text.Json.JsonException or InvalidOperationException;
}


