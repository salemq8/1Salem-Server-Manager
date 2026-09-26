using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Infrastructure.Connect;

namespace TsnetSmoke;

/// <summary>
/// The owner's OAuth client for this run: the production <see cref="TailscaleOAuthCredential"/>
/// for <see cref="TailscaleApiProvisioner"/>, and the same values for <see cref="SmokeTailnetApi"/>,
/// which cannot read the production type's internal secret. Nothing prints the secret.
/// </summary>
internal sealed class SmokeCredential
{
    // Must match $entropyText in Set-TsnetSmokeCredential.ps1. It only ties the blob to this
    // purpose among the user's DPAPI data; it is not a secret.
    private static readonly byte[] Entropy = "1Salem.TsnetSmoke.OAuthClient.v1"u8.ToArray();

    private SmokeCredential(TailscaleOAuthCredential production, string clientSecret)
    {
        Production = production;
        ClientSecret = clientSecret;
    }

    public TailscaleOAuthCredential Production { get; }

    public string ClientId => Production.ClientId;

    public string ClientSecret { get; }

    public override string ToString() => Production.ToString();

    /// <summary>
    /// Decrypts the file Set-TsnetSmokeCredential.ps1 wrote (DPAPI, current user, with the entropy
    /// below) in memory. The decrypted bytes are zeroed; the two strings cannot be.
    /// </summary>
    public static SmokeCredential Load(string path)
    {
        byte[] plaintext;
        try
        {
            plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("The stored OAuth client cannot be decrypted by this Windows user; store it again with Set-TsnetSmokeCredential.ps1.");
        }

        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            var clientId = root.GetProperty("clientId").GetString() ?? string.Empty;
            var clientSecret = root.GetProperty("clientSecret").GetString() ?? string.Empty;

            // The production type validates both (a tskey-client- secret) without echoing them.
            return new SmokeCredential(new TailscaleOAuthCredential(clientId, clientSecret), clientSecret);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("The stored OAuth client is not usable; store it again with Set-TsnetSmokeCredential.ps1.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
