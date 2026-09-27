using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

public sealed class ConnectNodeVerifierTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void FreshCorrectlyTaggedNode_IsConfirmed()
    {
        var candidate = Membership("mem_a", "dev_a", "node_a", "candidate", Now.AddMinutes(-1));
        var local = Local(candidate, Now.AddMinutes(-2));
        var device = Device("node_a", Now.AddMinutes(-1));

        var decision = ConnectNodeVerifier.Evaluate(candidate, [candidate], local, device, "node_host", Now);

        Assert.Equal(ConnectNodeVerificationKind.Confirm, decision.Kind);
    }

    [Fact]
    public void NodeCreatedBeforeEnrollment_IsRejected()
    {
        var candidate = Membership("mem_a", "dev_a", "node_a", "candidate", Now.AddMinutes(-1));
        var decision = ConnectNodeVerifier.Evaluate(
            candidate,
            [candidate],
            Local(candidate, Now.AddMinutes(-2)),
            Device("node_a", Now.AddMinutes(-10)),
            "node_host",
            Now);

        Assert.Equal(ConnectNodeVerificationKind.Reject, decision.Kind);
        Assert.Equal("node_predates_enrollment", decision.Reason);
    }

    [Fact]
    public void NodeBoundToDifferentDevice_IsRejected()
    {
        var candidate = Membership("mem_a", "dev_a", "node_a", "candidate", Now.AddMinutes(-1));
        var other = Membership("mem_b", "dev_b", "node_a", "confirmed", Now.AddHours(-1));

        var decision = ConnectNodeVerifier.Evaluate(
            candidate, [candidate, other], Local(candidate, Now.AddMinutes(-2)), Device("node_a", Now), "node_host", Now);

        Assert.Equal(ConnectNodeVerificationKind.Reject, decision.Kind);
        Assert.Equal("node_bound_to_another_device", decision.Reason);
    }

    [Fact]
    public void ExistingConfirmedNodeForSameDevice_CanBeReused()
    {
        var candidate = Membership("mem_a", "dev_a", "node_a", "candidate", Now.AddMinutes(-1));
        var other = Membership("mem_b", "dev_a", "node_a", "confirmed", Now.AddHours(-1));

        var decision = ConnectNodeVerifier.Evaluate(
            candidate, [candidate, other], Local(candidate, Now), Device("node_a", Now.AddDays(-5)), "node_host", Now);

        Assert.Equal(ConnectNodeVerificationKind.Confirm, decision.Kind);
    }

    [Fact]
    public void MissingNode_IsRetriedUntilGracePeriodEnds()
    {
        var recent = Membership("mem_a", "dev_a", "node_a", "candidate", Now.AddMinutes(-9));
        var old = recent with { NodeBoundAt = Now.AddMinutes(-11) };

        Assert.Equal(ConnectNodeVerificationKind.Retry,
            ConnectNodeVerifier.Evaluate(recent, [recent], Local(recent, Now), null, "node_host", Now).Kind);
        Assert.Equal(ConnectNodeVerificationKind.Reject,
            ConnectNodeVerifier.Evaluate(old, [old], Local(old, Now), null, "node_host", Now).Kind);
    }

    private static ConnectBrokerMembership Membership(
        string id, string deviceId, string nodeId, string nodeState, DateTimeOffset boundAt) =>
        new(id, Guid.Parse("11111111-1111-1111-1111-111111111111"), "Home", deviceId, "spki", "approved", 1,
            nodeId, nodeState, boundAt, nodeState == "confirmed" ? boundAt : null, Now.AddHours(-1), Now.AddMinutes(-20));

    private static ConnectMembershipState Local(ConnectBrokerMembership membership, DateTimeOffset mintedAt) =>
        new(membership.MembershipId, membership.DeviceId, membership.ServerId, "key", mintedAt, Now,
            null, null, membership.State, membership.CreatedAt, membership.ApprovedAt, membership.NodeState);

    private static ConnectTailnetDevice Device(string nodeId, DateTimeOffset createdAt) =>
        new(nodeId, [TailscaleApiProvisioner.FriendTag], createdAt, "friend", ["100.64.1.2"], false, null);
}
