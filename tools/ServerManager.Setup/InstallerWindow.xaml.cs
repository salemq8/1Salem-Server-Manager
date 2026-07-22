using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Media = System.Windows.Media;
using Forms = System.Windows.Forms;

namespace ServerManager.Setup;

public partial class InstallerWindow : Window
{
    private bool _isInstalling;
    private bool _completed;
    private bool _agentServiceBeforeClientOnly = true;
    private string _lastDetails = string.Empty;
    private string? _lastLogPath;

    public InstallerWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {InstallerEngine.ProductVersion} • Windows 10/11 x64 setup";
        InstallPathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "1Salem Server Manager");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the application folder",
            InitialDirectory = InstallPathBox.Text,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            InstallPathBox.Text = dialog.SelectedPath;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_completed)
        {
            Close();
            return;
        }

        if (_isInstalling)
        {
            return;
        }

        _isInstalling = true;
        OptionsPanel.IsEnabled = false;
        InstallButton.IsEnabled = false;
        DetailsButtons.Visibility = Visibility.Collapsed;
        Progress.Foreground = (Media.Brush)FindResource("AccentBrush");
        Progress.Value = 0;
        StateText.Text = "Installing";
        StateText.Foreground = (Media.Brush)FindResource("AccentBrush");
        StatusText.Text = "Starting pre-flight checks...";
        LogPathText.Text = string.Empty;

        try
        {
            var request = CaptureRequest();
            var progress = new Progress<InstallProgress>(value =>
            {
                Progress.Value = value.Percent;
                StatusText.Text = value.Message;
            });
            var result = await InstallerEngine.InstallAsync(request, progress);
            _lastLogPath = result.LogPath;
            _lastDetails =
                $"Installation completed successfully.{Environment.NewLine}" +
                $"Agent health verified: {result.ServiceHealthVerified}" +
                $"{Environment.NewLine}Warnings: {result.Warnings.Count}" +
                $"{Environment.NewLine}Log: {result.LogPath}";
            Progress.Value = 100;
            StateText.Text = result.Warnings.Count == 0
                ? "Installation succeeded"
                : "Installation succeeded with warnings";
            StateText.Foreground = result.Warnings.Count == 0
                ? Media.Brushes.MediumSeaGreen
                : Media.Brushes.Goldenrod;
            StatusText.Text = result.Warnings.Count == 0
                ? "Installation reached 100%. The Agent and dashboard are ready."
                : string.Join(Environment.NewLine, result.Warnings);
            LogPathText.Text = $"Install log: {result.LogPath}";
            OpenLogButton.Visibility = Visibility.Visible;
            CopyDetailsButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Collapsed;
            DetailsButtons.Visibility = Visibility.Visible;
            InstallButton.Content = "Finish";
            InstallButton.IsEnabled = true;
            _completed = true;
        }
        catch (Exception exception)
        {
            _lastLogPath = InstallerEngine.LastLogPath;
            _lastDetails = exception is InstallerFailureException failure
                ? $"{failure.Message}{Environment.NewLine}{Environment.NewLine}" +
                  $"{failure.Details}{Environment.NewLine}{Environment.NewLine}" +
                  $"Install log: {_lastLogPath}"
                : $"{exception}{Environment.NewLine}{Environment.NewLine}" +
                  $"Install log: {_lastLogPath}";
            Progress.Value = 100;
            Progress.Foreground = Media.Brushes.IndianRed;
            StateText.Text = "Installation failed";
            StateText.Foreground = Media.Brushes.IndianRed;
            StatusText.Text = exception is InstallerFailureException installerFailure
                ? installerFailure.Message
                : $"Setup could not complete: {exception.Message}";
            LogPathText.Text = string.IsNullOrWhiteSpace(_lastLogPath)
                ? string.Empty
                : $"Complete diagnostics: {_lastLogPath}";
            OpenLogButton.Visibility = string.IsNullOrWhiteSpace(_lastLogPath)
                ? Visibility.Collapsed
                : Visibility.Visible;
            CopyDetailsButton.Visibility = Visibility.Visible;
            CloseButton.Visibility = Visibility.Visible;
            DetailsButtons.Visibility = Visibility.Visible;
            OptionsPanel.IsEnabled = true;
            InstallButton.Content = "Retry";
            InstallButton.IsEnabled = true;
        }
        finally
        {
            _isInstalling = false;
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastLogPath) ||
            !File.Exists(_lastLogPath))
        {
            System.Windows.MessageBox.Show(
                "The install log is not available.",
                "1Salem Server Manager Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add($"/select,{_lastLogPath}");
        Process.Start(startInfo);
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastDetails))
        {
            System.Windows.Clipboard.SetText(_lastDetails);
            StatusText.Text = "Error details copied to the clipboard.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ModeBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (StartAgentBox is null ||
            ModeBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        var mode = selected.Tag?.ToString();
        if (mode == "ClientOnly")
        {
            _agentServiceBeforeClientOnly = StartAgentBox.IsChecked == true;
            StartAgentBox.IsChecked = false;
            StartAgentBox.IsEnabled = false;
        }
        else
        {
            StartAgentBox.IsEnabled = true;
            if (_agentServiceBeforeClientOnly)
            {
                StartAgentBox.IsChecked = true;
            }
        }

        var includesClient = mode != "AgentOnly";
        DesktopShortcutBox.IsEnabled = includesClient;
        StartMenuShortcutBox.IsEnabled = includesClient;
        StartupBox.IsEnabled = includesClient;
        LaunchBox.IsEnabled = includesClient;
    }

    private InstallRequest CaptureRequest()
    {
        var mode = ((ComboBoxItem)ModeBox.SelectedItem)
            .Tag?.ToString() ?? "AllInOne";
        return new InstallRequest(
            mode,
            InstallPathBox.Text,
            DesktopShortcutBox.IsChecked == true,
            StartMenuShortcutBox.IsChecked == true,
            StartAgentBox.IsChecked == true,
            FirewallBox.IsChecked == true,
            StartupBox.IsChecked == true,
            LaunchBox.IsChecked == true);
    }
}
