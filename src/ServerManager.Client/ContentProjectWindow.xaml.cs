using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Client;

/// <summary>
/// What one plugin is, in plain text. Nothing here installs by itself: the window reports
/// what the provider said and hands the decision back to the person.
/// </summary>
public partial class ContentProjectWindow : Window
{
    private Uri? _projectUrl;
    private ContentProjectChoice _choice = new();
    private IReadOnlyList<PluginSoftwareChoice> _softwareChoices = [];

    private ContentProjectWindow()
    {
        InitializeComponent();

        // Arabic lays this window out right to left, the same as the rest of the app.
        FlowDirection = LayoutDirectionService.ForCulture(CultureInfo.CurrentUICulture);
    }

    /// <summary>Returns true when the person chose to install from here.</summary>
    public static bool Show(
        Window? owner,
        ContentProjectDetail detail,
        ServerContentProfile profile)
        => ShowAction(owner, detail, profile, []).Install;

    public static ContentProjectChoice ShowAction(Window? owner, ContentProjectDetail detail,
        ServerContentProfile profile, IReadOnlyList<PluginSoftwareChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(profile);
        var window = new ContentProjectWindow { Owner = owner, _softwareChoices = choices };
        window.Render(detail, profile);
        window.ShowDialog();
        return window._choice;
    }

    private void Render(ContentProjectDetail detail, ServerContentProfile profile)
    {
        var project = detail.Project;
        var providerName = project.Provider == ContentProviderId.Hangar ? "Hangar" : "Modrinth";
        Title = $"{project.Name} — {LocalizationService.Get("ServerTab.Content")}";
        NameText.Text = project.Name;
        ByLine.Text = string.IsNullOrWhiteSpace(project.Author)
            ? providerName
            : LocalizationService.Format("Content.ByAuthor", project.Author);
        _projectUrl = project.ProjectUrl;

        IconImage.Source = new ContentIconConverter().Convert(
            project.IconUrl?.ToString(),
            typeof(object),
            null,
            CultureInfo.CurrentUICulture) as System.Windows.Media.ImageSource;

        FactsHeading.Text = LocalizationService.Get("Content.Details.Facts");
        AboutHeading.Text = LocalizationService.Get("Content.Details.About");
        VersionLabel.Text = LocalizationService.Get("Content.Details.LatestCompatible");
        PlatformLabel.Text = LocalizationService.Get("Content.Details.Platforms");
        GameVersionLabel.Text = LocalizationService.Get("Content.Details.GameVersions");
        LicenseLabel.Text = LocalizationService.Get("Content.Details.License");
        DependencyLabel.Text = LocalizationService.Get("Content.Details.Dependencies");
        SourceText.Text = LocalizationService.Format("Content.Source", providerName);
        ViewOnProviderButton.Content = LocalizationService.Format("Content.ViewOn", providerName);
        ViewOnProviderButton.IsEnabled = _projectUrl is not null;
        CloseButton.Content = LocalizationService.Get("Action.Close");

        var latest = detail.LatestCompatible;
        VersionValue.Text = latest is null
            ? LocalizationService.Get("Content.Details.NoCompatible")
            : $"{latest.VersionNumber} · {ChannelName(latest.Channel)}";

        // Only the platforms this server could actually run, so the line cannot mislead.
        var platforms = latest?.Platforms ?? project.Platforms;
        PlatformValue.Text = platforms.Count > 0
            ? string.Join(" · ", platforms.Select(Capitalize))
            : LocalizationService.Get("Content.Unknown");

        var gameVersions = latest?.GameVersions ?? project.GameVersions;
        GameVersionValue.Text = gameVersions.Count > 0
            ? string.Join(", ", gameVersions.Take(12))
            : LocalizationService.Get("Content.Unknown");

        LicenseValue.Text = string.IsNullOrWhiteSpace(project.License)
            ? LocalizationService.Get("Content.Unknown")
            : project.License;

        var required = latest?.Dependencies
            .Where(dependency => dependency.Kind == ContentDependencyKind.Required)
            .Select(dependency => dependency.Name ?? dependency.ProjectId ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToArray() ?? [];
        DependencyValue.Text = required.Length > 0
            ? string.Join(", ", required)
            : LocalizationService.Get("Content.Details.NoDependencies");

        // Provider prose is shown as text only; it is never parsed as markup.
        DescriptionText.Text = ContentTextSanitizer.ToPlainText(
            project.Description ?? project.Summary);

        var requiresSoftware = PluginSoftwarePresentation.RequiresSoftware(project, profile);
        InstallButton.Content = requiresSoftware ? PluginSoftwarePresentation.RequiresLabel : LocalizationService.Get("Content.Install");
        InstallButton.IsEnabled = !requiresSoftware && latest is not null;
        InstallButton.Visibility = requiresSoftware ? Visibility.Collapsed : Visibility.Visible;
        SoftwareNotice.Visibility = requiresSoftware ? Visibility.Visible : Visibility.Collapsed;
        if (requiresSoftware)
        {
            CloseButton.Content = LocalizationService.Get("Action.Cancel");
            VersionValue.Text = PluginSoftwarePresentation.RequiresLabel;
            SoftwareHeading.Text = PluginSoftwarePresentation.RequiresLabel;
            SoftwareBody.Text = _softwareChoices.Count == 0
                ? PluginSoftwarePresentation.Text("No compatible Paper or Purpur migration is proven for this plugin release and exact Minecraft version. Nothing will be installed or changed.", "لا يوجد تغيير موثق إلى Paper أو Purpur يتوافق مع إصدار هذه الإضافة وإصدار Minecraft الحالي نفسه. لن يتم تثبيت أو تغيير أي شيء.")
                : PluginSoftwarePresentation.Text("Choose only a verified compatible runtime. Migration safety restrictions remain mandatory. No plugin is installed until you explicitly install it after a successful change.", "اختر برنامج تشغيل متوافقًا وموثقًا فقط. تبقى قيود سلامة التغيير إلزامية. لن يتم تثبيت الإضافة إلا باختيارك الصريح بعد نجاح التغيير.");
            SoftwareChoices.ItemsSource = _softwareChoices.Select(choice => new SoftwareChoiceView(choice,
                PluginSoftwarePresentation.ChangeTo(choice.Software.Platform),
                PluginSoftwarePresentation.ChoiceExplanation(choice, project.Name))).ToArray();
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!InstallButton.IsEnabled) return;
        _choice = new ContentProjectChoice(Install: true);
        DialogResult = true;
        Close();
    }

    private void ChangeSoftware_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ServerPlatform platform } ||
            !_softwareChoices.Any(choice => choice.Software.Platform == platform && choice.Software.Available)) return;
        _choice = new ContentProjectChoice(ChangeSoftware: platform);
        DialogResult = true;
    }

    private sealed record SoftwareChoiceView(PluginSoftwareChoice Choice, string Label, string Explanation);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ViewOnProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_projectUrl is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_projectUrl.ToString()) { UseShellExecute = true });
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

    private static string ChannelName(ContentReleaseChannel channel) =>
        LocalizationService.Get(channel switch
        {
            ContentReleaseChannel.Beta => "Content.Channel.Beta",
            ContentReleaseChannel.Alpha => "Content.Channel.Alpha",
            _ => "Content.Channel.Release"
        });

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
