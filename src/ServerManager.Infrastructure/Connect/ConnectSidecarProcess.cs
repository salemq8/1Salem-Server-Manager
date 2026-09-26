using System.Diagnostics;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// Everything a Connect sidecar is started with. <see cref="Environment"/> is the child's
/// complete environment, not additions to the Agent's: the runner starts from an empty block,
/// so a variable the supervisor removed cannot come back through inheritance.
/// </summary>
public sealed record ConnectSidecarStartInfo(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory);

public interface IConnectSidecarProcess : IDisposable
{
    int Id { get; }

    /// <summary>Completes with the exit code when the process ends.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Ends the process and everything it started, waiting up to <paramref name="timeout"/>.</summary>
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Starts sidecar processes. The seam that lets the supervisor be tested without one.</summary>
public interface IConnectSidecarProcessRunner
{
    /// <param name="outputLine">Receives each line the process writes to stdout or stderr.</param>
    IConnectSidecarProcess Start(ConnectSidecarStartInfo startInfo, Action<string> outputLine);
}

public sealed class SystemConnectSidecarProcessRunner : IConnectSidecarProcessRunner
{
    public IConnectSidecarProcess Start(ConnectSidecarStartInfo startInfo, Action<string> outputLine)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(outputLine);
        var processStartInfo = new ProcessStartInfo
        {
            FileName = startInfo.FileName,
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in startInfo.Arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        // ProcessStartInfo.Environment starts as a copy of the Agent's environment. Clearing it
        // first is what makes the scrubbed block the whole environment.
        processStartInfo.Environment.Clear();
        foreach (var (name, value) in startInfo.Environment)
        {
            processStartInfo.Environment[name] = value;
        }

        // Create the job before the process: if Windows cannot provide the crash-containment
        // boundary, no uncontained sidecar is started.
        var job = ConnectKillOnCloseJob.Create();
        var process = new Process { StartInfo = processStartInfo };
        process.OutputDataReceived += (_, line) => Forward(line.Data, outputLine);
        process.ErrorDataReceived += (_, line) => Forward(line.Data, outputLine);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The Connect sidecar process could not be started.");
            }

            try
            {
                job.Assign(process);
            }
            catch
            {
                job.Dispose();
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return new SystemConnectSidecarProcess(process, job);
        }
        catch
        {
            job.Dispose();
            process.Dispose();
            throw;
        }
    }

    private static void Forward(string? line, Action<string> outputLine)
    {
        if (line is not null)
        {
            outputLine(line);
        }
    }

    private sealed class SystemConnectSidecarProcess(Process process, ConnectKillOnCloseJob job) : IConnectSidecarProcess
    {
        public int Id => process.Id;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        /// <summary>
        /// The Go sidecars stop cleanly on Ctrl+C, but a service has no console to deliver one
        /// through, so the tree is ended outright. Nothing is lost: tsnet writes its state
        /// atomically and every bridged stream ends with its connection anyway.
        /// </summary>
        public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            job.Dispose();
            process.Dispose();
        }
    }
}
