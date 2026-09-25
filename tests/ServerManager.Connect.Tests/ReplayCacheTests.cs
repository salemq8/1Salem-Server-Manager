using System.Security.Cryptography;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Connect.Tests.Support;

namespace ServerManager.Connect.Tests;

/// <summary>
/// The replay cache's bounds: an exact replay is refused, one ticket cannot fill the cache for
/// everyone, a full cache refuses instead of evicting, and a revoked friend's entries go.
/// </summary>
public sealed class ReplayCacheTests
{
    private const string Device = "dev_aaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherDevice = "dev_bbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly FixedTimeProvider _clock = new(TestTicketIssuer.StartUnix);

    [Fact]
    public void APairIsRegisteredOnce_AndAReplayIsReported()
    {
        var cache = new ReplayCache(_clock);
        var ticket = Claims();

        Assert.Equal(ReplayCheckResult.Registered, cache.TryRegister(ticket, "n1"));
        Assert.Equal(ReplayCheckResult.Replayed, cache.TryRegister(ticket, "n1"));
        Assert.Equal(ReplayCheckResult.Registered, cache.TryRegister(ticket, "n2"));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void OneTicket_CannotRegisterMoreThanItsLimit_AndOtherTicketsAreUnaffected()
    {
        var cache = new ReplayCache(_clock, perTicketLimit: 2);
        var greedy = Claims();
        var other = Claims();

        Assert.Equal(ReplayCheckResult.Registered, cache.TryRegister(greedy, "n1"));
        Assert.Equal(ReplayCheckResult.Registered, cache.TryRegister(greedy, "n2"));
        Assert.Equal(ReplayCheckResult.TicketLimitReached, cache.TryRegister(greedy, "n3"));

        // An exact replay is still reported as one, and the refusal did not take a slot.
        Assert.Equal(ReplayCheckResult.Replayed, cache.TryRegister(greedy, "n1"));
        Assert.Equal(2, cache.Count);
        Assert.Equal(ReplayCheckResult.Registered, cache.TryRegister(other, "n1"));
    }

    [Fact]
    public void AFullCache_RefusesInsteadOfEvicting_UntilEntriesExpireWithTheirTicket()
    {
        var cache = new ReplayCache(_clock, capacity: 1);
        var ticket = Claims(expiresIn: TimeSpan.FromMinutes(10));

        Assert.Equal(ReplayCheckResult.Registered, cache.TryRegister(ticket, "n1"));
        Assert.Equal(ReplayCheckResult.CapacityExceeded, cache.TryRegister(ticket, "n2"));
        Assert.Equal(ReplayCheckResult.CapacityExceeded, cache.TryRegister(Claims(), "n1"));

        // Kept through exp plus the verifier's skew, the last moment the ticket could verify.
        _clock.Advance(TimeSpan.FromMinutes(10) + TicketVerifier.ClockSkew);
        Assert.Equal(ReplayCheckResult.Replayed, cache.TryRegister(ticket, "n1"));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, cache.Count);
        Assert.Equal(ReplayCheckResult.Registered, cache.TryRegister(Claims(), "n2"));
    }

    [Fact]
    public void Revocations_ReleaseOnlyTheRevokedFriendsEntries()
    {
        var cache = new ReplayCache(_clock);
        var revokedTicket = Claims();
        var sameDevice = Claims();
        var otherMembership = Claims(device: OtherDevice, membership: "mem_other");
        cache.TryRegister(revokedTicket, "n1");
        cache.TryRegister(revokedTicket, "n2");
        cache.TryRegister(sameDevice, "n1");
        cache.TryRegister(otherMembership, "n1");

        cache.ForgetTicket(revokedTicket.TicketId);
        Assert.Equal(2, cache.Count);

        cache.ForgetDevice(Device);
        Assert.Equal(1, cache.Count);

        cache.ForgetMembership("mem_other");
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void AForgottenTicketsExpiry_DoesNotDisturbTheCount()
    {
        var cache = new ReplayCache(_clock);
        var forgotten = Claims(expiresIn: TimeSpan.FromMinutes(1));
        var kept = Claims(expiresIn: TimeSpan.FromMinutes(10));
        cache.TryRegister(forgotten, "n1");
        cache.TryRegister(kept, "n1");

        cache.ForgetTicket(forgotten.TicketId);
        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, cache.Count);
        Assert.Equal(ReplayCheckResult.Replayed, cache.TryRegister(kept, "n1"));
    }

    private TicketClaims Claims(string device = Device, string membership = "mem_test0001", TimeSpan? expiresIn = null)
    {
        var now = _clock.UnixSeconds;
        return new TicketClaims
        {
            Issuer = TicketVerifier.ExpectedIssuer,
            Audience = "own_aaaaaaaaaaaaaaaaaaaaaaaaaa",
            TicketId = Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
            DeviceId = device,
            MembershipId = membership,
            ServerId = TestTicketIssuer.MinecraftServer,
            Protocol = TicketVerifier.SupportedProtocol,
            NodeId = TestTicketIssuer.NodeId,
            SessionPublicKey = "unused-by-the-cache",
            HostBridge = "100.64.0.7:7780",
            AuthorizationVersion = 1,
            IssuedAt = now,
            NotBefore = now,
            ExpiresAt = now + (long)(expiresIn ?? TimeSpan.FromMinutes(10)).TotalSeconds
        };
    }
}
