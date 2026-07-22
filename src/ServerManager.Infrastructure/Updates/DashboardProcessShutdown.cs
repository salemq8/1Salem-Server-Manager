using System.Diagnostics;

namespace ServerManager.Infrastructure.Updates;

public static class DashboardProcessShutdown
{
    public static async Task CloseOnlyDashboardAsync(
        int processId,
        string installRoot,
        string stableClientPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableClientPath);

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            var expectedName = Path.GetFileNameWithoutExtension(stableClientPath);
            if (!process.ProcessName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"PID {processId} is not the dashboard; update shutdown was refused.");
            }

            var executable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable) ||
                !IsAllowedDashboardPath(executable, installRoot, stableClientPath))
            {
                throw new InvalidOperationException(
                    $"PID {processId} does not run from the installed dashboard path; update shutdown was refused.");
            }

            _ = process.CloseMainWindow();
            if (await WaitForExitAsync(process, TimeSpan.FromSeconds(3), cancellationToken))
            {
                return;
            }

            process.Kill(entireProcessTree: false);
            if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(10), cancellationToken))
            {
                throw new IOException(
                    "The dashboard did not close in time; no application files were changed.");
            }
        }
    }

    private static bool IsAllowedDashboardPath(
        string executable,
        string installRoot,
        string stableClientPath)
    {
        var actual = Path.GetFullPath(executable);
        var stable = Path.GetFullPath(stableClientPath);
        if (actual.Equals(stable, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var versionsRoot = Path.GetFullPath(Path.Combine(installRoot, "Versions"));
        var relative = Path.GetRelativePath(versionsRoot, actual);
        return !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative) &&
            Path.GetFileName(actual).Equals(
                Path.GetFileName(stable),
                StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return true;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }
}
