using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly UiPreferencesStore _preferencesStore = new();
    private bool _allowClose;
    private bool _syncingNavigation;
    private readonly DispatcherTimer _notificationTimer = new()
    {
        Interval = TimeSpan.FromSeconds(6)
    };

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();

        HomePage.ServerOpenRequested += (_, serverId) => OpenServer(serverId);
        HomePage.ServerStartRequested += async (_, serverId) => await StartServerAsync(serverId);
        ServersPage.ServerOpenRequested += (_, serverId) => OpenServer(serverId);
        ServersPage.ServerStartRequested += async (_, serverId) => await StartServerAsync(serverId);
        // A server's own Backups tab, where Create Backup and the backup list are.
        BackupsPage.BackupRequested += (_, serverId) => OpenServer(serverId, "Backups");
        ServerDetailPage.BackRequested += (_, _) =>
        {
            _viewModel.CloseServerDetail();
            SyncNavigationSelection();
        };
        SettingsPage.DiagnosticsRequested += async (_, _) => await ExportDiagnosticsAsync();
        _viewModel.RefreshRequested += (_, _) =>
        {
            BackupsPage.Reload();
            NetworkPage.Reload();
            SettingsPage.Reload();
        };
        SettingsPage.ExitForUpdateRequested += (_, _) =>
        {
            _allowClose = true;
            System.Windows.Application.Current.Shutdown();
        };
        SettingsPage.PreferencesApplied += (_, preferences) =>
        {
            // Applying preferences rebuilds the destination items, which drops both
            // ListBoxes' selection; without re-syncing, the sidebar loses its highlight.
            // A language or theme change must not move the sidebar either: the persisted
            // value can differ from what is on screen (a narrow window collapses it
            // automatically), and adopting it left labels drawn inside the 64px rail.
            _viewModel.ApplyUiPreferences(
                preferences with { SidebarCollapsed = _viewModel.SidebarCollapsed });
            SyncNavigationSelection();
        };

        Controls.ServerDeletion.ServerDeleted += OnServerDeleted;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        SizeChanged += OnSizeChanged;
        NotificationService.Published += OnNotificationPublished;
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            NotificationToast.Visibility = Visibility.Collapsed;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySidebarState(_viewModel.SidebarCollapsed, animate: false);
        SyncNavigationSelection();
        await _viewModel.StartAsync();
    }

    /// <summary>
    /// After the Agent confirmed a removal: close that server if it is open, land on the
    /// server list, and refresh it so the removed server is gone at once.
    /// </summary>
    private async void OnServerDeleted(object? sender, Guid serverId)
    {
        if (_viewModel.IsServerDetailOpen && _viewModel.OpenServerId == serverId)
        {
            _viewModel.CloseServerDetail();
        }

        ShowSection("Servers");
        await Controls.DashboardFeed.Shared.RefreshAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SettingsPage.Dispose();
        Controls.ServerDeletion.ServerDeleted -= OnServerDeleted;
        _viewModel.Dispose();
        NotificationService.Published -= OnNotificationPublished;
        _notificationTimer.Stop();
    }

    private void OnNotificationPublished(object? sender, AppNotification notification)
    {
        Dispatcher.Invoke(() =>
        {
            NotificationTitle.Text = notification.Title;
            NotificationMessage.Text = notification.Message;
            NotificationToast.BorderBrush = (System.Windows.Media.Brush)FindResource(
                notification.Kind switch
                {
                    NotificationKind.Success => "SuccessBrush",
                    NotificationKind.Warning => "WarningBrush",
                    NotificationKind.Error => "DangerBrush",
                    _ => "AccentBrush"
                });
            NotificationToast.Visibility = Visibility.Visible;
            AnnounceNotification(notification.Kind == NotificationKind.Error);
            _notificationTimer.Stop();
            if (!notification.Persistent)
            {
                _notificationTimer.Start();
            }
        });
    }

    /// <summary>
    /// The toast is the only report of whether a backup, restore or server action worked, so
    /// assistive technology is told about it too; failures interrupt, successes wait their turn.
    /// </summary>
    private void AnnounceNotification(bool isError)
    {
        System.Windows.Automation.AutomationProperties.SetName(
            NotificationMessage,
            $"{NotificationTitle.Text}. {NotificationMessage.Text}");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(
            NotificationMessage,
            isError
                ? System.Windows.Automation.AutomationLiveSetting.Assertive
                : System.Windows.Automation.AutomationLiveSetting.Polite);
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(
            NotificationMessage);
        peer?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void DismissNotification_Click(object sender, RoutedEventArgs e)
    {
        _notificationTimer.Stop();
        NotificationToast.Visibility = Visibility.Collapsed;
    }

    // --- Public surface used by the tray icon ---

    public void ShowDashboard()
    {
        Show();
        // Restore only from minimised. In-app links (Settings > Go to Backups) and the tray
        // both come through here, and forcing Normal un-maximised a maximised window.
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void ShowSection(string key)
    {
        ShowDashboard();
        _viewModel.SelectSection(key);
        SyncNavigationSelection();
    }

    /// <summary>
    /// Opens one named server in Server Detail. The tray still speaks in Build 5 tab indices
    /// (1 = Console, 2 = Settings). Several servers of the same game can be registered, so
    /// everything with a particular server in mind asks for it by id rather than by game.
    /// </summary>
    public void ShowServer(Guid serverId, int tabIndex = 0)
    {
        ShowDashboard();
        OpenServer(
            serverId,
            tabIndex switch
            {
                1 => "Console",
                2 => "Settings",
                _ => "Overview"
            });
    }

    /// <summary>The tray's Create item opens that game's installer, as it did in Build 5.</summary>
    public void OpenServerCreation(GameType game)
    {
        ShowSection("Servers");
        ServersPage.OpenInstaller(game);
    }

    public void PrepareForExit() => _allowClose = true;

    /// <summary>The PC-wide resource policy editor, for the tray's Resource Mode > Custom.</summary>
    public void OpenResourcePolicy()
    {
        ShowDashboard();
        new ResourceGovernorWindow { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Writes a diagnostics bundle where the person chooses. The export itself redacts
    /// secrets; this only picks the destination and reports the outcome.
    /// </summary>
    private async Task ExportDiagnosticsAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"1salem-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "Zip archive (*.zip)|*.zip"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var path = await DiagnosticsService.ExportAsync(dialog.FileName);
            NotificationService.Publish(
                NotificationKind.Success,
                LocalizationService.Get("Action.Diagnostics"),
                path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get("Action.Diagnostics"),
                exception.Message);
        }
    }

    private void OpenServer(Guid serverId, string tab = "Overview")
    {
        _viewModel.OpenServerDetail(serverId);
        ServerDetailPage.Show(serverId, tab);
        SyncNavigationSelection();
        // After layout: the page has only just been made visible.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(ServerDetailPage.FocusSelectedTab));
    }

    /// <summary>
    /// A card labelled Start must start the server, as Build 5's Home did. It opens the server
    /// so the person watches it come up, then uses Server Detail's own checked start path.
    /// </summary>
    private async Task StartServerAsync(Guid serverId)
    {
        OpenServer(serverId);
        await ServerDetailPage.StartAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    // --- Navigation ---

    // Selection is driven explicitly rather than by a TwoWay binding. The primary list cannot
    // hold the pinned Settings entry, so a bound Selector would keep resetting itself and
    // writing that reset back into the view model.
    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection fires while the XAML is still loading, before the pinned Settings list
        // below has been created, so the field can legitimately still be null here.
        if (_syncingNavigation || SettingsNavigationList is null)
        {
            return;
        }

        if (NavigationList.SelectedItem is not NavigationItem item)
        {
            return;
        }

        // Selecting a primary destination clears the pinned Settings entry below it.
        _syncingNavigation = true;
        SettingsNavigationList.SelectedItem = null;
        _viewModel.SelectSection(item.Key);
        _syncingNavigation = false;
    }

    private void SettingsNavigation_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingNavigation || NavigationList is null ||
            SettingsNavigationList.SelectedItem is null)
        {
            return;
        }

        _syncingNavigation = true;
        NavigationList.SelectedItem = null;
        _viewModel.SelectSection("Settings");
        _syncingNavigation = false;
    }

    /// <summary>Points the two lists at whatever the view model says is current.</summary>
    private void SyncNavigationSelection()
    {
        _syncingNavigation = true;
        if (_viewModel.IsSettingsSelected)
        {
            NavigationList.SelectedItem = null;
            SettingsNavigationList.SelectedIndex = 0;
        }
        else
        {
            SettingsNavigationList.SelectedItem = null;
            NavigationList.SelectedItem = _viewModel.PrimarySections.FirstOrDefault(
                item => item.Key.Equals(
                    _viewModel.SelectedSection.Key,
                    StringComparison.Ordinal));
        }

        _syncingNavigation = false;
    }

    private void ToggleNavigation_Click(object sender, RoutedEventArgs e)
    {
        var collapsed = !_viewModel.SidebarCollapsed;
        _viewModel.SidebarCollapsed = collapsed;
        ApplySidebarState(collapsed, animate: true);
        PersistSidebarPreference(collapsed);
    }

    private void ApplySidebarState(bool collapsed, bool animate)
    {
        const double expandedWidth = 228;
        const double collapsedWidth = 64;
        var target = collapsed ? collapsedWidth : expandedWidth;

        if (!animate)
        {
            NavigationColumn.BeginAnimation(WidthProperty, null);
            NavigationColumn.Width = target;
            return;
        }

        var storyboard = (Storyboard)FindResource(
            collapsed ? "CollapseSidebarStoryboard" : "ExpandSidebarStoryboard");
        storyboard.Begin(this);
    }

    private void PersistSidebarPreference(bool collapsed)
    {
        try
        {
            var current = _preferencesStore.Load();
            _preferencesStore.Save(current with { SidebarCollapsed = collapsed });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A preference that cannot be written must never break navigation.
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Collapse automatically on narrow windows, but never fight an explicit choice to
        // expand. Acting on width alone re-collapsed the sidebar on the very next resize
        // event after the person expanded it; only the crossing of the breakpoint counts.
        if (!e.WidthChanged)
        {
            return;
        }

        const double NarrowBreakpoint = 1080;
        var wasNarrow = e.PreviousSize.Width < NarrowBreakpoint;
        var isNarrow = e.NewSize.Width < NarrowBreakpoint;
        if (wasNarrow == isNarrow || !isNarrow || _viewModel.SidebarCollapsed)
        {
            return;
        }

        _viewModel.SidebarCollapsed = true;
        ApplySidebarState(true, animate: true);
    }
}
