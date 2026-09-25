using System.Collections.ObjectModel;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Services;

namespace ServerManager.Connect.App.ViewModels;

/// <summary>
/// The friend's servers. Each refresh also moves approved servers through enrollment: a server
/// shows "Enrollment pending" until its blob has been fetched, opened and handed to the transport
/// and the node id reported to the broker. While anything is pending the page polls with backoff;
/// otherwise it looks once a minute for approvals and revocations.
/// </summary>
public sealed class ServersViewModel : ObservableObject, IPageLifetime
{
    private static readonly TimeSpan FirstPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(1);

    private readonly IBrokerClient _broker;
    private readonly DeviceRegistration _registration;
    private readonly EnrollmentCoordinator _enrollment;
    private readonly IAppClock _clock;
    private readonly DiagnosticsLog _log;
    private readonly INavigator _navigator;
    private readonly PollBackoff _backoff = new(FirstPoll, IdlePoll);
    private CancellationTokenSource? _polling;
    private string? _errorText;
    private bool _hasLoaded;

    public ServersViewModel(
        IBrokerClient broker,
        DeviceRegistration registration,
        EnrollmentCoordinator enrollment,
        IAppClock clock,
        DiagnosticsLog log,
        INavigator navigator)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _enrollment = enrollment ?? throw new ArgumentNullException(nameof(enrollment));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        RefreshCommand = new AsyncCommand(RefreshNowAsync);
        AddServerCommand = new RelayCommand(navigator.ShowInvite);
    }

    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];

    public bool IsEmpty => _hasLoaded && Servers.Count == 0;

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

    public AsyncCommand RefreshCommand { get; }

    public RelayCommand AddServerCommand { get; }

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

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _registration.EnsureAsync(cancellationToken);
        var memberships = await _broker.GetMembershipsAsync(cancellationToken);
        var items = new List<ServerItemViewModel>(memberships.Count);
        foreach (var membership in memberships)
        {
            items.Add(await BuildItemAsync(membership, cancellationToken));
        }

        Servers.Clear();
        foreach (var item in items)
        {
            Servers.Add(item);
        }

        _hasLoaded = true;
        ErrorText = null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool HasPendingWork =>
        Servers.Any(item => item.Membership.State == MembershipState.Pending || item.Membership.NeedsEnrollment);

    private async Task<ServerItemViewModel> BuildItemAsync(Membership membership, CancellationToken cancellationToken)
    {
        if (!membership.NeedsEnrollment)
        {
            return new ServerItemViewModel(membership, null, _navigator);
        }

        try
        {
            var result = await _enrollment.TryCompleteAsync(membership, cancellationToken);
            return result.Outcome switch
            {
                EnrollmentOutcome.Completed => new ServerItemViewModel(membership with { NodeId = result.NodeId }, null, _navigator),
                EnrollmentOutcome.Failed => new ServerItemViewModel(membership, Text.ErrorSetupFailed, _navigator),
                _ => new ServerItemViewModel(membership, null, _navigator)
            };
        }
        catch (Exception exception) when (UserMessages.IsExpected(exception))
        {
            // One server's enrollment trouble must not hide the others; it stays "Enrollment
            // pending" with the reason underneath, and the next refresh tries again.
            _log.Record("enrollment", exception);
            return new ServerItemViewModel(membership, UserMessages.For(exception), _navigator);
        }
    }

    private async Task RefreshNowAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None);
        }
        catch (Exception exception) when (UserMessages.IsExpected(exception))
        {
            _log.Record("servers", exception);
            ErrorText = UserMessages.For(exception);
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var wait = IdlePoll;
                try
                {
                    await RefreshAsync(cancellationToken);
                    if (HasPendingWork)
                    {
                        wait = _backoff.Next();
                    }
                    else
                    {
                        _backoff.Reset();
                    }
                }
                catch (BrokerException exception) when (exception.Failure == BrokerFailure.RateLimited)
                {
                    _log.Record("servers", exception);
                    ErrorText = UserMessages.For(exception);
                    wait = exception.RetryAfter is { } retryAfter && retryAfter > wait ? retryAfter : wait;
                }
                catch (Exception exception) when (UserMessages.IsExpected(exception))
                {
                    _log.Record("servers", exception);
                    ErrorText = UserMessages.For(exception);
                    wait = _backoff.Next();
                }

                await _clock.Delay(wait, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // A fire-and-forget loop must not fail silently.
            _log.Record("servers", exception);
            ErrorText = Text.ErrorUnexpected;
        }
    }
}
