using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Controls;

/// <summary>
/// One poll of the Agent dashboard, shared by every page that needs it. Pages bind to this
/// instead of each opening their own HttpClient and timer, so the refresh cost does not grow
/// with the number of views, and cards are updated in place rather than rebuilt each tick.
/// </summary>
public sealed class DashboardFeed : INotifyPropertyChanged, IDisposable
{
    private static readonly Lazy<DashboardFeed> SharedInstance = new(() => new DashboardFeed());

    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromSeconds(20));
    private readonly DispatcherTimer _timer;
    /// <summary>Consecutive failed polls tolerated before the UI stops showing the old data.</summary>
    private const int FailuresBeforeErrorState = 2;

    private bool _isConnected;
    private bool _isAccessDenied;
    private bool _hasLoadedOnce;
    private bool _hasAttempted;
    private int _consecutiveFailures;
    private string? _lastErrorMessage;
    private int _refreshing;
    private bool _disposed;

    private DashboardFeed()
    {
        Servers = [];
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public static DashboardFeed Shared => SharedInstance.Value;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ServerCardViewModel> Servers { get; }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetField(ref _isConnected, value);
    }

    /// <summary>False until the first response arrives, so pages can show skeletons.</summary>
    public bool HasLoadedOnce
    {
        get => _hasLoadedOnce;
        private set => SetField(ref _hasLoadedOnce, value);
    }

    /// <summary>
    /// True once a poll has finished, successfully or not. Skeletons key off this rather than
    /// <see cref="HasLoadedOnce"/> so a service that never answers resolves into an error the
    /// person can act on instead of placeholders that pulse forever.
    /// </summary>
    public bool HasAttempted
    {
        get => _hasAttempted;
        private set => SetField(ref _hasAttempted, value);
    }

    public string? LastErrorMessage
    {
        get => _lastErrorMessage;
        private set => SetField(ref _lastErrorMessage, value);
    }

    /// <summary>The most recent failure was the Agent refusing the credential (401/403).</summary>
    public bool IsAccessDenied
    {
        get => _isAccessDenied;
        private set => SetField(ref _isAccessDenied, value);
    }

    /// <summary>
    /// The page cannot show servers because this session is not elevated. The Agent's local
    /// credential is readable only by Administrators, and the main shortcuts start the app
    /// without elevation, so "can't reach the background service" would name the wrong cause:
    /// the service answered. An already-elevated session never gets this message.
    /// </summary>
    public bool NeedsElevation => ShowErrorState && IsAccessDenied && !IsElevated.Value;

    private static readonly Lazy<bool> IsElevated = new(ElevationService.IsAdministrator);

    public DashboardSnapshot? Snapshot { get; private set; }

    public void Start()
    {
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var snapshot = await _httpClient.GetFromJsonAsync<DashboardSnapshot>(
                "/api/v1/dashboard");
            if (snapshot is null)
            {
                return;
            }

            Snapshot = snapshot;
            LastSuccessUtc = snapshot.CapturedAtUtc;
            _consecutiveFailures = 0;
            IsAccessDenied = false;
            IsConnected = true;
            LastErrorMessage = null;
            Merge(snapshot);
            HasLoadedOnce = true;
            RaiseDerived();
        }
        // This runs from an async void timer tick, so anything that escapes here takes the
        // whole app down. GetFromJsonAsync alone can surface HttpRequestException,
        // TaskCanceledException, JsonException, NotSupportedException and
        // InvalidOperationException; a poll failure of any kind means exactly one thing to the
        // UI — the data is not trustworthy — so all of them are treated the same.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_consecutiveFailures < int.MaxValue)
            {
                _consecutiveFailures++;
            }

            // The service answered but refused: that is a rights problem, not a lost service.
            IsAccessDenied = exception is HttpRequestException
            {
                StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
            };
            IsConnected = false;
            LastErrorMessage = LocalizationService.Get("Error.ServiceUnavailable");
            RaiseDerived();
        }
        finally
        {
            HasAttempted = true;
            Interlocked.Exchange(ref _refreshing, 0);
            RaiseDerived();
        }
    }

    /// <summary>
    /// Updates existing card view-models in place and only adds/removes when the set of
    /// servers actually changes, so binding targets and scroll position survive a refresh.
    /// </summary>
    private void Merge(DashboardSnapshot snapshot)
    {
        var incoming = snapshot.Servers;
        for (var index = Servers.Count - 1; index >= 0; index--)
        {
            if (!incoming.Any(card => card.ServerId == Servers[index].ServerId))
            {
                Servers.RemoveAt(index);
            }
        }

        foreach (var card in incoming)
        {
            var existing = Servers.FirstOrDefault(item => item.ServerId == card.ServerId);
            if (existing is null)
            {
                Servers.Add(new ServerCardViewModel(card, snapshot.CapturedAtUtc));
            }
            else
            {
                existing.Update(card, snapshot.CapturedAtUtc);
            }
        }
    }

    private void RaiseDerived()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Snapshot)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasServers)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowEmptyState)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSkeleton)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowErrorState)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCurrentData)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsElevation)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastSuccessUtc)));
    }

    public bool HasServers => Servers.Count > 0;

    /// <summary>
    /// Keyed off the error threshold rather than <see cref="IsConnected"/>, so the one-poll
    /// grace keeps the last trustworthy "no servers yet" instead of flickering it away.
    /// </summary>
    public bool ShowEmptyState => HasLoadedOnce && !ShowErrorState && Servers.Count == 0;

    /// <summary>
    /// Data on screen can be trusted: it has loaded at least once and the connection has not
    /// been declared lost. A single dropped poll does not change this — that is the grace.
    /// </summary>
    public bool HasCurrentData => HasLoadedOnce && !ShowErrorState;

    /// <summary>
    /// Nothing trustworthy to show and no way to get it: the person needs a next step, not a
    /// spinner. This deliberately does NOT require an empty server list — cards left over from
    /// the last successful poll are not evidence that the service is reachable, and rendering
    /// them as current fact once it is gone would state a status the app cannot know.
    /// One dropped poll is tolerated first, because blanking every page for a single missed
    /// request would be jarring and a reading a couple of seconds old is still true.
    /// </summary>
    public bool ShowErrorState =>
        HasAttempted && !IsConnected && _consecutiveFailures >= FailuresBeforeErrorState;

    /// <summary>
    /// Still settling: nothing has loaded yet and the failure threshold has not been reached.
    /// Before, a single failed first poll dropped the skeleton without raising the error
    /// state, so pages rendered with no data and stated things they could not know, such as
    /// "no internet address is set up".
    /// </summary>
    public bool ShowSkeleton => !HasLoadedOnce && !ShowErrorState;

    /// <summary>
    /// When the newest snapshot was captured, or null before the first success. Views use this
    /// to say how old the numbers are instead of implying they are live.
    /// </summary>
    public DateTimeOffset? LastSuccessUtc { get; private set; }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _httpClient.Dispose();
    }
}
