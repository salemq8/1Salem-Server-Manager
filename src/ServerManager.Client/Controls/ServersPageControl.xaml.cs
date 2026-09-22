using System.ComponentModel;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Controls;

public partial class ServersPageControl : UserControl, INotifyPropertyChanged
{
    private readonly DashboardFeed _feed = DashboardFeed.Shared;

    public ServersPageControl()
    {
        InitializeComponent();
        DataContext = this;
        ServerList.ItemsSource = _feed.Servers;
        _feed.PropertyChanged += (_, _) => Dispatcher.Invoke(Render);
        _feed.Servers.CollectionChanged += (_, _) => Dispatcher.Invoke(Render);
        LocalizationService.LanguageChanged += OnLanguageChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        Loaded += OnLoaded;
    }

    private void OnThemeChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            foreach (var card in _feed.Servers)
            {
                card.RefreshThemeBindings();
            }

            Render();
        });

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<Guid>? ServerOpenRequested;

    public event EventHandler<Guid>? ServerStartRequested;

    public string PlayersLabel => LocalizationService.Get("Metric.Players");

    public string UptimeLabel => LocalizationService.Get("Metric.Uptime");

    public string MemoryLabel => LocalizationService.Get("Metric.Memory");

    public string AdvancedLabel => LocalizationService.Get("Advanced.Title");

    public string MoreActionsLabel => LocalizationService.Get("Action.MoreActions");

    public string RootProcessLabel => LocalizationService.Get("Advanced.RootProcess");

    public string GameProcessLabel => LocalizationService.Get("Advanced.GameProcess");

    public string ChildProcessesLabel => LocalizationService.Get("Advanced.ChildProcesses");

    public string PortLabel => LocalizationService.Get("Advanced.Port");

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            foreach (var name in new[]
                     {
                         nameof(PlayersLabel),
                         nameof(UptimeLabel),
                         nameof(MemoryLabel),
                         nameof(AdvancedLabel),
                         nameof(MoreActionsLabel),
                         nameof(RootProcessLabel),
                         nameof(GameProcessLabel),
                         nameof(ChildProcessesLabel),
                         nameof(PortLabel)
                     })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }

            foreach (var card in _feed.Servers)
            {
                card.RefreshLocalizedText();
            }

            Render();
        });

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _feed.Start();
        await _feed.RefreshAsync();
        Render();
    }

    private void Render()
    {
        ListHeading.Text = LocalizationService.Get("Servers");
        AddServerButton.Content = LocalizationService.Get("Empty.AddServer");
        MenuAddPalworld.Header = LocalizationService.Get("Palworld");
        MenuAddMinecraft.Header = LocalizationService.Get("Minecraft");

        var stateShown = StateView.Apply(
            _feed,
            "Empty.NoServers",
            "Empty.NoServersMessage");
        ServerList.Visibility = stateShown ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Creating a server is game-specific, so the button asks which rather than guessing.
    /// The install flows themselves are the proven Build 5 windows, unchanged.
    /// </summary>
    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        AddServerMenu.PlacementTarget = AddServerButton;
        AddServerMenu.IsOpen = true;
    }

    private void AddPalworld_Click(object sender, RoutedEventArgs e) =>
        OpenInstaller(GameType.Palworld);

    private void AddMinecraft_Click(object sender, RoutedEventArgs e) =>
        OpenInstaller(GameType.Minecraft);

    /// <summary>Also used by the tray's Create item, which already knows the game.</summary>
    public void OpenInstaller(GameType game) =>
        ShowInstaller(game == GameType.Minecraft
            ? new MinecraftInstallWindow()
            : new PalworldInstallWindow());

    private async void ShowInstaller(Window window)
    {
        window.Owner = Window.GetWindow(this);
        window.ShowDialog();
        await DashboardFeed.Shared.RefreshAsync();
        Render();
    }

    private async void State_RetryRequested(object? sender, EventArgs e)
    {
        await _feed.RefreshAsync();
        Render();
    }

    private void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        // The card itself is clickable; without this the click also opens it a second time.
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: ServerCardViewModel card })
        {
            return;
        }

        if (card.PrimaryActionStartsServer)
        {
            ServerStartRequested?.Invoke(this, card.ServerId);
        }
        else
        {
            ServerOpenRequested?.Invoke(this, card.ServerId);
        }
    }

    private void MoreActions_Click(object sender, RoutedEventArgs e)
    {
        // The card itself is clickable; without this the click also opens it a second time.
        e.Handled = true;

        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            return;
        }

        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            item.Header = LocalizationService.Get(
                item.Tag as string == "Delete" ? "Action.DeleteServer" : "Action.Open");
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void MenuOpenServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ServerCardViewModel card })
        {
            ServerOpenRequested?.Invoke(this, card.ServerId);
        }
    }

    private async void MenuDeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ServerCardViewModel card })
        {
            await ServerDeletion.RequestAsync(Window.GetWindow(this), card);
        }
    }
}
