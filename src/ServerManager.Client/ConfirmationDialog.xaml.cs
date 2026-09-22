using System.Globalization;
using System.Windows;
using ServerManager.Client.Shell;

namespace ServerManager.Client;

/// <summary>A confirmation whose destructive button says exactly what it will do.</summary>
public partial class ConfirmationDialog : Window
{
    private ConfirmationDialog()
    {
        InitializeComponent();
        FlowDirection = LayoutDirectionService.ForCulture(CultureInfo.CurrentUICulture);
    }

    /// <summary>
    /// Returns true only when the person chose the destructive button. Closing the window,
    /// pressing Escape or Enter, or choosing Cancel all return false.
    /// </summary>
    public static bool Confirm(
        Window? owner,
        string title,
        string heading,
        string body,
        string? detail,
        string confirmLabel)
    {
        var dialog = new ConfirmationDialog
        {
            Owner = owner,
            Title = title
        };
        dialog.HeadingText.Text = heading;
        dialog.BodyText.Text = body;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            dialog.DetailText.Text = detail;
            dialog.DetailText.Visibility = Visibility.Visible;
        }

        dialog.CancelButton.Content = LocalizationService.Get("Action.Cancel");
        dialog.ConfirmButton.Content = confirmLabel;
        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
