using ServerManager.Contracts;

namespace ServerManager.Core.Content;

/// <summary>
/// Where a plugin download is allowed to come from. A URL is only ever used when it arrived
/// in a provider's own response and points at that provider's own hosts; a person cannot
/// paste a download address into the marketplace, and a redirect cannot walk off to a
/// different site.
/// </summary>
public static class ContentDownloadPolicy
{
    /// <summary>A generous ceiling for a plugin JAR. Anything larger is refused unread.</summary>
    public const long MaximumFileBytes = 256L * 1024 * 1024;

    /// <summary>Redirect hops allowed before a download is abandoned.</summary>
    public const int MaximumRedirects = 5;

    private static readonly string[] ModrinthHosts =
    [
        "cdn.modrinth.com",
        "api.modrinth.com"
    ];

    private static readonly string[] HangarHosts =
    [
        "hangarcdn.papermc.io",
        "hangar.papermc.io"
    ];

    public static IReadOnlyList<string> AllowedHosts(ContentProviderId provider) =>
        provider switch
        {
            ContentProviderId.Modrinth => ModrinthHosts,
            ContentProviderId.Hangar => HangarHosts,
            _ => []
        };

    /// <summary>
    /// HTTPS only, on a host the provider actually serves from. Host comparison is exact:
    /// a suffix test would accept "cdn.modrinth.com.example.com".
    /// </summary>
    public static bool IsAllowedDownloadUrl(ContentProviderId provider, Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (url.IsDefaultPort is false && url.Port != 443)
        {
            return false;
        }

        return AllowedHosts(provider)
            .Any(host => string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A size the provider reports is only believed when it is sane. Zero means the provider
    /// did not say, which is allowed; the transfer itself still enforces the ceiling.
    /// </summary>
    public static bool IsAcceptableSize(long sizeBytes) =>
        sizeBytes >= 0 && sizeBytes <= MaximumFileBytes;
}
