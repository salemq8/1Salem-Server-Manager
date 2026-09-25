using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Identity;
using ServerManager.Connect.Core.Json;

namespace ServerManager.Connect.Core.Enrollment;

/// <summary>
/// Encrypts the friend's one-off Tailscale auth key to the friend's device public key, so the
/// broker relays only ciphertext (contract §7). Both ends are this library: the Agent encrypts,
/// 1Salem Connect decrypts.
/// <list type="bullet">
/// <item>ECDH-ES on P-256 between a fresh ephemeral key and the device key. The raw shared secret
/// (the 32-byte X coordinate) is the HKDF input.</item>
/// <item>HKDF-SHA256 with an empty salt and
/// <c>info = "1SALEM-ENROLL-V1" ‖ 0x00 ‖ ephemeral SPKI ‖ device SPKI</c> gives the 32-byte
/// AES key. Putting both public keys in <c>info</c> binds the key to this exact pair.</item>
/// <item>AES-256-GCM with a random 96-bit nonce and a 128-bit tag.
/// <c>AAD = UTF-8("{membershipId}|{deviceId}|{ownerId}")</c>, so a blob cannot be replayed into
/// another membership, device or owner.</item>
/// </list>
/// Note: the device's ECDSA signing key is also used for this ECDH, as the contract specifies.
/// The HKDF label and the signing domains ("1SALEM-REQ-V1", "1SALEM-CONN-V1") keep the two uses
/// apart.
/// </summary>
public static class EnrollmentCrypto
{
    public const int Version = 1;
    public const string Algorithm = "ECDH-ES+HKDF-SHA256+A256GCM";

    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaxPlaintextLength = 4096;

    private static readonly byte[] InfoLabel = "1SALEM-ENROLL-V1\0"u8.ToArray();

    public static EnrollmentEnvelope Encrypt(
        EnrollmentSecret secret,
        ReadOnlySpan<byte> deviceSpki,
        EnrollmentBinding binding)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(binding);
        if (!Es256.IsValidPublicKey(deviceSpki))
        {
            throw new ArgumentException("The device key must be a P-256 SPKI public key.", nameof(deviceSpki));
        }

        // Refuse to encrypt to a key that is not the device named in the AAD: a mix-up here
        // would hand one friend's auth key to another friend's device.
        if (!string.Equals(ConnectKeyIds.ForDevice(deviceSpki), binding.DeviceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The device key does not belong to the bound device id.", nameof(deviceSpki));
        }

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var device = ECDiffieHellman.Create();
        device.ImportSubjectPublicKeyInfo(deviceSpki, out _);
        var ephemeralSpki = ephemeral.ExportSubjectPublicKeyInfo();

        var key = DeriveKey(ephemeral, device.PublicKey, ephemeralSpki, deviceSpki);
        var plaintext = secret.ToJsonUtf8();
        var aad = binding.ToAssociatedData();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            }

            return new EnrollmentEnvelope(
                Base64Url.Encode(ephemeralSpki),
                Base64Url.Encode(nonce),
                Base64Url.Encode(ciphertext),
                Base64Url.Encode(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Decrypts with the device identity. Every failure (wrong key, wrong binding, tampering,
    /// malformed envelope) raises the same <see cref="EnrollmentDecryptionException"/>, so the
    /// failures cannot be told apart from outside.
    /// </summary>
    public static EnrollmentSecret Decrypt(
        EnrollmentEnvelope envelope,
        ConnectIdentity device,
        EnrollmentBinding binding)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(binding);
        if (device.Kind != ConnectIdentityKind.Device ||
            !string.Equals(device.KeyId, binding.DeviceId, StringComparison.Ordinal) ||
            !Base64Url.TryDecode(envelope.EphemeralPublicKey, out var ephemeralSpki) ||
            !Es256.IsValidPublicKey(ephemeralSpki) ||
            !Base64Url.IsEncodingOfLength(envelope.Nonce, NonceLength) ||
            !Base64Url.IsEncodingOfLength(envelope.Tag, TagLength) ||
            !Base64Url.TryDecode(envelope.Ciphertext, out var ciphertext) ||
            ciphertext.Length is 0 or > MaxPlaintextLength)
        {
            throw new EnrollmentDecryptionException();
        }

        var deviceSpki = device.PublicKeySpki.ToArray();
        byte[]? key = null;
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var ephemeral = ECDiffieHellman.Create();
            ephemeral.ImportSubjectPublicKeyInfo(ephemeralSpki, out _);
            key = device.UsePrivateKey(privateKey =>
            {
                var parameters = privateKey.ExportParameters(includePrivateParameters: true);
                try
                {
                    using var agreement = ECDiffieHellman.Create(parameters);
                    return DeriveKey(agreement, ephemeral.PublicKey, ephemeralSpki, deviceSpki);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(parameters.D);
                }
            });

            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Decrypt(
                    Base64Url.Decode(envelope.Nonce),
                    ciphertext,
                    Base64Url.Decode(envelope.Tag),
                    plaintext,
                    binding.ToAssociatedData());
            }

            return EnrollmentSecret.FromJsonUtf8(plaintext);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new EnrollmentDecryptionException(exception);
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DeriveKey(
        ECDiffieHellman privateKey,
        ECDiffieHellmanPublicKey publicKey,
        ReadOnlySpan<byte> ephemeralSpki,
        ReadOnlySpan<byte> deviceSpki)
    {
        var shared = privateKey.DeriveRawSecretAgreement(publicKey);
        var info = new byte[InfoLabel.Length + ephemeralSpki.Length + deviceSpki.Length];
        InfoLabel.CopyTo(info, 0);
        ephemeralSpki.CopyTo(info.AsSpan(InfoLabel.Length));
        deviceSpki.CopyTo(info.AsSpan(InfoLabel.Length + ephemeralSpki.Length));
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, KeyLength, salt: [], info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }
}

/// <summary>
/// Who an enrollment blob is for. It becomes the AEAD associated data
/// <c>membershipId|deviceId|ownerId</c>. A '|' inside a part would make that string ambiguous,
/// so it is rejected.
/// </summary>
public sealed record EnrollmentBinding
{
    public EnrollmentBinding(string membershipId, string deviceId, string ownerId)
    {
        MembershipId = RequirePart(membershipId, nameof(membershipId));
        DeviceId = ConnectKeyIds.IsDeviceId(deviceId)
            ? deviceId
            : throw new ArgumentException("Expected a dev_ device id.", nameof(deviceId));
        OwnerId = ConnectKeyIds.IsOwnerId(ownerId)
            ? ownerId
            : throw new ArgumentException("Expected an own_ owner id.", nameof(ownerId));
    }

    public string MembershipId { get; }

    public string DeviceId { get; }

    public string OwnerId { get; }

    internal byte[] ToAssociatedData() => Encoding.UTF8.GetBytes($"{MembershipId}|{DeviceId}|{OwnerId}");

    private static string RequirePart(string value, string name) =>
        !string.IsNullOrEmpty(value) && !value.Contains('|') && !value.Any(char.IsControl)
            ? value
            : throw new ArgumentException("Binding parts must be non-empty and contain no '|'.", name);
}

/// <summary>
/// The plaintext <c>{"authKey","keyId"}</c> (contract §7). A managed string cannot be wiped,
/// so hold this object only as long as it takes to hand the key to the transport once.
/// Only a <c>tskey-auth-</c> key is accepted, on both the encrypting and the decrypting side.
/// tsnet treats a <c>tskey-client-</c> value as an OAuth client secret that can mint new keys
/// (contract §11), and a friend machine must never hold anything that can mint keys, so a
/// mix-up in the Agent fails here instead of reaching a friend.
/// </summary>
public sealed record EnrollmentSecret
{
    public const string AuthKeyPrefix = "tskey-auth-";

    public EnrollmentSecret(string authKey, string keyId)
    {
        ArgumentException.ThrowIfNullOrEmpty(authKey);
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        if (!IsAuthKey(authKey))
        {
            // The value itself is never echoed: it may be a real secret of the wrong kind.
            throw new ArgumentException("Only a one-off tskey-auth- key can be enrolled.", nameof(authKey));
        }

        AuthKey = authKey;
        KeyId = keyId;
    }

    public string AuthKey { get; }

    public string KeyId { get; }

    /// <summary>Never prints the auth key, so the record cannot leak it through logging.</summary>
    public override string ToString() => $"EnrollmentSecret {{ KeyId = {KeyId}, AuthKey = [REDACTED] }}";

    internal byte[] ToJsonUtf8()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("authKey", AuthKey);
            writer.WriteString("keyId", KeyId);
            writer.WriteEndObject();
        }

        // The caller zeroes the returned copy; Clear zeroes the writer's own buffer.
        var bytes = buffer.WrittenSpan.ToArray();
        buffer.Clear();
        return bytes;
    }

    internal static EnrollmentSecret FromJsonUtf8(byte[] json) =>
        StrictJsonObject.TryParse(json, out var value) &&
        value.TryGetString("authKey", out var authKey) &&
        value.TryGetString("keyId", out var keyId) &&
        IsAuthKey(authKey) &&
        keyId.Length > 0
            ? new EnrollmentSecret(authKey, keyId)
            : throw new FormatException("The enrollment plaintext is not {authKey, keyId} with a tskey-auth- key.");

    private static bool IsAuthKey(string value) =>
        value.Length > AuthKeyPrefix.Length &&
        value.StartsWith(AuthKeyPrefix, StringComparison.Ordinal) &&
        !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));
}

/// <summary>
/// The JSON envelope the broker stores and relays:
/// <c>{"v":1,"alg":"ECDH-ES+HKDF-SHA256+A256GCM","epk":…,"iv":…,"ct":…,"tag":…}</c>, all
/// binary values unpadded base64url; <c>epk</c> is the ephemeral public key as SPKI DER.
/// </summary>
public sealed record EnrollmentEnvelope(string EphemeralPublicKey, string Nonce, string Ciphertext, string Tag)
{
    public const int MaxJsonLength = 16 * 1024;

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", EnrollmentCrypto.Version);
            writer.WriteString("alg", EnrollmentCrypto.Algorithm);
            writer.WriteString("epk", EphemeralPublicKey);
            writer.WriteString("iv", Nonce);
            writer.WriteString("ct", Ciphertext);
            writer.WriteString("tag", Tag);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static EnrollmentEnvelope Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxJsonLength ||
            !StrictJsonObject.TryParse(Encoding.UTF8.GetBytes(json), out var value) ||
            !value.TryGetInt64("v", out var version) ||
            version != EnrollmentCrypto.Version ||
            !value.TryGetString("alg", out var algorithm) ||
            algorithm != EnrollmentCrypto.Algorithm ||
            !value.TryGetString("epk", out var ephemeralPublicKey) ||
            !value.TryGetString("iv", out var nonce) ||
            !value.TryGetString("ct", out var ciphertext) ||
            !value.TryGetString("tag", out var tag))
        {
            throw new EnrollmentDecryptionException();
        }

        return new EnrollmentEnvelope(ephemeralPublicKey, nonce, ciphertext, tag);
    }
}

public sealed class EnrollmentDecryptionException : Exception
{
    private const string DefaultMessage = "The enrollment data could not be opened.";

    public EnrollmentDecryptionException()
        : base(DefaultMessage)
    {
    }

    public EnrollmentDecryptionException(Exception innerException)
        : base(DefaultMessage, innerException)
    {
    }
}
