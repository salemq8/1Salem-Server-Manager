using System.Runtime.Versioning;
using System.Security.Principal;

namespace ServerManager.Connect.Core.Pipes;

/// <summary>
/// The two Connect pipe names (contract §11), without the <c>\\.\pipe\</c> prefix that .NET
/// adds itself. The friend pipe carries the user's SID so that two Windows users on one PC never
/// share a transport.
/// </summary>
public static class ConnectPipeNames
{
    /// <summary>Host transport ↔ Agent. The Agent is the server.</summary>
    public const string HostAuthorization = "1Salem.Connect.HostAuthz.v1";

    /// <summary>Agent ↔ the one Agent-supervised host transport.</summary>
    public const string HostTransportAgent = "1Salem.Connect.Host.Transport.Agent.v1";

    private const string FriendTransportPrefix = "1Salem.Connect.Transport.";
    private const string HostTransportPrefix = "1Salem.Connect.Host.Transport.";

    /// <summary>Friend UI ↔ friend transport for <paramref name="user"/>. The transport is the server.</summary>
    [SupportedOSPlatform("windows")]
    public static string FriendTransport(SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return FriendTransportPrefix + user.Value;
    }

    /// <summary>Agent ↔ host transport control pipe for <paramref name="user"/>.</summary>
    [SupportedOSPlatform("windows")]
    public static string HostTransport(SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return HostTransportPrefix + user.Value;
    }

    /// <summary>
    /// A bare pipe name: no path separators (which would make it a different pipe path) and not
    /// the reserved "anonymous".
    /// </summary>
    internal static void Validate(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) ||
            pipeName.IndexOfAny(['\\', '/', ':']) >= 0 ||
            pipeName.Any(char.IsControl) ||
            string.Equals(pipeName, "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Expected a bare pipe name such as 1Salem.Connect.HostAuthz.v1.", nameof(pipeName));
        }
    }
}
