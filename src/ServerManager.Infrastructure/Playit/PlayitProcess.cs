using System.Diagnostics;

namespace ServerManager.Infrastructure.Playit;

public interface IPlayitProcess : IDisposable
{
    event Action<string>? OutputReceived;
    event Action<string>? ErrorReceived;
    event Action<int>? Exited;

    int Id { get; }

    /// <summary>
    /// The OS-reported start time of this process, used to detect PID reuse when re-adopting a
    /// process recorded by a prior Agent instance: a different start time at the same PID means
    /// Windows recycled that PID for an unrelated process, and it must not be adopted. Null if
    /// unavailable (e.g. the process has already exited, or its start time could not be read).
    /// </summary>
    DateTimeOffset? StartTimeUtc { get; }

    bool HasExited { get; }

    void Start();

    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IPlayitProcessFactory
{
    IPlayitProcess Create(ProcessStartInfo startInfo);

    /// <summary>
    /// Wraps an already-running process by PID -- used to re-adopt a Playit agent left running
    /// by a prior Agent instance, instead of starting a new one. Never itself validates identity
    /// beyond "a process with this PID currently exists"; the caller is responsible for
    /// confirming it is genuinely the expected Playit process (executable path, prior recorded
    /// start time) before trusting it. Returns null if no process with that PID exists.
    /// </summary>
    IPlayitProcess? Attach(int processId);
}

public interface IPlayitProcessDiscovery
{
    IReadOnlyList<int> FindRunningProcessIds(string executablePath);
}

public sealed class SystemPlayitProcessFactory : IPlayitProcessFactory
{
    public IPlayitProcess Create(ProcessStartInfo startInfo) =>
        new SystemPlayitProcess(startInfo);

    public IPlayitProcess? Attach(int processId)
    {
        try
        {
            return new SystemPlayitProcess(Process.GetProcessById(processId));
        }
        catch (ArgumentException)
        {
            // No process with this PID exists (already exited, or never existed).
            return null;
        }
    }
}

public sealed class SystemPlayitProcessDiscovery : IPlayitProcessDiscovery
{
    public IReadOnlyList<int> FindRunningProcessIds(string executablePath)
    {
        var expected = Path.GetFullPath(executablePath);
        var result = new List<int>();
        foreach (var process in Process.GetProcessesByName("playit"))
        {
            using (process)
            {
                var path = TryReadMainModuleFileName(process);
                if (path is not null &&
                    Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(process.Id);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Process.MainModule can transiently report an empty FileName for a process that has only
    /// just been created -- the Windows loader has not finished initializing the main module
    /// yet, a documented Win32/.NET race, not specific to this application (the same underlying
    /// issue DashboardProcessShutdown.ReadMainModuleFileNameAsync retries around on a different
    /// call path). Retries briefly rather than treating a freshly-started, genuinely-matching
    /// Playit process as "not found" -- which would otherwise risk starting a duplicate.
    /// </summary>
    private static string? TryReadMainModuleFileName(Process process)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                return null;
            }

            if (process.HasExited)
            {
                return null;
            }

            Thread.Sleep(25);
        }

        return null;
    }
}

internal sealed class SystemPlayitProcess : IPlayitProcess
{
    private readonly Process _process;
    private readonly bool _adopted;

    public SystemPlayitProcess(ProcessStartInfo startInfo)
    {
        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                OutputReceived?.Invoke(args.Data);
            }
        };
        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                ErrorReceived?.Invoke(args.Data);
            }
        };
        _process.Exited += (_, _) =>
        {
            try
            {
                Exited?.Invoke(_process.ExitCode);
            }
            catch (InvalidOperationException)
            {
                Exited?.Invoke(-1);
            }
        };
    }

    /// <summary>
    /// Wraps an already-running process (re-adoption), rather than one this instance starts
    /// itself. Output/error redirection is unavailable for a process this instance did not
    /// start -- Windows does not allow retroactively redirecting another process's already-open
    /// standard streams -- so <see cref="OutputReceived"/>/<see cref="ErrorReceived"/> never
    /// fire for an adopted process; only <see cref="Exited"/> (which needs no redirection, just
    /// <c>EnableRaisingEvents</c>) and <see cref="StopAsync"/> remain available.
    /// </summary>
    public SystemPlayitProcess(Process existingProcess)
    {
        _process = existingProcess;
        _adopted = true;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            try
            {
                Exited?.Invoke(_process.ExitCode);
            }
            catch (InvalidOperationException)
            {
                Exited?.Invoke(-1);
            }
        };
    }

    public event Action<string>? OutputReceived;

    public event Action<string>? ErrorReceived;

    public event Action<int>? Exited;

    public int Id => _process.Id;

    public DateTimeOffset? StartTimeUtc
    {
        get
        {
            try
            {
                return _process.HasExited ? null : _process.StartTime.ToUniversalTime();
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException)
            {
                return null;
            }
        }
    }

    public bool HasExited => _process.HasExited;

    public void Start()
    {
        if (_adopted)
        {
            // Already running -- adoption means using the existing process as-is, never
            // starting a second one.
            return;
        }

        if (!_process.Start())
        {
            throw new InvalidOperationException("The official Playit process could not be started.");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_process.HasExited)
        {
            return;
        }

        _process.Kill(entireProcessTree: true);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await _process.WaitForExitAsync(timeoutSource.Token);
    }

    public void Dispose() => _process.Dispose();
}
