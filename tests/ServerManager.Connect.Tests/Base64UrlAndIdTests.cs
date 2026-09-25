using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Tickets;

namespace ServerManager.Connect.Tests;

public sealed class Base64UrlAndIdTests
{
    [Theory]
    [InlineData("AQ==")]
    [InlineData("AQ=")]
    [InlineData("A+8")]
    [InlineData("A/8")]
    [InlineData("AR")]
    [InlineData("A Q")]
    [InlineData("A")]
    public void NonCanonicalSpellings_AreRefused(string value) =>
        Assert.False(Base64Url.TryDecode(value, out _));

    [Fact]
    public void CanonicalSpelling_RoundTrips()
    {
        var bytes = new byte[] { 0xFB, 0xFF, 0x01 };

        Assert.Equal("-_8B", Base64Url.Encode(bytes));
        Assert.Equal(bytes, Base64Url.Decode("-_8B"));
    }

    [Fact]
    public void Ids_DeriveFromTheKeyAndHaveTheContractShape()
    {
        using var key = Es256.CreateKey();
        var spki = Es256.ExportPublicKey(key);

        var owner = ConnectKeyIds.ForOwner(spki);
        var device = ConnectKeyIds.ForDevice(spki);

        Assert.Matches("^own_[a-z2-7]{26}$", owner);
        Assert.Matches("^dev_[a-z2-7]{26}$", device);
        Assert.Equal(owner[4..], device[4..]);
        Assert.Equal(owner, ConnectKeyIds.ForOwner(spki));
        Assert.False(ConnectKeyIds.IsOwnerId(device));
        // The broker rejects any other case, so an uppercased id must not pass as valid here.
        Assert.False(ConnectKeyIds.IsOwnerId("own_" + owner[4..].ToUpperInvariant()));
    }

    [Fact]
    public void Ids_UseStandardBase32OfSha256()
    {
        // RFC 4648 base32 takes the top five bits of the first digest byte first, which pins both
        // the alphabet and the bit order that the broker and the Go side must reproduce.
        using var key = Es256.CreateKey();
        var spki = Es256.ExportPublicKey(key);
        var digest = System.Security.Cryptography.SHA256.HashData(spki);

        var expectedFirst = "abcdefghijklmnopqrstuvwxyz234567"[digest[0] >> 3];

        Assert.Equal(expectedFirst, ConnectKeyIds.ForDevice(spki)[4]);
    }

    [Fact]
    public void KeySet_PinsEs256AndRejectsDuplicates()
    {
        using var key = Es256.CreateKey();
        var spki = Base64Url.Encode(Es256.ExportPublicKey(key));

        Assert.Throws<FormatException>(() => TicketKeySet.Parse($"{{\"keys\":[{{\"kid\":\"a\",\"alg\":\"RS256\",\"spki\":\"{spki}\"}}]}}"));
        Assert.Throws<FormatException>(() => TicketKeySet.Parse(
            $"{{\"keys\":[{{\"kid\":\"a\",\"alg\":\"ES256\",\"spki\":\"{spki}\"}},{{\"kid\":\"a\",\"alg\":\"ES256\",\"spki\":\"{spki}\"}}]}}"));
        Assert.Throws<FormatException>(() => TicketKeySet.Parse("{\"keys\":[]}"));

        var parsed = TicketKeySet.Parse($"{{\"keys\":[{{\"kid\":\"a\",\"alg\":\"ES256\",\"spki\":\"{spki}\"}}]}}");
        Assert.Contains(spki, parsed.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Es256_RefusesKeysOffTheCurveOrOnOtherCurves()
    {
        using var key = Es256.CreateKey();
        var spki = Es256.ExportPublicKey(key);
        var offCurve = (byte[])spki.Clone();
        offCurve[^1] ^= 0x01;
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);

        Assert.True(Es256.IsValidPublicKey(spki));
        Assert.False(Es256.IsValidPublicKey(offCurve));
        Assert.False(Es256.IsValidPublicKey(p384.ExportSubjectPublicKeyInfo()));
    }
}
