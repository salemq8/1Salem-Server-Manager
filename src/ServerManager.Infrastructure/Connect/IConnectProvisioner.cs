using ServerManager.Connect.Core.Enrollment;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// The Agent's calls into the owner's own tailnet (contract §2 option O1, §7, §12): mint a
/// one-off key for an approved friend, clean up an unused key, confirm the node a friend
/// reports, and delete a revoked friend's node. The credential behind it lives on the owner's
/// PC only; the broker never sees it.
/// </summary>
public interface IConnectProvisioner
{
    /// <summary>
    /// A one-off, pre-authorized, non-ephemeral key tagged <c>tag:1salem-client</c>, valid for one
    /// day. Hold the result only as long as it takes to encrypt it to the friend's device key.
    /// </summary>
    Task<EnrollmentSecret> CreateFriendAuthKeyAsync(string membershipId, CancellationToken cancellationToken);

    /// <returns>False when the key no longer exists (already used, expired or deleted).</returns>
    Task<bool> DeleteAuthKeyAsync(string keyId, CancellationToken cancellationToken);

    /// <returns>Null when the tailnet has no such node.</returns>
    Task<ConnectTailnetDevice?> GetDeviceAsync(string nodeId, CancellationToken cancellationToken);

    /// <returns>False when the tailnet has no such node, which for a revocation means it is already gone.</returns>
    Task<bool> DeleteDeviceAsync(string nodeId, CancellationToken cancellationToken);
}

/// <summary>What the Agent checks about a node a friend reported (contract §7).</summary>
public sealed record ConnectTailnetDevice(string NodeId, IReadOnlyList<string> Tags, DateTimeOffset? CreatedAt)
{
    public bool HasTag(string tag) => Tags.Contains(tag, StringComparer.Ordinal);
}

/// <summary>
/// The provisioning call failed. The message names the operation and the HTTP status only;
/// response bodies and credentials never appear in it.
/// </summary>
public sealed class ConnectProvisioningException : Exception
{
    public ConnectProvisioningException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Provisioning was requested but the owner has not connected a tailnet credential. Phase 1
/// never has one, so every provisioning path ends here instead of pretending to succeed.
/// </summary>
public sealed class ConnectNotConfiguredException : InvalidOperationException
{
    public ConnectNotConfiguredException()
        : base("1Salem Connect is not configured: no tailnet credential has been set up on this PC.")
    {
    }
}
