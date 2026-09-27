namespace ServerManager.Infrastructure.Connect;

public enum ConnectNodeVerificationKind { Confirm, Retry, Reject }

public sealed record ConnectNodeVerification(ConnectNodeVerificationKind Kind, string Reason);

/// <summary>Pure owner-side verification of a device-reported candidate node (plan §5).</summary>
public static class ConnectNodeVerifier
{
    public static readonly TimeSpan CreationClockSkew = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MissingNodeGrace = TimeSpan.FromMinutes(10);

    public static ConnectNodeVerification Evaluate(
        ConnectBrokerMembership candidate,
        IReadOnlyList<ConnectBrokerMembership> liveMemberships,
        ConnectMembershipState? localState,
        ConnectTailnetDevice? device,
        string hostNodeId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(liveMemberships);
        ArgumentException.ThrowIfNullOrEmpty(hostNodeId);
        if (candidate.NodeId is not { Length: > 0 } nodeId || candidate.NodeState != "candidate")
        {
            throw new ArgumentException("Expected a candidate membership with a node id.", nameof(candidate));
        }

        if (liveMemberships.Any(other =>
                other.MembershipId != candidate.MembershipId &&
                other.DeviceId != candidate.DeviceId &&
                other.NodeId == nodeId &&
                other.NodeState is "candidate" or "confirmed"))
        {
            return Reject("node_bound_to_another_device");
        }

        if (device is null)
        {
            return candidate.NodeBoundAt is { } bound && now - bound >= MissingNodeGrace
                ? Reject("node_not_found")
                : Retry("node_not_visible_yet");
        }

        if (device.NodeId != nodeId || nodeId == hostNodeId ||
            !device.HasTag(TailscaleApiProvisioner.FriendTag) ||
            device.HasTag(TailscaleApiProvisioner.HostTag) ||
            device.IsEphemeral)
        {
            return Reject("node_identity_failed");
        }

        var alreadyConfirmedForDevice = liveMemberships.Any(other =>
            other.MembershipId != candidate.MembershipId &&
            other.DeviceId == candidate.DeviceId &&
            other.NodeId == nodeId &&
            other.NodeState == "confirmed");
        if (alreadyConfirmedForDevice)
        {
            return Confirm();
        }

        if (localState?.KeyMintedAt is not { } mintedAt || device.CreatedAt is not { } createdAt)
        {
            return candidate.NodeBoundAt is { } bound && now - bound >= MissingNodeGrace
                ? Reject("node_creation_cannot_be_verified")
                : Retry("node_creation_pending");
        }

        return createdAt >= mintedAt - CreationClockSkew
            ? Confirm()
            : Reject("node_predates_enrollment");
    }

    private static ConnectNodeVerification Confirm() => new(ConnectNodeVerificationKind.Confirm, "confirmed");
    private static ConnectNodeVerification Retry(string reason) => new(ConnectNodeVerificationKind.Retry, reason);
    private static ConnectNodeVerification Reject(string reason) => new(ConnectNodeVerificationKind.Reject, reason);
}
