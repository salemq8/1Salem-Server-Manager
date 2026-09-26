namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// The owner's Tailscale OAuth client (contract §2 O1): scopes <c>auth_keys</c> and
/// <c>devices:core</c>, tagged only <c>tag:onesalem-host</c>, which owns <c>tag:onesalem-client</c>
/// in the tailnet policy, so one client can mint both host and friend keys. It is only
/// ever constructed from something the owner explicitly entered; nothing reads it from the
/// environment. The secret never appears in <see cref="ToString"/> or in an exception.
/// </summary>
public sealed class TailscaleOAuthCredential
{
    public const string ClientSecretPrefix = "tskey-client-";

    private const int MaxLength = 256;

    public TailscaleOAuthCredential(string clientId, string clientSecret)
    {
        if (!IsToken(clientId))
        {
            throw new ArgumentException("An OAuth client id is required.", nameof(clientId));
        }

        if (!IsToken(clientSecret) ||
            clientSecret.Length <= ClientSecretPrefix.Length ||
            !clientSecret.StartsWith(ClientSecretPrefix, StringComparison.Ordinal))
        {
            // The value is never echoed: it may be a real secret of the wrong kind.
            throw new ArgumentException("An OAuth client secret (tskey-client-…) is required.", nameof(clientSecret));
        }

        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    public string ClientId { get; }

    internal string ClientSecret { get; }

    public override string ToString() => $"TailscaleOAuthCredential {{ ClientId = {ClientId}, ClientSecret = [REDACTED] }}";

    private static bool IsToken(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= MaxLength &&
        !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));
}
