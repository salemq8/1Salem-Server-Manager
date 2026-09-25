using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Invites;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Services;

namespace ServerManager.Connect.App.ViewModels;

/// <summary>
/// Paste a link or code, redeem it. Every way an invite can fail to work (malformed, unknown,
/// used, expired, revoked, for another server) shows the same sentence: the broker makes them
/// indistinguishable on purpose (§14), and so does the app.
/// </summary>
public sealed class InviteViewModel : ObservableObject
{
    private readonly IBrokerClient _broker;
    private readonly DeviceRegistration _registration;
    private readonly DiagnosticsLog _log;
    private readonly INavigator _navigator;
    private string _inviteText = string.Empty;
    private string? _errorText;
    private bool _isBusy;

    public InviteViewModel(IBrokerClient broker, DeviceRegistration registration, DiagnosticsLog log, INavigator navigator, Action goBack)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        JoinCommand = new AsyncCommand(JoinAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(InviteText));
        BackCommand = new RelayCommand(goBack);
    }

    public string InviteText
    {
        get => _inviteText;
        set
        {
            if (Set(ref _inviteText, value ?? string.Empty))
            {
                JoinCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorText is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                JoinCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    public AsyncCommand JoinCommand { get; }

    public RelayCommand BackCommand { get; }

    public async Task JoinAsync()
    {
        ErrorText = null;
        if (!InviteParser.TryParse(InviteText, out var secret))
        {
            ErrorText = Text.InviteNotValid;
            return;
        }

        IsBusy = true;
        try
        {
            await _registration.EnsureAsync(CancellationToken.None);
            var redemption = await _broker.RedeemInviteAsync(secret, CancellationToken.None);

            // The secret has done its job; do not keep it on screen or in the view model.
            InviteText = string.Empty;
            _navigator.ShowWaiting(redemption);
        }
        catch (BrokerException exception) when (exception.Failure is BrokerFailure.NotFound or BrokerFailure.Rejected)
        {
            _log.Record("invite", exception);
            ErrorText = Text.InviteNotValid;
        }
        catch (Exception exception) when (UserMessages.IsExpected(exception))
        {
            _log.Record("invite", exception);
            ErrorText = UserMessages.For(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
