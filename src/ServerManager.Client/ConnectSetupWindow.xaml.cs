using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Client.Transport;
using ServerManager.Contracts;

namespace ServerManager.Client;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Application = System.Windows.Application;

public partial class ConnectSetupWindow : Window
{
    private readonly ConnectOwnerClient _client = new(TimeSpan.FromMinutes(2));
    private readonly DispatcherTimer _timer;
    private ConnectStatusResponse? _status;
    private bool _busy;
    private bool _loading;
    private int _statusGeneration;

    public ConnectSetupWindow()
    {
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
            _statusGeneration++;
            _client.Dispose();
        };
    }

    public static void Open(Window? owner) =>
        new ConnectSetupWindow { Owner = owner }.ShowDialog();

    private void Localize()
    {
        Title = LocalizationService.Get("Connect.Setup.Title");
        HeadingText.Text = LocalizationService.Get("Connect.Setup.Title");
        IntroText.Text = LocalizationService.Get("Connect.Setup.Intro");
        PrivacyText.Text = LocalizationService.Get("Connect.Setup.Privacy");
        ClientIdLabel.Text = LocalizationService.Get("Connect.Setup.ClientId");
        ClientSecretLabel.Text = LocalizationService.Get("Connect.Setup.ClientSecret");
        SaveButton.Content = LocalizationService.Get("Connect.Setup.Save");
        CheckButton.Content = LocalizationService.Get("Connect.Setup.CheckAgain");
        TurnOffButton.Content = LocalizationService.Get("Connect.Setup.TurnOff");
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
        var generation = _statusGeneration;
        try
        {
            var status = await _client.GetStatusAsync();
            if (generation == _statusGeneration && IsVisible)
            {
                _status = status;
                Render();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation == _statusGeneration && IsVisible)
            {
                ActionStatus.Text = LocalizationService.Get("Connect.State.ErrorDetail");
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void Render()
    {
        if (StateText is null)
        {
            return;
        }

        var view = ConnectPresentation.Setup(_status);
        StateText.Text = view.State;
        StateDetail.Text = view.Detail;
        StateDot.Fill = ToneBrush(view.Tone);
        CheckButton.IsEnabled = !_busy && view.CanCheckAgain;
        TurnOffButton.IsEnabled = !_busy && view.CanTurnOff;
        SaveButton.IsEnabled = !_busy;
        // ClientIdHint is masked display data, not a credential that can be submitted.
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var clientId = ClientIdBox.Text.Trim();
        var secret = ClientSecretBox.Password;
        if (clientId.Length == 0 || secret.Length == 0)
        {
            ActionStatus.Text = LocalizationService.Get("Connect.Setup.NeedCredential");
            return;
        }

        try
        {
            await RunAsync(async () => _status = await _client.SaveCredentialAsync(clientId, secret));
        }
        finally
        {
            // The PasswordBox owns the only UI copy and is cleared after exactly one PUT attempt.
            ClientSecretBox.Clear();
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () => _status = await _client.CheckAsync());

    private async void TurnOff_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmationDialog.Confirm(
                this,
                LocalizationService.Get("Connect.Setup.TurnOffTitle"),
                LocalizationService.Get("Connect.Setup.TurnOffTitle"),
                LocalizationService.Get("Connect.Setup.TurnOffBody"),
                null,
                LocalizationService.Get("Connect.Setup.TurnOff")))
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _client.TurnOffAsync();
            _status = await _client.GetStatusAsync();
            ClientIdBox.Clear();
            ClientSecretBox.Clear();
        });
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _statusGeneration++;
        ActionStatus.Text = LocalizationService.Get("Connect.State.Checking");
        Render();
        try
        {
            await operation();
            ActionStatus.Text = string.Empty;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ActionStatus.Text = LocalizationService.Get("Connect.State.ErrorDetail");
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    private static Brush ToneBrush(UiStatusTone tone)
    {
        var key = tone switch
        {
            UiStatusTone.Positive => "SuccessBrush",
            UiStatusTone.Caution => "WarningBrush",
            UiStatusTone.Negative => "DangerBrush",
            _ => "TextSecondaryBrush"
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
