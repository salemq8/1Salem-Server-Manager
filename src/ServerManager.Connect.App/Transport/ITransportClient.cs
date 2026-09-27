namespace ServerManager.Connect.App.Transport;

/// <summary>
/// The friend pipe to <c>1Salem.Connect.Transport.exe</c> (contract §11). No operation takes a
/// destination: the transport only ever dials the host bridge inside a ticket whose signature it
/// verified, so nothing this app sends can point it anywhere else. Failures are
/// <see cref="TransportException"/>.
/// </summary>
public interface ITransportClient
{
    Task<TransportHello> HelloAsync(CancellationToken cancellationToken);

    Task<TransportStatus> StatusAsync(CancellationToken cancellationToken);

    /// <summary>Hands a one-off <c>tskey-auth-</c> key to the transport. Returns the new node id.</summary>
    Task<string> EnrollAsync(string node, string authKey, string hostname, CancellationToken cancellationToken);

    /// <summary>Stops an owner's stale node and safely removes only that node's state directory.</summary>
    Task ForgetAsync(string node, CancellationToken cancellationToken);

    /// <summary>Opens a loopback listener for a ticket. <paramref name="preferredPort"/> 0 means 18211.</summary>
    Task<TransportOpened> OpenAsync(string node, string ticket, string sessionKey, int preferredPort, CancellationToken cancellationToken);

    Task RefreshAsync(string sessionId, string ticket, string sessionKey, CancellationToken cancellationToken);

    Task CloseAsync(string sessionId, CancellationToken cancellationToken);

    Task<TransportDiagnostics> DiagnosticsAsync(CancellationToken cancellationToken);
}
