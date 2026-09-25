namespace ServerManager.Connect.App.Broker;

/// <summary>
/// The friend's side of the broker API (contract §13). Control plane only: invites, approval
/// state, the one-time enrollment pickup and session tickets. Every call except
/// <see cref="GetTicketKeysAsync"/> is signed by the device key. Failures are
/// <see cref="BrokerException"/>.
/// </summary>
public interface IBrokerClient
{
    /// <summary>POST /v1/devices. Idempotent: the id derives from the key.</summary>
    Task<string> RegisterDeviceAsync(CancellationToken cancellationToken);

    /// <summary>POST /v1/invites/redeem. The secret travels in the body, never in the URL (§6).</summary>
    Task<InviteRedemption> RedeemInviteAsync(string secret, CancellationToken cancellationToken);

    /// <summary>GET /v1/devices/me/memberships.</summary>
    Task<IReadOnlyList<Membership>> GetMembershipsAsync(CancellationToken cancellationToken);

    /// <summary>GET /v1/memberships/{id}/enrollment. Null when there is nothing to pick up (yet).</summary>
    Task<EnrollmentPackage?> TakeEnrollmentAsync(string membershipId, CancellationToken cancellationToken);

    /// <summary>POST /v1/memberships/{id}/node.</summary>
    Task BindNodeAsync(string membershipId, string nodeId, CancellationToken cancellationToken);

    /// <summary>POST /v1/sessions.</summary>
    Task<SessionTicket> CreateSessionAsync(string membershipId, string sessionSpki, CancellationToken cancellationToken);

    /// <summary>GET /v1/keys, verbatim, for the transport's pinned keyset file.</summary>
    Task<byte[]> GetTicketKeysAsync(CancellationToken cancellationToken);
}
