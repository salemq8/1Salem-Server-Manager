namespace ServerManager.Connect.App.Transport;

/// <summary><c>hello</c>: protocol version, <c>fake</c> or <c>tsnet</c>, and the transport build.</summary>
public sealed record TransportHello(int ProtocolVersion, string Mode, string Version);

/// <summary>One node (one per owner tailnet). <see cref="NodeId"/> is null until it has enrolled.</summary>
public sealed record TransportNode(string Node, string? NodeId, string State);

/// <summary>One open session. <see cref="State"/> is <c>listening</c> or <c>expired</c>.</summary>
public sealed record TransportSession(string SessionId, string ServerId, string Local, string State);

public sealed record TransportStatus(IReadOnlyList<TransportNode> Nodes, IReadOnlyList<TransportSession> Sessions);

/// <summary><c>open</c>: the session and its loopback address, for example <c>127.0.0.1:18211</c>.</summary>
public sealed record TransportOpened(string SessionId, string Local);

public sealed record TransportSessionDetail(
    string SessionId,
    string Local,
    string State,
    string Node,
    string HostBridge,
    int Connections,
    long ExpiresAt);

/// <summary><c>diag</c>: technical detail for the Diagnostics page only. The log is already redacted by the transport.</summary>
public sealed record TransportDiagnostics(
    string Mode,
    string Version,
    IReadOnlyList<string> TicketKeyIds,
    IReadOnlyList<TransportNode> Nodes,
    IReadOnlyList<TransportSessionDetail> Sessions,
    IReadOnlyList<string> Log);
