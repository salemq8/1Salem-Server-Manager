using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Connect.Tests.Support;

namespace ServerManager.Connect.Tests;

public sealed class TicketVerifierTests : IDisposable
{
    private readonly TestTicketIssuer _issuer = new();

    public void Dispose() => _issuer.Dispose();

    [Fact]
    public void GoodTicket_IsAccepted()
    {
        var result = Verify(_issuer.Issue());

        Assert.True(result.IsValid, result.Reason.ToString());
        Assert.Equal(AccessDenialReason.None, result.Reason);
        Assert.Equal(_issuer.DeviceId, result.Claims!.DeviceId);
        Assert.Equal(TestTicketIssuer.MinecraftServer, result.Claims.ServerId);
        Assert.Equal(TestTicketIssuer.MinecraftPort, result.Server!.LocalPort);
    }

    [Fact]
    public void TamperedPayload_FailsSignature()
    {
        var parts = _issuer.Issue().Split('.');
        var forged = _issuer.Claims(claims => claims["av"] = 99).ToJsonString();
        var ticket = $"{parts[0]}.{Base64Url.Encode(Encoding.UTF8.GetBytes(forged))}.{parts[2]}";

        AssertDenied(AccessDenialReason.InvalidSignature, ticket);
    }

    [Fact]
    public void TamperedHeader_FailsSignature()
    {
        var parts = _issuer.Issue().Split('.');

        // Same members, different bytes: every header check passes, the signature does not.
        var header = $"{{\"kid\":\"{TestTicketIssuer.KeyId}\",\"typ\":\"1salem-ticket+jwt\",\"alg\":\"ES256\"}}";
        var ticket = $"{Base64Url.Encode(Encoding.UTF8.GetBytes(header))}.{parts[1]}.{parts[2]}";

        AssertDenied(AccessDenialReason.InvalidSignature, ticket);
    }

    [Fact]
    public void UnknownKid_IsRefused()
    {
        var header = _issuer.Header();
        header["kid"] = "someone-elses-key";

        AssertDenied(AccessDenialReason.UnknownSigningKey, _issuer.Issue(header, _issuer.Claims()));
    }

    [Fact]
    public void MissingKid_IsRefused()
    {
        var header = _issuer.Header();
        header.Remove("kid");

        AssertDenied(AccessDenialReason.UnknownSigningKey, _issuer.Issue(header, _issuer.Claims()));
    }

    [Fact]
    public void KnownKidSignedByAnotherKey_FailsSignature()
    {
        using var attacker = Es256.CreateKey();
        var ticket = TestTicketIssuer.Issue(
            _issuer.Header().ToJsonString(),
            _issuer.Claims().ToJsonString(),
            input => Es256.Sign(attacker, input));

        AssertDenied(AccessDenialReason.InvalidSignature, ticket);
    }

    [Fact]
    public void AlgNone_WithEmptySignature_IsMalformed()
    {
        var header = _issuer.Header();
        header["alg"] = "none";
        var signed = _issuer.Issue(header, _issuer.Claims());
        var unsigned = signed[..(signed.LastIndexOf('.') + 1)];

        AssertDenied(AccessDenialReason.MalformedTicket, unsigned);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("ES384")]
    [InlineData("es256")]
    public void OtherAlgorithms_AreRefused(string algorithm)
    {
        var header = _issuer.Header();
        header["alg"] = algorithm;

        AssertDenied(AccessDenialReason.UnsupportedAlgorithm, _issuer.Issue(header, _issuer.Claims()));
    }

    [Fact]
    public void WrongType_IsRefused()
    {
        var header = _issuer.Header();
        header["typ"] = "JWT";

        AssertDenied(AccessDenialReason.WrongTokenType, _issuer.Issue(header, _issuer.Claims()));
    }

    [Fact]
    public void HeaderAskingForAnotherKey_IsRefused()
    {
        var header = _issuer.Header();
        header["jku"] = "https://attacker.example/keys";

        AssertDenied(AccessDenialReason.MalformedTicket, _issuer.Issue(header, _issuer.Claims()));
    }

    [Fact]
    public void DerSignature_IsRefused()
    {
        var ticket = TestTicketIssuer.Issue(
            _issuer.Header().ToJsonString(),
            _issuer.Claims().ToJsonString(),
            input => _issuer.BrokerKey.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        AssertDenied(AccessDenialReason.InvalidSignature, ticket);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(32)]
    public void SignatureOfWrongLength_IsRefused(int length)
    {
        var ticket = TestTicketIssuer.Issue(
            _issuer.Header().ToJsonString(),
            _issuer.Claims().ToJsonString(),
            input =>
            {
                var signature = Es256.Sign(_issuer.BrokerKey, input);
                var resized = new byte[length];
                signature.AsSpan(0, Math.Min(length, signature.Length)).CopyTo(resized);
                return resized;
            });

        AssertDenied(AccessDenialReason.InvalidSignature, ticket);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("eyJ.eyJ.==")]
    public void MalformedShape_IsRefused(string ticket) =>
        AssertDenied(AccessDenialReason.MalformedTicket, ticket);

    [Fact]
    public void WrongIssuer_IsRefused() =>
        AssertDenied(AccessDenialReason.WrongIssuer, _issuer.Issue(claims => claims["iss"] = "someone-else"));

    [Fact]
    public void WrongAudience_IsRefused()
    {
        using var otherOwner = Es256.CreateKey();
        var otherOwnerId = ConnectKeyIds.ForOwner(Es256.ExportPublicKey(otherOwner));

        AssertDenied(AccessDenialReason.WrongAudience, _issuer.Issue(claims => claims["aud"] = otherOwnerId));
    }

    [Fact]
    public void Expired_IsRefused()
    {
        var now = _issuer.Clock.UnixSeconds;

        AssertDenied(AccessDenialReason.Expired, _issuer.Issue(claims =>
        {
            claims["iat"] = now - 700;
            claims["nbf"] = now - 700;
            claims["exp"] = now - 100;
        }));
    }

    [Fact]
    public void Expiry_AllowsThirtySecondsOfSkewAndNoMore()
    {
        var ticket = _issuer.Issue();
        var expiresAt = _issuer.Clock.UnixSeconds - 10 + 600;

        _issuer.Clock.Advance(TimeSpan.FromSeconds(expiresAt - _issuer.Clock.UnixSeconds + 29));
        Assert.True(Verify(ticket).IsValid);

        _issuer.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(AccessDenialReason.Expired, Verify(ticket).Reason);
    }

    [Fact]
    public void NotYetValid_IsRefused()
    {
        var now = _issuer.Clock.UnixSeconds;

        AssertDenied(AccessDenialReason.NotYetValid, _issuer.Issue(claims =>
        {
            claims["iat"] = now;
            claims["nbf"] = now + 31;
            claims["exp"] = now + 600;
        }));
    }

    [Fact]
    public void IssuedInTheFuture_IsRefused()
    {
        var now = _issuer.Clock.UnixSeconds;

        AssertDenied(AccessDenialReason.NotYetValid, _issuer.Issue(claims =>
        {
            claims["iat"] = now + 120;
            claims["nbf"] = now;
            claims["exp"] = now + 600;
        }));
    }

    [Fact]
    public void LifetimeOver900Seconds_IsRefused()
    {
        var now = _issuer.Clock.UnixSeconds;

        AssertDenied(AccessDenialReason.InvalidLifetime, _issuer.Issue(claims =>
        {
            claims["iat"] = now;
            claims["nbf"] = now;
            claims["exp"] = now + 901;
        }));
    }

    [Fact]
    public void LifetimeOfExactly900Seconds_IsAccepted()
    {
        var now = _issuer.Clock.UnixSeconds;

        Assert.True(Verify(_issuer.Issue(claims =>
        {
            claims["iat"] = now;
            claims["nbf"] = now;
            claims["exp"] = now + 900;
        })).IsValid);
    }

    [Fact]
    public void UdpProtocol_IsRefusedInPhase1() =>
        AssertDenied(AccessDenialReason.UnsupportedProtocol, _issuer.Issue(claims => claims["proto"] = "udp"));

    [Fact]
    public void UnknownServer_IsRefused() =>
        AssertDenied(AccessDenialReason.UnknownServer, _issuer.Issue(claims => claims["sid"] = Guid.NewGuid().ToString("D")));

    [Fact]
    public void UppercaseServerId_IsMalformed() =>
        AssertDenied(
            AccessDenialReason.MalformedTicket,
            _issuer.Issue(claims => claims["sid"] = TestTicketIssuer.MinecraftServer.ToString("D").ToUpperInvariant()));

    [Fact]
    public void NonMinecraftServer_IsRefused() =>
        AssertDenied(
            AccessDenialReason.UnsupportedGame,
            _issuer.Issue(claims => claims["sid"] = TestTicketIssuer.PalworldServer.ToString("D")));

    [Fact]
    public void DisabledServer_IsRefused() =>
        AssertDenied(
            AccessDenialReason.ServerNotEnabled,
            _issuer.Issue(claims => claims["sid"] = TestTicketIssuer.DisabledServer.ToString("D")));

    [Fact]
    public void WrongPeerNode_IsRefused()
    {
        var ticket = _issuer.Issue();

        Assert.Equal(AccessDenialReason.WrongPeerNode, _issuer.CreateVerifier().Verify(ticket, "nSomeOtherNodeCNTRL").Reason);
        Assert.Equal(AccessDenialReason.WrongPeerNode, _issuer.CreateVerifier().Verify(ticket, null).Reason);
    }

    [Fact]
    public void RevokedTicketId_IsRefused()
    {
        var claims = _issuer.Claims();
        _issuer.Revocations.RevokeTicket(claims["jti"]!.GetValue<string>());

        AssertDenied(AccessDenialReason.TicketRevoked, _issuer.Issue(_issuer.Header(), claims));
    }

    [Fact]
    public void RevokedDevice_IsRefused()
    {
        _issuer.Revocations.RevokeDevice(_issuer.DeviceId);

        AssertDenied(AccessDenialReason.DeviceRevoked, _issuer.Issue());
    }

    [Fact]
    public void RevokedMembership_IsRefused()
    {
        _issuer.Revocations.RevokeMembership(TestTicketIssuer.MembershipId);

        AssertDenied(AccessDenialReason.MembershipRevoked, _issuer.Issue());
    }

    [Fact]
    public void AuthorizationVersionBelowFloor_IsRefused()
    {
        _issuer.Revocations.RaiseAuthorizationFloor(TestTicketIssuer.MembershipId, 2);

        AssertDenied(AccessDenialReason.AuthorizationVersionRevoked, _issuer.Issue(claims => claims["av"] = 1));
        Assert.True(Verify(_issuer.Issue(claims => claims["av"] = 2)).IsValid);
    }

    [Fact]
    public void AuthorizationFloor_NeverDrops()
    {
        _issuer.Revocations.RaiseAuthorizationFloor(TestTicketIssuer.MembershipId, 5);
        _issuer.Revocations.RaiseAuthorizationFloor(TestTicketIssuer.MembershipId, 1);

        AssertDenied(AccessDenialReason.AuthorizationVersionRevoked, _issuer.Issue(claims => claims["av"] = 4));
    }

    [Fact]
    public void DuplicateClaim_IsMalformedEvenWhenSigned()
    {
        var claims = _issuer.Claims().ToJsonString();
        var duplicated = claims.Insert(claims.Length - 1, $",\"aud\":\"{_issuer.OwnerId}\"");
        var ticket = TestTicketIssuer.Issue(_issuer.Header().ToJsonString(), duplicated, input => Es256.Sign(_issuer.BrokerKey, input));

        AssertDenied(AccessDenialReason.MalformedTicket, ticket);
    }

    [Fact]
    public void FractionalTime_IsMalformed() =>
        AssertDenied(AccessDenialReason.MalformedTicket, _issuer.Issue(claims => claims["exp"] = JsonValue.Create(1_767_226_000.5)));

    private TicketVerificationResult Verify(string ticket) =>
        _issuer.CreateVerifier().Verify(ticket, TestTicketIssuer.NodeId);

    private void AssertDenied(AccessDenialReason expected, string ticket)
    {
        var result = Verify(ticket);
        Assert.False(result.IsValid);
        Assert.Equal(expected, result.Reason);
        Assert.Null(result.Claims);
        Assert.Null(result.Server);
    }
}
