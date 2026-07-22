using System.IO;
using System.Windows;
using Microsoft.Win32;
using ServerManager.Client.Shell;

namespace ServerManager.Client;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
    {
        InitializeComponent();
        VersionText.Text = ProductInfo.DiagnosticsVersionLabel;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Diagnostic Report",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"1Salem-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            StatusText.Text = "Collecting and redacting diagnostics...";
            var path = await DiagnosticsService.ExportAsync(dialog.FileName);
            StatusText.Text = $"Diagnostic report saved: {path}";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text =
                $"Diagnostic export failed: {exception.Message}. Choose another folder and retry.";
        }
    }
}
