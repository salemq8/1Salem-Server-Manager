namespace ServerManager.Connect.App.Configuration;

/// <summary>
/// Which broker addresses the app will send signed requests and invite secrets to. HTTPS always;
/// plain HTTP only to this PC (<c>127.0.0.1</c> or <c>localhost</c>) and only in development
/// mode, for the local broker (<c>wrangler dev --local</c>, contract §13). The address must be an
/// origin: a path, query, fragment or user info would change what the request signature covers
/// or where credentials end up.
/// </summary>
public static class BrokerEndpointPolicy
{
    public static bool IsAllowed(Uri address, bool developmentMode, out string reason)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri)
        {
            reason = "The broker address must be absolute.";
            return false;
        }

        if (address.UserInfo.Length > 0 ||
            address.Query.Length > 0 ||
            address.Fragment.Length > 0 ||
            address.AbsolutePath != "/")
        {
            reason = "The broker address must be an origin such as https://broker.example with no path, query or credentials.";
            return false;
        }

        if (address.Scheme == Uri.UriSchemeHttps)
        {
            reason = string.Empty;
            return true;
        }

        if (address.Scheme == Uri.UriSchemeHttp && developmentMode && IsThisPc(address.Host))
        {
            reason = string.Empty;
            return true;
        }

        reason = developmentMode
            ? "Plain HTTP is allowed only to 127.0.0.1 or localhost."
            : "The broker address must use HTTPS.";
        return false;
    }

    // Exact names only: "localhost.example.com" or "127.0.0.2" are not this PC for our purposes.
    private static bool IsThisPc(string host) =>
        host == "127.0.0.1" || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
}
