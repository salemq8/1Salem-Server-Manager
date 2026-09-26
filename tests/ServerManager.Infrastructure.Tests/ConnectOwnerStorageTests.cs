using System.Runtime.Versioning;
using ServerManager.Connect.Core.Identity;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConnectOwnerStorageTests : IDisposable
{
    private const string SecretMarker = "credential-marker-never-plaintext";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "1salem-owner-state-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public void Paths_AreAllUnderTheConnectRoot_AndDirectoriesAreProtected()
    {
        var paths = new ConnectOwnerPaths(_dataRoot);
        paths.EnsureDirectories();

        Assert.Equal(Path.Combine(_dataRoot, "connect"), paths.Root);
        Assert.Equal(Path.Combine(paths.Root, "identity"), paths.IdentityDirectory);
        Assert.Equal(Path.Combine(paths.Root, "host-transport"), paths.HostTransportDirectory);
        Assert.All(
            new[] { paths.StateFile, paths.RevocationsFile, paths.TicketKeysFile, paths.OAuthCredentialFile },
            path => Assert.Equal(paths.Root, Path.GetDirectoryName(path)));
        ConnectProtectedDirectory.Verify(paths.Root);
        ConnectProtectedDirectory.Verify(paths.IdentityDirectory);
        ConnectProtectedDirectory.Verify(paths.HostTransportDirectory);
    }

    [Fact]
    public void OAuthCredential_IsDpapiProtected_RoundTrips_Deletes_AndNeverPrintsTheSecret()
    {
        var paths = new ConnectOwnerPaths(_dataRoot);
        var store = new ConnectOAuthCredentialStore(paths);
        var credential = new TailscaleOAuthCredential("kTestClient1", "tskey-client-kTestClient1-" + SecretMarker);

        store.Save(credential);
        var disk = File.ReadAllText(paths.OAuthCredentialFile);
        var loaded = store.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal(credential.ClientId, loaded.ClientId);
        Assert.DoesNotContain(SecretMarker, disk, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMarker, loaded.ToString(), StringComparison.Ordinal);
        store.Delete();
        Assert.False(store.Exists);
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public async Task StateAndRevocations_RoundTripAtomically_WithoutInviteSecrets()
    {
        var paths = new ConnectOwnerPaths(_dataRoot);
        var store = new ConnectStateStore(paths);
        var serverId = Guid.NewGuid();
        var ownerId = ConnectTestBroker.NewOwnerId();
        var state = new ConnectOwnerState
        {
            OwnerId = ownerId,
            HostNodeId = "nHost1",
            HostAddresses = ["100.100.1.2"],
            HostBridge = "100.100.1.2:7780",
            FeedCursor = 22,
            EnabledServers = [serverId],
            Servers = [new ConnectRegisteredServerState(serverId, "Home", "100.100.1.2:7780")],
            Invites = [new ConnectInviteState("inv_test", serverId, DateTimeOffset.UtcNow.AddHours(1), "active")],
            Memberships = [new ConnectMembershipState("mem_test", ConnectTestBroker.NewDeviceId(), serverId, "key1", DateTimeOffset.UtcNow, null, null, "Friend")]
        };
        var revocations = new ConnectRevocationState
        {
            Devices = [ConnectTestBroker.NewDeviceId()],
            Memberships = ["mem_revoked"],
            Tickets = [new ConnectTicketRevocation("ticket", DateTimeOffset.UtcNow.AddMinutes(15))],
            AuthorizationFloors = [new ConnectAuthorizationFloor("mem_revoked", 3)]
        };

        await store.SaveStateAsync(state, CancellationToken.None);
        await store.SaveRevocationsAsync(revocations, CancellationToken.None);

        var loadedState = await store.LoadStateAsync(CancellationToken.None);
        var loadedRevocations = await store.LoadRevocationsAsync(CancellationToken.None);
        Assert.Equal(state.OwnerId, loadedState.OwnerId);
        Assert.Equal(state.HostAddresses, loadedState.HostAddresses);
        Assert.Equal(state.EnabledServers, loadedState.EnabledServers);
        Assert.Equal(state.Invites, loadedState.Invites);
        Assert.Equal(revocations.Devices, loadedRevocations.Devices);
        Assert.Equal(revocations.Tickets, loadedRevocations.Tickets);
        Assert.Equal(revocations.AuthorizationFloors, loadedRevocations.AuthorizationFloors);
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(paths.StateFile), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(paths.Root, "*.tmp"));
    }

    [Fact]
    public async Task CorruptState_IsRefused_NotSilentlyReplaced()
    {
        var paths = new ConnectOwnerPaths(_dataRoot);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.StateFile, "not json");

        await Assert.ThrowsAsync<ConnectStateUnavailableException>(() =>
            new ConnectStateStore(paths).LoadStateAsync(CancellationToken.None));
        Assert.Equal("not json", await File.ReadAllTextAsync(paths.StateFile));
    }
}
