using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Minecraft;

namespace ServerManager.Infrastructure.Processes;

public sealed class ProcessSupervisor(
    ILogger<ProcessSupervisor> logger,
    IAuditLogStore auditLogStore,
    IProcessTreeDiscovery? processTreeDiscovery = null) :
    IProcessSupervisor,
    IProcessResourceController,
    IConsoleService,
    ILogStreamService,
    IMinecraftConsoleChannel,
    IDisposable
{
    private const string NoConsoleMessage =
        "This server was started before the Agent last restarted, so its console is not connected. " +
        "Restart the server from 1Salem to use console commands and live controls.";

    private readonly IProcessTreeDiscovery _processTreeDiscovery =
        processTreeDiscovery ?? new WindowsProcessTreeDiscovery();
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<Guid, ManagedProcess> _processes = new();
    private readonly ConcurrentDictionary<Guid, ProcessLogBuffer> _logs = new();
    private readonly ConcurrentDictionary<Guid, RestartPolicy> _restartPolicies = new();
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _restartAttempts = new();
    private int _disposed;

    public async Task<ProcessSnapshot> StartAsync(
        GameServerDefinition server,
        ProcessLaunchSpec launchSpec,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(launchSpec);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLaunchSpec(launchSpec);

        if (_processes.TryGetValue(server.Id, out var existing) && !existing.Process.HasExited)
        {
            return existing.CreateSnapshot();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = launchSpec.FileName,
            WorkingDirectory = launchSpec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = launchSpec.RedirectStandardInput,
            RedirectStandardOutput = launchSpec.RedirectStandardOutput,
            RedirectStandardError = launchSpec.RedirectStandardError
        };
        if (launchSpec.ArgumentList is { Count: > 0 })
        {
            foreach (var argument in launchSpec.ArgumentList)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else
        {
            startInfo.Arguments = launchSpec.Arguments;
        }

        foreach (var (key, value) in launchSpec.Environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var logBuffer = _logs.GetOrAdd(server.Id, _ => new ProcessLogBuffer());
        var managed = new ManagedProcess(server, launchSpec, process, logBuffer, _processTreeDiscovery);
        process.OutputDataReceived += (_, args) =>
            PublishOutput(managed, args.Data, false);
        process.ErrorDataReceived += (_, args) =>
            PublishOutput(managed, args.Data, true);
        process.Exited += (_, _) => _ = HandleExitAsync(managed);

        if (!_processes.TryAdd(server.Id, managed))
        {
            process.Dispose();
            throw new InvalidOperationException($"Server {server.Id} is already being started.");
        }

        try
        {
            managed.SetState(ServerState.Starting);
            if (!process.Start())
            {
                throw new InvalidOperationException($"Windows did not start {launchSpec.FileName}.");
            }

            managed.MarkStarted();
            managed.Job.Assign(process);
            if (launchSpec.RedirectStandardOutput)
            {
                process.BeginOutputReadLine();
            }

            if (launchSpec.RedirectStandardError)
            {
                process.BeginErrorReadLine();
            }

            managed.SetState(ServerState.Running);
            logBuffer.Publish(
                new LogEntry(
                    DateTimeOffset.UtcNow,
                    "Information",
                    "Agent",
                    $"Process started with PID {process.Id}."));
            logger.LogInformation(
                "Started {Game} server {ServerId} with PID {ProcessId}.",
                server.Game,
                server.Id,
                process.Id);
            await auditLogStore.WriteAsync(
                "Agent",
                "ProcessStarted",
                server.Id.ToString(),
                true,
                $"PID {process.Id}",
                cancellationToken);
            return managed.CreateSnapshot();
        }
        catch
        {
            _processes.TryRemove(server.Id, out _);
            managed.Dispose();
            throw;
        }
    }

    public async Task<ProcessSnapshot?> AdoptAsync(
        GameServerDefinition server,
        ProcessLaunchSpec launchSpec,
        int? expectedProcessId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(launchSpec);
        ValidateLaunchSpec(launchSpec);
        cancellationToken.ThrowIfCancellationRequested();
        if (_processes.TryGetValue(server.Id, out var existing) &&
            !existing.Process.HasExited)
        {
            return existing.CreateSnapshot();
        }

        var process = FindAdoptableProcess(launchSpec.FileName, expectedProcessId);
        if (process is null)
        {
            return null;
        }

        var logBuffer = _logs.GetOrAdd(server.Id, _ => new ProcessLogBuffer());
        var managed = new ManagedProcess(server, launchSpec, process, logBuffer, _processTreeDiscovery);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ = HandleExitAsync(managed);
        if (!_processes.TryAdd(server.Id, managed))
        {
            process.Dispose();
            return _processes.TryGetValue(server.Id, out existing)
                ? existing.CreateSnapshot()
                : null;
        }

        try
        {
            managed.MarkAdopted();
            managed.Job.Assign(process);
            AttachPreExistingDescendantsToJob(managed);
            managed.SetState(ServerState.Running);
            logBuffer.Publish(new LogEntry(
                DateTimeOffset.UtcNow,
                "Information",
                "Agent",
                $"Re-adopted existing process PID {process.Id}."));
            await auditLogStore.WriteAsync(
                "Agent",
                "ProcessReadopted",
                server.Id.ToString(),
                true,
                $"PID {process.Id}",
                cancellationToken);
            return managed.CreateSnapshot();
        }
        catch
        {
            _processes.TryRemove(server.Id, out _);
            managed.Dispose();
            throw;
        }
    }

    public async Task<OperationResult> StopAsync(
        Guid serverId,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!_processes.TryGetValue(serverId, out var managed) || managed.Process.HasExited)
        {
            return OperationResult.Fail("ProcessNotRunning", "The server process is not running.");
        }

        managed.ExpectedExit = true;
        managed.SetState(ServerState.Stopping);
        try
        {
            if (force)
            {
                managed.Process.Kill(true);
            }
            else
            {
                await RequestGracefulStopAsync(managed, cancellationToken);
                var completed = await Task.WhenAny(
                    managed.ExitCompletion.Task,
                    Task.Delay(GracefulStopTimeout, CancellationToken.None));
                cancellationToken.ThrowIfCancellationRequested();
                if (completed != managed.ExitCompletion.Task && !managed.Process.HasExited)
                {
                    logger.LogWarning(
                        "Graceful stop timed out for server {ServerId}; terminating its process tree.",
                        serverId);
                    managed.Process.Kill(true);
                }
            }

            await managed.ExitCompletion.Task.WaitAsync(cancellationToken);
            await auditLogStore.WriteAsync(
                "Agent",
                force ? "ProcessForceStopped" : "ProcessStopped",
                serverId.ToString(),
                true,
                cancellationToken: cancellationToken);
            managed.Dispose();
            return OperationResult.Ok();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogError(exception, "Failed to stop server {ServerId}.", serverId);
            await auditLogStore.WriteAsync(
                "Agent",
                "ProcessStopFailed",
                serverId.ToString(),
                false,
                exception.Message,
                cancellationToken);
            return OperationResult.Fail("ProcessStopFailed", exception.Message);
        }
    }

    public async Task<ProcessSnapshot> RestartAsync(
        GameServerDefinition server,
        ProcessLaunchSpec launchSpec,
        CancellationToken cancellationToken = default)
    {
        if (_processes.TryGetValue(server.Id, out var managed) && !managed.Process.HasExited)
        {
            managed.SetState(ServerState.Restarting);
            var result = await StopAsync(server.Id, false, cancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }
        }

        return await StartAsync(server, launchSpec, cancellationToken);
    }

    public Task<ProcessSnapshot?> GetSnapshotAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _processes.TryGetValue(serverId, out var managed)
                ? managed.CreateSnapshot()
                : null);
    }

    public Task<IReadOnlyList<ProcessSnapshot>> GetAllSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ProcessSnapshot> snapshots = _processes.Values
            .Select(process => process.CreateSnapshot())
            .ToArray();
        return Task.FromResult(snapshots);
    }

    public void ConfigureRestartPolicy(Guid serverId, RestartPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaximumAttempts < 1 ||
            policy.Delay < TimeSpan.Zero ||
            policy.AttemptWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "Restart policy values must use a positive window and attempt count.");
        }

        _restartPolicies[serverId] = policy;
    }

    public async Task<OperationResult> ApplyResourcesAsync(
        Guid serverId,
        ProcessPriorityClass priority,
        long? cpuAffinityMask,
        CancellationToken cancellationToken = default,
        long? hardMemoryLimitBytes = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (priority == ProcessPriorityClass.RealTime)
        {
            return OperationResult.Fail(
                "RealtimePriorityRejected",
                "Realtime process priority is never permitted.");
        }

        if (cpuAffinityMask is <= 0)
        {
            return OperationResult.Fail(
                "InvalidCpuAffinity",
                "CPU affinity must select at least one processor.");
        }

        if (!_processes.TryGetValue(serverId, out var managed) || managed.Process.HasExited)
        {
            return OperationResult.Fail("ProcessNotRunning", "The server process is not running.");
        }

        try
        {
            var processIds = OperatingSystem.IsWindows()
                ? _processTreeDiscovery.DescendantsOf(managed.Process.Id)
                : [managed.Process.Id];
            if (processIds.Count == 0)
            {
                return OperationResult.Fail(
                    "ProcessNotRunning",
                    "The managed server process tree is empty.");
            }

            foreach (var processId in processIds.Distinct())
            {
                using var member = processId == managed.Process.Id
                    ? null
                    : Process.GetProcessById(processId);
                var target = member ?? managed.Process;
                target.Refresh();
                target.PriorityClass = priority;
                if (cpuAffinityMask is not null)
                {
                    if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                    {
                        return OperationResult.Fail(
                            "CpuAffinityUnsupported",
                            "CPU affinity is not supported on this operating system.");
                    }

                    target.ProcessorAffinity = new IntPtr(cpuAffinityMask.Value);
                }

                target.Refresh();
                if (target.PriorityClass != priority ||
                    (cpuAffinityMask is not null &&
                     (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) &&
                     target.ProcessorAffinity.ToInt64() != cpuAffinityMask.Value))
                {
                    return OperationResult.Fail(
                        "ResourceVerificationFailed",
                        $"Priority or CPU affinity verification failed for process {processId}.");
                }
            }

            managed.Job.SetMemoryLimit(hardMemoryLimitBytes);
            managed.LastAppliedPriority = priority;
            managed.LastAppliedAffinity = cpuAffinityMask;
            var uncoveredByJob = hardMemoryLimitBytes is null || !OperatingSystem.IsWindows()
                ? []
                : processIds.Except(managed.Job.GetProcessIds()).ToArray();
            if (uncoveredByJob.Length > 0)
            {
                // Priority/affinity above were applied directly per-process and cover every
                // discovered descendant regardless of Job membership. The hard memory limit
                // is enforced by Windows at the Job level, so it only ever covers whichever
                // descendants actually joined this server's Job Object -- typically every
                // descendant of a process this Agent instance itself started, but possibly
                // not every descendant of one it re-adopted (see AttachPreExistingDescendantsToJob).
                logger.LogWarning(
                    "Hard memory limit for server {ServerId} does not cover process(es) {ProcessIds}; they are outside this server's Job Object and were not restarted to force membership.",
                    serverId,
                    string.Join(',', uncoveredByJob));
            }

            await auditLogStore.WriteAsync(
                "ResourceGovernor",
                "ProcessResourcesApplied",
                serverId.ToString(),
                true,
                $"Priority={priority}; Affinity={cpuAffinityMask?.ToString() ?? "All"}; Processes={string.Join(',', processIds)}; HardMemoryLimit={hardMemoryLimitBytes?.ToString() ?? "Disabled"}; UncoveredByMemoryLimit={(uncoveredByJob.Length > 0 ? string.Join(',', uncoveredByJob) : "None")}",
                cancellationToken);
            return OperationResult.Ok();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            PlatformNotSupportedException or
            ArgumentException)
        {
            logger.LogWarning(
                exception,
                "Could not apply resource policy to server {ServerId}.",
                serverId);
            return OperationResult.Fail(
                exception is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
                    ? "PermissionDenied"
                    : "ResourcePolicyFailed",
                exception.Message);
        }
    }

    public async Task<OperationResult> SendCommandAsync(
        Guid serverId,
        string command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command) ||
            command.Length > 512 ||
            command.Contains('\r', StringComparison.Ordinal) ||
            command.Contains('\n', StringComparison.Ordinal))
        {
            return OperationResult.Fail(
                "InvalidConsoleCommand",
                "Console commands must contain 1 to 512 characters and cannot contain newlines.");
        }

        if (!_processes.TryGetValue(serverId, out var managed) ||
            managed.Process.HasExited ||
            !managed.Spec.RedirectStandardInput)
        {
            return OperationResult.Fail(
                "ConsoleUnavailable",
                "The server process console is not available.");
        }

        // A process re-adopted after an Agent restart was opened without pipes; writing to it
        // would throw, so say what is going on instead.
        if (!managed.HasConsole)
        {
            return OperationResult.Fail("ConsoleUnavailable", NoConsoleMessage);
        }

        await managed.InputLock.WaitAsync(cancellationToken);
        try
        {
            await managed.Process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
            await managed.Process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            managed.InputLock.Release();
        }

        managed.Logs.Publish(
            new LogEntry(
                DateTimeOffset.UtcNow,
                "Information",
                "Console",
                $"> {command}"));
        return OperationResult.Ok();
    }

    public event EventHandler<Guid>? ServerReady;

    public MinecraftConsoleState GetState(Guid serverId)
    {
        if (!_processes.TryGetValue(serverId, out var managed))
        {
            return MinecraftConsoleState.NotRunning;
        }

        try
        {
            if (managed.Process.HasExited)
            {
                return MinecraftConsoleState.NotRunning;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return MinecraftConsoleState.NotRunning;
        }

        if (!managed.HasConsole)
        {
            return MinecraftConsoleState.NoConsole;
        }

        return managed.IsReady ? MinecraftConsoleState.Ready : MinecraftConsoleState.Starting;
    }

    public async Task<ConsoleExchangeResult> ExchangeAsync(
        Guid serverId,
        string command,
        Func<string, bool> isAnswer,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isAnswer);
        var state = GetState(serverId);
        if (state != MinecraftConsoleState.Ready || !_processes.TryGetValue(serverId, out var managed))
        {
            return new ConsoleExchangeResult(
                state switch
                {
                    MinecraftConsoleState.NoConsole => OperationResult.Fail("ConsoleUnavailable", NoConsoleMessage),
                    MinecraftConsoleState.Starting => OperationResult.Fail("ServerStarting", "The server is still starting."),
                    _ => OperationResult.Fail("ServerNotRunning", "The server is not running.")
                },
                null,
                []);
        }

        // Answers carry no correlation id, so one exchange at a time per server; the listener is
        // in place before the command is written, so a fast answer cannot be missed.
        await managed.ExchangeLock.WaitAsync(cancellationToken);
        try
        {
            var lines = new List<string>();
            var answered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLine(string line)
            {
                lock (lines)
                {
                    lines.Add(line);
                }

                try
                {
                    if (isAnswer(line))
                    {
                        answered.TrySetResult(line);
                    }
                }
                catch (Exception exception)
                {
                    answered.TrySetException(exception);
                }
            }

            managed.AddOutputListener(OnLine);
            try
            {
                var sent = await SendCommandAsync(serverId, command, cancellationToken);
                if (!sent.Success)
                {
                    return new ConsoleExchangeResult(sent, null, []);
                }

                try
                {
                    var answer = await answered.Task.WaitAsync(timeout, cancellationToken);
                    return new ConsoleExchangeResult(OperationResult.Ok(), answer, Copy(lines));
                }
                catch (TimeoutException)
                {
                    return new ConsoleExchangeResult(
                        OperationResult.Fail("ConsoleTimeout", "The server did not answer in time."),
                        null,
                        Copy(lines));
                }
            }
            finally
            {
                managed.RemoveOutputListener(OnLine);
            }
        }
        finally
        {
            managed.ExchangeLock.Release();
        }

        static IReadOnlyList<string> Copy(List<string> lines)
        {
            lock (lines)
            {
                return [.. lines];
            }
        }
    }

    public IAsyncEnumerable<LogEntry> StreamAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        _logs.GetOrAdd(serverId, _ => new ProcessLogBuffer())
            .SubscribeAsync(cancellationToken);

    public IReadOnlyList<LogEntry> GetRecentLogs(Guid serverId) =>
        _logs.TryGetValue(serverId, out var buffer)
            ? buffer.Snapshot()
            : [];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var managed in _processes.Values)
        {
            managed.Dispose();
        }

        _processes.Clear();
    }

    private static void ValidateLaunchSpec(ProcessLaunchSpec spec)
    {
        if (!Path.IsPathFullyQualified(spec.FileName) || !File.Exists(spec.FileName))
        {
            throw new FileNotFoundException(
                "Server executables must use an existing absolute path.",
                spec.FileName);
        }

        if (!Path.IsPathFullyQualified(spec.WorkingDirectory) ||
            !Directory.Exists(spec.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The server working directory does not exist: {spec.WorkingDirectory}");
        }
    }

    private static Process? FindAdoptableProcess(
        string executablePath,
        int? expectedProcessId)
    {
        var expectedPath = Path.GetFullPath(executablePath);
        if (expectedProcessId is { } processId)
        {
            try
            {
                var expected = Process.GetProcessById(processId);
                if (!expected.HasExited && ProcessPathMatches(expected, expectedPath))
                {
                    return expected;
                }

                expected.Dispose();
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
        }

        var candidates = new List<Process>();
        foreach (var candidate in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(expectedPath)))
        {
            try
            {
                if (!candidate.HasExited && ProcessPathMatches(candidate, expectedPath))
                {
                    candidates.Add(candidate);
                }
                else
                {
                    candidate.Dispose();
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException)
            {
                candidate.Dispose();
            }
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        foreach (var candidate in candidates)
        {
            candidate.Dispose();
        }

        return null;
    }

    /// <summary>
    /// Best-effort brings every currently-live descendant of a just-adopted root into its
    /// Job Object, so resource governance (in particular the Job-enforced hard memory limit,
    /// which -- unlike priority/affinity -- cannot be applied directly per-process) covers
    /// them too. A descendant that already belongs to a different Job (Windows does not allow
    /// silently moving a process between Jobs without nested-job support) is skipped and
    /// logged rather than failing the whole adoption; process-tree discovery and telemetry
    /// remain fully correct either way, since they no longer depend on Job membership.
    /// </summary>
    private void AttachPreExistingDescendantsToJob(ManagedProcess managed)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var descendantId in _processTreeDiscovery.DescendantsOf(managed.Process.Id))
        {
            if (descendantId == managed.Process.Id)
            {
                continue;
            }

            try
            {
                using var descendant = Process.GetProcessById(descendantId);
                managed.Job.Assign(descendant);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException)
            {
                logger.LogWarning(
                    exception,
                    "Could not bring pre-existing descendant process {ProcessId} of server {ServerId} into its Job Object; resource-limit enforcement will not cover it unless the game restarts.",
                    descendantId,
                    managed.Server.Id);
            }
        }
    }

    private static bool ProcessPathMatches(Process process, string expectedPath) =>
        process.MainModule?.FileName is { } path &&
        Path.GetFullPath(path).Equals(expectedPath, StringComparison.OrdinalIgnoreCase);

    private static async Task RequestGracefulStopAsync(
        ManagedProcess managed,
        CancellationToken cancellationToken)
    {
        if (managed.Server.Game == GameType.Minecraft && managed.Spec.RedirectStandardInput && managed.HasConsole)
        {
            await managed.InputLock.WaitAsync(cancellationToken);
            try
            {
                await managed.Process.StandardInput.WriteLineAsync("stop".AsMemory(), cancellationToken);
                await managed.Process.StandardInput.FlushAsync(cancellationToken);
            }
            finally
            {
                managed.InputLock.Release();
            }

            return;
        }

        managed.Process.CloseMainWindow();
    }

    private void PublishOutput(ManagedProcess managed, string? line, bool standardError)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        managed.Logs.Publish(
            new LogEntry(
                DateTimeOffset.UtcNow,
                standardError ? "Error" : "Information",
                managed.Server.Game.ToString(),
                line,
                standardError));
        managed.PublishToListeners(line);

        // Ready is tracked per run, so a "Done" line from an earlier run can never count.
        if (managed.Server.Game == GameType.Minecraft &&
            MinecraftConsoleReplies.IsReady(line) &&
            managed.TryMarkReady())
        {
            var serverId = managed.Server.Id;
            _ = Task.Run(() =>
            {
                try
                {
                    ServerReady?.Invoke(this, serverId);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "A ready handler failed for server {ServerId}.", serverId);
                }
            });
        }
    }

    private async Task HandleExitAsync(ManagedProcess managed)
    {
        var expected = managed.ExpectedExit;
        int? exitCode = null;
        try
        {
            exitCode = managed.Process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        managed.SetState(expected ? ServerState.Stopped : ServerState.Crashed);
        managed.Logs.Publish(
            new LogEntry(
                DateTimeOffset.UtcNow,
                expected ? "Information" : "Error",
                "Agent",
                expected
                    ? $"Process exited with code {exitCode}."
                    : $"Process crashed with code {exitCode}."));

        if (_processes.TryGetValue(managed.Server.Id, out var current) &&
            ReferenceEquals(current, managed))
        {
            _processes.TryRemove(managed.Server.Id, out _);
        }

        logger.Log(
            expected ? LogLevel.Information : LogLevel.Error,
            "Server {ServerId} process exited with code {ExitCode}; expected={Expected}.",
            managed.Server.Id,
            exitCode,
            expected);
        await auditLogStore.WriteAsync(
            "Agent",
            expected ? "ProcessExited" : "ProcessCrashed",
            managed.Server.Id.ToString(),
            expected,
            $"ExitCode={exitCode}");

        var shouldRestart = !expected && ShouldAutoRestart(managed.Server.Id);
        var server = managed.Server;
        var spec = managed.Spec;
        managed.ExitCompletion.TrySetResult(exitCode);

        if (expected)
        {
            return;
        }

        managed.Dispose();
        if (!shouldRestart || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        var policy = _restartPolicies[server.Id];
        await Task.Delay(policy.Delay);
        try
        {
            await StartAsync(server, spec);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Automatic restart failed for server {ServerId}.",
                server.Id);
        }
    }

    private bool ShouldAutoRestart(Guid serverId)
    {
        if (!_restartPolicies.TryGetValue(serverId, out var policy) || !policy.Enabled)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var attempts = _restartAttempts.GetOrAdd(serverId, _ => new Queue<DateTimeOffset>());
        lock (attempts)
        {
            while (attempts.TryPeek(out var oldest) && now - oldest > policy.AttemptWindow)
            {
                attempts.Dequeue();
            }

            if (attempts.Count >= policy.MaximumAttempts)
            {
                logger.LogError(
                    "Crash-loop protection stopped automatic restarts for server {ServerId}.",
                    serverId);
                return false;
            }

            attempts.Enqueue(now);
            return true;
        }
    }

    private sealed class ManagedProcess(
        GameServerDefinition server,
        ProcessLaunchSpec spec,
        Process process,
        ProcessLogBuffer logs,
        IProcessTreeDiscovery processTreeDiscovery) : IDisposable
    {
        private readonly object _metricsSync = new();
        private readonly Dictionary<int, TimeSpan> _lastProcessorTimes = [];
        private DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
        private long _lastSampleTimestamp;
        private long _peakAggregateWorkingSetBytes;
        private int _state = (int)ServerState.Starting;

        public GameServerDefinition Server { get; } = server;

        public ProcessLaunchSpec Spec { get; } = spec;

        public Process Process { get; } = process;

        public ProcessLogBuffer Logs { get; } = logs;

        public WindowsJobObject Job { get; } = new();

        public bool ExpectedExit { get; set; }

        public ProcessPriorityClass? LastAppliedPriority { get; set; }

        public long? LastAppliedAffinity { get; set; }

        public TaskCompletionSource<int?> ExitCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True only for a process this Agent started with redirected pipes.</summary>
        public bool HasConsole { get; private set; }

        /// <summary>Serializes writes to stdin (commands and "stop").</summary>
        public SemaphoreSlim InputLock { get; } = new(1, 1);

        /// <summary>Held for a whole command-and-answer exchange.</summary>
        public SemaphoreSlim ExchangeLock { get; } = new(1, 1);

        private int _ready;
        private Action<string>[] _listeners = [];

        public bool IsReady => Volatile.Read(ref _ready) == 1;

        public bool TryMarkReady() => Interlocked.Exchange(ref _ready, 1) == 0;

        public void AddOutputListener(Action<string> listener)
        {
            lock (_metricsSync)
            {
                _listeners = [.. _listeners, listener];
            }
        }

        public void RemoveOutputListener(Action<string> listener)
        {
            lock (_metricsSync)
            {
                _listeners = _listeners.Where(existing => existing != listener).ToArray();
            }
        }

        public void PublishToListeners(string line)
        {
            foreach (var listener in Volatile.Read(ref _listeners))
            {
                listener(line);
            }
        }

        public void MarkStarted()
        {
            HasConsole = Spec.RedirectStandardInput;
            _startedAtUtc = DateTimeOffset.UtcNow;
            _lastSampleTimestamp = Stopwatch.GetTimestamp();
            _lastProcessorTimes[Process.Id] = Process.TotalProcessorTime;
        }

        public void MarkAdopted()
        {
            try
            {
                _startedAtUtc = new DateTimeOffset(Process.StartTime.ToUniversalTime());
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException)
            {
                _startedAtUtc = DateTimeOffset.UtcNow;
            }

            _lastSampleTimestamp = Stopwatch.GetTimestamp();
            _lastProcessorTimes[Process.Id] = Process.TotalProcessorTime;
        }

        public void SetState(ServerState state) =>
            Interlocked.Exchange(ref _state, (int)state);

        public ProcessSnapshot CreateSnapshot()
        {
            try
            {
                Process.Refresh();
                var members = CaptureProcessTree();
                var cpu = CalculateCpuPercent(members);
                var workingSet = members.Sum(member => member.WorkingSetBytes);
                var privateMemory = members.Sum(member => member.PrivateMemoryBytes);
                _peakAggregateWorkingSetBytes = Math.Max(
                    _peakAggregateWorkingSetBytes,
                    workingSet);
                var gameProcess = SelectGameProcess(members);
                var rootProcess = members.FirstOrDefault(
                    member => member.ProcessId == Process.Id);
                return new ProcessSnapshot(
                    Server.Id,
                    Process.Id,
                    (ServerState)Volatile.Read(ref _state),
                    _startedAtUtc,
                    workingSet,
                    cpu,
                    Process.HasExited ? Process.ExitCode : null,
                    privateMemory,
                    _peakAggregateWorkingSetBytes,
                    Process.PriorityClass,
                    GetAffinity(Process),
                    Spec.ArgumentList ?? [],
                    gameProcess?.ProcessId,
                    Math.Max(0, members.Count - 1),
                    rootProcess?.ExecutableName,
                    gameProcess?.ExecutableName,
                    members.Sum(member => member.ThreadCount));
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                PlatformNotSupportedException)
            {
                return new ProcessSnapshot(
                    Server.Id,
                    0,
                    (ServerState)Volatile.Read(ref _state),
                    _startedAtUtc,
                    0,
                    0,
                    null,
                    Arguments: Spec.ArgumentList ?? []);
            }
        }

        public void Dispose()
        {
            Process.Dispose();
            Job.Dispose();
        }

        private List<ProcessTreeMember> CaptureProcessTree()
        {
            var processIds = OperatingSystem.IsWindows()
                ? processTreeDiscovery.DescendantsOf(Process.Id)
                : [Process.Id];
            if (processIds.Count == 0 && !Process.HasExited)
            {
                processIds = [Process.Id];
            }

            var members = new List<ProcessTreeMember>(processIds.Count);
            foreach (var processId in processIds.Distinct())
            {
                try
                {
                    using var member = processId == Process.Id
                        ? null
                        : System.Diagnostics.Process.GetProcessById(processId);
                    var candidate = member ?? Process;
                    candidate.Refresh();
                    if (candidate.HasExited)
                    {
                        continue;
                    }

                    members.Add(new ProcessTreeMember(
                        candidate.Id,
                        candidate.ProcessName,
                        candidate.WorkingSet64,
                        candidate.PrivateMemorySize64,
                        candidate.TotalProcessorTime,
                        candidate.Threads.Count));
                }
                catch (Exception exception) when (
                    exception is ArgumentException or
                    InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException)
                {
                    // A child can exit between the Job query and the process read.
                }
            }

            return members;
        }

        private double CalculateCpuPercent(IReadOnlyList<ProcessTreeMember> members)
        {
            lock (_metricsSync)
            {
                var timestamp = Stopwatch.GetTimestamp();
                if (_lastSampleTimestamp == 0)
                {
                    _lastSampleTimestamp = timestamp;
                    ReplaceProcessorTimes(members);
                    return 0;
                }

                var elapsedSeconds =
                    (timestamp - _lastSampleTimestamp) / (double)Stopwatch.Frequency;
                if (elapsedSeconds <= 0)
                {
                    return 0;
                }

                var cpuSeconds = 0d;
                foreach (var member in members)
                {
                    if (_lastProcessorTimes.TryGetValue(
                            member.ProcessId,
                            out var previous))
                    {
                        cpuSeconds += Math.Max(
                            0,
                            (member.TotalProcessorTime - previous).TotalSeconds);
                    }
                }

                _lastSampleTimestamp = timestamp;
                ReplaceProcessorTimes(members);
                return Math.Clamp(
                    cpuSeconds / elapsedSeconds / Environment.ProcessorCount * 100,
                    0,
                    100);
            }
        }

        private void ReplaceProcessorTimes(
            IReadOnlyList<ProcessTreeMember> members)
        {
            _lastProcessorTimes.Clear();
            foreach (var member in members)
            {
                _lastProcessorTimes[member.ProcessId] = member.TotalProcessorTime;
            }
        }

        private static ProcessTreeMember? SelectGameProcess(
            IReadOnlyList<ProcessTreeMember> members)
        {
            string[] preferredNames =
            [
                "PalServer-Win64-Shipping-Cmd",
                "PalServer-Win64-Test-Cmd",
                "PalServer"
            ];
            foreach (var name in preferredNames)
            {
                var preferred = members.FirstOrDefault(
                    member => member.ExecutableName.Equals(
                        name,
                        StringComparison.OrdinalIgnoreCase));
                if (preferred is not null)
                {
                    return preferred;
                }
            }

            return members
                .OrderByDescending(member => member.WorkingSetBytes)
                .FirstOrDefault();
        }

        private static long? GetAffinity(Process process) =>
            OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
                ? process.ProcessorAffinity.ToInt64()
                : null;

        private sealed record ProcessTreeMember(
            int ProcessId,
            string ExecutableName,
            long WorkingSetBytes,
            long PrivateMemoryBytes,
            TimeSpan TotalProcessorTime,
            int ThreadCount);
    }
}
