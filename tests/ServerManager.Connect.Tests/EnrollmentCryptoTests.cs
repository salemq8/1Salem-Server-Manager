using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Enrollment;
using ServerManager.Connect.Core.Identity;

namespace ServerManager.Connect.Tests;

public sealed class EnrollmentCryptoTests : IDisposable
{
    private const string AuthKey = "tskey-auth-kTESTTESTTEST11CNTRL-0000000000000000000000000000000000";
    private const string KeyId = "kTESTTESTTEST11CNTRL";

    private readonly ConnectIdentity _device = ConnectIdentity.Generate(ConnectIdentityKind.Device);
    private readonly ConnectIdentity _owner = ConnectIdentity.Generate(ConnectIdentityKind.Owner);

    public void Dispose()
    {
        _device.Dispose();
        _owner.Dispose();
    }

    [Fact]
    public void RoundTrip_ThroughTheBrokerJson()
    {
        var binding = Binding();
        var envelope = EnrollmentCrypto.Encrypt(new EnrollmentSecret(AuthKey, KeyId), _device.PublicKeySpki, binding);

        var relayed = EnrollmentEnvelope.Parse(envelope.ToJson());
        var secret = EnrollmentCrypto.Decrypt(relayed, _device, binding);

        Assert.Equal(AuthKey, secret.AuthKey);
        Assert.Equal(KeyId, secret.KeyId);
        Assert.DoesNotContain("tskey", envelope.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void EachEncryption_UsesAFreshEphemeralKeyAndNonce()
    {
        var first = EnrollmentCrypto.Encrypt(new EnrollmentSecret(AuthKey, KeyId), _device.PublicKeySpki, Binding());
        var second = EnrollmentCrypto.Encrypt(new EnrollmentSecret(AuthKey, KeyId), _device.PublicKeySpki, Binding());

        Assert.NotEqual(first.EphemeralPublicKey, second.EphemeralPublicKey);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Theory]
    [InlineData("ct")]
    [InlineData("tag")]
    [InlineData("iv")]
    [InlineData("epk")]
    public void TamperedEnvelope_CannotBeOpened(string part)
    {
        var binding = Binding();
        var envelope = EnrollmentCrypto.Encrypt(new EnrollmentSecret(AuthKey, KeyId), _device.PublicKeySpki, binding);
        using var otherEphemeral = Es256.CreateKey();
        var tampered = part switch
        {
            "ct" => envelope with { Ciphertext = FlipFirstBit(envelope.Ciphertext) },
            "tag" => envelope with { Tag = FlipFirstBit(envelope.Tag) },
            "iv" => envelope with { Nonce = FlipFirstBit(envelope.Nonce) },
            _ => envelope with { EphemeralPublicKey = Base64Url.Encode(Es256.ExportPublicKey(otherEphemeral)) }
        };

        Assert.Throws<EnrollmentDecryptionException>(() => EnrollmentCrypto.Decrypt(tampered, _device, binding));
    }

    [Fact]
    public void WrongAssociatedData_CannotBeOpened()
    {
        var envelope = EnrollmentCrypto.Encrypt(new EnrollmentSecret(AuthKey, KeyId), _device.PublicKeySpki, Binding());
        using var otherOwner = ConnectIdentity.Generate(ConnectIdentityKind.Owner);

        Assert.Throws<EnrollmentDecryptionException>(() =>
            EnrollmentCrypto.Decrypt(envelope, _device, new EnrollmentBinding("mem_other", _device.KeyId, _owner.KeyId)));
        Assert.Throws<EnrollmentDecryptionException>(() =>
            EnrollmentCrypto.Decrypt(envelope, _device, new EnrollmentBinding("mem_1", _device.KeyId, otherOwner.KeyId)));
    }

    [Fact]
    public void AnotherDevice_CannotOpenIt()
    {
        var envelope = EnrollmentCrypto.Encrypt(new EnrollmentSecret(AuthKey, KeyId), _device.PublicKeySpki, Binding());
        using var otherDevice = ConnectIdentity.Generate(ConnectIdentityKind.Device);

        Assert.Throws<EnrollmentDecryptionException>(() =>
            EnrollmentCrypto.Decrypt(envelope, otherDevice, new EnrollmentBinding("mem_1", otherDevice.KeyId, _owner.KeyId)));
        Assert.Throws<EnrollmentDecryptionException>(() => EnrollmentCrypto.Decrypt(envelope, otherDevice, Binding()));
    }

    [Fact]
    public void Encrypt_RefusesAKeyThatIsNotTheBoundDevice()
    {
        using var otherDevice = ConnectIdentity.Generate(ConnectIdentityKind.Device);

        Assert.Throws<ArgumentException>(() =>
            EnrollmentCrypto.Encrypt(new EnrollmentSecret(AuthKey, KeyId), otherDevice.PublicKeySpki, Binding()));
    }

    [Theory]
    [InlineData("tskey-client-kABC-secret")]
    [InlineData("tskey-api-kABC-secret")]
    [InlineData("tskey-auth-")]
    [InlineData("tskey-auth-has space")]
    [InlineData("not-a-key")]
    public void OnlyTskeyAuthKeys_CanBeEnrolled(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() => new EnrollmentSecret(value, KeyId));

        // The same fixed text for every input: the rejected value is never echoed.
        Assert.Equal("Only a one-off tskey-auth- key can be enrolled. (Parameter 'authKey')", exception.Message);
    }

    [Fact]
    public void Secret_NeverPrintsTheAuthKey() =>
        Assert.DoesNotContain(AuthKey, new EnrollmentSecret(AuthKey, KeyId).ToString(), StringComparison.Ordinal);

    [Fact]
    public void MalformedEnvelopeJson_IsTheSameGenericFailure()
    {
        Assert.Throws<EnrollmentDecryptionException>(() => EnrollmentEnvelope.Parse("{}"));
        Assert.Throws<EnrollmentDecryptionException>(() => EnrollmentEnvelope.Parse("{\"v\":2,\"alg\":\"x\"}"));
        Assert.Throws<EnrollmentDecryptionException>(() => EnrollmentEnvelope.Parse("[]"));
    }

    private EnrollmentBinding Binding() => new("mem_1", _device.KeyId, _owner.KeyId);

    private static string FlipFirstBit(string base64Url)
    {
        var bytes = Base64Url.Decode(base64Url);
        bytes[0] ^= 0x01;
        return Base64Url.Encode(bytes);
    }
}
