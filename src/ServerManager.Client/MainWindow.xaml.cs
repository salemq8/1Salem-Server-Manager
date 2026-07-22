using System.Windows;
using System.ComponentModel;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;

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
        window.ShowDialog();
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
