using System.Net;
using System.Runtime.Versioning;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Connect;

/// <summary>Serializes owner actions with reconciliation and exposes only UI-safe values.</summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectOwnerWorkflow(ConnectHost host, TimeProvider clock)
{
    private static readonly HashSet<int> SensitivePorts = [3389, 5357, 5985, 5986, 8212, 25575];
    private readonly ConnectHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public ConnectStatusResponse Status => _host.Status;

    public Task SetCredentialAsync(ConnectCredentialRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret) ||
            request.ClientId.Length > 256 || request.ClientSecret.Length > 1024)
        {
            throw new ConnectHostOperationException(ConnectErrorCodes.InvalidRequest, "A client id and secret are required.");
        }

        return _host.SetCredentialAsync(
            new TailscaleOAuthCredential(request.ClientId.Trim(), request.ClientSecret),
            cancellationToken);
    }

    public Task RemoveCredentialAsync(CancellationToken cancellationToken) => _host.RemoveCredentialAsync(cancellationToken);
    public Task CheckAsync(CancellationToken cancellationToken) => _host.CheckAsync(cancellationToken);

    public async Task<ServerConnectResponse> GetServerAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var server = await RequiredServerAsync(serverId, cancellationToken).ConfigureAwait(false);
        var issues = await EligibilityAsync(server, cancellationToken).ConfigureAwait(false);
        var state = _host.State;
        return new ServerConnectResponse(
            serverId,
            _host.Status.State == ConnectSetupState.Ready,
            issues.Count == 0,
            issues,
            state.EnabledServers.Contains(serverId),
            state.Invites.Where(invite => invite.ServerId == serverId).Select(ToInvite).ToArray(),
            state.Memberships.Where(member => member.ServerId == serverId && member.State is "pending" or "approved")
                .Select(ToFriend).ToArray());
    }

    public async Task EnableAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await InHostLockAsync(async () =>
        {
            RequireReady();
            var server = await RequiredServerAsync(serverId, cancellationToken).ConfigureAwait(false);
            var issues = await EligibilityAsync(server, cancellationToken).ConfigureAwait(false);
            if (issues.Count != 0)
            {
                throw new ConnectHostOperationException(ConnectErrorCodes.NotEligible, "This server is not eligible for private friend access.");
            }

            await _host.Broker.PutServerAsync(serverId, server.Name, _host.State.HostBridge!, cancellationToken).ConfigureAwait(false);
            _host.EnabledServers.Enable(serverId);
            var registered = _host.State.Servers.Where(item => item.ServerId != serverId)
                .Append(new ConnectRegisteredServerState(serverId, server.Name, _host.State.HostBridge!)).ToArray();
            _host.SetState(_host.State with
            {
                EnabledServers = _host.EnabledServers.Snapshot(),
                Servers = registered
            });
            await _host.SaveStateAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisableAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await InHostLockAsync(async () =>
        {
            if (!_host.State.EnabledServers.Contains(serverId))
            {
                return;
            }

            if (_host.ActiveAuthorization is { } authorization)
            {
                await authorization.DisableConnectAsync(serverId, cancellationToken).ConfigureAwait(false);
            }
            _host.EnabledServers.Disable(serverId);
            _host.SetState(_host.State with
            {
                EnabledServers = _host.EnabledServers.Snapshot(),
                Servers = _host.State.Servers.Where(item => item.ServerId != serverId).ToArray()
            });
            await _host.SaveStateAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectInviteCreated> CreateInviteAsync(Guid serverId, int ttlSeconds, CancellationToken cancellationToken)
    {
        ConnectInviteCreated? result = null;
        await InHostLockAsync(async () =>
        {
            RequireReady();
            if (!_host.EnabledServers.IsEnabled(serverId))
            {
                throw new ConnectHostOperationException(ConnectErrorCodes.InvalidState, "Enable private friend access before creating an invite.");
            }

            if (ttlSeconds is < 60 or > 604800)
            {
                throw new ConnectHostOperationException(ConnectErrorCodes.InvalidRequest, "Invite validity must be between one minute and seven days.");
            }

            var invite = await _host.Broker.CreateInviteAsync(serverId, ttlSeconds, cancellationToken).ConfigureAwait(false);
            var createdAt = _clock.GetUtcNow();
            _host.SetState(_host.State with
            {
                Invites = _host.State.Invites.Where(item => item.InviteId != invite.InviteId)
                    .Append(new ConnectInviteState(invite.InviteId, serverId, invite.ExpiresAt, "active", createdAt)).ToArray()
            });
            await _host.SaveStateAsync(cancellationToken).ConfigureAwait(false);
            result = new ConnectInviteCreated(
                invite.InviteId,
                "https://connect.1salem.app/i#" + invite.Secret,
                invite.Secret,
                invite.ExpiresAt);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    public Task RevokeInviteAsync(string inviteId, CancellationToken cancellationToken) =>
        InHostLockAsync(async () =>
        {
            var invite = _host.State.Invites.SingleOrDefault(item => item.InviteId == inviteId) ??
                throw new ConnectHostOperationException(ConnectErrorCodes.NotFound, "The invite was not found.");
            await _host.Broker.RevokeInviteAsync(invite.InviteId, cancellationToken).ConfigureAwait(false);
            ReplaceInvite(invite with { State = "revoked" });
            await _host.SaveStateAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task ApproveAsync(string membershipId, CancellationToken cancellationToken) =>
        DecideMembershipAsync(membershipId, approve: true, cancellationToken);

    public Task RejectAsync(string membershipId, CancellationToken cancellationToken) =>
        DecideMembershipAsync(membershipId, approve: false, cancellationToken);

    public Task SetNicknameAsync(string membershipId, string? nickname, CancellationToken cancellationToken) =>
        InHostLockAsync(async () =>
        {
            var membership = RequiredMembership(membershipId);
            var normalized = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
            if (normalized?.Length > 80)
            {
                throw new ConnectHostOperationException(ConnectErrorCodes.InvalidRequest, "A nickname can contain at most 80 characters.");
            }

            ReplaceMembership(membership with { LocalNickname = normalized });
            await _host.SaveStateAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<ConnectRevokeResult> RevokeAsync(string membershipId, CancellationToken cancellationToken)
    {
        ConnectRevokeResult? result = null;
        await InHostLockAsync(async () =>
        {
            var membership = RequiredMembership(membershipId);
            ConnectBrokerRevocationResult revoked;
            try
            {
                revoked = await _host.Broker.RevokeMembershipAsync(membershipId, cancellationToken).ConfigureAwait(false);
            }
            catch (ConnectOwnerBrokerException exception)
            {
                result = new(false, ConnectStepOutcome.Failed, ConnectStepOutcome.NotStarted,
                    ConnectStepOutcome.NotStarted,
                    exception.Failure is ConnectOwnerBrokerFailure.Unavailable or ConnectOwnerBrokerFailure.Unreachable or ConnectOwnerBrokerFailure.RateLimited
                        ? ConnectErrorCodes.BrokerUnavailable
                        : ConnectErrorCodes.BrokerRejected);
                return;
            }

            var local = ConnectStepOutcome.Done;
            try
            {
                await _host.Authorization.RevokeMembershipAsync(membershipId, cancellationToken).ConfigureAwait(false);
                await _host.PersistMembershipRevocationAsync(membershipId, revoked.AuthorizationVersion, cancellationToken).ConfigureAwait(false);
                ReplaceMembership(membership with { State = "revoked" });
                await _host.SaveStateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                local = ConnectStepOutcome.Failed;
            }

            var tailnet = ConnectStepOutcome.NotNeeded;
            if (membership.ConfirmedNodeId is { } nodeId)
            {
                try
                {
                    var live = await _host.Broker.GetMembershipsAsync(cancellationToken).ConfigureAwait(false);
                    if (live.Any(item => item.MembershipId != membershipId && item.State == "approved" &&
                            item.NodeId == nodeId && item.NodeState is "candidate" or "confirmed"))
                    {
                        tailnet = ConnectStepOutcome.KeptInUse;
                    }
                    else
                    {
                        await _host.Provisioner.DeleteFriendDeviceAsync(nodeId, _host.State.HostNodeId!, cancellationToken).ConfigureAwait(false);
                        ReplaceMembership(RequiredMembership(membershipId) with { ConfirmedNodeId = null });
                        await _host.SaveStateAsync(cancellationToken).ConfigureAwait(false);
                        tailnet = ConnectStepOutcome.Done;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    tailnet = ConnectStepOutcome.Failed;
                }
            }

            var completed = local == ConnectStepOutcome.Done && tailnet is ConnectStepOutcome.Done or ConnectStepOutcome.NotNeeded or ConnectStepOutcome.KeptInUse;
            result = new(completed, ConnectStepOutcome.Done, local, tailnet,
                completed ? null : local == ConnectStepOutcome.Failed ? ConnectErrorCodes.NotReady : ConnectErrorCodes.TailnetUnavailable);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    private async Task DecideMembershipAsync(string membershipId, bool approve, CancellationToken cancellationToken) =>
        await InHostLockAsync(async () =>
        {
            RequireReady();
            var membership = RequiredMembership(membershipId);
            if (membership.State != "pending")
            {
                throw new ConnectHostOperationException(ConnectErrorCodes.InvalidState, "This friend request is no longer pending.");
            }

            if (approve)
            {
                await _host.Broker.ApproveMembershipAsync(membershipId, cancellationToken).ConfigureAwait(false);
                ReplaceMembership(membership with { State = "approved", ApprovedAt = _clock.GetUtcNow() });
            }
            else
            {
                await _host.Broker.RejectMembershipAsync(membershipId, cancellationToken).ConfigureAwait(false);
                _host.SetState(_host.State with
                {
                    Memberships = _host.State.Memberships.Where(item => item.MembershipId != membershipId).ToArray()
                });
            }

            await _host.SaveStateAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<ConnectEligibilityIssue>> EligibilityAsync(GameServerDefinition server, CancellationToken cancellationToken)
    {
        var issues = new List<ConnectEligibilityIssue>();
        if (server.Game != GameType.Minecraft) issues.Add(ConnectEligibilityIssue.NotMinecraft);
        if (server.Port < 1024) issues.Add(ConnectEligibilityIssue.PortTooLow);
        if (SensitivePorts.Contains(server.Port)) issues.Add(ConnectEligibilityIssue.SensitivePort);
        if (_host.AgentPorts.Contains(server.Port)) issues.Add(ConnectEligibilityIssue.AgentPort);
        var all = await _host.ServerStore.ListAsync(cancellationToken).ConfigureAwait(false);
        if (all.Any(other => other.Id != server.Id && other.Port == server.Port)) issues.Add(ConnectEligibilityIssue.SharedPort);

        if (server.Game == GameType.Minecraft)
        {
            var path = Path.Combine(server.RootPath, "server.properties");
            if (File.Exists(path))
            {
                var properties = MinecraftPropertiesSerializer.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
                if (properties.TryGetValue("prevent-proxy-connections", out var prevent) &&
                    bool.TryParse(prevent, out var blocked) && blocked)
                {
                    issues.Add(ConnectEligibilityIssue.PreventProxyConnections);
                }

                if (properties.TryGetValue("server-ip", out var configured) && !IsLoopbackBinding(configured))
                {
                    issues.Add(ConnectEligibilityIssue.NonLoopbackServerIp);
                }
            }
        }

        return issues.Distinct().ToArray();
    }

    private async Task<GameServerDefinition> RequiredServerAsync(Guid serverId, CancellationToken cancellationToken) =>
        await _host.ServerStore.GetAsync(serverId, cancellationToken).ConfigureAwait(false) ??
        throw new ConnectHostOperationException(ConnectErrorCodes.NotFound, "The server was not found.");

    private ConnectMembershipState RequiredMembership(string membershipId) =>
        _host.State.Memberships.SingleOrDefault(item => item.MembershipId == membershipId) ??
        throw new ConnectHostOperationException(ConnectErrorCodes.NotFound, "The friend request was not found.");

    private void ReplaceMembership(ConnectMembershipState membership) =>
        _host.SetState(_host.State with
        {
            Memberships = _host.State.Memberships.Where(item => item.MembershipId != membership.MembershipId).Append(membership).ToArray()
        });

    private void ReplaceInvite(ConnectInviteState invite) =>
        _host.SetState(_host.State with
        {
            Invites = _host.State.Invites.Where(item => item.InviteId != invite.InviteId).Append(invite).ToArray()
        });

    private void RequireReady()
    {
        if (_host.Status.State != ConnectSetupState.Ready)
        {
            throw new ConnectHostOperationException(
                _host.Status.State == ConnectSetupState.NotSetUp ? ConnectErrorCodes.NotSetUp : ConnectErrorCodes.NotReady);
        }
    }

    private async Task InHostLockAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _host.Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _host.Operations.Release(); }
    }

    private ConnectInviteItem ToInvite(ConnectInviteState invite) => new(
        invite.InviteId,
        invite.CreatedAt,
        invite.ExpiresAt,
        invite.State == "active" && invite.ExpiresAt <= _clock.GetUtcNow()
            ? Contracts.ConnectInviteState.Expired
            : invite.State switch
        {
            "used" => Contracts.ConnectInviteState.Used,
            "revoked" => Contracts.ConnectInviteState.Revoked,
            "expired" => Contracts.ConnectInviteState.Expired,
            _ => Contracts.ConnectInviteState.Active
        });

    private static ConnectFriendItem ToFriend(ConnectMembershipState member) => new(
        member.MembershipId,
        member.ServerId,
        member.DeviceId,
        member.LocalNickname,
        member.State == "approved" ? ConnectFriendState.Approved : ConnectFriendState.Pending,
        member.State != "approved" ? ConnectFriendSetupState.None : member.NodeState switch
        {
            "candidate" => ConnectFriendSetupState.Checking,
            "confirmed" => ConnectFriendSetupState.Ready,
            "rejected" => ConnectFriendSetupState.Failed,
            _ => ConnectFriendSetupState.WaitingForFriend
        },
        member.CreatedAt,
        member.ApprovedAt);

    private static bool IsLoopbackBinding(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 || trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(trimmed, out var address) && IPAddress.IsLoopback(address);
    }
}
