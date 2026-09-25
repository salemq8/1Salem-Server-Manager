using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Identity;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.Core.Enrollment;

namespace ServerManager.Connect.App.Services;

public enum EnrollmentOutcome
{
    /// <summary>Nothing to pick up yet: the owner's Server Manager has not posted the blob.</summary>
    Pending,

    /// <summary>The node is enrolled and bound to the membership.</summary>
    Completed,

    /// <summary>The blob could not be used. The owner has to approve again to send a new one.</summary>
    Failed
}

public sealed record EnrollmentResult(EnrollmentOutcome Outcome, string? NodeId);

/// <summary>
/// Turns an approved membership into a bound tailnet node (contract §7): fetch the one-time blob,
/// open it with the device key, hand the auth key to the transport once, report the node id.
/// <list type="bullet">
/// <item>If this owner's node already exists (a second server of the same owner, or a bind that
/// failed after enrolling), it is bound directly and no auth key is used at all.</item>
/// <item>Each auth key goes over the pipe at most once per run, recorded before the call, so a
/// retry after an ambiguous failure or a broker that serves the same blob twice cannot make the
/// transport see the key again.</item>
/// <item>A blob is marked used (<see cref="ConsumedEnrollments"/>) the moment it is taken, since
/// the broker deletes it on read. From then on, until the membership is bound or a new blob
/// arrives, finding no blob and no node means the enrollment failed, whatever ended the attempt:
/// a refusal, a transport that went away, did not answer or is not this app's, a cancellation,
/// or the app exiting.</item>
/// <item>Calls are serialized, so two refreshes racing cannot both enroll.</item>
/// </list>
/// </summary>
public sealed class EnrollmentCoordinator
{
    private readonly IBrokerClient _broker;
    private readonly ITransportClient _transport;
    private readonly ITransportProcess _process;
    private readonly IDeviceIdentity _identity;
    private readonly ConsumedEnrollments _consumed;
    private readonly DiagnosticsLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _handedOffKeyIds = new(StringComparer.Ordinal);

    public EnrollmentCoordinator(
        IBrokerClient broker,
        ITransportClient transport,
        ITransportProcess process,
        IDeviceIdentity identity,
        ConsumedEnrollments consumed,
        DiagnosticsLog log)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _consumed = consumed ?? throw new ArgumentNullException(nameof(consumed));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<EnrollmentResult> TryCompleteAsync(Membership membership, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(membership);
        if (!membership.NeedsEnrollment)
        {
            throw new ArgumentException("Only an approved membership without a node needs enrollment.", nameof(membership));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
            var node = TransportNodeNames.ForOwner(membership.OwnerId);
            var nodeId = await EnrolledNodeIdAsync(node, cancellationToken).ConfigureAwait(false);
            if (nodeId is null)
            {
                var enrolled = await EnrollFromBlobAsync(membership, node, cancellationToken).ConfigureAwait(false);
                if (enrolled.Outcome != EnrollmentOutcome.Completed)
                {
                    return enrolled;
                }

                nodeId = enrolled.NodeId!;
            }

            await _broker.BindNodeAsync(membership.MembershipId, nodeId, cancellationToken).ConfigureAwait(false);
            _consumed.Remove(membership.MembershipId);
            return new EnrollmentResult(EnrollmentOutcome.Completed, nodeId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<EnrollmentResult> EnrollFromBlobAsync(Membership membership, string node, CancellationToken cancellationToken)
    {
        var package = await _broker.TakeEnrollmentAsync(membership.MembershipId, cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            // The blob is read-once, so once taken (in this run or an earlier one) there is
            // nothing left to retry with, and no node came of it: keep reporting the failure
            // until the owner sends a new one.
            return _consumed.Contains(membership.MembershipId) ? Failed() : new EnrollmentResult(EnrollmentOutcome.Pending, null);
        }

        // Before anything else can end this attempt: the broker no longer has the blob.
        _consumed.Add(membership.MembershipId);
        EnrollmentSecret secret;
        try
        {
            secret = package.OwnerId == membership.OwnerId
                ? _identity.OpenEnrollment(package.Ciphertext, membership.MembershipId, membership.OwnerId)
                : throw new EnrollmentDecryptionException();
        }
        catch (EnrollmentDecryptionException exception)
        {
            _log.Record("enrollment", exception);
            return Failed();
        }

        if (!_handedOffKeyIds.Add(secret.KeyId))
        {
            _log.Record("enrollment", "An enrollment key arrived a second time and was not handed to the transport again.");
            return Failed();
        }

        // Only a definite answer is settled here. Anything else (the transport went away, did not
        // answer in time or is not this app's, or the call was cancelled) is left to the caller:
        // whether a node came up anyway is checked first on the next attempt, and the blob is
        // already marked used.
        try
        {
            var nodeId = await _transport.EnrollAsync(node, secret.AuthKey, TransportNodeNames.Hostname(_identity.DeviceId), cancellationToken)
                .ConfigureAwait(false);
            return new EnrollmentResult(EnrollmentOutcome.Completed, nodeId);
        }
        catch (TransportException exception) when (exception.Code == TransportErrorCodes.AlreadyEnrolled)
        {
            // This owner's node came up in the meantime; it serves this membership too.
            var existing = await EnrolledNodeIdAsync(node, cancellationToken).ConfigureAwait(false);
            return existing is null ? Failed() : new EnrollmentResult(EnrollmentOutcome.Completed, existing);
        }
        catch (TransportException exception) when (exception.Code is not (TransportErrorCodes.Unavailable
                                                                          or TransportErrorCodes.NoAnswer
                                                                          or TransportErrorCodes.Untrusted))
        {
            // The transport refused or could not use the key (bad_auth_key, enroll_failed, …).
            _log.Record("enrollment", exception);
            return Failed();
        }
    }

    private async Task<string?> EnrolledNodeIdAsync(string node, CancellationToken cancellationToken)
    {
        var status = await _transport.StatusAsync(cancellationToken).ConfigureAwait(false);
        var nodeId = status.Nodes.FirstOrDefault(entry => entry.Node == node)?.NodeId;
        return BrokerFormats.IsNodeId(nodeId) ? nodeId : null;
    }

    private static EnrollmentResult Failed() => new(EnrollmentOutcome.Failed, null);
}
