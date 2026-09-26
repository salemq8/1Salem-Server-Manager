using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Identity;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// Atomic, protected persistence for the owner's reconciliation state and fail-closed local
/// revocations. The models deliberately have no property for an invite secret.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectStateStore
{
    private const int FormatVersion = 1;
    private const int MaxFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly ConnectOwnerPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConnectStateStore(ConnectOwnerPaths paths) => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public Task<ConnectOwnerState> LoadStateAsync(CancellationToken cancellationToken) =>
        LoadAsync(_paths.StateFile, static () => new ConnectOwnerState(), ValidateState, cancellationToken);

    public Task SaveStateAsync(ConnectOwnerState state, CancellationToken cancellationToken) =>
        SaveAsync(_paths.StateFile, state, ValidateState, cancellationToken);

    public Task<ConnectRevocationState> LoadRevocationsAsync(CancellationToken cancellationToken) =>
        LoadAsync(_paths.RevocationsFile, static () => new ConnectRevocationState(), ValidateRevocations, cancellationToken);

    public Task SaveRevocationsAsync(ConnectRevocationState revocations, CancellationToken cancellationToken) =>
        SaveAsync(_paths.RevocationsFile, revocations, ValidateRevocations, cancellationToken);

    private async Task<T> LoadAsync<T>(string path, Func<T> empty, Action<T> validate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return empty();
            }

            ConnectProtectedDirectory.Verify(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length is 0 or > MaxFileBytes)
            {
                throw new ConnectStateUnavailableException();
            }

            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                throw new ConnectStateUnavailableException();
            validate(value);
            return value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new ConnectStateUnavailableException(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync<T>(string path, T value, Action<T> validate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        validate(value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConnectProtectedDirectory.Ensure(Path.GetDirectoryName(path)!);
            var content = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            if (content.Length > MaxFileBytes)
            {
                throw new ConnectStateUnavailableException();
            }

            ConnectOAuthCredentialStore.AtomicWrite(path, content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConnectStateUnavailableException(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateState(ConnectOwnerState state)
    {
        if (state.Version != FormatVersion || state.FeedCursor < 0 ||
            (state.OwnerId is not null && !ConnectKeyIds.IsOwnerId(state.OwnerId)) ||
            state.EnabledServers.Any(id => id == Guid.Empty) ||
            state.Servers.Any(server => server.ServerId == Guid.Empty || string.IsNullOrWhiteSpace(server.Label)) ||
            state.Invites.Any(invite => string.IsNullOrWhiteSpace(invite.InviteId) || invite.ServerId == Guid.Empty) ||
            state.Memberships.Any(member => string.IsNullOrWhiteSpace(member.MembershipId) || member.ServerId == Guid.Empty))
        {
            throw new ArgumentException("Invalid 1Salem Connect owner state.", nameof(state));
        }
    }

    private static void ValidateRevocations(ConnectRevocationState state)
    {
        if (state.Version != FormatVersion ||
            state.Devices.Any(string.IsNullOrWhiteSpace) ||
            state.Memberships.Any(string.IsNullOrWhiteSpace) ||
            state.Tickets.Any(ticket => string.IsNullOrWhiteSpace(ticket.TicketId)) ||
            state.AuthorizationFloors.Any(entry => string.IsNullOrWhiteSpace(entry.MembershipId) || entry.MinimumVersion < 0))
        {
            throw new ArgumentException("Invalid 1Salem Connect revocation state.", nameof(state));
        }
    }
}

public sealed record ConnectOwnerState
{
    public int Version { get; init; } = 1;
    public string? OwnerId { get; init; }
    public string? HostNodeId { get; init; }
    public IReadOnlyList<string> HostAddresses { get; init; } = [];
    public string? HostBridge { get; init; }
    public long FeedCursor { get; init; }
    public IReadOnlyList<Guid> EnabledServers { get; init; } = [];
    public IReadOnlyList<ConnectRegisteredServerState> Servers { get; init; } = [];
    public IReadOnlyList<ConnectInviteState> Invites { get; init; } = [];
    public IReadOnlyList<ConnectMembershipState> Memberships { get; init; } = [];
}

public sealed record ConnectRegisteredServerState(Guid ServerId, string Label, string HostBridge);

/// <summary>Invite metadata only. The one-time secret is intentionally not representable.</summary>
public sealed record ConnectInviteState(string InviteId, Guid ServerId, DateTimeOffset ExpiresAt, string State);

public sealed record ConnectMembershipState(
    string MembershipId,
    string DeviceId,
    Guid ServerId,
    string? KeyId,
    DateTimeOffset? KeyMintedAt,
    DateTimeOffset? EnrollmentPostedAt,
    string? ConfirmedNodeId,
    string? LocalNickname);

public sealed record ConnectRevocationState
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<string> Devices { get; init; } = [];
    public IReadOnlyList<string> Memberships { get; init; } = [];
    public IReadOnlyList<ConnectTicketRevocation> Tickets { get; init; } = [];
    public IReadOnlyList<ConnectAuthorizationFloor> AuthorizationFloors { get; init; } = [];
}

public sealed record ConnectTicketRevocation(string TicketId, DateTimeOffset ExpiresAt);

public sealed record ConnectAuthorizationFloor(string MembershipId, long MinimumVersion);

public sealed class ConnectStateUnavailableException : Exception
{
    private const string SafeMessage = "The saved 1Salem Connect host state cannot be trusted or read.";
    public ConnectStateUnavailableException() : base(SafeMessage) { }
    public ConnectStateUnavailableException(Exception innerException) : base(SafeMessage, innerException) { }
}
