using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Tickets;

namespace ServerManager.Connect.Tests.Support;

/// <summary>
/// Plays the broker for tests: throwaway in-memory keys, a pinned key set, a catalog with one
/// Connect-enabled Minecraft server, and tickets built from editable header and claim objects so
/// each test can break exactly one thing.
/// </summary>
internal sealed class TestTicketIssuer : IDisposable
{
    public const string KeyId = "test-key-1";
    public const string MembershipId = "mem_test0001";
    public const string NodeId = "nTestFriend1CNTRL";
    public const int MinecraftPort = 25565;
    public const long StartUnix = 1_767_225_600;

    public static readonly Guid MinecraftServer = Guid.Parse("6f1c1d7e-3b1e-4a55-9d0e-2f9a1b7c4d11");
    public static readonly Guid PalworldServer = Guid.Parse("0b8e5a3c-6d2f-4c7a-8e19-5a4d3c2b1a00");
    public static readonly Guid DisabledServer = Guid.Parse("9a7b6c5d-4e3f-4a1b-8c9d-0e1f2a3b4c5d");

    public TestTicketIssuer()
    {
        BrokerKey = Es256.CreateKey();
        SessionKey = Es256.CreateKey();
        using var owner = Es256.CreateKey();
        using var device = Es256.CreateKey();
        OwnerId = ConnectKeyIds.ForOwner(Es256.ExportPublicKey(owner));
        DeviceId = ConnectKeyIds.ForDevice(Es256.ExportPublicKey(device));
        KeySet = new TicketKeySet([new TicketSigningKey(KeyId, Es256.Algorithm, Es256.ExportPublicKey(BrokerKey))]);
        Catalog = new InMemoryCatalog(
            new ConnectServerEntry(MinecraftServer, ConnectGameKind.Minecraft, ConnectProtocol.Tcp, MinecraftPort, connectEnabled: true),
            new ConnectServerEntry(PalworldServer, ConnectGameKind.Palworld, ConnectProtocol.Udp, 8211, connectEnabled: true),
            new ConnectServerEntry(DisabledServer, ConnectGameKind.Minecraft, ConnectProtocol.Tcp, 25566, connectEnabled: false));
        Clock = new FixedTimeProvider(StartUnix);
        Revocations = new RevocationSet(Clock);
    }

    public ECDsa BrokerKey { get; }

    public ECDsa SessionKey { get; }

    public string OwnerId { get; }

    public string DeviceId { get; }

    public TicketKeySet KeySet { get; }

    public InMemoryCatalog Catalog { get; }

    public FixedTimeProvider Clock { get; }

    public RevocationSet Revocations { get; }

    public TicketVerifier CreateVerifier() => new(KeySet, OwnerId, Catalog, Revocations, Clock);

    public HostAuthorizer CreateAuthorizer() => new(CreateVerifier(), new ReplayCache(Clock));

    public JsonObject Header() => new()
    {
        ["alg"] = "ES256",
        ["typ"] = "1salem-ticket+jwt",
        ["kid"] = KeyId
    };

    /// <summary>A valid ticket's claims, issued 10 s ago with the broker's usual 600 s lifetime.</summary>
    public JsonObject Claims()
    {
        var issuedAt = Clock.UnixSeconds - 10;
        return new JsonObject
        {
            ["iss"] = "1salem-connect-broker",
            ["aud"] = OwnerId,
            ["jti"] = Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
            ["sub"] = DeviceId,
            ["mid"] = MembershipId,
            ["sid"] = MinecraftServer.ToString("D"),
            ["proto"] = "tcp",
            ["nid"] = NodeId,
            ["skp"] = Base64Url.Encode(Es256.ExportPublicKey(SessionKey)),
            ["hb"] = "100.64.0.7:7780",
            ["av"] = 1,
            ["iat"] = issuedAt,
            ["nbf"] = issuedAt,
            ["exp"] = issuedAt + 600
        };
    }

    public string Issue(Action<JsonObject>? editClaims = null) => Issue(Header(), Claims(editClaims));

    public JsonObject Claims(Action<JsonObject>? edit)
    {
        var claims = Claims();
        edit?.Invoke(claims);
        return claims;
    }

    public string Issue(JsonObject header, JsonObject claims) =>
        Issue(header.ToJsonString(), claims.ToJsonString(), input => Es256.Sign(BrokerKey, input));

    /// <summary>Full control over every byte, for tampering and malformed-signature cases.</summary>
    public static string Issue(string headerJson, string claimsJson, Func<byte[], byte[]> sign)
    {
        var signingInput = Base64Url.Encode(Encoding.UTF8.GetBytes(headerJson)) + "." +
            Base64Url.Encode(Encoding.UTF8.GetBytes(claimsJson));
        return signingInput + "." + Base64Url.Encode(sign(Encoding.ASCII.GetBytes(signingInput)));
    }

    public void Dispose()
    {
        BrokerKey.Dispose();
        SessionKey.Dispose();
    }
}

internal sealed class InMemoryCatalog : IConnectServerCatalog
{
    private readonly Dictionary<Guid, ConnectServerEntry> _entries;

    public InMemoryCatalog(params ConnectServerEntry[] entries)
    {
        _entries = entries.ToDictionary(entry => entry.ServerId);
    }

    public ConnectServerEntry? Find(Guid serverId) => _entries.GetValueOrDefault(serverId);
}
