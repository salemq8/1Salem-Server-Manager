using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerManager.Client.Shell;
using ServerManager.Client.Transport;
using ServerManager.Contracts;

namespace ServerManager.Client;

public partial class ConnectInviteWindow : Window
{
    private readonly Guid _serverId;
    private readonly ConnectOwnerClient _client = new();
    private ConnectInviteCreated? _invite;

    public ConnectInviteWindow(Guid serverId)
    {
        _serverId = serverId;
        InitializeComponent();
        FlowDirection = LayoutDirectionService.ForCulture(CultureInfo.CurrentUICulture);
        Localize();
        Closed += (_, _) => _client.Dispose();
    }

    private void Localize()
    {
        Title = LocalizationService.Get("Connect.Invite.Title");
        HeadingText.Text = LocalizationService.Get("Connect.Invite.Title");
        IntroText.Text = LocalizationService.Get("Connect.Invite.Intro");
        ValidityLabel.Text = LocalizationService.Get("Connect.Invite.Validity");
        ((ComboBoxItem)ValidityBox.Items[0]).Content = LocalizationService.Get("Connect.Invite.24Hours");
        ((ComboBoxItem)ValidityBox.Items[1]).Content = LocalizationService.Get("Connect.Invite.3Days");
        ((ComboBoxItem)ValidityBox.Items[2]).Content = LocalizationService.Get("Connect.Invite.7Days");
        CreateButton.Content = LocalizationService.Get("Connect.Invite.Create");
        ShownOnceText.Text = LocalizationService.Get("Connect.Invite.ShownOnce");
        LinkLabel.Text = LocalizationService.Get("Connect.Invite.Link");
        CodeLabel.Text = LocalizationService.Get("Connect.Invite.Code");
        CopyLinkButton.Content = LocalizationService.Get("Connect.Invite.CopyLink");
        CopyCodeButton.Content = LocalizationService.Get("Connect.Invite.CopyCode");
        RevokeButton.Content = LocalizationService.Get("Connect.Invite.Revoke");
        CloseButton.Content = LocalizationService.Get("Action.Close");
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var item = (ComboBoxItem)ValidityBox.SelectedItem;
        var ttl = int.Parse((string)item.Tag, CultureInfo.InvariantCulture);
        await RunAsync(async () =>
        {
            _invite = await _client.CreateInviteAsync(_serverId, ttl);
            LinkBox.Text = _invite.Link;
            CodeBox.Text = _invite.Code;
            ExpiryText.Text = LocalizationService.Format(
                "Connect.Invite.Expires",
                _invite.ExpiresAtUtc.ToLocalTime());
            CreateCard.Visibility = Visibility.Collapsed;
            ResultCard.Visibility = Visibility.Visible;
        });
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e) => CopySecret(LinkBox.Text);

    private void CopyCode_Click(object sender, RoutedEventArgs e) => CopySecret(CodeBox.Text);

    private void CopySecret(string value)
    {
        if (!SafeClipboard.TrySetSensitiveText(value))
        {
            StatusText.Text = LocalizationService.Get("Network.ClipboardBusy");
        }
    }

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (_invite is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _client.RevokeInviteAsync(_invite.InviteId);
            LinkBox.Clear();
            CodeBox.Clear();
            _invite = null;
            ResultCard.Visibility = Visibility.Collapsed;
            CreateCard.Visibility = Visibility.Visible;
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        CreateButton.IsEnabled = false;
        RevokeButton.IsEnabled = false;
        StatusText.Text = LocalizationService.Get("Connect.State.Checking");
        try
        {
            await action();
            StatusText.Text = string.Empty;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText.Text = DiagnosticsService.Redact(exception.Message);
        }
        finally
        {
            CreateButton.IsEnabled = true;
            RevokeButton.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
