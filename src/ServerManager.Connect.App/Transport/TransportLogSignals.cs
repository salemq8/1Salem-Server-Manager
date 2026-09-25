namespace ServerManager.Connect.App.Transport;

public enum SessionReachability
{
    /// <summary>No connection has been attempted through this session yet.</summary>
    Unknown,

    /// <summary>The last connection reached the server: the owner's side accepted it (it may have ended since).</summary>
    Reached,

    /// <summary>The last connection could not reach the host bridge at all.</summary>
    Unreachable,

    /// <summary>The host bridge answered but refused the connection (revoked, or the server is not running).</summary>
    Refused
}

/// <summary>The newest reachability line for one session, and the line itself so a caller can tell a new event from an old one.</summary>
public sealed record SessionSignal(SessionReachability Reachability, string? Line);

/// <summary>
/// Whether a session's game connections actually reach the owner's server. The transport reports
/// a session only as <c>listening</c> or <c>expired</c>; the outcome of each connection appears
/// only in its redacted <c>diag</c> log. These are the exact lines the finished transport
/// (connect/transport/internal/friend/manager.go) writes per session, so "Server offline" is based
/// on what happened, not on a guess. If the transport's wording changes, the state simply stays
/// "Connected" rather than claiming something false.
/// </summary>
public static class TransportLogSignals
{
    public static SessionSignal Latest(IReadOnlyList<string> log, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(log);
        var prefix = $"friend: session {sessionId} ";
        for (var index = log.Count - 1; index >= 0; index--)
        {
            var line = log[index];
            var start = line.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var reachability = Classify(line.AsSpan(start + prefix.Length));
            if (reachability != SessionReachability.Unknown)
            {
                return new SessionSignal(reachability, line);
            }
        }

        return new SessionSignal(SessionReachability.Unknown, null);
    }

    private static SessionReachability Classify(ReadOnlySpan<char> message)
    {
        if (message.StartsWith("could not reach the host bridge", StringComparison.Ordinal))
        {
            return SessionReachability.Unreachable;
        }

        if (message.StartsWith("connection not accepted", StringComparison.Ordinal))
        {
            return SessionReachability.Refused;
        }

        // "accepted" is written as soon as the owner's side lets a connection in, before the game's
        // traffic flows; "ended" only after that traffic stops. Without the first, a failure logged
        // earlier would stay the newest line for a whole game that is working.
        return message.StartsWith("connection accepted", StringComparison.Ordinal) ||
               message.StartsWith("connection ended", StringComparison.Ordinal)
            ? SessionReachability.Reached
            : SessionReachability.Unknown;
    }
}
