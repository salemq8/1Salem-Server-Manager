using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Enrollment;
using ServerManager.Connect.Core.Identity;
using ServerManager.Connect.Core.Pipes;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// Owns the Agent's complete Connect host lifetime: protected state, owner identity, broker,
/// authorization pipe, host transport, policy gate and reconciliation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectHost : IAsyncDisposable
{
    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DeviceVisibilityTimeout = TimeSpan.FromMinutes(1);

    private readonly ConnectHostOptions _options;
    private readonly ConnectOwnerPaths _paths;
    private readonly ConnectOAuthCredentialStore _credentials;
    private readonly ConnectStateStore _store;
    private readonly IGameServerStore _servers;
    private readonly TimeProvider _clock;
    private readonly IConnectHostRuntimeFactory _factory;
    private readonly ILogger<ConnectHost> _logger;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly ConnectEnabledServers _enabled = new();

    private ConnectOwnerState _state = new();
    private ConnectRevocationState _revocations = new();
    private ConnectIdentity? _identity;
    private IConnectOwnerBrokerClient? _broker;
    private IConnectProvisioner? _provisioner;
    private IConnectHostAuthorizationServer? _authorization;
    private IConnectHostTransportSupervisor? _supervisor;
    private IConnectHostTransportControlClient? _control;
    private ConnectServerCatalog? _catalog;
    private CancellationTokenSource? _runtimeStopping;
    private Task? _reconcileLoop;
    private int _started;
    private ConnectStatusResponse _status = EmptyStatus();

    public ConnectHost(
        ConnectHostOptions options,
        IGameServerStore servers,
        TimeProvider clock,
        IConnectHostRuntimeFactory factory,
        ILogger<ConnectHost> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _paths = new ConnectOwnerPaths(options.DataRoot);
        _credentials = new ConnectOAuthCredentialStore(_paths);
        _store = new ConnectStateStore(_paths);
        _servers = servers ?? throw new ArgumentNullException(nameof(servers));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ConnectStatusResponse Status
    {
        get
        {
            var status = Volatile.Read(ref _status);
            var authorization = _authorization?.Status;
            return authorization is null
                ? status
                : status with
                {
                    RevocationChannel = authorization.SubscriberPresent,
                    LiveConnections = authorization.LiveConnections
                };
        }
    }

    internal ConnectOwnerState State => _state;
    internal ConnectEnabledServers EnabledServers => _enabled;
    internal IGameServerStore ServerStore => _servers;
    internal IReadOnlyList<int> AgentPorts => _options.AgentPorts;
    internal IConnectHostAuthorizationServer? ActiveAuthorization => _authorization;
    internal IConnectOwnerBrokerClient Broker => _broker ?? throw new ConnectHostOperationException(ConnectErrorCodes.NotReady);
    internal IConnectProvisioner Provisioner => _provisioner ?? throw new ConnectHostOperationException(ConnectErrorCodes.NotReady);
    internal IConnectHostAuthorizationServer Authorization => _authorization ?? throw new ConnectHostOperationException(ConnectErrorCodes.NotReady);
    internal SemaphoreSlim Operations => _operations;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task SetCredentialAsync(TailscaleOAuthCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // Validate the client and policy_file:read before it is persisted. An unsafe policy is a
        // valid credential and is kept so the UI can explain what the owner must change.
        using (var validation = _factory.CreateProvisioner(credential))
        {
            var policy = await validation.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _ = ConnectPolicyAnalyzer.Analyze(policy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(policy);
            }
        }

        await RestartAsync(() => _credentials.Save(credential), cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveCredentialAsync(CancellationToken cancellationToken) =>
        RestartAsync(_credentials.Delete, cancellationToken, startAgain: false);

    public Task CheckAsync(CancellationToken cancellationToken) =>
        RestartAsync(static () => { }, cancellationToken);

    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _started, 0);
        await StopLoopAsync().ConfigureAwait(false);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeRuntimeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _operations.Dispose();
    }

    internal Task SaveStateAsync(CancellationToken cancellationToken) =>
        _store.SaveStateAsync(_state, cancellationToken);

    internal void SetState(ConnectOwnerState state) => _state = state;

    internal async Task PersistMembershipRevocationAsync(string membershipId, long authorizationFloor, CancellationToken cancellationToken)
    {
        _revocations = _revocations with
        {
            Memberships = Add(_revocations.Memberships, membershipId),
            AuthorizationFloors = UpsertFloor(_revocations.AuthorizationFloors, membershipId, authorizationFloor)
        };
        await _store.SaveRevocationsAsync(_revocations, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestartAsync(Action mutateCredential, CancellationToken cancellationToken, bool startAgain = true)
    {
        await StopLoopAsync().ConfigureAwait(false);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeRuntimeAsync().ConfigureAwait(false);
            mutateCredential();
            if (startAgain)
            {
                Volatile.Write(ref _started, 1);
                await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Publish(EmptyStatus());
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        TailscaleOAuthCredential? credential;
        try
        {
            credential = _credentials.TryLoad();
        }
        catch (Exception exception)
        {
            Fail(ConnectErrorCodes.CredentialInvalid, exception);
            return;
        }

        if (credential is null)
        {
            Publish(EmptyStatus());
            return;
        }

        Publish(EmptyStatus() with
        {
            State = ConnectSetupState.Starting,
            CredentialStored = true,
            ClientIdHint = Hint(credential.ClientId)
        });

        try
        {
            _paths.EnsureDirectories();
            _state = await _store.LoadStateAsync(cancellationToken).ConfigureAwait(false);
            _revocations = await _store.LoadRevocationsAsync(cancellationToken).ConfigureAwait(false);
            _identity = new ConnectIdentityStore(_paths.IdentityDirectory, ConnectIdentityKind.Owner).LoadOrCreate();
            _broker = _factory.CreateBroker(_identity);
            _provisioner = _factory.CreateProvisioner(credential);

            var ownerId = await _broker.RegisterOwnerAsync(cancellationToken).ConfigureAwait(false);
            var ticketKeys = await LoadTicketKeysAsync(cancellationToken).ConfigureAwait(false);
            _enabled.Replace([]);
            _catalog = new ConnectServerCatalog(_servers, _enabled, _options.AgentPorts);
            await _catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
            _authorization = _factory.CreateAuthorizationServer(
                new ConnectHostAuthorizationOptions(
                    ownerId,
                    ticketKeys,
                    pipeName: _options.AuthorizationPipeName,
                    initialRevocations: ConnectRevocationSeed.FromState(_revocations, _clock.GetUtcNow())),
                _catalog);
            _authorization.Start();

            var transportOptions = new ConnectHostTransportOptions(
                _options.TransportExecutablePath,
                ConnectTransportMode.Tsnet,
                ":7780",
                _paths.HostTransportDirectory,
                authorizationPipeName: _options.AuthorizationPipeName,
                controlPipeName: _options.ControlPipeName);
            _supervisor = _factory.CreateSupervisor(transportOptions);
            _control = _factory.CreateControlClient(transportOptions, _supervisor);
            _supervisor.Start();
            await WaitForControlAsync(cancellationToken).ConfigureAwait(false);

            var host = await EnsureHostNodeAsync(cancellationToken).ConfigureAwait(false);
            _state = _state with
            {
                OwnerId = ownerId,
                HostNodeId = host.NodeId,
                HostAddresses = host.Addresses,
                HostBridge = host.Bridge
            };
            await _store.SaveStateAsync(_state, cancellationToken).ConfigureAwait(false);
            await CheckPolicyAsync(credential, host, cancellationToken).ConfigureAwait(false);

            // Capture the token: StopAsync may clear the field before Task.Run begins.
            var runtimeStopping = new CancellationTokenSource();
            var runtimeToken = runtimeStopping.Token;
            _runtimeStopping = runtimeStopping;
            _reconcileLoop = Task.Run(() => ReconcileLoopAsync(runtimeToken), CancellationToken.None);
        }
        catch (ConnectPolicyNotPermittedException exception)
        {
            PublishAttention(ConnectPolicyState.NotPermitted, ConnectErrorCodes.PolicyNotPermitted, exception.Message);
            await DisposeRuntimeAsync().ConfigureAwait(false);
        }
        catch (ConnectTailnetLockException exception)
        {
            Publish(Status with
            {
                State = ConnectSetupState.NeedsAttention,
                HostNode = ConnectHostNodeState.TailnetLockUnsupported,
                ErrorCode = ConnectErrorCodes.TailnetLockUnsupported,
                LastCheckedAtUtc = _clock.GetUtcNow()
            });
            _logger.LogWarning("1Salem Connect cannot use this tailnet because Tailnet Lock is enabled: {Error}", exception.Message);
            await DisposeRuntimeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Fail(MapError(exception), exception);
            await DisposeRuntimeAsync().ConfigureAwait(false);
        }
    }

    private async Task<TicketKeySet> LoadTicketKeysAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_paths.TicketKeysFile))
        {
            if (new FileInfo(_paths.TicketKeysFile).Length is <= 0 or > 64 * 1024)
            {
                throw new InvalidDataException("The pinned Connect ticket key set has an invalid size.");
            }

            var content = await File.ReadAllTextAsync(_paths.TicketKeysFile, cancellationToken).ConfigureAwait(false);
            return TicketKeySet.Parse(content);
        }

        var bytes = await Broker.GetTicketKeysAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var keys = TicketKeySet.Parse(Encoding.UTF8.GetString(bytes));
            ConnectOAuthCredentialStore.AtomicWrite(_paths.TicketKeysFile, bytes.ToArray());
            return keys;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task WaitForControlAsync(CancellationToken cancellationToken)
    {
        var started = _clock.GetTimestamp();
        while (_clock.GetElapsedTime(started) < _options.ControlStartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = await _control!.HelloAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ConnectHostTransportException exception) when (exception.Code is "unavailable" or "no_answer")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new ConnectHostTransportException("unavailable");
    }

    private async Task<HostNode> EnsureHostNodeAsync(CancellationToken cancellationToken)
    {
        var status = await _control!.StatusAsync(cancellationToken).ConfigureAwait(false);
        var host = status.Nodes.SingleOrDefault(node => node.Node == "host");
        var nodeId = host?.NodeId;
        if (string.IsNullOrEmpty(nodeId))
        {
            Publish(Status with { HostNode = ConnectHostNodeState.Enrolling });
            var key = await Provisioner.CreateHostAuthKeyAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                nodeId = await _control.EnrollAsync(key.AuthKey, Hostname(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await Provisioner.DeleteAuthKeyAsync(key.KeyId, cancellationToken).ConfigureAwait(false);
                }
                catch (ConnectProvisioningException exception)
                {
                    _logger.LogWarning("1Salem Connect could not clean up the used host key: {Error}", exception.Message);
                }
            }
        }

        var device = await WaitForDeviceAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (device.Tags.Count != 1 || !device.HasTag(TailscaleApiProvisioner.HostTag) || device.IsEphemeral)
        {
            throw new ConnectHostOperationException(ConnectErrorCodes.CredentialRejected, "The enrolled host node did not have the required host-only tag.");
        }

        if (!string.IsNullOrWhiteSpace(device.TailnetLockError))
        {
            throw new ConnectTailnetLockException(device.TailnetLockError);
        }

        var address = device.Addresses.Select(ParseTailnetIPv4).FirstOrDefault(value => value is not null) ??
            throw new ConnectHostOperationException(ConnectErrorCodes.TailnetUnavailable, "The host node has no Tailscale IPv4 address.");
        await WaitForHostBridgeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return new HostNode(nodeId, device.Addresses, address + ":7780");
    }

    private async Task WaitForHostBridgeAsync(string nodeId, CancellationToken cancellationToken)
    {
        var started = _clock.GetTimestamp();
        do
        {
            var status = await _control!.StatusAsync(cancellationToken).ConfigureAwait(false);
            var node = status.Nodes.SingleOrDefault(item => item.Node == "host");
            if (node?.NodeId == nodeId && node.State == "running" && status.Bridge.State == "running" &&
                !string.IsNullOrWhiteSpace(status.Bridge.Listen))
            {
                return;
            }

            if (node?.State == "failed" || status.Bridge.State == "failed")
            {
                throw new ConnectHostTransportException("bridge_failed");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        while (_clock.GetElapsedTime(started) < _options.ControlStartupTimeout);

        throw new ConnectHostTransportException("bridge_not_ready");
    }

    private async Task<ConnectTailnetDevice> WaitForDeviceAsync(string nodeId, CancellationToken cancellationToken)
    {
        var started = _clock.GetTimestamp();
        do
        {
            var device = await Provisioner.GetDeviceAsync(nodeId, cancellationToken).ConfigureAwait(false);
            if (device is not null)
            {
                return device;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        while (_clock.GetElapsedTime(started) < DeviceVisibilityTimeout);

        throw new ConnectHostOperationException(ConnectErrorCodes.TailnetUnavailable, "The enrolled host node did not appear in the tailnet API.");
    }

    private async Task CheckPolicyAsync(TailscaleOAuthCredential credential, HostNode host, CancellationToken cancellationToken)
    {
        var bytes = await Provisioner.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        ConnectPolicyVerdict verdict;
        try
        {
            verdict = ConnectPolicyAnalyzer.Analyze(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        if (!verdict.IsSafe)
        {
            _enabled.Replace([]);
            var policyState = verdict.Kind == ConnectPolicyVerdictKind.Unverifiable
                ? ConnectPolicyState.Unverifiable
                : verdict.Reasons.Any(reason => reason.Contains("does not grant", StringComparison.Ordinal))
                    ? ConnectPolicyState.Incomplete
                    : ConnectPolicyState.Unsafe;
            Publish(Status with
            {
                State = ConnectSetupState.NeedsAttention,
                CredentialStored = true,
                ClientIdHint = Hint(credential.ClientId),
                Policy = policyState,
                PolicyReasons = verdict.Reasons,
                HostNode = ConnectHostNodeState.Enrolled,
                HostNodeId = host.NodeId,
                HostBridge = host.Bridge,
                BrokerReachable = true,
                OwnerId = _state.OwnerId,
                LastCheckedAtUtc = _clock.GetUtcNow(),
                ErrorCode = ConnectErrorCodes.PolicyUnsafe
            });
            return;
        }

        _enabled.Replace(_state.EnabledServers);
        await _catalog!.RefreshAsync(cancellationToken).ConfigureAwait(false);
        Publish(Status with
        {
            State = ConnectSetupState.Ready,
            CredentialStored = true,
            ClientIdHint = Hint(credential.ClientId),
            Policy = ConnectPolicyState.Safe,
            PolicyReasons = [],
            HostNode = ConnectHostNodeState.Enrolled,
            HostNodeId = host.NodeId,
            HostBridge = host.Bridge,
            BridgeRunning = true,
            BrokerReachable = true,
            OwnerId = _state.OwnerId,
            LastCheckedAtUtc = _clock.GetUtcNow(),
            ErrorCode = null
        });
    }

    private async Task ReconcileLoopAsync(CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await _operations.WaitAsync(stopping).ConfigureAwait(false);
                try
                {
                    await ReconcileOnceAsync(stopping).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stopping.IsCancellationRequested)
                {
                    // An in-flight request can fail normally after shutdown cancels its token.
                    // Do not let that failure prevent StopAsync from disposing the runtime.
                    if (!stopping.IsCancellationRequested)
                    {
                        _logger.LogWarning("1Salem Connect reconciliation failed: {Error}", exception.Message);
                        Publish(Status with { ErrorCode = MapError(exception), LastCheckedAtUtc = _clock.GetUtcNow() });
                    }
                }
                finally
                {
                    _operations.Release();
                }

                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3001));
                await Task.Delay(_options.ReconcileInterval + jitter, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    internal async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        if (_broker is null || _provisioner is null || _authorization is null || _state.OwnerId is null)
        {
            return;
        }

        await PullRevocationsAsync(cancellationToken).ConfigureAwait(false);
        var memberships = await _broker.GetMembershipsAsync(cancellationToken).ConfigureAwait(false);
        if (Status.State == ConnectSetupState.Ready)
        {
            await ReconcileMembershipsAsync(memberships, cancellationToken).ConfigureAwait(false);
            await ReconcileServersAsync(cancellationToken).ConfigureAwait(false);
        }

        await CleanupRevokedNodesAsync(memberships, cancellationToken).ConfigureAwait(false);
        await _store.SaveStateAsync(_state, cancellationToken).ConfigureAwait(false);
        Publish(Status with { BrokerReachable = true, LastCheckedAtUtc = _clock.GetUtcNow(), ErrorCode = null });
    }

    private async Task PullRevocationsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ConnectBrokerRevocationPage page;
            try
            {
                page = await Broker.GetRevocationsAsync(_state.FeedCursor, cancellationToken).ConfigureAwait(false);
            }
            catch (ConnectOwnerBrokerException exception) when (exception.ErrorCode == "cursor_ahead" && _state.FeedCursor != 0)
            {
                _state = _state with { FeedCursor = 0 };
                await _store.SaveStateAsync(_state, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var item in page.Revocations)
            {
                await ApplyRevocationAsync(item, cancellationToken).ConfigureAwait(false);
            }

            _state = _state with { FeedCursor = page.Cursor };
            await _store.SaveRevocationsAsync(_revocations, cancellationToken).ConfigureAwait(false);
            await _store.SaveStateAsync(_state, cancellationToken).ConfigureAwait(false);
            if (!page.More)
            {
                return;
            }
        }
    }

    private async Task ApplyRevocationAsync(ConnectBrokerRevocation item, CancellationToken cancellationToken)
    {
        if (item.Kind == "device" && item.DeviceId is { } deviceId)
        {
            await Authorization.RevokeDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
            _revocations = _revocations with { Devices = Add(_revocations.Devices, deviceId) };
        }

        if (item.Kind == "membership" && item.MembershipId is { } membershipId)
        {
            await Authorization.RevokeMembershipAsync(membershipId, cancellationToken).ConfigureAwait(false);
            _revocations = _revocations with
            {
                Memberships = Add(_revocations.Memberships, membershipId),
                AuthorizationFloors = item.AuthorizationVersion is { } floor
                    ? UpsertFloor(_revocations.AuthorizationFloors, membershipId, floor)
                    : _revocations.AuthorizationFloors
            };
        }

        if (item.Kind == "session" && item.TicketId is { } ticketId)
        {
            await Authorization.RevokeTicketAsync(ticketId, cancellationToken).ConfigureAwait(false);
            _revocations = _revocations with
            {
                Tickets = AddTicket(_revocations.Tickets, new ConnectTicketRevocation(ticketId, item.At + RevocationSet.TicketRevocationRetention))
            };
        }
    }

    private async Task ReconcileMembershipsAsync(IReadOnlyList<ConnectBrokerMembership> memberships, CancellationToken cancellationToken)
    {
        var local = _state.Memberships.ToDictionary(item => item.MembershipId, StringComparer.Ordinal);
        foreach (var membership in memberships)
        {
            local.TryGetValue(membership.MembershipId, out var kept);
            kept ??= FromBroker(membership, nickname: null);
            kept = kept with
            {
                DeviceId = membership.DeviceId,
                ServerId = membership.ServerId,
                State = membership.State,
                NodeState = membership.NodeState,
                CreatedAt = membership.CreatedAt,
                ApprovedAt = membership.ApprovedAt
            };

            if (membership.State == "approved" && membership.NodeId is null &&
                (kept.EnrollmentPostedAt is null || _clock.GetUtcNow() - kept.EnrollmentPostedAt >= EnrollmentLifetime))
            {
                if (kept.KeyId is { } oldKey)
                {
                    await TryDeleteKeyAsync(oldKey, cancellationToken).ConfigureAwait(false);
                }

                if (!Base64Url.TryDecode(membership.DeviceSpki, out var deviceSpki) ||
                    !Es256.IsValidPublicKey(deviceSpki) ||
                    ConnectKeyIds.ForDevice(deviceSpki) != membership.DeviceId)
                {
                    throw new ConnectHostOperationException(
                        ConnectErrorCodes.BrokerRejected,
                        "The broker returned a membership whose device key did not match its device id.");
                }

                var secret = await Provisioner.CreateFriendAuthKeyAsync(membership.MembershipId, cancellationToken).ConfigureAwait(false);
                try
                {
                    var envelope = EnrollmentCrypto.Encrypt(
                        secret,
                        deviceSpki,
                        new EnrollmentBinding(membership.MembershipId, membership.DeviceId, _state.OwnerId!));
                    await Broker.PutEnrollmentAsync(membership.MembershipId, envelope.ToJson(), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await TryDeleteKeyAsync(secret.KeyId, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                var postedAt = _clock.GetUtcNow();
                kept = kept with
                {
                    KeyId = secret.KeyId,
                    KeyMintedAt = postedAt,
                    EnrollmentPostedAt = postedAt
                };
                local[membership.MembershipId] = kept;
                await PersistMembershipsAsync(local, cancellationToken).ConfigureAwait(false);
            }

            if (membership.State == "approved" && membership.NodeState == "candidate" && membership.NodeId is not null)
            {
                ConnectTailnetDevice? device = null;
                try
                {
                    device = await Provisioner.GetDeviceAsync(membership.NodeId, cancellationToken).ConfigureAwait(false);
                }
                catch (ConnectProvisioningException)
                {
                    local[membership.MembershipId] = kept;
                    continue;
                }

                var decision = ConnectNodeVerifier.Evaluate(
                    membership,
                    memberships,
                    kept,
                    device,
                    _state.HostNodeId!,
                    _clock.GetUtcNow());
                if (decision.Kind == ConnectNodeVerificationKind.Confirm)
                {
                    await Broker.ConfirmNodeAsync(membership.MembershipId, membership.NodeId, cancellationToken).ConfigureAwait(false);
                    kept = kept with { ConfirmedNodeId = membership.NodeId, NodeState = "confirmed" };
                }
                else if (decision.Kind == ConnectNodeVerificationKind.Reject)
                {
                    await Broker.RejectNodeAsync(membership.MembershipId, membership.NodeId, cancellationToken).ConfigureAwait(false);
                    kept = kept with { NodeState = "rejected" };
                }
            }

            if (membership.NodeState == "confirmed" && membership.NodeId is not null)
            {
                if (kept.KeyId is { } keyId)
                {
                    await TryDeleteKeyAsync(keyId, cancellationToken).ConfigureAwait(false);
                }

                kept = kept with { KeyId = null, ConfirmedNodeId = membership.NodeId, NodeState = "confirmed" };
            }

            local[membership.MembershipId] = kept;
        }

        _state = _state with { Memberships = local.Values.OrderBy(item => item.CreatedAt).ToArray() };
    }

    private async Task PersistMembershipsAsync(
        IReadOnlyDictionary<string, ConnectMembershipState> memberships,
        CancellationToken cancellationToken)
    {
        _state = _state with { Memberships = memberships.Values.OrderBy(item => item.CreatedAt).ToArray() };
        await _store.SaveStateAsync(_state, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileServersAsync(CancellationToken cancellationToken)
    {
        var definitions = await _servers.ListAsync(cancellationToken).ConfigureAwait(false);
        var registered = _state.Servers.ToDictionary(server => server.ServerId);
        foreach (var serverId in _enabled.Snapshot())
        {
            var definition = definitions.SingleOrDefault(server => server.Id == serverId);
            if (definition is null)
            {
                _enabled.Disable(serverId);
                continue;
            }

            if (!registered.TryGetValue(serverId, out var existing) || existing.Label != definition.Name || existing.HostBridge != _state.HostBridge)
            {
                await Broker.PutServerAsync(serverId, definition.Name, _state.HostBridge!, cancellationToken).ConfigureAwait(false);
                registered[serverId] = new ConnectRegisteredServerState(serverId, definition.Name, _state.HostBridge!);
            }
        }

        _state = _state with
        {
            EnabledServers = _enabled.Snapshot(),
            Servers = registered.Values.Where(server => _enabled.IsEnabled(server.ServerId)).ToArray()
        };
    }

    private async Task CleanupRevokedNodesAsync(IReadOnlyList<ConnectBrokerMembership> liveMemberships, CancellationToken cancellationToken)
    {
        var local = _state.Memberships.ToDictionary(item => item.MembershipId, StringComparer.Ordinal);
        foreach (var membershipId in _revocations.Memberships)
        {
            if (!local.TryGetValue(membershipId, out var revoked) || revoked.ConfirmedNodeId is not { } nodeId)
            {
                continue;
            }

            if (liveMemberships.Any(member => member.State == "approved" && member.NodeId == nodeId &&
                    member.NodeState is "candidate" or "confirmed"))
            {
                continue;
            }

            await Provisioner.DeleteFriendDeviceAsync(nodeId, _state.HostNodeId!, cancellationToken).ConfigureAwait(false);
            local[membershipId] = revoked with { ConfirmedNodeId = null };
        }

        _state = _state with { Memberships = local.Values.ToArray() };
    }

    private async Task TryDeleteKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        try
        {
            await Provisioner.DeleteAuthKeyAsync(keyId, cancellationToken).ConfigureAwait(false);
        }
        catch (ConnectProvisioningException exception)
        {
            _logger.LogWarning("1Salem Connect could not clean up an enrollment key: {Error}", exception.Message);
        }
    }

    private async Task StopLoopAsync()
    {
        var stopping = Interlocked.Exchange(ref _runtimeStopping, null);
        var loop = Interlocked.Exchange(ref _reconcileLoop, null);
        stopping?.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        stopping?.Dispose();
    }

    private async Task DisposeRuntimeAsync()
    {
        _enabled.Replace([]);
        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning("1Salem Connect host transport did not stop cleanly: {Error}", exception.Message);
            }
        }

        if (_authorization is not null)
        {
            try
            {
                await _authorization.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning("1Salem Connect authorization pipe did not stop cleanly: {Error}", exception.Message);
            }
        }

        _broker?.Dispose();
        _provisioner?.Dispose();
        _identity?.Dispose();
        _supervisor = null;
        _authorization = null;
        _broker = null;
        _provisioner = null;
        _identity = null;
        _control = null;
        _catalog = null;
    }

    private void Publish(ConnectStatusResponse status) => Volatile.Write(ref _status, status);

    private void PublishAttention(ConnectPolicyState policy, string code, string reason) =>
        Publish(Status with
        {
            State = ConnectSetupState.NeedsAttention,
            Policy = policy,
            PolicyReasons = [reason],
            ErrorCode = code,
            LastCheckedAtUtc = _clock.GetUtcNow()
        });

    private void Fail(string code, Exception exception)
    {
        Publish(Status with
        {
            State = ConnectSetupState.Error,
            ErrorCode = code,
            LastCheckedAtUtc = _clock.GetUtcNow()
        });
        _logger.LogError("1Salem Connect host failed ({Code}): {Error}", code, exception.Message);
    }

    private static string MapError(Exception exception) => exception switch
    {
        ConnectOwnerBrokerException => ConnectErrorCodes.BrokerUnavailable,
        ConnectProvisioningException => ConnectErrorCodes.TailnetUnavailable,
        ConnectPipeNameInUseException => ConnectErrorCodes.Busy,
        ConnectHostOperationException operation => operation.ErrorCode,
        _ => ConnectErrorCodes.NotReady
    };

    private static string Hint(string clientId) => clientId.Length <= 8 ? clientId : "…" + clientId[^8..];

    private static string Hostname()
    {
        var cleaned = new string(Environment.MachineName.ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray()).Trim('-');
        cleaned = cleaned.Length == 0 ? "windows-pc" : cleaned;
        var hostname = "1salem-" + cleaned;
        return hostname[..Math.Min(63, hostname.Length)].TrimEnd('-');
    }

    private static string? ParseTailnetIPv4(string text) =>
        IPAddress.TryParse(text, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        address.GetAddressBytes() is var bytes && bytes[0] == 100 && bytes[1] is >= 64 and <= 127
            ? address.ToString()
            : null;

    private static ConnectStatusResponse EmptyStatus() => new(
        ConnectSetupState.NotSetUp,
        false,
        null,
        ConnectPolicyState.Unknown,
        [],
        ConnectHostNodeState.NotEnrolled,
        null,
        null,
        false,
        false,
        0,
        false,
        null,
        null,
        null);

    private static ConnectMembershipState FromBroker(ConnectBrokerMembership membership, string? nickname) => new(
        membership.MembershipId,
        membership.DeviceId,
        membership.ServerId,
        null,
        null,
        null,
        membership.NodeState == "confirmed" ? membership.NodeId : null,
        nickname,
        membership.State,
        membership.CreatedAt,
        membership.ApprovedAt,
        membership.NodeState);

    private static IReadOnlyList<string> Add(IReadOnlyList<string> values, string value) =>
        values.Contains(value, StringComparer.Ordinal) ? values : [.. values, value];

    private static IReadOnlyList<ConnectTicketRevocation> AddTicket(
        IReadOnlyList<ConnectTicketRevocation> values,
        ConnectTicketRevocation value) =>
        values.Any(item => item.TicketId == value.TicketId) ? values : [.. values, value];

    private static IReadOnlyList<ConnectAuthorizationFloor> UpsertFloor(
        IReadOnlyList<ConnectAuthorizationFloor> values,
        string membershipId,
        long floor) =>
        [.. values.Where(value => value.MembershipId != membershipId), new ConnectAuthorizationFloor(membershipId, floor)];

    private sealed record HostNode(string NodeId, IReadOnlyList<string> Addresses, string Bridge);
}

public sealed class ConnectHostOperationException : InvalidOperationException
{
    public ConnectHostOperationException(string errorCode, string? message = null)
        : base(message ?? "1Salem Connect is not ready for that operation.") => ErrorCode = errorCode;
    public string ErrorCode { get; }
}

public sealed class ConnectTailnetLockException(string message) : Exception(message);
