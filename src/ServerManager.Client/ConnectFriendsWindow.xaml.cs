using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Client.Transport;
using ServerManager.Contracts;

namespace ServerManager.Client;

using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using WrapPanel = System.Windows.Controls.WrapPanel;

public partial class ConnectFriendsWindow : Window
{
    private readonly Guid _serverId;
    private readonly ConnectOwnerClient _client = new();
    private readonly DispatcherTimer _timer;
    private ServerConnectResponse? _status;
    private bool _busy;
    private bool _loading;
    private bool _hasActionStatus;
    private long _refreshVersion;
    private readonly ConnectNicknameDrafts _nicknameDrafts = new();

    public ConnectFriendsWindow(Guid serverId)
    {
        _serverId = serverId;
        InitializeComponent();
        FlowDirection = LayoutDirectionService.ForCulture(CultureInfo.CurrentUICulture);
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _timer.Tick += async (_, _) => await LoadAsync();
        Loaded += async (_, _) =>
        {
            Localize();
            _timer.Start();
            await LoadAsync();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _client.Dispose();
        };
    }

    private void Localize()
    {
        Title = LocalizationService.Get("Connect.Friends.Title");
        HeadingText.Text = LocalizationService.Get("Connect.Friends.Title");
        CloseButton.Content = LocalizationService.Get("Action.Close");
        Render();
    }

    private async Task LoadAsync()
    {
        if (_busy || !IsVisible || _loading)
        {
            return;
        }

        _loading = true;
        var version = _refreshVersion;
        try
        {
            var status = await _client.GetServerAsync(_serverId);
            if (version != _refreshVersion || !IsVisible)
            {
                return;
            }

            _status = status;
            if (!_hasActionStatus)
            {
                StatusText.Text = string.Empty;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (version == _refreshVersion && !_hasActionStatus)
            {
                StatusText.Text = LocalizationService.Get("Connect.State.ErrorDetail");
            }
        }
        finally
        {
            _loading = false;
        }

        Render();
    }

    private void Render()
    {
        if (FriendsPanel is null)
        {
            return;
        }

        // Keep the active editor (and its caret/focus) intact while background status changes.
        if (Keyboard.FocusedElement is TextBox editor && FriendsPanel.IsAncestorOf(editor))
        {
            return;
        }

        if (_status is not null)
        {
            _nicknameDrafts.Retain(_status.Friends.Select(friend => friend.MembershipId));
        }
        FriendsPanel.Children.Clear();
        if (_status is null)
        {
            AddMessage(LocalizationService.Get("Connect.State.Checking"));
            return;
        }

        if (_status.Friends.Count == 0)
        {
            AddMessage(LocalizationService.Get("Connect.Friends.Empty"));
            return;
        }

        foreach (var friend in _status.Friends.OrderBy(item => item.State).ThenBy(item => item.CreatedAtUtc))
        {
            FriendsPanel.Children.Add(BuildFriendCard(friend));
        }
    }

    private Border BuildFriendCard(ConnectFriendItem friend)
    {
        var view = ConnectPresentation.Friend(friend);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = view.State,
            Style = (Style)FindResource("SectionTitleStyle")
        });
        panel.Children.Add(new TextBlock
        {
            Text = view.Setup,
            Style = (Style)FindResource("SecondaryTextStyle"),
            Margin = new Thickness(0, 4, 0, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = view.DeviceId,
            FlowDirection = System.Windows.FlowDirection.LeftToRight,
            Style = (Style)FindResource("TechnicalValueStyle"),
            Margin = new Thickness(0, 8, 0, 0)
        });

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (view.IsPending)
        {
            actions.Children.Add(ActionButton(
                LocalizationService.Get("Connect.Friends.Approve"),
                async () => await _client.ApproveAsync(friend.MembershipId),
                primary: true));
            actions.Children.Add(ActionButton(
                LocalizationService.Get("Connect.Friends.Reject"),
                async () => await _client.RejectAsync(friend.MembershipId)));
        }
        else
        {
            var nickname = new TextBox
            {
                Text = _nicknameDrafts.Get(friend.MembershipId, view.Nickname),
                Width = 210,
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = LocalizationService.Get("Connect.Friends.Nickname")
            };
            nickname.TextChanged += (_, _) => _nicknameDrafts.Set(friend.MembershipId, nickname.Text);
            actions.Children.Add(nickname);
            actions.Children.Add(ActionButton(
                LocalizationService.Get("Connect.Friends.SaveNickname"),
                async () =>
                {
                    var submitted = nickname.Text;
                    await _client.SetNicknameAsync(friend.MembershipId, submitted.Trim());
                    _nicknameDrafts.Saved(friend.MembershipId, submitted);
                }));

            var revoke = new Button
            {
                Content = LocalizationService.Get("Connect.Friends.Revoke"),
                Style = (Style)FindResource("QuietButtonStyle"),
                Margin = new Thickness(0, 0, 8, 8)
            };
            revoke.Click += async (_, _) => await RevokeAsync(friend.MembershipId);
            actions.Children.Add(revoke);
        }

        panel.Children.Add(actions);
        return new Border
        {
            Style = (Style)FindResource("SectionCardStyle"),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    private Button ActionButton(string label, Func<Task> action, bool primary = false)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource(primary ? "PrimaryActionStyle" : "QuietButtonStyle"),
            Margin = new Thickness(0, 0, 8, 8)
        };
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    private async Task RevokeAsync(string membershipId)
    {
        if (!ConfirmationDialog.Confirm(
                this,
                LocalizationService.Get("Connect.Friends.RevokeTitle"),
                LocalizationService.Get("Connect.Friends.RevokeTitle"),
                LocalizationService.Get("Connect.Friends.RevokeBody"),
                null,
                LocalizationService.Get("Connect.Friends.Revoke")))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _client.RevokeAsync(membershipId);
            StatusText.Text = LocalizationService.Get(result.Completed
                ? "Connect.Revoke.Complete"
                : "Connect.Revoke.Incomplete") + Environment.NewLine + ConnectPresentation.RevokeOutcome(result);
        }, clearStatus: false);
    }

    private async Task RunAsync(Func<Task> action, bool clearStatus = true)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _refreshVersion++;
        _hasActionStatus = true;
        StatusText.Text = LocalizationService.Get("Connect.State.Checking");
        try
        {
            await action();
            _status = await _client.GetServerAsync(_serverId);
            if (clearStatus)
            {
                _hasActionStatus = false;
                StatusText.Text = string.Empty;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText.Text = LocalizationService.Get("Connect.State.ErrorDetail");
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    private void AddMessage(string message) => FriendsPanel.Children.Add(new TextBlock
    {
        Text = message,
        Style = (Style)FindResource("SecondaryTextStyle"),
        TextWrapping = TextWrapping.Wrap
    });

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
