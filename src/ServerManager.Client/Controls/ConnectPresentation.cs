using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Client.Shell;

namespace ServerManager.Client.Controls;

public enum ConnectServerAction
{
    None = 0,
    Enable = 1,
    Disable = 2
}

public sealed record ConnectSetupViewModel(
    string State,
    string Detail,
    UiStatusTone Tone,
    bool CanCheckAgain,
    bool CanTurnOff);

public sealed record ConnectServerViewModel(
    string State,
    string Detail,
    UiStatusTone Tone,
    ConnectServerAction PrimaryAction,
    string PrimaryLabel,
    bool CanRunPrimaryAction,
    bool CanInvite,
    bool CanManageFriends,
    int PendingCount);

public sealed record ConnectFriendViewModel(
    string MembershipId,
    string DeviceId,
    string Nickname,
    string State,
    string Setup,
    bool IsPending,
    bool IsApproved);

/// <summary>
/// Pure, headless presentation for Connect. WPF views consume these records but all state and
/// localization decisions remain testable without constructing a Window.
/// </summary>
public static class ConnectPresentation
{
    public static ConnectSetupViewModel Setup(ConnectStatusResponse? status)
    {
        if (status is null)
        {
            return new(
                LocalizationService.Get("Connect.State.Checking"),
                LocalizationService.Get("Connect.State.CheckingDetail"),
                UiStatusTone.Neutral,
                false,
                false);
        }

        if (!status.CredentialStored || status.State == ConnectSetupState.NotSetUp)
        {
            return new(
                LocalizationService.Get("Connect.State.NotSetUp"),
                LocalizationService.Get("Connect.State.NotSetUpDetail"),
                UiStatusTone.Neutral,
                false,
                false);
        }

        if (status.HostNode == ConnectHostNodeState.TailnetLockUnsupported)
        {
            return new(
                LocalizationService.Get("Connect.State.TailnetLock"),
                LocalizationService.Get("Connect.State.TailnetLockDetail"),
                UiStatusTone.Negative,
                true,
                true);
        }

        if (status.Policy is ConnectPolicyState.Unsafe or ConnectPolicyState.Incomplete or
            ConnectPolicyState.NotPermitted or ConnectPolicyState.Unverifiable)
        {
            var reasons = status.PolicyReasons.Count == 0
                ? LocalizationService.Get("Connect.State.PolicyDetail")
                : string.Join(Environment.NewLine, status.PolicyReasons);
            return new(
                LocalizationService.Get("Connect.State.Policy"),
                reasons,
                UiStatusTone.Caution,
                true,
                true);
        }

        if (status.State == ConnectSetupState.Starting)
        {
            var hostStarting = status.HostNode is ConnectHostNodeState.Enrolling or
                ConnectHostNodeState.Enrolled;
            return new(
                LocalizationService.Get(hostStarting
                    ? "Connect.State.HostStarting"
                    : "Connect.State.Checking"),
                LocalizationService.Get(hostStarting
                    ? "Connect.State.HostStartingDetail"
                    : "Connect.State.CheckingDetail"),
                UiStatusTone.Neutral,
                true,
                true);
        }

        if (status.State == ConnectSetupState.Ready)
        {
            return new(
                LocalizationService.Get("Connect.State.Ready"),
                LocalizationService.Get("Connect.State.ReadyDetail"),
                UiStatusTone.Positive,
                true,
                true);
        }

        return new(
            LocalizationService.Get("Connect.State.Error"),
            string.IsNullOrWhiteSpace(status.ErrorCode)
                ? LocalizationService.Get("Connect.State.ErrorDetail")
                : LocalizationService.Format("Connect.State.ErrorCode", status.ErrorCode),
            UiStatusTone.Negative,
            true,
            true);
    }

    public static ConnectServerViewModel Server(GameType game, ServerConnectResponse? status)
    {
        if (game != GameType.Minecraft)
        {
            return Disabled("Connect.Server.NotAvailable", "Connect.Server.NotAvailableDetail");
        }

        if (status is null)
        {
            return Disabled("Connect.State.Checking", "Connect.State.CheckingDetail");
        }

        var pending = status.Friends.Count(friend => friend.State == ConnectFriendState.Pending);
        if (!status.AccountReady)
        {
            return Disabled("Connect.Server.SetupRequired", "Connect.Server.SetupRequiredDetail", pending);
        }

        if (!status.Eligible)
        {
            var reasons = status.Issues.Count == 0
                ? LocalizationService.Get("Connect.Eligibility.Unknown")
                : string.Join(Environment.NewLine, status.Issues.Select(EligibilityReason));
            return new(
                LocalizationService.Get("Connect.Server.NotEligible"),
                reasons,
                UiStatusTone.Caution,
                ConnectServerAction.None,
                LocalizationService.Get("ServerSettings.ConnectEnable"),
                false,
                false,
                pending > 0,
                pending);
        }

        if (!status.Enabled)
        {
            return new(
                LocalizationService.Get("Connect.Server.Off"),
                LocalizationService.Get("Connect.Server.OffDetail"),
                UiStatusTone.Neutral,
                ConnectServerAction.Enable,
                LocalizationService.Get("ServerSettings.ConnectEnable"),
                true,
                false,
                status.Friends.Count > 0,
                pending);
        }

        return new(
            LocalizationService.Get("Connect.Server.On"),
            LocalizationService.Get("Connect.Server.OnDetail"),
            UiStatusTone.Positive,
            ConnectServerAction.Disable,
            LocalizationService.Get("ServerSettings.ConnectDisable"),
            true,
            true,
            true,
            pending);
    }

    public static ConnectFriendViewModel Friend(ConnectFriendItem friend) => new(
        friend.MembershipId,
        friend.DeviceId,
        friend.Nickname ?? string.Empty,
        LocalizationService.Get(friend.State == ConnectFriendState.Pending
            ? "Connect.Friends.Pending"
            : "Connect.Friends.Approved"),
        LocalizationService.Get(friend.Setup switch
        {
            ConnectFriendSetupState.WaitingForFriend => "Connect.Friends.Waiting",
            ConnectFriendSetupState.Checking => "Connect.Friends.Checking",
            ConnectFriendSetupState.Ready => "Connect.Friends.Ready",
            ConnectFriendSetupState.Failed => "Connect.Friends.Failed",
            _ => "Connect.Friends.Pending"
        }),
        friend.State == ConnectFriendState.Pending,
        friend.State == ConnectFriendState.Approved);

    public static string RevokeOutcome(ConnectRevokeResult result) =>
        string.Join(
            Environment.NewLine,
            LocalizationService.Format("Connect.Revoke.Broker", Step(result.Broker)),
            LocalizationService.Format("Connect.Revoke.ThisPc", Step(result.ThisPc)),
            LocalizationService.Format("Connect.Revoke.Tailnet", Step(result.TailnetDevice)));

    private static ConnectServerViewModel Disabled(string stateKey, string detailKey, int pending = 0) => new(
        LocalizationService.Get(stateKey),
        LocalizationService.Get(detailKey),
        UiStatusTone.Neutral,
        ConnectServerAction.None,
        LocalizationService.Get("ServerSettings.ConnectEnable"),
        false,
        false,
        pending > 0,
        pending);

    private static string EligibilityReason(ConnectEligibilityIssue issue) =>
        LocalizationService.Get($"Connect.Eligibility.{issue}");

    private static string Step(ConnectStepOutcome outcome) =>
        LocalizationService.Get($"Connect.Revoke.{outcome}");
}
