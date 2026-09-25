using System.ComponentModel;
using System.Diagnostics;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Services;

namespace ServerManager.Connect.App.Transport;

/// <summary>
/// Starts and stops the friend transport. The command line carries only the mode, the pipe name,
/// the pinned keyset file and either the state directory (tsnet) or the fake node id; auth keys
/// and session keys only ever travel over the verified pipe (§11). The child's environment is
/// scrubbed of every <c>TS_*</c>/<c>TSNET_*</c> variable, so an auth key or control-server
/// override lying around in the friend's environment can never reach it. A transport it starts
/// is put in this app's <see cref="KillOnCloseJob"/>, so it ends with the app even when the app
/// crashes or is killed.
/// </summary>
public sealed class TransportProcess : ITransportProcess, IDisposable
{
    private const string PipePathPrefix = @"\\.\pipe\";
    private const string KeysFileName = "ticket-keys.json";
    private const string StateDirectoryName = "state";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);

    private readonly ConnectAppSettings _settings;
    private readonly IBrokerClient _broker;
    private readonly ITransportClient _client;
    private readonly TransportServerVerifier _server;
    private readonly string _dataDirectory;
    private readonly string _pipeName;
    private readonly DiagnosticsLog? _log;
    private readonly Func<ProcessStartInfo, Process?> _start;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private KillOnCloseJob? _job;

    // Set once a transport this app started was released after it had exited: it served this app,
    // and every address it listened on went with it, although this app holds no process any more.
    private volatile bool _releasedExitedTransport;

    /// <param name="pipeName">The bare pipe name <paramref name="client"/> connects to (<see cref="FriendPipeName"/>).</param>
    /// <param name="server">
    /// The verifier <paramref name="client"/> checks each connection with. It is told which process
    /// this app started, so only that process can serve the pipe while it runs; it tells whether
    /// the running transports it accepted instead have exited (<see cref="UsedTransportsExited"/>).
    /// </param>
    /// <param name="log">
    /// Where a started transport that could not be tied to this app's lifetime is recorded; null
    /// for a caller without Diagnostics.
    /// </param>
    public TransportProcess(
        ConnectAppSettings settings,
        IBrokerClient broker,
        ITransportClient client,
        string dataDirectory,
        string pipeName,
        TransportServerVerifier server,
        DiagnosticsLog? log = null)
        : this(settings, broker, client, dataDirectory, pipeName, server, log, Process.Start)
    {
    }

    /// <param name="start">Starts the process; tests replace it to stand in a process of their own.</param>
    internal TransportProcess(
        ConnectAppSettings settings,
        IBrokerClient broker,
        ITransportClient client,
        string dataDirectory,
        string pipeName,
        TransportServerVerifier server,
        DiagnosticsLog? log,
        Func<ProcessStartInfo, Process?> start)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _pipeName = FriendPipeName.Validate(pipeName);
        _log = log;
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public bool UsedTransportsExited
    {
        get
        {
            var started = HeldTransportExited() ?? (_releasedExitedTransport ? true : null);
            var reused = _server.ReusedTransportsExited;
            if (started is null && reused is null)
            {
                // Nothing has served this app, so nothing is known about any listener.
                return false;
            }

            // A kind of transport that never served this app has no listener to wait for.
            return (started ?? true) && (reused ?? true);
        }
    }

    /// <summary>%LOCALAPPDATA%\1Salem Connect\transport: the keyset file and, in tsnet mode, node state.</summary>
    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "1Salem Connect", "transport");

    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReleaseExitedTransport();
            if (await IsServingAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await StartAsync(cancellationToken).ConfigureAwait(false);
            await WaitUntilServingAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        // While the handle is still open, so the id is never trusted once it could be reused.
        _server.TransportStopped(process.Id);
        try
        {
            if (process.HasExited)
            {
                _releasedExitedTransport = true;
            }
            else
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // It exited between the check and the kill.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        _job?.Dispose();
        _gate.Dispose();
    }

    internal ProcessStartInfo BuildStartInfo(string keysPath)
    {
        var startInfo = new ProcessStartInfo(_settings.TransportExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_settings.TransportExecutablePath) ?? _dataDirectory,

            // The transport keeps its own redacted log for Diagnostics (diag). Its output must not
            // go to whatever console or pipe this app was started with: it would mix into that
            // stream and hold it open for as long as the transport runs. See StartAsync.
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add(ExpectedMode);

        // Named explicitly, with the same name the client connects to. The transport takes the
        // full \\.\pipe\ path; .NET's pipe classes take the bare name.
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(PipePathPrefix + _pipeName);
        startInfo.ArgumentList.Add("--keys");
        startInfo.ArgumentList.Add(keysPath);
        if (_settings.TransportMode == TransportMode.Fake)
        {
            startInfo.ArgumentList.Add("--fake-node-id");
            startInfo.ArgumentList.Add(_settings.FakeNodeId ?? throw new InvalidOperationException("Fake mode needs a fake node id."));
        }
        else
        {
            startInfo.ArgumentList.Add("--state-dir");
            startInfo.ArgumentList.Add(Path.Combine(_dataDirectory, StateDirectoryName));
        }

        ScrubEnvironment(startInfo.Environment);
        return startInfo;
    }

    internal static void ScrubEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(IsTailscaleVariable).ToList())
        {
            environment.Remove(name);
        }
    }

    private string ExpectedMode => _settings.TransportMode == TransportMode.Fake ? "fake" : "tsnet";

    /// <summary>Whether the transport this app started and still holds has exited; null when it holds none.</summary>
    private bool? HeldTransportExited()
    {
        var process = Volatile.Read(ref _process);
        if (process is null)
        {
            return null;
        }

        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // Released by a concurrent Stop: then nothing is known here any more.
            return false;
        }
    }

    private static bool IsTailscaleVariable(string name) =>
        name.StartsWith("TS_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("TSNET_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lets go of the transport this app started once it has exited. Until then the verifier
    /// accepts only that process id, so a genuine transport another copy of the app started since
    /// would be refused as untrusted for the rest of this run. <see cref="Stop"/> releases the id
    /// while the handle still reserves it.
    /// </summary>
    private void ReleaseExitedTransport()
    {
        if (HeldTransportExited() == true)
        {
            Stop();
        }
    }

    /// <summary>
    /// False only when nothing answers on the pipe. A transport that is there but did not answer
    /// in time (<see cref="TransportErrorCodes.NoAnswer"/>) or is not this app's
    /// (<see cref="TransportErrorCodes.Untrusted"/>) fails the call instead: starting another one
    /// would kill a busy transport and its sessions, or could not take the pipe anyway.
    /// </summary>
    private async Task<bool> IsServingAsync(CancellationToken cancellationToken)
    {
        TransportHello hello;
        try
        {
            hello = await _client.HelloAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException exception) when (exception.Code == TransportErrorCodes.Unavailable)
        {
            return false;
        }

        // A transport left running (say, from an earlier start in the other mode) is not quietly
        // reused: a fake transport answering where a real one is expected must be noticed.
        if (hello.Mode != ExpectedMode)
        {
            throw new TransportException(TransportErrorCodes.ModeMismatch);
        }

        return true;
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settings.TransportExecutablePath))
        {
            throw new TransportException(TransportErrorCodes.Missing);
        }

        // The keyset is fetched fresh from the configured broker over HTTPS each start. It holds
        // public keys only; the transport pins exactly these for the life of the process.
        var keys = await _broker.GetTicketKeysAsync(cancellationToken).ConfigureAwait(false);
        var keysPath = await WriteKeysAsync(keys, cancellationToken).ConfigureAwait(false);

        Stop();
        Process process;
        try
        {
            process = _start(BuildStartInfo(keysPath)) ?? throw new TransportException(TransportErrorCodes.Exited);
        }
        catch (Win32Exception exception)
        {
            throw new TransportException(TransportErrorCodes.Missing, exception);
        }

        // Before the first hello: from now on only this process may serve the pipe.
        _server.TransportStarted(process.Id);
        _process = process;
        TieToThisApp(process);

        // Read and dropped (no handler is attached): an unread redirected stream fills up and
        // then blocks the transport's next log write, and with it the session that is logging.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>
    /// Writes the keyset where the transport is told to read it. A data folder this user cannot
    /// write is reported like any other transport that cannot start, not as a crash of the caller.
    /// </summary>
    private async Task<string> WriteKeysAsync(byte[] keys, CancellationToken cancellationToken)
    {
        var keysPath = Path.Combine(_dataDirectory, KeysFileName);
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            await File.WriteAllBytesAsync(keysPath, keys, cancellationToken).ConfigureAwait(false);
            return keysPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new TransportException(TransportErrorCodes.DataUnwritable, exception);
        }
    }

    /// <summary>
    /// Puts the started transport in this app's kill-on-close job, so a crash or a kill of the app
    /// cannot leave it running with its sessions and node up. Not a reason to fail the start: a
    /// transport outside the job works the same and is still stopped on a clean exit.
    /// </summary>
    private void TieToThisApp(Process process)
    {
        try
        {
            _job ??= KillOnCloseJob.Create();
            _job.Assign(process);
        }
        catch (Win32Exception exception)
        {
            _log?.Record("transport", $"The transport could not be tied to this app, so a crash of the app could leave it running: {exception.Message}");
        }
    }

    private async Task WaitUntilServingAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        try
        {
            while (true)
            {
                if (_process is null || _process.HasExited)
                {
                    throw new TransportException(TransportErrorCodes.Exited);
                }

                if (await IsServingAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new TransportException(TransportErrorCodes.Unavailable);
                }

                await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is TransportException or OperationCanceledException)
        {
            // A transport that never served its pipe (or answered wrongly, or someone else answered
            // for it) is not left running half-started.
            Stop();
            throw;
        }
    }
}
