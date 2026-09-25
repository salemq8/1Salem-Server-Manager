using System.Security.Cryptography;
using System.Text;
using ServerManager.Connect.Core.Broker;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Identity;
using ServerManager.Connect.Tests.Support;

namespace ServerManager.Connect.Tests;

public sealed class SignedRequestSignerTests : IDisposable
{
    private const string Nonce = "AAECAwQFBgcICQoLDA0ODw"; // bytes 0x00..0x0F

    private readonly ConnectIdentity _device = ConnectIdentity.Generate(ConnectIdentityKind.Device);
    private readonly FixedTimeProvider _clock = new(1_767_225_600);

    public void Dispose() => _device.Dispose();

    [Fact]
    public void CanonicalString_IsExactlyTheContractLayout()
    {
        // SHA-256 of the empty body is e3b0c442…b855, which is 47DEQpj8…SuFU in base64url.
        var canonical = SignedRequestSigner.BuildCanonicalString("GET", "/v1/devices/me/memberships", 1_767_225_600, Nonce, []);

        Assert.Equal(
            "1SALEM-REQ-V1\nGET\n/v1/devices/me/memberships\n1767225600\nAAECAwQFBgcICQoLDA0ODw\n47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU",
            canonical);
    }

    [Fact]
    public void CanonicalString_HashesTheRawBodyBytes()
    {
        var body = Encoding.UTF8.GetBytes("{\"membershipId\":\"mem_1\",\"sessionSpki\":\"x\"}");

        var canonical = SignedRequestSigner.BuildCanonicalString("POST", "/v1/sessions", 42, Nonce, body);

        Assert.Equal(
            $"1SALEM-REQ-V1\nPOST\n/v1/sessions\n42\n{Nonce}\n{Base64Url.Encode(SHA256.HashData(body))}",
            canonical);
        Assert.DoesNotContain("\r", canonical, StringComparison.Ordinal);
        Assert.False(canonical.EndsWith('\n'));
    }

    [Theory]
    [InlineData("post", "/v1/sessions")]
    [InlineData("POST", "v1/sessions")]
    [InlineData("POST", "/v1/owners/me/revocations?since=1")]
    [InlineData("POST", "/v1/i#fragment")]
    [InlineData("POST", "/v1/a b")]
    public void CanonicalString_RejectsAmbiguousInput(string method, string path) =>
        Assert.Throws<ArgumentException>(() => SignedRequestSigner.BuildCanonicalString(method, path, 1, Nonce, []));

    [Fact]
    public void CanonicalString_RequiresASixteenByteNonce() =>
        Assert.Throws<ArgumentException>(() => SignedRequestSigner.BuildCanonicalString("GET", "/v1/keys", 1, "AAAA", []));

    [Fact]
    public void Sign_ProducesVerifiableHeaders()
    {
        var body = "{\"secret\":\"x\"}"u8.ToArray();

        var headers = new SignedRequestSigner(_device, _clock).Sign("POST", "/v1/invites/redeem", body);

        Assert.Equal(_device.KeyId, headers.KeyId);
        Assert.Equal("1767225600", headers.Time);
        Assert.True(Base64Url.IsEncodingOfLength(headers.Nonce, 16));
        var signature = Base64Url.Decode(headers.Signature);
        Assert.Equal(Es256.SignatureLength, signature.Length);
        var canonical = SignedRequestSigner.BuildCanonicalString("POST", "/v1/invites/redeem", 1_767_225_600, headers.Nonce, body);
        Assert.True(Es256.Verify(_device.PublicKeySpki, Encoding.UTF8.GetBytes(canonical), signature));
    }

    [Fact]
    public async Task SignAsync_SignsThePathWithoutQueryAndTheExactBody()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://broker.invalid/v1/owners/me/revocations?since=100");

        await new SignedRequestSigner(_device, _clock).SignAsync(request, CancellationToken.None);

        var nonce = request.Headers.GetValues(SignedRequestSigner.NonceHeader).Single();
        var signature = Base64Url.Decode(request.Headers.GetValues(SignedRequestSigner.SignatureHeader).Single());
        var canonical = SignedRequestSigner.BuildCanonicalString("GET", "/v1/owners/me/revocations", 1_767_225_600, nonce, []);
        Assert.Equal(_device.KeyId, request.Headers.GetValues(SignedRequestSigner.KeyHeader).Single());
        Assert.True(Es256.Verify(_device.PublicKeySpki, Encoding.UTF8.GetBytes(canonical), signature));
    }
}
