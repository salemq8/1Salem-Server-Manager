using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
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
    private readonly DispatcherTimer _searchDebounce = new()
    {
        // Providers are not hammered on every keystroke.
        Interval = TimeSpan.FromMilliseconds(450)
    };

    private ServerContentProfile? _profile;
    private CancellationTokenSource? _inFlight;
    private bool _loaded;
    private bool _busy;

    public ServerContentTab()
    {
        InitializeComponent();
        DiscoverList.ItemsSource = _discovered;
        InstalledList.ItemsSource = _installed;
        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await SearchAsync();
        };

        _context.Changed += (_, _) => Dispatcher.Invoke(() =>
        {
            _loaded = false;
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
        if (_profile is not { SupportsPlugins: true })
        {
            return;
        }

        if (TabDiscover.IsChecked == true)
        {
            await SearchAsync();
        }
        else
        {
            await LoadInstalledAsync();
        }
    }

    private void Localize()
    {
        if (Heading is null)
        {
            return;
        }

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
        SearchHint.Text = LocalizationService.Get("Content.SearchLabel");
        DiscoverList.Tag = LocalizationService.Get("Content.ResultsList");
        AutomationProperties.SetName(SearchBox, LocalizationService.Get("Content.SearchLabel"));
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
    }

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

        if (!_profile.SupportsPlugins)
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

    private async Task SearchAsync()
    {
        if (_profile is not { SupportsPlugins: true })
        {
            return;
        }

        var token = BeginRequest();
        ShowState("Content.Loading", "Content.LoadingMessage");
        try
        {
            using var client = _context.CreateClient(TimeSpan.FromSeconds(30));
            var sort = SortBox.SelectedIndex switch
            {
                1 => ContentSortOrder.Downloads,
                2 => ContentSortOrder.Updated,
                3 => ContentSortOrder.Newest,
                _ => ContentSortOrder.Relevance
            };
            var provider = ProviderBox.SelectedIndex switch
            {
                1 => "Modrinth",
                2 => "Hangar",
                _ => null
            };
            var query = new List<string>
            {
                $"sort={sort}",
                $"compatibleOnly={(CompatibleOnlyBox.IsChecked == true).ToString().ToLowerInvariant()}",
                "limit=30"
            };
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                query.Add($"query={Uri.EscapeDataString(SearchBox.Text.Trim())}");
            }

            if (provider is not null)
            {
                query.Add($"provider={provider}");
            }

            var result = await client.GetFromJsonAsync<ContentSearchResult>(
                $"/api/v1/servers/{_context.ServerId}/content/search?{string.Join('&', query)}",
                token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var installedNames = _installed.Select(item => item.Record.ProjectId).ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            _discovered.Clear();
            foreach (var project in result?.Projects ?? [])
            {
                var item = new ContentItemViewModel(Describe(project))
                {
                    IsInstalled = project.ProjectId is { Length: > 0 } id && installedNames.Contains(id)
                };
                _discovered.Add(item);
            }

            if (_discovered.Count == 0)
            {
                ShowState(
                    "Content.Empty.Title",
                    result?.ProviderErrors.Count > 0
                        ? "Content.Error.ProvidersMessage"
                        : "Content.Empty.Message",
                    retry: result?.ProviderErrors.Count > 0);
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
            ShowState("Content.Error.ProvidersTitle", "Content.Error.ProvidersMessage", retry: true);
        }
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
        if (_profile is not { SupportsPlugins: true })
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

        var request = new ContentInstallRequest(
            _context.ServerId,
            item.Provider,
            item.ProjectId);

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
                Notify(NotificationKind.Error, "Content.Error.ProvidersTitle", "Content.Error.ProvidersMessage");
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
        if (sender is not Button { DataContext: ContentItemViewModel item } || _profile is null)
        {
            return;
        }

        try
        {
            using var client = _context.CreateClient(TimeSpan.FromSeconds(30));
            var detail = await client.GetFromJsonAsync<ContentProjectDetail>(
                $"/api/v1/servers/{_context.ServerId}/content/projects/{item.Provider}/{Uri.EscapeDataString(item.ProjectId)}");
            if (detail is null)
            {
                Notify(NotificationKind.Error, "Content.Error.ProvidersTitle", "Content.Error.ProvidersMessage");
                return;
            }

            if (ContentProjectWindow.Show(Window.GetWindow(this), detail, _profile))
            {
                await InstallItemAsync(item);
            }
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            Notify(NotificationKind.Error, "Content.Error.ProvidersTitle", "Content.Error.ProvidersMessage");
        }
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

        _ = discovering ? SearchAsync() : LoadInstalledAsync();
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

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || sender is not (ComboBox or CheckBox))
        {
            return;
        }

        _ = SearchAsync();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) =>
        Filter_Changed(sender, (RoutedEventArgs)e);

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
            "ProviderUnavailable" or "ProviderSchema" => "Content.Error.ProvidersMessage",
            "HashMismatch" => "Content.Error.HashMismatch",
            "InvalidJar" => "Content.Error.InvalidJar",
            "ServerBusy" => "Content.Error.ServerBusy",
            "FileInUse" => "Content.Error.FileInUse",
            "AccessDenied" => "Content.Error.AccessDenied",
            "NoCompatibleVersion" => "Content.Error.NoCompatibleVersion",
            "NoRollback" or "RollbackMissing" => "Content.Error.NoRollback",
            "AlreadyCurrent" => "Content.Error.AlreadyCurrent",
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
