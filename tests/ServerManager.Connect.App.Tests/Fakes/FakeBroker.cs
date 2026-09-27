using ServerManager.Connect.App.Broker;

namespace ServerManager.Connect.App.Tests.Fakes;

/// <summary>An in-memory broker. Tests set its state and failures, and read back what it was asked.</summary>
internal sealed class FakeBroker : IBrokerClient
{
    public List<Membership> Memberships { get; } = [];

    public List<string> RedeemedSecrets { get; } = [];

    public InviteRedemption? Redemption { get; set; }

    public Exception? RedeemFailure { get; set; }

    /// <summary>What GET …/enrollment returns for a membership.</summary>
    public Dictionary<string, EnrollmentPackage> Enrollments { get; } = [];

    /// <summary>The real broker deletes the blob on read; false plays a misbehaving broker that serves it again.</summary>
    public bool DeleteEnrollmentOnRead { get; set; } = true;

    public int TakeEnrollmentCalls { get; private set; }

    public Exception? NextBindFailure { get; set; }

    public List<(string MembershipId, string NodeId)> Bound { get; } = [];

    public List<string> SessionSpkis { get; } = [];

    public Exception? SessionFailure { get; set; }

    public DateTimeOffset SessionExpiresAt { get; set; } = DateTimeOffset.UnixEpoch.AddDays(20000);

    public int RegisterCalls { get; private set; }

    public Task<string> RegisterDeviceAsync(CancellationToken cancellationToken)
    {
        RegisterCalls++;
        return Task.FromResult("dev_registered");
    }

    public Task<InviteRedemption> RedeemInviteAsync(string secret, CancellationToken cancellationToken)
    {
        RedeemedSecrets.Add(secret);
        if (RedeemFailure is not null)
        {
            return Task.FromException<InviteRedemption>(RedeemFailure);
        }

        return Task.FromResult(Redemption ?? throw new InvalidOperationException("No redemption configured."));
    }

    public Exception? MembershipsFailure { get; set; }

    public Task<IReadOnlyList<Membership>> GetMembershipsAsync(CancellationToken cancellationToken) =>
        MembershipsFailure is not null
            ? Task.FromException<IReadOnlyList<Membership>>(MembershipsFailure)
            : Task.FromResult<IReadOnlyList<Membership>>([.. Memberships]);

    public Task<EnrollmentPackage?> TakeEnrollmentAsync(string membershipId, CancellationToken cancellationToken)
    {
        TakeEnrollmentCalls++;
        if (!Enrollments.TryGetValue(membershipId, out var package))
        {
            return Task.FromResult<EnrollmentPackage?>(null);
        }

        if (DeleteEnrollmentOnRead)
        {
            Enrollments.Remove(membershipId);
        }

        return Task.FromResult<EnrollmentPackage?>(package);
    }

    public Task BindNodeAsync(string membershipId, string nodeId, CancellationToken cancellationToken)
    {
        if (NextBindFailure is { } failure)
        {
            NextBindFailure = null;
            return Task.FromException(failure);
        }

        Bound.Add((membershipId, nodeId));
        var index = Memberships.FindIndex(membership => membership.MembershipId == membershipId);
        Memberships[index] = Memberships[index] with { NodeId = nodeId, NodeState = MembershipNodeState.Candidate };
        return Task.CompletedTask;
    }

    public Task<SessionTicket> CreateSessionAsync(string membershipId, string sessionSpki, CancellationToken cancellationToken)
    {
        SessionSpkis.Add(sessionSpki);
        return SessionFailure is not null
            ? Task.FromException<SessionTicket>(SessionFailure)
            : Task.FromResult(new SessionTicket($"eyJ.ticket{SessionSpkis.Count}.sig", SessionExpiresAt, "127.0.0.1:7780"));
    }

    public Task<byte[]> GetTicketKeysAsync(CancellationToken cancellationToken) =>
        Task.FromResult("{\"keys\":[{\"kid\":\"k1\",\"alg\":\"ES256\",\"spki\":\"x\"}]}"u8.ToArray());

    public void SetState(string membershipId, MembershipState state)
    {
        var index = Memberships.FindIndex(membership => membership.MembershipId == membershipId);
        Memberships[index] = Memberships[index] with { State = state };
    }

    public void SetNodeState(string membershipId, MembershipNodeState state)
    {
        var index = Memberships.FindIndex(membership => membership.MembershipId == membershipId);
        Memberships[index] = Memberships[index] with { NodeState = state };
    }
}
