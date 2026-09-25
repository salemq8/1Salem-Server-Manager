using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Services;

namespace ServerManager.Connect.App.ViewModels;

/// <summary>
/// Waits for the owner's decision on one membership, polling with backoff (5 s doubling to a
/// minute). Approval leads to the server list, where enrollment continues.
/// </summary>
public sealed class WaitingViewModel : ObservableObject, IPageLifetime
{
    private static readonly TimeSpan FirstPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowestPoll = TimeSpan.FromMinutes(1);

    private readonly string _membershipId;
    private readonly IBrokerClient _broker;
    private readonly IAppClock _clock;
    private readonly DiagnosticsLog _log;
    private readonly INavigator _navigator;
    private readonly PollBackoff _backoff = new(FirstPoll, SlowestPoll);
    private CancellationTokenSource? _polling;
    private string _statusText = Text.WaitingStatus;
    private bool _isWaiting = true;

    public WaitingViewModel(InviteRedemption redemption, IBrokerClient broker, IAppClock clock, DiagnosticsLog log, INavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(redemption);
        _membershipId = redemption.MembershipId;
        ServerLabel = redemption.ServerLabel;
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        BackCommand = new RelayCommand(navigator.ShowServers);
    }

    public string ServerLabel { get; }

    public string Body => Text.Format(Text.WaitingBody, ServerLabel);

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsWaiting
    {
        get => _isWaiting;
        private set => Set(ref _isWaiting, value);
    }

    public RelayCommand BackCommand { get; }

    public void OnShown()
    {
        OnHidden();
        _polling = new CancellationTokenSource();
        _ = PollLoopAsync(_polling.Token);
    }

    public void OnHidden()
    {
        _polling?.Cancel();
        _polling = null;
    }

    /// <summary>One look at the membership. True when the wait is over, one way or the other.</summary>
    internal async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        var memberships = await _broker.GetMembershipsAsync(cancellationToken);
        switch (memberships.FirstOrDefault(membership => membership.MembershipId == _membershipId)?.State)
        {
            case MembershipState.Approved:
                IsWaiting = false;
                _navigator.ShowServers();
                return true;
            case MembershipState.Rejected:
                Finish(Text.WaitingDeclined);
                return true;
            case MembershipState.Revoked:
                Finish(Text.StateAccessRevoked);
                return true;
            default:
                StatusText = Text.WaitingStatus;
                return false;
        }
    }

    private void Finish(string statusText)
    {
        StatusText = statusText;
        IsWaiting = false;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var wait = _backoff.Next();
                await _clock.Delay(wait, cancellationToken);
                try
                {
                    if (await PollOnceAsync(cancellationToken))
                    {
                        return;
                    }
                }
                catch (BrokerException exception) when (exception.Failure == BrokerFailure.RateLimited)
                {
                    _log.Record("waiting", exception);
                    if (exception.RetryAfter is { } retryAfter && retryAfter > wait)
                    {
                        await _clock.Delay(retryAfter - wait, cancellationToken);
                    }
                }
                catch (Exception exception) when (UserMessages.IsExpected(exception))
                {
                    // Keep waiting: the owner's decision does not depend on this PC being online.
                    _log.Record("waiting", exception);
                    StatusText = UserMessages.For(exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // A fire-and-forget loop must not fail silently.
            _log.Record("waiting", exception);
            Finish(Text.ErrorUnexpected);
        }
    }
}
