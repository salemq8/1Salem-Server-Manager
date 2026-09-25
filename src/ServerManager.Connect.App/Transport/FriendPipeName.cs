using System.Text.RegularExpressions;
using ServerManager.Connect.Core.Pipes;

namespace ServerManager.Connect.App.Transport;

/// <summary>
/// The friend pipe's bare name (no <c>\\.\pipe\</c> prefix). One value is handed to both the
/// client that connects and the process that serves, so the two can never disagree about it. It
/// must fit the transport's own rule for <c>--pipe</c> (connect/transport/internal/pipe): a name
/// with a separator in it would be a different pipe path to .NET than to the transport.
/// </summary>
public static partial class FriendPipeName
{
    /// <summary><c>1Salem.Connect.Transport.&lt;user SID&gt;</c>, the name a release uses.</summary>
    public static string ForCurrentUser() => ConnectPipeNames.FriendTransport(ConnectPipeSecurity.CurrentUser);

    public static string Validate(string pipeName) =>
        pipeName is not null && Pattern().IsMatch(pipeName)
            ? pipeName
            : throw new ArgumentException("Expected a bare pipe name of 1-200 characters [A-Za-z0-9._-].", nameof(pipeName));

    [GeneratedRegex("^[A-Za-z0-9._-]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
