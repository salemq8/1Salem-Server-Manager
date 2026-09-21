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
        HomePage.ServerStartRequested += (_, serverId) => OpenServer(serverId);
        ServersPage.ServerOpenRequested += (_, serverId) => OpenServer(serverId);
        ServersPage.ServerStartRequested += (_, serverId) => OpenServer(serverId);
        BackupsPage.BackupRequested += (_, serverId) => OpenServer(serverId);
        ServerDetailPage.BackRequested += (_, _) =>
        {
            _viewModel.CloseServerDetail();
            SyncNavigationSelection();
        };
        SettingsPage.DiagnosticsRequested += async (_, _) => await ExportDiagnosticsAsync();
        SettingsPage.PreferencesApplied += (_, preferences) =>
        {
            // Applying preferences rebuilds the destination items, which drops both
            // ListBoxes' selection; without re-syncing, the sidebar loses its highlight.
            _viewModel.ApplyUiPreferences(preferences);
            SyncNavigationSelection();
        };

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

    private void OnClosed(object? sender, EventArgs e)
    {
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
            _notificationTimer.Stop();
            if (!notification.Persistent)
            {
                _notificationTimer.Start();
            }
        });
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
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ShowSection(string key)
    {
        ShowDashboard();
        _viewModel.SelectSection(key);
        SyncNavigationSelection();
    }

    public void ShowServer(GameType game, int tabIndex = 0)
    {
        // Individual games no longer have their own destination; they live inside Servers.
        _ = game;
        _ = tabIndex;
        ShowSection("Servers");
    }

    public void OpenServerCreation(GameType game)
    {
        _ = game;
        ShowSection("Servers");
    }

    public void PrepareForExit() => _allowClose = true;

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

    private void OpenServer(Guid serverId)
    {
        _viewModel.OpenServerDetail(serverId);
        ServerDetailPage.Show(serverId);
        SyncNavigationSelection();
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
