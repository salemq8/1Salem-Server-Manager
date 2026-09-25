using System.ComponentModel;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;

namespace ServerManager.Connect.App.Transport;

/// <summary>
/// Decides whether the process serving the friend pipe is this app's transport, before anything
/// is written to it. The pipe name is predictable and the owner check
/// (<c>ConnectPipeSecurity.VerifyServerOwner</c>) only shuts out other accounts: a sandboxed,
/// lower-integrity process of this same user owns its pipes as this user too, and could create
/// the name first to collect auth keys and session keys.
/// <list type="bullet">
/// <item>While this app runs the transport it started, the server must be exactly that process.
/// Its id cannot be reused while <see cref="TransportProcess"/> holds the process handle, which
/// it does until it reports the transport stopped.</item>
/// <item>Otherwise (a transport started by another copy of this app, or left running by an
/// earlier run, is reused) the server's image must be the configured transport executable,
/// running at no lower integrity level than this app: a sandboxed process can start the real
/// executable, but only at its own lower level. The server is opened once and both are read
/// through that handle, which is then kept: its id cannot pass to another process while it is
/// open, and when that process exits it is known exactly (<see cref="ReusedTransportsExited"/>),
/// as it is for a transport this app started.</item>
/// </list>
/// Anything that cannot be read is refused.
/// </summary>
public sealed class TransportServerVerifier : IDisposable
{
    private readonly string _transportExecutablePath;
    private readonly IProcessInspector _inspector;

    // Every reused transport that passed, by id, from its verification until it is known to have
    // exited or this verifier is disposed. Also the lock for itself and _disposed.
    private readonly Dictionary<int, IOpenedProcess> _reused = [];
    private bool _disposed;

    // 0 = none: process id 0 is the idle process, never a pipe server.
    private int _startedProcessId;

    internal TransportServerVerifier(string transportExecutablePath, IProcessInspector inspector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportExecutablePath);
        _transportExecutablePath = Path.GetFullPath(transportExecutablePath);
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    /// <summary>The verifier for a transport started from (or already running as) <paramref name="transportExecutablePath"/>.</summary>
    public static TransportServerVerifier ForTransport(string transportExecutablePath) =>
        new(transportExecutablePath, new WindowsProcessInspector());

    /// <summary>
    /// Whether every reused transport (one this app did not start) that served the pipe has exited
    /// since; null when none has. The local addresses a transport listened on close with it.
    /// </summary>
    internal bool? ReusedTransportsExited
    {
        get
        {
            lock (_reused)
            {
                return _reused.Count == 0 ? null : _reused.Values.All(process => process.HasExited);
            }
        }
    }

    /// <summary>From now on only <paramref name="processId"/> may serve the pipe.</summary>
    public void TransportStarted(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        Volatile.Write(ref _startedProcessId, processId);
    }

    /// <summary>
    /// Called before the handle of <paramref name="processId"/> is released, so its id is never
    /// trusted after it could belong to another process.
    /// </summary>
    public void TransportStopped(int processId) =>
        Interlocked.CompareExchange(ref _startedProcessId, 0, processId);

    /// <summary>Throws <see cref="TransportServerUntrustedException"/> unless this app's transport serves <paramref name="pipe"/>.</summary>
    public void Verify(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        int serverProcessId;
        try
        {
            serverProcessId = _inspector.ServerProcessId(pipe.SafePipeHandle);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ObjectDisposedException or OverflowException)
        {
            throw new TransportServerUntrustedException("The process serving the friend pipe could not be identified.", exception);
        }

        VerifyServerProcess(serverProcessId);
    }

    /// <summary>
    /// Releases the reused transports' handles. Afterwards a reused transport is refused, since it
    /// could no longer be held; the transport this app started is still accepted by its id.
    /// </summary>
    public void Dispose()
    {
        lock (_reused)
        {
            _disposed = true;
            foreach (var process in _reused.Values)
            {
                process.Dispose();
            }

            _reused.Clear();
        }
    }

    internal void VerifyServerProcess(int serverProcessId)
    {
        var started = Volatile.Read(ref _startedProcessId);
        if (started != 0)
        {
            if (serverProcessId != started)
            {
                throw new TransportServerUntrustedException("The friend pipe is served by another process than the transport this app started.");
            }

            return;
        }

        var server = _inspector.Open(serverProcessId) ??
            throw new TransportServerUntrustedException("The process serving the friend pipe could not be inspected.");
        try
        {
            VerifyIsTransport(server.Inspect());
        }
        catch
        {
            server.Dispose();
            throw;
        }

        Keep(serverProcessId, server);
    }

    private void VerifyIsTransport(InspectedProcess? server)
    {
        if (server is null)
        {
            throw new TransportServerUntrustedException("The process serving the friend pipe could not be inspected.");
        }

        if (!string.Equals(Normalize(server.ImagePath), _transportExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new TransportServerUntrustedException("The friend pipe is served by a process that is not the configured transport.");
        }

        using var self = _inspector.Open(Environment.ProcessId);
        var own = self?.Inspect() ??
            throw new TransportServerUntrustedException("This app's own integrity level could not be read.");
        if (server.IntegrityLevel < own.IntegrityLevel)
        {
            throw new TransportServerUntrustedException("The transport serving the friend pipe runs at a lower integrity level than this app.");
        }
    }

    /// <summary>Takes <paramref name="server"/> over: it is kept, or released when it is not needed.</summary>
    private void Keep(int processId, IOpenedProcess server)
    {
        lock (_reused)
        {
            if (_disposed)
            {
                server.Dispose();
                throw new ObjectDisposedException(nameof(TransportServerVerifier));
            }

            // Already kept: the handle kept for this id holds it, so it is the same process.
            if (_reused.ContainsKey(processId))
            {
                server.Dispose();
                return;
            }

            // Those that have exited have nothing more to tell, so they need not stay open; the
            // answer is unchanged, since "every one has exited" already held for them.
            foreach (var (id, exited) in _reused.Where(entry => entry.Value.HasExited).ToList())
            {
                exited.Dispose();
                _reused.Remove(id);
            }

            _reused.Add(processId, server);
        }
    }

    private static string? Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>What <see cref="TransportServerVerifier"/> needs to know about processes; the seam tests replace.</summary>
internal interface IProcessInspector
{
    /// <summary>The id of the process serving the other end of a connected pipe.</summary>
    int ServerProcessId(SafePipeHandle pipe);

    /// <summary>Opens the process for reading and for telling when it has exited, or null when it cannot be opened.</summary>
    IOpenedProcess? Open(int processId);
}

/// <summary>
/// A process held open by handle. While it is open the process's id cannot be given to another
/// process, so everything read through it is about the one process that was opened.
/// </summary>
internal interface IOpenedProcess : IDisposable
{
    /// <summary>True once the process has exited; false while it runs, or when that cannot be read.</summary>
    bool HasExited { get; }

    /// <summary>The process's image path and integrity level, or null when either cannot be read.</summary>
    InspectedProcess? Inspect();
}

/// <param name="IntegrityLevel">The mandatory label's RID: 0x1000 low, 0x2000 medium, 0x3000 high.</param>
internal sealed record InspectedProcess(string ImagePath, int IntegrityLevel);

/// <summary>The friend pipe is not served by this app's transport. Nothing was sent to it. The message is safe to log.</summary>
public sealed class TransportServerUntrustedException : Exception
{
    public TransportServerUntrustedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
