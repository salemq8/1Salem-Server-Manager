using ServerManager.Connect.Core.Tickets;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// The Agent's authoritative answer to "which local endpoint is ServerId X" for 1Salem Connect
/// (contract §3, §9). It maps the registered servers in <see cref="IGameServerStore"/> to
/// <see cref="ConnectServerEntry"/> values:
/// <list type="bullet">
/// <item>Only Minecraft servers appear, always as TCP. Palworld is UDP and not bridged in
/// Phase 1 (§18), so a Palworld ServerId is as unknown as a made-up one.</item>
/// <item>The port is the server's own registered port. The address is never stored here: the
/// authorizer always pairs the port with 127.0.0.1.</item>
/// <item>A friend's stream reaches whatever listens on that loopback port, so only a port that
/// can be nothing but this game server is admitted (<see cref="IsBridgeablePort"/>). A server
/// on any other port is left out, and its tickets are refused as for an unknown server.</item>
/// <item>Whether Connect is on comes from <see cref="ConnectEnabledServers"/> at the moment of
/// the lookup, so turning Connect off refuses the next connection without a refresh.</item>
/// </list>
/// <see cref="IConnectServerCatalog.Find"/> is synchronous because the ticket verifier is, so
/// the registrations are read ahead of time by <see cref="RefreshAsync"/>. The host
/// authorization pipe refreshes before every <c>authorize</c>, so a port change or a deleted
/// server is picked up by the very next connection.
/// </summary>
public sealed class ConnectServerCatalog : IConnectServerCatalog
{
    /// <summary>
    /// Ports below this belong to Windows and system services (SMB on 445, RPC on 135, …).
    /// No Minecraft server needs one.
    /// </summary>
    public const int LowestBridgeablePort = 1024;

    /// <summary>
    /// The Agent's LAN HTTPS port unless it is started with <c>--lan-port</c> (the default of
    /// <c>AgentOptions.LanPort</c>). Kestrel binds it on every address, loopback included.
    /// </summary>
    public const int DefaultAgentLanPort = 5252;

    /// <summary>
    /// Loopback services at or above <see cref="LowestBridgeablePort"/> that a friend must never
    /// reach, even through a server registered on their port by mistake or by an import: Remote
    /// Desktop (3389), WinRM over HTTP and HTTPS (5985, 5986), WSDAPI (5357), and the default
    /// Palworld REST API (8212) and RCON (25575, also Minecraft's default RCON port). The last two
    /// are listed because a server registration carries only its game port; a REST or RCON port
    /// moved away from its default is not known here.
    /// </summary>
    private static readonly int[] SensitivePorts = [3389, 5357, 5985, 5986, 8212, 25575];

    private readonly IGameServerStore _store;
    private readonly ConnectEnabledServers _enabledServers;
    private readonly HashSet<int> _refusedPorts;
    private readonly object _publishGate = new();
    private long _refreshesStarted;
    private long _publishedRefresh;
    private volatile IReadOnlyDictionary<Guid, RegisteredServer> _servers =
        new Dictionary<Guid, RegisteredServer>();

    /// <summary>
    /// A catalog that refuses the Agent's default API ports (<see cref="DefaultAgentPorts"/>).
    /// An Agent started on other ports passes them to the other constructor.
    /// </summary>
    public ConnectServerCatalog(IGameServerStore store, ConnectEnabledServers enabledServers)
        : this(store, enabledServers, DefaultAgentPorts)
    {
    }

    /// <param name="agentPorts">
    /// Every port the Agent's own API listens on (loopback HTTP and LAN HTTPS). A server
    /// registered on one of them is never bridged.
    /// </param>
    public ConnectServerCatalog(IGameServerStore store, ConnectEnabledServers enabledServers, IEnumerable<int> agentPorts)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _enabledServers = enabledServers ?? throw new ArgumentNullException(nameof(enabledServers));
        ArgumentNullException.ThrowIfNull(agentPorts);
        _refusedPorts = [.. SensitivePorts, .. agentPorts];
    }

    /// <summary>
    /// The Agent's loopback API port (<see cref="AgentTransportDefaults.LoopbackApiUrl"/>) and
    /// <see cref="DefaultAgentLanPort"/>.
    /// </summary>
    public static IReadOnlyList<int> DefaultAgentPorts { get; } =
        [new Uri(AgentTransportDefaults.LoopbackApiUrl).Port, DefaultAgentLanPort];

    /// <summary>The switch the pipe server turns off together with ending the server's connections.</summary>
    internal ConnectEnabledServers EnabledServers => _enabledServers;

    /// <summary>
    /// Re-reads the registered servers. If the store fails, the exception propagates and the
    /// previous snapshot is kept; the caller refuses the connection rather than guessing.
    /// Refreshes may overlap (every <c>authorize</c> runs one, and so does the pipe server's
    /// periodic check). A read that started earlier never replaces one that started later, so
    /// a server just deleted or moved cannot come back from a slower read.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var refresh = Interlocked.Increment(ref _refreshesStarted);
        var definitions = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var servers = Bridgeable(definitions);
        lock (_publishGate)
        {
            if (refresh > _publishedRefresh)
            {
                _servers = servers;
                _publishedRefresh = refresh;
            }
        }
    }

    public ConnectServerEntry? Find(Guid serverId)
    {
        if (!_servers.TryGetValue(serverId, out var server))
        {
            return null;
        }

        return new ConnectServerEntry(
            server.Id,
            ConnectGameKind.Minecraft,
            ConnectProtocol.Tcp,
            server.Port,
            _enabledServers.IsEnabled(server.Id));
    }

    /// <summary>
    /// True when a friend's stream to 127.0.0.1:<paramref name="port"/> can only reach the one
    /// registered server: a valid port at or above <see cref="LowestBridgeablePort"/>, none of
    /// the refused ports, and no other registered server of any game on it. With two servers on
    /// one port, the one listening could be the one the friend was never approved for.
    /// </summary>
    private bool IsBridgeablePort(int port, IReadOnlyDictionary<int, int> serversPerPort) =>
        port is >= LowestBridgeablePort and <= 65535 &&
        !_refusedPorts.Contains(port) &&
        serversPerPort[port] == 1;

    private Dictionary<Guid, RegisteredServer> Bridgeable(IReadOnlyList<GameServerDefinition> definitions)
    {
        var serversPerPort = definitions
            .GroupBy(definition => definition.Port)
            .ToDictionary(group => group.Key, group => group.Count());
        var servers = new Dictionary<Guid, RegisteredServer>();
        foreach (var definition in definitions)
        {
            if (definition.Game == GameType.Minecraft && IsBridgeablePort(definition.Port, serversPerPort))
            {
                servers[definition.Id] = new RegisteredServer(definition.Id, definition.Port);
            }
        }

        return servers;
    }

    private sealed record RegisteredServer(Guid Id, int Port);
}
