using System.Security.Cryptography;
using ServerManager.Connect.Core.Crypto;

namespace ServerManager.Connect.App.Services;

/// <summary>
/// A per-ticket P-256 session key (contract §5, §10). It lives only in memory: its public half
/// goes to the broker as base64url SPKI (the ticket's <c>skp</c>), its private half to the
/// transport as base64url PKCS#8, and then it is disposed. It is never written anywhere.
/// </summary>
internal sealed class SessionKey : IDisposable
{
    private readonly ECDsa _key;

    private SessionKey(ECDsa key)
    {
        _key = key;
        PublicKeySpki = Base64Url.Encode(Es256.ExportPublicKey(key));
    }

    public string PublicKeySpki { get; }

    public static SessionKey Create() => new(Es256.CreateKey());

    /// <summary>
    /// The pipe carries the key as text, so a managed string copy is unavoidable; the byte copy
    /// made on the way is wiped.
    /// </summary>
    public string ExportPrivateKey()
    {
        var pkcs8 = _key.ExportPkcs8PrivateKey();
        try
        {
            return Base64Url.Encode(pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    public void Dispose() => _key.Dispose();
}
