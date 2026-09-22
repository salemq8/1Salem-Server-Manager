using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Infrastructure.Windows;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

/// <summary>
/// Application-wide settings only. Anything belonging to one server lives in that server's own
/// Settings tab, and nothing here shows a raw configuration key or a credential.
/// </summary>
public partial class SettingsPageControl : UserControl, IDisposable
{
    private const string ArabicDisplayName = "العربية";

    private readonly UiPreferencesStore _store = new();
    private readonly WindowsStartupManager _startupManager = new();
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromSeconds(30));
    private bool _loading;
    private bool _updateDetailsOpenedForOffer;
    private AgentStatusResponse? _agent;

    public SettingsPageControl()
    {
        InitializeComponent();
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Localize);
        UpdateInstaller.ExitForUpdateRequested += (_, _) =>
            ExitForUpdateRequested?.Invoke(this, EventArgs.Empty);
        // One source of truth for update status: the proven control's own refresh.
        UpdateInstaller.StatusChanged += (_, _) => RenderUpdates();
        Loaded += OnLoaded;
    }

    /// <summary>Raised after preferences are saved so the shell can re-apply them.</summary>
    public event EventHandler<UiPreferences>? PreferencesApplied;

    /// <summary>Raised when the person asks for the diagnostics export.</summary>
    public event EventHandler? DiagnosticsRequested;

    /// <summary>
    /// Raised once the updater has been launched and is waiting on this process to exit.
    /// The shell must actually exit: the updater will not replace the Client's files while
    /// the process it was told to wait for is still running.
    /// </summary>
    public event EventHandler? ExitForUpdateRequested;

    public void Dispose()
    {
        UpdateInstaller.Dispose();
        _httpClient.Dispose();
    }

    /// <summary>Re-reads service and update status, e.g. from the top-bar Refresh.</summary>
    public void Reload()
    {
        _ = LoadAgentAsync();
        _ = UpdateInstaller.RefreshNowAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        Localize();
        var preferences = _store.Load();
        SelectByTag(LanguageBox, preferences.Language);
        SelectByTag(ThemeBox, preferences.Theme);
        AppearanceStatus.Text = string.Empty;
        _loading = false;
        ApplyCategory();
        await LoadAgentAsync();
    }

    /// <summary>
    /// Re-read whenever Advanced or About is shown, so the service details are never a
    /// snapshot from whenever the app happened to start.
    /// </summary>
    private async Task LoadAgentAsync()
    {
        try
        {
            _agent = await _httpClient.GetFromJsonAsync<AgentStatusResponse>("/api/v1/agent/status");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _agent = null;
        }

        RenderAgent();
    }

    private string CurrentCategory =>
        (CategoryList.SelectedItem as ListBoxItem)?.Tag as string ?? "General";

    private void Localize()
    {
        if (CategoryList is null)
        {
            return;
        }

        foreach (var item in CategoryList.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is string tag)
            {
                item.Content = LocalizationService.Get($"Settings.Category.{tag}");
            }
        }

        GeneralTitle.Text = LocalizationService.Get("Settings.Category.General");
        GeneralDescription.Text = LocalizationService.Get("Settings.GeneralDescription");
        StartWithWindowsLabel.Text = LocalizationService.Get("Settings.StartWithWindows");
        StartWithWindowsNote.Text = LocalizationService.Get("Settings.StartWithWindowsUnavailable");
        GeneralNote.Text = LocalizationService.Get("Settings.CloseBehaviour");

        AppearanceTitle.Text = LocalizationService.Get("Appearance.Title");
        AppearanceDescription.Text = LocalizationService.Get("Appearance.Description");
        LanguageLabel.Text = LocalizationService.Get("Appearance.Language");
        ThemeLabel.Text = LocalizationService.Get("Appearance.Theme");

        UpdatesTitle.Text = LocalizationService.Get("Settings.Category.Updates");
        UpdatesVersionLabel.Text = LocalizationService.Get("Settings.Version");
        CheckUpdatesButton.Content = LocalizationService.Get("Action.CheckUpdates");
        UpdateDetails.Header = LocalizationService.Get("Updates.Details");

        AdvancedTitle.Text = LocalizationService.Get("Settings.Category.Advanced");
        AdvancedIntro.Text = LocalizationService.Get("Settings.AdvancedIntro");
        DiagnosticsButton.Content = LocalizationService.Get("Action.Diagnostics");
        OpenDataFolderButton.Content = LocalizationService.Get("Settings.OpenDataFolder");
        CopyAgentInfoButton.Content = LocalizationService.Get("Settings.CopyServiceInfo");
        AdminToolsButton.Content = LocalizationService.Get("Shell.AdministratorTools");
        AgentExpander.Header = LocalizationService.Get("Settings.ServiceInformation");

        AboutProduct.Text = LocalizationService.Get("AppTitle");
        AboutLogsButton.Content = LocalizationService.Get("Settings.OpenLogs");
        AboutDiagnosticsButton.Content = LocalizationService.Get("Action.Diagnostics");
        AboutCopyright.Text = LocalizationService.Get("Settings.Copyright");

        BuildLanguageItems();
        BuildThemeItems();
        ApplyCategory();
    }

    private void BuildLanguageItems()
    {
        if (LanguageBox.Items.Count == 0)
        {
            // Language names stay in their own language by convention.
            LanguageBox.Items.Add(new ComboBoxItem { Content = "English", Tag = "en-US" });
            LanguageBox.Items.Add(new ComboBoxItem { Content = ArabicDisplayName, Tag = "ar-SA" });
        }
    }

    private void BuildThemeItems()
    {
        if (ThemeBox.Items.Count == 0)
        {
            ThemeBox.Items.Add(new ComboBoxItem { Tag = AppTheme.Dark });
            ThemeBox.Items.Add(new ComboBoxItem { Tag = AppTheme.Light });
            ThemeBox.Items.Add(new ComboBoxItem { Tag = AppTheme.FollowWindows });
        }

        foreach (var item in ThemeBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag switch
            {
                AppTheme.Dark => LocalizationService.Get("Appearance.Dark"),
                AppTheme.Light => LocalizationService.Get("Appearance.Light"),
                _ => LocalizationService.Get("Appearance.FollowWindows")
            };
        }
    }

    private static void SelectByTag(ComboBox box, object tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (Equals(item.Tag, tag))
            {
                box.SelectedItem = item;
                return;
            }
        }

        box.SelectedIndex = 0;
    }

    private void Category_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires mid-parse before the sections exist; OnLoaded applies the initial state.
        if (GeneralSection is null)
        {
            return;
        }

        ApplyCategory();
    }

    private void ApplyCategory()
    {
        if (GeneralSection is null)
        {
            return;
        }

        var category = CurrentCategory;
        GeneralSection.Visibility = Show(category, "General");
        AppearanceSection.Visibility = Show(category, "Appearance");
        UpdatesSection.Visibility = Show(category, "Updates");
        AdvancedSection.Visibility = Show(category, "Advanced");
        AboutSection.Visibility = Show(category, "About");
        PolicySection.Visibility = category is "Backups" or "Network"
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (PolicySection.Visibility == Visibility.Visible)
        {
            RenderPolicy(category);
        }

        if (category == "General")
        {
            RenderStartup();
        }

        if (category == "Updates")
        {
            RenderUpdates();
            _ = UpdateInstaller.RefreshNowAsync();
        }

        if (category is "Advanced" or "About")
        {
            RenderAgent();
            _ = LoadAgentAsync();
        }
    }

    private static Visibility Show(string category, string expected) =>
        string.Equals(category, expected, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Global backup and remote-access policy is owned by the agent and configured per server
    /// today. Rather than invent application-level settings the backend does not have, this
    /// says where the real controls are and takes the person there.
    /// </summary>
    private void RenderPolicy(string category)
    {
        PolicyTitle.Text = LocalizationService.Get($"Settings.Category.{category}");
        PolicyBody.Text = LocalizationService.Get($"Settings.{category}Policy");
        PolicyActionButton.Content = LocalizationService.Get($"Settings.{category}PolicyAction");
        PolicyActionButton.Tag = category;
    }

    private void RenderUpdates()
    {
        // Visible product version is 1.5; the build revision is a detail of Update details.
        UpdatesVersionValue.Text = LocalizationService.Format(
            "Settings.VersionValue",
            ProductInfo.Version);

        var status = UpdateInstaller.CurrentStatus;
        UpdatesStatus.Text = ApplicationUpdateControl.DescribeSimpleStatus(
            status,
            UpdateInstaller.LastRefreshFailed);

        // When there is something to do, open the flow for the person — once per offer, so
        // closing it again is respected until the next update comes along.
        var actionable =
            status is { IsUpdateAvailable: true } ||
            status?.Stage is ApplicationUpdateStage.Downloading
                or ApplicationUpdateStage.Verified
                or ApplicationUpdateStage.ReadyToInstall;
        if (actionable && !_updateDetailsOpenedForOffer)
        {
            UpdateDetails.IsExpanded = true;
            _updateDetailsOpenedForOffer = true;
        }
        else if (!actionable)
        {
            _updateDetailsOpenedForOffer = false;
        }
    }

    private void RenderAgent()
    {
        AboutVersion.Text = LocalizationService.Format(
            "Settings.VersionValue",
            ProductInfo.Version);
        AboutBuild.Text = LocalizationService.Format(
            "Settings.BuildValue",
            ProductInfo.BuildRevision);

        AgentBody.Text = _agent is { } agent
            ? string.Join(
                Environment.NewLine,
                LocalizationService.Format("Settings.Agent.Version", agent.Version),
                LocalizationService.Format("Settings.Agent.Machine", agent.MachineName),
                LocalizationService.Get(
                    agent.DatabaseReady ? "Settings.Agent.DatabaseReady" : "Settings.Agent.DatabaseStarting"))
            : LocalizationService.Get("Status.Unavailable");
    }

    /// <summary>Reflects what Windows actually has registered, not what was last clicked.</summary>
    private void RenderStartup()
    {
        var available = TrayIconService.IsDashboardExecutable(Environment.ProcessPath);
        StartWithWindowsNote.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            StartWithWindowsToggle.IsChecked = _startupManager.IsEnabled();
            StartWithWindowsToggle.IsEnabled = available;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StartWithWindowsToggle.IsEnabled = false;
        }
    }

    // --- actions ----------------------------------------------------------------

    private void Preference_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "en-US";
        var theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as AppTheme? ?? AppTheme.Dark;

        // Apply first and tell the shell, so the whole window follows the change even when it
        // cannot be written down; a failed save must not leave the page half switched.
        var preferences = _store.Load() with { Language = language, Theme = theme };
        LocalizationService.Apply(language);
        ThemeService.Apply(theme);
        PreferencesApplied?.Invoke(this, preferences);
        try
        {
            _store.Save(preferences);
            AppearanceStatus.Text = LocalizationService.Get("Appearance.Applied");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            AppearanceStatus.Text = LocalizationService.Get("Appearance.NotSaved");
        }
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        // Same registration the tray menu's "Start Dashboard with Windows" item performs.
        var executable = Environment.ProcessPath;
        if (TrayIconService.IsDashboardExecutable(executable))
        {
            try
            {
                var result = _startupManager.SetEnabled(
                    executable!,
                    StartWithWindowsToggle.IsChecked == true);
                if (!result.Success)
                {
                    NotificationService.Publish(
                        NotificationKind.Error,
                        LocalizationService.Get("Settings.StartWithWindows"),
                        LocalizationService.Get("Settings.StartWithWindowsFailed"));
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                NotificationService.Publish(
                    NotificationKind.Error,
                    LocalizationService.Get("Settings.StartWithWindows"),
                    LocalizationService.Get("Settings.StartWithWindowsFailed"));
            }
        }

        RenderStartup();
    }

    private void PolicyAction_Click(object sender, RoutedEventArgs e)
    {
        // Takes them to where the real controls live rather than duplicating them here.
        if (Window.GetWindow(this) is MainWindow window)
        {
            window.ShowSection(
                string.Equals(PolicyActionButton.Tag as string, "Backups", StringComparison.Ordinal)
                    ? "Backups"
                    : "Network");
        }
    }

    /// <summary>
    /// Asks the agent to check, then reads the result back through the update control. The
    /// outcome — including a failed check — comes from the agent's own status, so this card
    /// can never claim "up to date" on the strength of a request that did not work.
    /// </summary>
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdatesStatus.Text = LocalizationService.Get("Updates.Stage.Checking");
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/application-updates/check",
                new { });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Reported below from the status read-back, which fails the same way.
        }
        finally
        {
            await UpdateInstaller.RefreshNowAsync();
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e) =>
        DiagnosticsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The proven admin tools window, unchanged, reachable from Advanced.</summary>
    private void AdminTools_Click(object sender, RoutedEventArgs e) =>
        new AdminToolsWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void CopyAgentInfo_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AgentBody.Text))
        {
            SafeClipboard.TrySetText(AgentBody.Text);
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(AgentTransportDefaults.ResolveDataRoot());

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(AgentTransportDefaults.ResolveDataRoot(), "logs"));

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var info = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
            info.ArgumentList.Add(path);
            Process.Start(info);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get("Settings.OpenDataFolder"),
                exception.Message);
        }
    }
}
