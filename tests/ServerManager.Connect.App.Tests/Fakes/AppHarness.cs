using System.Security.Cryptography;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Identity;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.ViewModels;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Enrollment;
using ServerManager.Connect.Core.Identity;

namespace ServerManager.Connect.App.Tests.Fakes;

/// <summary>The real view models wired to fakes: an in-memory broker and transport, and a hand-set clock.</summary>
internal sealed class AppHarness : IDisposable
{
    public const string ServerLabel = "Salem's world";

    public AppHarness(bool configured = true)
    {
        Log = new DiagnosticsLog(Clock);
        Broker.SessionExpiresAt = Clock.UtcNow.AddMinutes(10);
        Context = new ConnectAppContext
        {
            Settings = configured
                ? new ConnectAppSettings
                {
                    BrokerUrl = new Uri("https://broker.test/"),
                    TransportExecutablePath = @"C:\1salem-connect-test-missing\1Salem.Connect.Transport.exe"
                }
                : ConnectAppSettings.Unconfigured(@"C:\1salem-connect-test-missing"),
            Services = configured ? new ConnectServices(Broker, Identity, Process) : null,
            Transport = Transport,
            Clock = Clock,
            Log = Log,
            Clipboard = Clipboard
        };
        Main = new MainViewModel(Context);
    }

    public FakeBroker Broker { get; } = new();

    public FakeTransport Transport { get; } = new();

    public FakeTransportProcess Process { get; } = new();

    public ManualClock Clock { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public DeviceIdentity Identity { get; } = TestIds.NewDevice();

    public string OwnerId { get; } = TestIds.NewOwnerId();

    public DiagnosticsLog Log { get; }

    public ConnectAppContext Context { get; }

    public MainViewModel Main { get; }

    public Membership AddMembership(MembershipState state, string? nodeId = null, char idLetter = 'a')
    {
        var membership = new Membership(
            TestIds.MembershipId(idLetter),
            OwnerId,
            "5b0f7f2e-3c1a-4d57-9a7e-2f1d8c0b6a41",
            ServerLabel,
            state,
            nodeId);
        Broker.Memberships.Add(membership);
        return membership;
    }

    /// <summary>An enrollment blob exactly as the owner's Agent would post it for <paramref name="membership"/>.</summary>
    public EnrollmentPackage EnrollmentFor(Membership membership, string authKey, string keyId)
    {
        var envelope = EnrollmentCrypto.Encrypt(
            new EnrollmentSecret(authKey, keyId),
            Base64Url.Decode(Identity.PublicKeySpkiBase64Url),
            new EnrollmentBinding(membership.MembershipId, Identity.DeviceId, membership.OwnerId));
        return new EnrollmentPackage(membership.MembershipId, membership.OwnerId, envelope.ToJson());
    }

    public void Dispose()
    {
        Main.Shutdown();
        Identity.Dispose();
    }
}

internal static class TestIds
{
    public static DeviceIdentity NewDevice() =>
        new(ConnectIdentity.Generate(ConnectIdentityKind.Device), TimeProvider.System);

    public static string NewOwnerId()
    {
        using var owner = ConnectIdentity.Generate(ConnectIdentityKind.Owner);
        return owner.KeyId;
    }

    public static string MembershipId(char letter = 'a') => "mem_" + new string(letter, 26);

    public static string NewInviteSecret() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
}
