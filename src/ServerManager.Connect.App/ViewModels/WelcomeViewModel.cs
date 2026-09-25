using ServerManager.Connect.App.Localization;

namespace ServerManager.Connect.App.ViewModels;

/// <summary>
/// The first page. It is also where the app says plainly that it cannot work: not configured,
/// identity unusable, or the broker unreachable at startup (with a retry).
/// </summary>
public sealed class WelcomeViewModel : ObservableObject
{
    private WelcomeViewModel(string headline, string detail, string? errorText, Action? getStarted, Func<Task>? retry)
    {
        Headline = headline;
        Detail = detail;
        ErrorText = errorText;
        GetStartedCommand = new RelayCommand(() => getStarted?.Invoke(), () => getStarted is not null);
        RetryCommand = new AsyncCommand(() => retry?.Invoke() ?? Task.CompletedTask, () => retry is not null);
        CanGetStarted = getStarted is not null;
        CanRetry = retry is not null;
    }

    public string Headline { get; }

    public string Detail { get; }

    public string? ErrorText { get; }

    public bool HasError => ErrorText is not null;

    public bool CanGetStarted { get; }

    public bool CanRetry { get; }

    public RelayCommand GetStartedCommand { get; }

    public AsyncCommand RetryCommand { get; }

    public static WelcomeViewModel Ready(INavigator navigator) =>
        new(Text.WelcomeTitle, Text.WelcomeBody, null, navigator.ShowInvite, null);

    /// <summary>No broker address: nothing can work, and the page says exactly that.</summary>
    public static WelcomeViewModel NotConfigured() =>
        new(Text.NotConfigured, Text.NotConfiguredDetail, null, null, null);

    /// <summary>A problem the app cannot fix by retrying, such as a device identity this user cannot open.</summary>
    public static WelcomeViewModel Blocked(string problem) =>
        new(Text.WelcomeTitle, Text.WelcomeBody, problem, null, null);

    public static WelcomeViewModel Failed(string errorText, Func<Task> retry) =>
        new(Text.WelcomeTitle, Text.WelcomeBody, errorText, null, retry);
}
