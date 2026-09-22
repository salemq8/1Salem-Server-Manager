using System.Windows;
using ServerManager.Client.Shell;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

/// <summary>
/// The one place a page says "loading", "nothing here yet" or "this did not work". Pages that
/// hand-roll these three states drift apart: one shows a spinner forever, another shows a blank
/// region, a third states something it cannot actually know. Routing them through a single
/// component keeps the wording, spacing and recovery action identical everywhere.
/// </summary>
public partial class PageStateView : UserControl
{
    private const string EmptyGlyph = "";
    private const string ErrorGlyph = "";

    private bool _offerElevation;

    public PageStateView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the person asks to try the failed operation again.</summary>
    public event EventHandler? RetryRequested;

    /// <summary>
    /// Shows whichever state the feed is in and reports whether the page's own content should
    /// be hidden. Callers stay a single line: <c>Content.Visibility = view.Apply(...)</c>.
    /// </summary>
    /// <returns>True when this view is showing something and the page content must step aside.</returns>
    public bool Apply(DashboardFeed feed, string emptyTitleKey, string emptyMessageKey)
    {
        ArgumentNullException.ThrowIfNull(feed);

        if (feed.ShowSkeleton)
        {
            SkeletonState.Visibility = Visibility.Visible;
            MessageState.Visibility = Visibility.Collapsed;
            Visibility = Visibility.Visible;
            return true;
        }

        SkeletonState.Visibility = Visibility.Collapsed;

        if (feed.ShowErrorState)
        {
            // The service answering "not allowed" is a different problem with a different fix.
            _offerElevation = feed.NeedsElevation;
            ShowMessage(
                ErrorGlyph,
                _offerElevation ? "WarningBrush" : "DangerBrush",
                LocalizationService.Get(
                    _offerElevation ? "Error.NeedsAdministrator" : "Error.ServiceUnavailable"),
                LocalizationService.Get(
                    _offerElevation ? "Error.NeedsAdministratorHint" : "Error.ServiceHint"),
                LocalizationService.Get(
                    _offerElevation ? "Error.RestartAsAdministrator" : "Error.Retry"));
            return true;
        }

        if (feed.ShowEmptyState)
        {
            ShowMessage(
                EmptyGlyph,
                "TextTertiaryBrush",
                LocalizationService.Get(emptyTitleKey),
                LocalizationService.Get(emptyMessageKey),
                actionLabel: null);
            return true;
        }

        MessageState.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Collapsed;
        return false;
    }

    private void ShowMessage(
        string glyph,
        string glyphBrushKey,
        string title,
        string message,
        string? actionLabel)
    {
        StateGlyph.Text = glyph;
        StateGlyph.Foreground = ResolveBrush(glyphBrushKey);
        StateTitle.Text = title;
        StateMessage.Text = message;

        if (actionLabel is null)
        {
            StateAction.Visibility = Visibility.Collapsed;
        }
        else
        {
            StateAction.Content = actionLabel;
            StateAction.IsEnabled = true;
            StateAction.Visibility = Visibility.Visible;
        }

        MessageState.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
    }

    private static Brush ResolveBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_offerElevation)
        {
            (Application.Current as App)?.RestartAsAdministrator();
            return;
        }

        // Not disabled while retrying: disabling the focused button threw keyboard focus
        // away. The feed already ignores a refresh that is still in flight.
        RetryRequested?.Invoke(this, EventArgs.Empty);
    }
}
