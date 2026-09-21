using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
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
public partial class SettingsPageControl : UserControl
{
    private const string ArabicDisplayName = "العربية";

    private readonly UiPreferencesStore _store = new();
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromSeconds(30));
    private bool _loading;
    private ApplicationUpdateStatusResponse? _updates;
    private AgentStatusResponse? _agent;

    public SettingsPageControl()
    {
        InitializeComponent();
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Localize);
        Loaded += OnLoaded;
    }

    /// <summary>Raised after preferences are saved so the shell can re-apply them.</summary>
    public event EventHandler<UiPreferences>? PreferencesApplied;

    /// <summary>Raised when the person asks for the diagnostics export.</summary>
    public event EventHandler? DiagnosticsRequested;

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

        ApplyCategory();
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
        MinimiseToTrayLabel.Text = LocalizationService.Get("Settings.MinimiseToTray");
        GeneralNote.Text = LocalizationService.Get("Settings.MinimiseToTrayNote");

        AppearanceTitle.Text = LocalizationService.Get("Appearance.Title");
        AppearanceDescription.Text = LocalizationService.Get("Appearance.Description");
        LanguageLabel.Text = LocalizationService.Get("Appearance.Language");
        ThemeLabel.Text = LocalizationService.Get("Appearance.Theme");

        UpdatesTitle.Text = LocalizationService.Get("Settings.Category.Updates");
        UpdatesVersionLabel.Text = LocalizationService.Get("Settings.Version");
        CheckUpdatesButton.Content = LocalizationService.Get("Action.CheckUpdates");
        UpdatesAdvanced.Header = LocalizationService.Get("Advanced.Title");

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

        if (category == "Updates")
        {
            RenderUpdates();
        }

        if (category is "Advanced" or "About")
        {
            RenderAgent();
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
        // Visible product version is 1.5; the build revision is a diagnostic detail.
        UpdatesVersionValue.Text = LocalizationService.Format(
            "Settings.VersionValue",
            ProductInfo.Version);

        if (_updates is { } status)
        {
            UpdatesStatus.Text = status.IsUpdateAvailable
                ? LocalizationService.Format(
                    "Settings.UpdateAvailable",
                    status.LatestVersion ?? "—")
                : LocalizationService.Get("Settings.UpToDate");

            UpdatesAdvancedBody.Text = string.Join(
                Environment.NewLine,
                $"Channel  {status.Channel}",
                $"Build  {status.CurrentBuildRevision.ToString(CultureInfo.InvariantCulture)}",
                $"Stage  {status.Stage}",
                status.LastCheckedAtUtc is { } checkedAt
                    ? $"Checked  {checkedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}"
                    : "Checked  —");
        }
        else
        {
            UpdatesStatus.Text = LocalizationService.Get("ServerSettings.UpdatesUnknown");
            UpdatesAdvancedBody.Text = LocalizationService.Format(
                "Settings.BuildValue",
                ProductInfo.BuildRevision);
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
                $"Service  {agent.Version}",
                $"Machine  {agent.MachineName}",
                $"Database  {(agent.DatabaseReady ? "ready" : "starting")}")
            : LocalizationService.Get("Status.Unavailable");
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

        try
        {
            var preferences = _store.Load() with { Language = language, Theme = theme };
            LocalizationService.Apply(language);
            ThemeService.Apply(theme);
            _store.Save(preferences);
            AppearanceStatus.Text = LocalizationService.Get("Appearance.Applied");
            PreferencesApplied?.Invoke(this, preferences);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            AppearanceStatus.Text = LocalizationService.Get("Error.ServiceUnavailable");
        }
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

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/application-updates/check",
                new { });
            if (response.IsSuccessStatusCode)
            {
                _updates = await _httpClient.GetFromJsonAsync<ApplicationUpdateStatusResponse>(
                    "/api/v1/application-updates");
            }

            RenderUpdates();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            UpdatesStatus.Text = LocalizationService.Get("Error.ServiceUnavailable");
        }
        finally
        {
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
