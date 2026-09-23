using ServerManager.Contracts;

namespace ServerManager.Core;

public sealed record PortConflict(Guid ServerId, string ServerName, int Port);

/// <summary>
/// Keeps two managed servers from being configured on the same listening port. With several
/// Minecraft servers on one machine this is the difference between a second server and a
/// second server that silently refuses to start.
/// </summary>
public static class ServerPortAllocationPolicy
{
    /// <summary>The range a server may listen on, leaving the well-known ports alone.</summary>
    public const int MinimumPort = 1024;

    public const int MaximumPort = 65535;

    /// <summary>
    /// Which already-registered server is using this port, if any. The server being edited is
    /// excluded so saving its own settings is not reported as a clash with itself.
    /// </summary>
    public static PortConflict? FindConflict(
        IEnumerable<GameServerDefinition> servers,
        int port,
        Guid? excludingServerId = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        foreach (var server in servers)
        {
            if (excludingServerId is { } excluded && server.Id == excluded)
            {
                continue;
            }

            if (server.Port == port)
            {
                return new PortConflict(server.Id, server.Name, server.Port);
            }
        }

        return null;
    }

    public static bool IsInValidRange(int port) => port is >= MinimumPort and <= MaximumPort;

    /// <summary>
    /// The next free port at or after <paramref name="preferredPort"/>, skipping anything a
    /// managed server already uses. Returns null when nothing is free, rather than handing
    /// back a port that is already taken.
    /// </summary>
    public static int? SuggestPort(
        IEnumerable<GameServerDefinition> servers,
        int preferredPort,
        Guid? excludingServerId = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var taken = servers
            .Where(server => excludingServerId is not { } excluded || server.Id != excluded)
            .Select(server => server.Port)
            .ToHashSet();

        var start = IsInValidRange(preferredPort) ? preferredPort : 25565;
        for (var port = start; port <= MaximumPort; port++)
        {
            if (!taken.Contains(port))
            {
                return port;
            }
        }

        // Wrap around for the unusual case of starting high.
        for (var port = MinimumPort; port < start; port++)
        {
            if (!taken.Contains(port))
            {
                return port;
            }
        }

        return null;
    }
}
