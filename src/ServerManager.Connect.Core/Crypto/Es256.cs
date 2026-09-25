using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;

namespace ServerManager.Connect.Core.Crypto;

/// <summary>
/// ECDSA P-256 with SHA-256 (ES256) in the one form every Connect component shares: IEEE P1363
/// signatures (r‖s, exactly 64 bytes) and public keys as uncompressed SubjectPublicKeyInfo DER.
/// The signature format is always passed explicitly. .NET's default for ECDsa happens to be P1363
/// too, but X.509 and some other APIs default to DER, and a verifier that silently accepted
/// both would give every signature a second valid spelling.
/// </summary>
public static class Es256
{
    public const string Algorithm = "ES256";
    public const int SignatureLength = 64;
    public const int SpkiLength = 91;

    private const DSASignatureFormat SignatureFormat =
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    // SEQUENCE { SEQUENCE { id-ecPublicKey, prime256v1 }, BIT STRING (0 unused bits) } followed
    // by the 0x04 uncompressed-point marker. Pinning these exact bytes rejects compressed points,
    // explicit curve parameters, other curves and trailing data before any parser runs.
    private static readonly byte[] SpkiPrefix = Convert.FromHexString(
        "3059301306072A8648CE3D020106082A8648CE3D030107034200" + "04");

    private static readonly BigInteger FieldPrime = ParseHex(
        "FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");

    private static readonly BigInteger CurveB = ParseHex(
        "5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B");

    public static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] Sign(ECDsa key, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureP256(key);
        var signature = new byte[SignatureLength];
        if (!key.TrySignData(data, signature, HashAlgorithmName.SHA256, SignatureFormat, out var written) ||
            written != SignatureLength)
        {
            throw new CryptographicException("ES256 signing did not produce a 64-byte signature.");
        }

        return signature;
    }

    public static bool Verify(ECDsa publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        // Length first: a DER signature (70-72 bytes) or a truncated one is refused before any
        // cryptographic work, regardless of what the underlying provider might tolerate.
        if (signature.Length != SignatureLength || !IsP256(publicKey))
        {
            return false;
        }

        return publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, SignatureFormat);
    }

    public static bool Verify(ReadOnlySpan<byte> spki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureLength || !TryImportPublicKey(spki, out var key))
        {
            return false;
        }

        using (key)
        {
            return Verify(key, data, signature);
        }
    }

    public static byte[] ExportPublicKey(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureP256(key);
        return key.ExportSubjectPublicKeyInfo();
    }

    public static ECDsa ImportPublicKey(ReadOnlySpan<byte> spki) =>
        TryImportPublicKey(spki, out var key)
            ? key
            : throw new CryptographicException("The public key is not an uncompressed P-256 SubjectPublicKeyInfo.");

    public static bool TryImportPublicKey(ReadOnlySpan<byte> spki, [NotNullWhen(true)] out ECDsa? key)
    {
        key = null;
        if (!IsValidPublicKey(spki))
        {
            return false;
        }

        var candidate = ECDsa.Create();
        try
        {
            candidate.ImportSubjectPublicKeyInfo(spki, out var bytesRead);
            if (bytesRead != SpkiLength || !IsP256(candidate))
            {
                candidate.Dispose();
                return false;
            }
        }
        catch (CryptographicException)
        {
            candidate.Dispose();
            return false;
        }

        key = candidate;
        return true;
    }

    /// <summary>
    /// Checks the exact SPKI encoding and that the point lies on P-256. The on-curve check is
    /// done here rather than trusted to the platform provider because the same keys feed ECDH
    /// in enrollment, where accepting an off-curve point is the classic invalid-curve attack.
    /// P-256 has cofactor 1, so every on-curve point other than infinity (which the
    /// uncompressed encoding cannot express) is in the prime-order group.
    /// </summary>
    public static bool IsValidPublicKey(ReadOnlySpan<byte> spki)
    {
        if (spki.Length != SpkiLength || !spki[..SpkiPrefix.Length].SequenceEqual(SpkiPrefix))
        {
            return false;
        }

        var x = new BigInteger(spki.Slice(SpkiPrefix.Length, 32), isUnsigned: true, isBigEndian: true);
        var y = new BigInteger(spki.Slice(SpkiPrefix.Length + 32, 32), isUnsigned: true, isBigEndian: true);
        if (x >= FieldPrime || y >= FieldPrime)
        {
            return false;
        }

        var left = BigInteger.ModPow(y, 2, FieldPrime);
        var right = (BigInteger.ModPow(x, 3, FieldPrime) - 3 * x + CurveB) % FieldPrime;
        if (right.Sign < 0)
        {
            right += FieldPrime;
        }

        return left == right;
    }

    internal static bool IsP256(ECDsa key)
    {
        if (key.KeySize != 256)
        {
            return false;
        }

        var curve = key.ExportParameters(false).Curve;
        return curve.IsNamed &&
            (curve.Oid.Value == "1.2.840.10045.3.1.7" ||
             curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256" or "secP256r1" or "prime256v1");
    }

    private static void EnsureP256(ECDsa key)
    {
        if (!IsP256(key))
        {
            throw new CryptographicException("1Salem Connect keys must be ECDSA P-256.");
        }
    }

    private static BigInteger ParseHex(string hex) =>
        new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);
}
