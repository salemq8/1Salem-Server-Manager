using System.Windows;
using System.ComponentModel;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using System.Windows.Threading;
using System.Windows.Controls;

namespace ServerManager.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private bool _navigationCollapsed;
    private bool _changingMainNavigation;
    private readonly DispatcherTimer _notificationTimer = new()
    {
        Interval = TimeSpan.FromSeconds(6)
    };

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        MinecraftPage.Configure(GameType.Minecraft);
        PalworldPage.Configure(GameType.Palworld);
        HomeDashboard.CreateMinecraftRequested += (_, _) => OpenMinecraftInstaller();
        HomeDashboard.CreatePalworldRequested += (_, _) => OpenPalworldInstaller();
        HomeDashboard.ManageRequested += (_, request) =>
            ShowServer(request.Game, request.TabIndex);
        MinecraftPage.CreateRequested += (_, _) => OpenMinecraftInstaller();
        PalworldPage.CreateRequested += (_, _) => OpenPalworldInstaller();
        MinecraftPage.RemoteAccessRequested += (_, _) => ShowSection("RemoteAccess");
        PalworldPage.RemoteAccessRequested += (_, _) => ShowSection("RemoteAccess");
        ApplicationUpdatePage.ExitForUpdateRequested += (_, _) =>
        {
            _allowClose = true;
            System.Windows.Application.Current.Shutdown();
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
        LoadInlineAppearance();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) =>
        await _viewModel.StartAsync();

    private void OnClosed(object? sender, EventArgs e)
    {
        HomeDashboard.Dispose();
        MinecraftPage.Dispose();
        PalworldPage.Dispose();
        RemoteAccessPage.Dispose();
        ApplicationUpdatePage.Dispose();
        NetworkDashboard.Dispose();
        _viewModel.Dispose();
        NotificationService.Published -= OnNotificationPublished;
        _notificationTimer.Stop();
    }

    private void OnNotificationPublished(
        object? sender,
        AppNotification notification)
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

    private void DismissNotification_Click(
        object sender,
        RoutedEventArgs e)
    {
        _notificationTimer.Stop();
        NotificationToast.Visibility = Visibility.Collapsed;
    }

    public void ShowDashboard()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ShowSection(string key)
    {
        ShowDashboard();
        _viewModel.SelectedSection = _viewModel.Sections.First(section =>
            section.Key.Equals(key, StringComparison.Ordinal));
    }

    public void ShowServer(GameType game, int tabIndex = 0)
    {
        ShowDashboard();
        SelectGame(game);
        (game == GameType.Minecraft ? MinecraftPage : PalworldPage)
            .SelectTab(tabIndex);
    }

    public void OpenServerCreation(GameType game)
    {
        ShowDashboard();
        if (game == GameType.Minecraft)
        {
            OpenMinecraftInstaller();
        }
        else
        {
            OpenPalworldInstaller();
        }
    }

    public void PrepareForExit() => _allowClose = true;

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

    private void ToggleNavigation_Click(object sender, RoutedEventArgs e) =>
        SetNavigationCollapsed(!_navigationCollapsed);

    private async void NavigationList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_changingMainNavigation ||
            e.AddedItems.Count == 0 ||
            e.RemovedItems.Count == 0)
        {
            return;
        }

        var previous = e.RemovedItems[0] as NavigationItem;
        var requested = e.AddedItems[0] as NavigationItem;
        if (previous is null || requested is null)
        {
            return;
        }

        var page = previous.Key switch
        {
            "Minecraft" => MinecraftPage,
            "Palworld" => PalworldPage,
            _ => null
        };
        if (page is null || !page.HasUnsavedChanges)
        {
            return;
        }

        _changingMainNavigation = true;
        NavigationList.SelectedItem = previous;
        _viewModel.SelectedSection = previous;
        _changingMainNavigation = false;

        if (!await page.ConfirmNavigationAwayAsync())
        {
            return;
        }

        _changingMainNavigation = true;
        NavigationList.SelectedItem = requested;
        _viewModel.SelectedSection = requested;
        _changingMainNavigation = false;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && e.NewSize.Width < 1080 && !_navigationCollapsed)
        {
            SetNavigationCollapsed(true);
        }
    }

    private void SetNavigationCollapsed(bool collapsed)
    {
        _navigationCollapsed = collapsed;
        NavigationColumn.Width = collapsed
            ? new GridLength(0)
            : new GridLength(210);
        NavigationPanel.Visibility =
            collapsed ? Visibility.Collapsed : Visibility.Visible;
        NavigationToggleButton.Content =
            collapsed
                ? LocalizationService.Get("Shell.ShowNavigation")
                : LocalizationService.Get("Shell.HideNavigation");
    }

    private void OpenMinecraftInstaller_Click(object sender, RoutedEventArgs e)
        => OpenMinecraftInstaller();

    private void OpenMinecraftInstaller()
    {
        var wizard = new MinecraftInstallWindow
        {
            Owner = this
        };
        wizard.ServerCreated += async (_, _) =>
        {
            SelectGame(GameType.Minecraft);
            await MinecraftPage.RefreshNowAsync();
            await HomeDashboard.RefreshNowAsync();
        };
        wizard.ShowDialog();
    }

    private void OpenMinecraftManager_Click(object sender, RoutedEventArgs e) =>
        OpenServerManager(GameType.Minecraft);

    private void OpenPalworldInstaller_Click(object sender, RoutedEventArgs e)
        => OpenPalworldInstaller();

    private void OpenPalworldInstaller()
    {
        var wizard = new PalworldInstallWindow
        {
            Owner = this
        };
        wizard.ShowDialog();
        _ = PalworldPage.RefreshNowAsync();
        _ = HomeDashboard.RefreshNowAsync();
    }

    private void OpenPalworldManager_Click(object sender, RoutedEventArgs e) =>
        OpenServerManager(GameType.Palworld);

    private void OpenServerManager(GameType game)
    {
        SelectGame(game);
    }

    private void OpenBackupCenter_Click(object sender, RoutedEventArgs e)
    {
        var window = new BackupCenterWindow
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenResourceGovernor_Click(object sender, RoutedEventArgs e)
    {
        var window = new ResourceGovernorWindow
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenLanPairing_Click(object sender, RoutedEventArgs e)
    {
        var window = new LanPairingWindow
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenNetworkStatus_Click(object sender, RoutedEventArgs e)
    {
        var window = new NetworkStatusWindow
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenAdminTools_Click(object sender, RoutedEventArgs e)
    {
        var window = new AdminToolsWindow
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenAppearance_Click(object sender, RoutedEventArgs e)
    {
        var window = new AppearanceWindow
        {
            Owner = this
        };
        window.PreferencesApplied += (_, preferences) =>
            _viewModel.ApplyUiPreferences(preferences);
        window.ShowDialog();
    }

    private void LoadInlineAppearance()
    {
        var preferences = new UiPreferencesStore().Load();
        SelectByTag(SettingsLanguageBox, preferences.Language);
        SelectByTag(SettingsThemeBox, preferences.Theme.ToString());
    }

    private void ApplyInlineAppearance_Click(
        object sender,
        RoutedEventArgs e)
    {
        var language = SelectedTag(SettingsLanguageBox);
        if (!Enum.TryParse<AppTheme>(
                SelectedTag(SettingsThemeBox),
                out var theme))
        {
            theme = AppTheme.Dark;
        }

        var preferences = new UiPreferences(language, theme);
        new UiPreferencesStore().Save(preferences);
        LocalizationService.Apply(language);
        ThemeService.Apply(theme);
        _viewModel.ApplyUiPreferences(preferences);
        SetNavigationCollapsed(_navigationCollapsed);
        InlineAppearanceStatusText.Text =
            LocalizationService.Get("Appearance.Applied");
        NotificationService.Publish(
            NotificationKind.Success,
            LocalizationService.Get("Appearance.Title"),
            InlineAppearanceStatusText.Text);
    }

    private static string SelectedTag(
        System.Windows.Controls.Primitives.Selector selector) =>
        (selector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
        string.Empty;

    private static void SelectByTag(
        System.Windows.Controls.Primitives.Selector selector,
        string tag)
    {
        selector.SelectedItem = selector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                string.Equals(
                    item.Tag?.ToString(),
                    tag,
                    StringComparison.OrdinalIgnoreCase));
        if (selector.SelectedIndex < 0)
        {
            selector.SelectedIndex = 0;
        }
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var window = new DiagnosticsWindow
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenFileManager_Click(object sender, RoutedEventArgs e)
    {
        var window = new FileManagerWindow
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenMinecraftUpdates_Click(object sender, RoutedEventArgs e)
    {
        SelectGame(GameType.Minecraft);
        MinecraftPage.SelectTab(6);
    }

    private void OpenPalworldUpdates_Click(object sender, RoutedEventArgs e)
    {
        SelectGame(GameType.Palworld);
        PalworldPage.SelectTab(6);
    }

    private void OpenMinecraftConsole_Click(object sender, RoutedEventArgs e)
    {
        SelectGame(GameType.Minecraft);
        MinecraftPage.SelectTab(1);
    }

    private void OpenPalworldConsole_Click(object sender, RoutedEventArgs e)
    {
        SelectGame(GameType.Palworld);
        PalworldPage.SelectTab(1);
    }

    private void SelectGame(GameType game)
    {
        var key = game == GameType.Minecraft ? "Minecraft" : "Palworld";
        _viewModel.SelectedSection = _viewModel.Sections.First(section =>
            section.Key.Equals(key, StringComparison.Ordinal));
    }
}
