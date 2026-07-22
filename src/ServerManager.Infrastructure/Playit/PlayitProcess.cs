using System.Diagnostics;

namespace ServerManager.Infrastructure.Playit;

public interface IPlayitProcess : IDisposable
{
    event Action<string>? OutputReceived;
    event Action<string>? ErrorReceived;
    event Action<int>? Exited;

    int Id { get; }

    bool HasExited { get; }

    void Start();

    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IPlayitProcessFactory
{
    IPlayitProcess Create(ProcessStartInfo startInfo);
}

public interface IPlayitProcessDiscovery
{
    IReadOnlyList<int> FindRunningProcessIds(string executablePath);
}

public sealed class SystemPlayitProcessFactory : IPlayitProcessFactory
{
    public IPlayitProcess Create(ProcessStartInfo startInfo) =>
        new SystemPlayitProcess(startInfo);
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
                try
                {
                    if (process.MainModule?.FileName is { } path &&
                        Path.GetFullPath(path).Equals(
                            expected,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(process.Id);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return result;
    }
}

internal sealed class SystemPlayitProcess : IPlayitProcess
{
    private readonly Process _process;

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

    public event Action<string>? OutputReceived;

    public event Action<string>? ErrorReceived;

    public event Action<int>? Exited;

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public void Start()
    {
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
