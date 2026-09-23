using System.Globalization;
using System.IO;
using System.Windows;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client;

/// <summary>
/// Collects what a new modpack server needs, after showing what the pack actually is. A pack
/// that cannot be installed says so here and offers no Create button at all.
/// </summary>
public partial class ModpackInstallWindow : Window
{
    private ModpackInstallWindow()
    {
        InitializeComponent();
        FlowDirection = LayoutDirectionService.ForCulture(CultureInfo.CurrentUICulture);
    }

    /// <summary>The choices, or null when the person backed out.</summary>
    public static ModpackInstallRequest? Ask(Window? owner, ModpackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var window = new ModpackInstallWindow { Owner = owner };
        window.Render(plan);
        return window.ShowDialog() == true ? window.Result : null;
    }

    private ModpackInstallRequest? Result { get; set; }

    private void Render(ModpackPlan plan)
    {
        Title = LocalizationService.Get("Content.Modpack.Title");
        HeadingText.Text = plan.ProjectName;
        SubheadingText.Text = LocalizationService.Get("Content.Modpack.Subtitle");

        MinecraftLabel.Text = LocalizationService.Get("Content.Details.GameVersions");
        LoaderLabel.Text = LocalizationService.Get("Content.Modpack.Loader");
        PackVersionLabel.Text = LocalizationService.Get("Content.Modpack.PackVersion");
        SizeLabel.Text = LocalizationService.Get("Content.Modpack.Size");
        NameLabel.Text = LocalizationService.Get("Content.Modpack.ServerName");
        LocationLabel.Text = LocalizationService.Get("Content.Modpack.Location");
        PortLabel.Text = LocalizationService.Get("Content.Modpack.Port");
        BrowseButton.Content = LocalizationService.Get("Content.Modpack.Browse");
        EulaBox.Content = LocalizationService.Get("Content.Modpack.Eula");
        CancelButton.Content = LocalizationService.Get("Action.Cancel");
        CreateButton.Content = LocalizationService.Get("Content.Modpack.Create");

        MinecraftValue.Text = plan.MinecraftVersion ?? LocalizationService.Get("Content.Unknown");
        LoaderValue.Text = plan.Loader is { Length: > 0 }
            ? $"{Capitalize(plan.Loader)} {plan.LoaderVersion}".Trim()
            : LocalizationService.Get("Content.Unknown");
        PackVersionValue.Text = plan.VersionNumber;
        SizeValue.Text = LocalizationService.Format(
            "Content.Modpack.SizeValue",
            plan.ServerFileCount,
            FormatBytes(plan.DownloadBytes));

        NameBox.Text = plan.ProjectName;
        PortBox.Text = "25565";
        LocationBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "1Salem Servers",
            SanitizeFolder(plan.ProjectName));

        if (plan.Blocked)
        {
            // Most often a loader whose own installer this app does not run. Saying which,
            // rather than failing later, is the point of showing the plan first.
            FormPanel.Visibility = Visibility.Collapsed;
            CreateButton.Visibility = Visibility.Collapsed;
            BlockedPanel.Visibility = Visibility.Visible;
            BlockedText.Text = DescribeBlocked(plan.BlockedReason);
            CancelButton.Content = LocalizationService.Get("Action.Close");
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowError("Content.Modpack.NeedName");
            return;
        }

        if (string.IsNullOrWhiteSpace(LocationBox.Text))
        {
            ShowError("Content.Modpack.NeedLocation");
            return;
        }

        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            ShowError("Content.Modpack.NeedPort");
            return;
        }

        if (EulaBox.IsChecked != true)
        {
            ShowError("Content.Modpack.NeedEula");
            return;
        }

        string destination;
        try
        {
            destination = Path.GetFullPath(LocationBox.Text.Trim());
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowError("Content.Modpack.NeedLocation");
            return;
        }

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            ShowError("Content.Modpack.FolderNotEmpty");
            return;
        }

        Result = new ModpackInstallRequest(
            ContentProviderId.Modrinth,
            string.Empty,
            string.Empty,
            NameBox.Text.Trim(),
            destination,
            port,
            true);
        DialogResult = true;
        Close();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = LocalizationService.Get("Content.Modpack.Location"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            LocationBox.Text = Path.Combine(dialog.SelectedPath, SanitizeFolder(NameBox.Text));
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string key)
    {
        ErrorText.Text = LocalizationService.Get(key);
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string DescribeBlocked(string? reason)
    {
        if (reason is null)
        {
            return LocalizationService.Get("Content.Error.Generic");
        }

        if (reason.StartsWith("LoaderNotSupported:", StringComparison.Ordinal))
        {
            return LocalizationService.Format(
                "Content.Modpack.LoaderUnsupported",
                Capitalize(reason["LoaderNotSupported:".Length..]));
        }

        return LocalizationService.Get(reason switch
        {
            "UnsafePath" or "UntrustedHost" or "NoHash" => "Content.Modpack.UnsafePack",
            "NoLoader" or "NoMinecraftVersion" or "MalformedIndex" or "UnsupportedFormat"
                or "UnsupportedGame" => "Content.Modpack.UnreadablePack",
            "PackTooLarge" or "TooManyFiles" or "FileTooLarge" => "Content.Modpack.TooLarge",
            _ => "Content.Error.Generic"
        });
    }

    private static string SanitizeFolder(string value)
    {
        var cleaned = new string(value
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' '
                ? character
                : '-')
            .ToArray())
            .Trim();
        return cleaned.Length == 0 ? "Modpack server" : cleaned;
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
            >= 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes} B"
        };

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
