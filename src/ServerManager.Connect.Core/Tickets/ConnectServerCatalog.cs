namespace ServerManager.Connect.Core.Tickets;

public enum ConnectGameKind
{
    Unknown = 0,
    Minecraft,
    Palworld
}

public enum ConnectProtocol
{
    Tcp,
    Udp
}

/// <summary>
/// What the Agent knows about one server, and the only source of the endpoint a friend may
/// reach. The port is always a local port on 127.0.0.1. No field anywhere in a ticket or a
/// preamble can change it.
/// </summary>
public sealed record ConnectServerEntry
{
    public ConnectServerEntry(
        Guid serverId,
        ConnectGameKind game,
        ConnectProtocol protocol,
        int localPort,
        bool connectEnabled)
    {
        if (localPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort), "Ports must be between 1 and 65535.");
        }

        ServerId = serverId;
        Game = game;
        Protocol = protocol;
        LocalPort = localPort;
        ConnectEnabled = connectEnabled;
    }

    public Guid ServerId { get; }

    public ConnectGameKind Game { get; }

    public ConnectProtocol Protocol { get; }

    public int LocalPort { get; }

    /// <summary>The owner has turned 1Salem Connect on for this server.</summary>
    public bool ConnectEnabled { get; }
}

/// <summary>Implemented by the Agent over its authoritative server store.</summary>
public interface IConnectServerCatalog
{
    ConnectServerEntry? Find(Guid serverId);
}
