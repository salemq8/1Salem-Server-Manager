using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Localization;

namespace ServerManager.Connect.App.ViewModels;

/// <summary>One server card. Rebuilt on every refresh, so it holds a snapshot and never changes.</summary>
public sealed class ServerItemViewModel : ObservableObject
{
    public ServerItemViewModel(Membership membership, string? problem, INavigator navigator, bool setupFailed = false)
    {
        Membership = membership ?? throw new ArgumentNullException(nameof(membership));
        ArgumentNullException.ThrowIfNull(navigator);
        Problem = problem;
        // A failed setup must not also read "pending" right above the failure it explains.
        StatusText = setupFailed ? Text.StatusSetupFailed : Describe(membership, navigator.FindConnection(membership.MembershipId));
        OpenCommand = new RelayCommand(() => navigator.ShowConnection(Membership), () => CanOpen);
    }

    public Membership Membership { get; }

    public string Label => Membership.ServerLabel;

    public string StatusText { get; }

    /// <summary>Why the server is stuck, when it is (for example enrollment failed).</summary>
    public string? Problem { get; }

    public bool HasProblem => Problem is not null;

    public bool CanOpen => Membership.CanConnect;

    public string OpenAutomationName => Text.Format(Text.ServersOpenNamed, Label);

    public RelayCommand OpenCommand { get; }

    private static string Describe(Membership membership, ConnectionViewModel? connection) => membership.State switch
    {
        MembershipState.Pending => Text.StatusWaitingForApproval,
        MembershipState.Approved when membership.NodeState == MembershipNodeState.Candidate => Text.StatusConfirmationPending,
        MembershipState.Approved when membership.NodeState == MembershipNodeState.Rejected => Text.StatusConfirmationFailed,
        MembershipState.Approved when membership.NeedsEnrollment => Text.StatusEnrollmentPending,
        MembershipState.Approved when connection is { State: not ConnectionState.Disconnected } => connection.StateText,
        MembershipState.Approved when membership.CanConnect => Text.StatusReady,
        MembershipState.Approved => Text.StatusUnavailable,
        MembershipState.Rejected => Text.StatusDeclined,
        MembershipState.Revoked => Text.StateAccessRevoked,
        _ => Text.StatusUnavailable
    };
}
