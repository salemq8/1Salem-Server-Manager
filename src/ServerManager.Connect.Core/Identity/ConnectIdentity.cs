using System.Security.Cryptography;
using ServerManager.Connect.Core.Crypto;

namespace ServerManager.Connect.Core.Identity;

public enum ConnectIdentityKind
{
    /// <summary>A Server Manager installation (<c>own_…</c>), created by the Agent.</summary>
    Owner,

    /// <summary>A friend's Windows user on one PC (<c>dev_…</c>), created by 1Salem Connect.</summary>
    Device
}

/// <summary>
/// A long-lived P-256 identity whose id derives from its public key (contract §5). The private
/// key never leaves this object except through <see cref="ConnectIdentityStore"/>, which writes
/// it only DPAPI-protected.
/// </summary>
public sealed class ConnectIdentity : IDisposable
{
    private readonly object _gate = new();
    private readonly ECDsa _key;
    private readonly byte[] _spki;

    internal ConnectIdentity(ConnectIdentityKind kind, ECDsa key)
    {
        _key = key;
        _spki = Es256.ExportPublicKey(key);
        Kind = kind;
        KeyId = kind == ConnectIdentityKind.Owner
            ? ConnectKeyIds.ForOwner(_spki)
            : ConnectKeyIds.ForDevice(_spki);
    }

    public ConnectIdentityKind Kind { get; }

    /// <summary>The <c>own_…</c> or <c>dev_…</c> id, sent as <c>X-1S-Key</c>.</summary>
    public string KeyId { get; }

    public ReadOnlySpan<byte> PublicKeySpki => _spki;

    public string PublicKeySpkiBase64Url => Base64Url.Encode(_spki);

    /// <summary>A fresh identity that exists only in memory until a store persists it.</summary>
    public static ConnectIdentity Generate(ConnectIdentityKind kind) => new(kind, Es256.CreateKey());

    /// <summary>ES256 (P1363, 64 bytes) over <paramref name="data"/>.</summary>
    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        // Instance members of the platform ECDsa types are not documented as thread-safe,
        // and one identity signs every concurrent broker request of the app.
        lock (_gate)
        {
            return Es256.Sign(_key, data);
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> with the private key under the same lock as signing.
    /// Used by enrollment decryption and the store; the key must not escape the callback.
    /// </summary>
    internal T UsePrivateKey<T>(Func<ECDsa, T> operation)
    {
        lock (_gate)
        {
            return operation(_key);
        }
    }

    public void Dispose() => _key.Dispose();
}
