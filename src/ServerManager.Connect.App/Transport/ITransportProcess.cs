namespace ServerManager.Connect.App.Transport;

/// <summary>Owns the lifetime of <c>1Salem.Connect.Transport.exe</c>.</summary>
public interface ITransportProcess
{
    /// <summary>
    /// Returns once the transport serves this user's pipe in the configured mode, starting it if
    /// needed. Throws <see cref="TransportException"/> when it cannot.
    /// </summary>
    Task EnsureRunningAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ends a transport this app started, and with it every session it carries. A transport it
    /// reused is not this app's to end and keeps running.
    /// </summary>
    void Stop();

    /// <summary>
    /// True when every transport process that has served this app has exited: the one it started
    /// (still held, or let go of once it had exited), and each running one it reused (started by
    /// another copy of the app, or left by an earlier run). Every local address they listened on
    /// is closed then, even though the pipe can no longer say so. False while any of them runs,
    /// when none has served yet, and when it cannot be told.
    /// </summary>
    bool UsedTransportsExited { get; }
}
