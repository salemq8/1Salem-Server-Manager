using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using ServerManager.Connect.Core.Diagnostics;
using ServerManager.Connect.Core.Pipes;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// Runs <c>1Salem.Connect.Host.Transport.exe</c> for the Agent and keeps it running
/// (contract §3, §11):
/// <list type="bullet">
/// <item>The sidecar gets a scrubbed environment (<see cref="ConnectSidecarEnvironment"/>) and an
/// explicit command line. Nothing secret is ever on that command line; the host node's auth
/// key would arrive over the sidecar's own pipe.</item>
/// <item>It is told which account must own the authorization pipe: this process's account,
/// which is the owner <see cref="ConnectPipeSecurity"/> gives the pipe the Agent serves. A
/// squatter under any other account is refused before the sidecar sends a ticket to it.</item>
/// <item>If it exits, it is restarted after a capped exponential delay that resets after a
/// healthy run. Stopping the supervisor ends the sidecar and never restarts it.</item>
/// </list>
/// Output lines are logged through <see cref="SecretRedactor"/>. The sidecar already redacts
/// its own log; this is the second layer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectHostTransportSupervisor : IAsyncDisposable
{
    private readonly ConnectHostTransportOptions _options;
    private readonly IConnectSidecarProcessRunner _runner;
    private readonly TimeProvider _clock;
    private readonly ILogger<ConnectHostTransportSupervisor> _logger;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _run;
    private int _stopped;

    public ConnectHostTransportSupervisor(
        ConnectHostTransportOptions options,
        IConnectSidecarProcessRunner runner,
        TimeProvider clock,
        ILogger<ConnectHostTransportSupervisor> logger)
        : this(options, runner, clock, logger, ConnectSidecarEnvironment.Current())
    {
    }

    /// <param name="parentEnvironment">The environment to scrub; the Agent's own by default.</param>
    internal ConnectHostTransportSupervisor(
        ConnectHostTransportOptions options,
        IConnectSidecarProcessRunner runner,
        TimeProvider clock,
        ILogger<ConnectHostTransportSupervisor> logger,
        IEnumerable<KeyValuePair<string, string>> parentEnvironment)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = ConnectSidecarEnvironment.Scrub(parentEnvironment);
    }

    /// <summary>
    /// Starts the sidecar and its restart loop. Throws <see cref="FileNotFoundException"/> when
    /// the executable is not installed, because retrying cannot fix that.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) != 0, this);
        if (_run is not null)
        {
            throw new InvalidOperationException("The host transport supervisor is already running.");
        }

        if (!File.Exists(_options.ExecutablePath))
        {
            throw new FileNotFoundException("The 1Salem Connect host transport is not installed.", _options.ExecutablePath);
        }

        _run = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
    }

    /// <summary>Ends the sidecar and the restart loop. Safe to call more than once.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        if (_run is not null)
        {
            await _run.ConfigureAwait(false);
        }

        _stopping.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>The exact start request, built from the options and the scrubbed environment.</summary>
    internal ConnectSidecarStartInfo CreateStartInfo()
    {
        List<string> arguments =
        [
            "--mode", _options.Mode == ConnectTransportMode.Tsnet ? "tsnet" : "fake",
            "--authz-pipe", @"\\.\pipe\" + _options.AuthorizationPipeName,
            "--expected-authz-owner", ConnectPipeSecurity.CurrentUser.Value
        ];
        if (_options.StateDirectory is not null)
        {
            arguments.Add("--state-dir");
            arguments.Add(_options.StateDirectory);
        }

        arguments.Add("--bridge-listen");
        arguments.Add(_options.BridgeListen);
        return new ConnectSidecarStartInfo(
            _options.ExecutablePath,
            arguments,
            _environment,
            Path.GetDirectoryName(_options.ExecutablePath)!);
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        var backoff = new ConnectRestartBackoff(_options.InitialRestartDelay, _options.MaximumRestartDelay);
        var startInfo = CreateStartInfo();
        while (!stopping.IsCancellationRequested)
        {
            var startedAt = _clock.GetTimestamp();
            if (!await RunOnceAsync(startInfo, stopping).ConfigureAwait(false))
            {
                return;
            }

            if (_clock.GetElapsedTime(startedAt) >= _options.StableRunTime)
            {
                backoff.Reset();
            }

            var delay = backoff.Next();
            _logger.LogInformation("1Salem Connect host transport restarts in {Delay}.", delay);
            try
            {
                await Task.Delay(delay, _clock, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <returns>False once the supervisor is stopping; true when the sidecar should be restarted.</returns>
    private async Task<bool> RunOnceAsync(ConnectSidecarStartInfo startInfo, CancellationToken stopping)
    {
        IConnectSidecarProcess process;
        try
        {
            process = _runner.Start(startInfo, line =>
                _logger.LogInformation("host transport: {Line}", SecretRedactor.Redact(line)));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "1Salem Connect host transport could not start: {Error}",
                SecretRedactor.Redact(exception.Message));
            return !stopping.IsCancellationRequested;
        }

        using (process)
        {
            _logger.LogInformation(
                "1Salem Connect host transport started as process {ProcessId} in {Mode} mode.",
                process.Id,
                _options.Mode);
            try
            {
                var exitCode = await process.WaitForExitAsync(stopping).ConfigureAwait(false);
                _logger.LogWarning("1Salem Connect host transport exited with code {ExitCode}.", exitCode);
                return !stopping.IsCancellationRequested;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
                return false;
            }
        }
    }

    private async Task StopProcessAsync(IConnectSidecarProcess process)
    {
        try
        {
            await process.StopAsync(_options.StopTimeout, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("1Salem Connect host transport stopped.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "1Salem Connect host transport did not stop cleanly: {Error}",
                SecretRedactor.Redact(exception.Message));
        }
    }
}
