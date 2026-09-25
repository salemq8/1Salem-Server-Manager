using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ServerManager.Connect.Core.Crypto;

namespace ServerManager.Connect.Core.Tickets;

/// <summary>
/// Per-connection proof of possession (contract §10): ES256 by the session private key over
/// <c>1SALEM-CONN-V1\n{jti}\n{n}\n{ts}\n{sid}</c> (UTF-8, <c>ts</c> as decimal seconds, no
/// trailing newline). Only the friend transport holds the session private key, in memory, so a
/// captured ticket cannot open connections by itself.
/// </summary>
public static class ConnectionProof
{
    public const string Domain = "1SALEM-CONN-V1";

    public static string BuildSigningInput(string ticketId, string nonce, long timestamp, string serverId) =>
        string.Join(
            '\n',
            Domain,
            ticketId,
            nonce,
            timestamp.ToString(CultureInfo.InvariantCulture),
            serverId);

    public static string Sign(ECDsa sessionKey, string ticketId, string nonce, long timestamp, string serverId)
    {
        var input = Encoding.UTF8.GetBytes(BuildSigningInput(ticketId, nonce, timestamp, serverId));
        return Base64Url.Encode(Es256.Sign(sessionKey, input));
    }

    public static bool Verify(
        ReadOnlySpan<byte> sessionPublicKeySpki,
        string ticketId,
        string nonce,
        long timestamp,
        string serverId,
        string proof)
    {
        if (!Base64Url.TryDecode(proof, out var signature) || signature.Length != Es256.SignatureLength)
        {
            return false;
        }

        var input = Encoding.UTF8.GetBytes(BuildSigningInput(ticketId, nonce, timestamp, serverId));
        return Es256.Verify(sessionPublicKeySpki, input, signature);
    }
}
