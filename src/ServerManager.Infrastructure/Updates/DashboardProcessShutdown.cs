using System.ComponentModel;
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

            var executable = await ReadMainModuleFileNameAsync(process, cancellationToken);
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

    /// <summary>
    /// Process.MainModule can transiently report an empty FileName for a process that has only
    /// just been created -- the Windows loader has not finished initializing the main module
    /// yet, a documented race in the underlying Win32 API, not specific to this application. A
    /// legitimately-matching dashboard process must never be refused shutdown just because it
    /// happened to be inspected within a few milliseconds of starting, so this retries briefly
    /// rather than accepting a single inconclusive (empty) read. It does NOT retry after a real,
    /// non-empty path is obtained -- a definitive mismatch is still rejected immediately.
    /// </summary>
    private static async Task<string?> ReadMainModuleFileNameAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                process.Refresh();
                var fileName = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    return fileName;
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception)
            {
                // The process may have exited, or its module list may be transiently
                // inaccessible while it is still initializing; fall through to retry or
                // exhaust attempts below.
            }

            if (process.HasExited)
            {
                return null;
            }

            await Task.Delay(25, cancellationToken);
        }

        return null;
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
