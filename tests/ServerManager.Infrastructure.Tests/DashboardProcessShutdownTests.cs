using System.Diagnostics;
using ServerManager.Infrastructure.Updates;

namespace ServerManager.Infrastructure.Tests;

public sealed class DashboardProcessShutdownTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-dashboard-shutdown-{Guid.NewGuid():N}");

    [Fact]
    public async Task CloseOnlyDashboard_StopsExactClientAndLeavesUnrelatedProcessRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var client = CreateDisposableDashboard();
        using var unrelated = StartDisposableProcess(Path.Combine(
            Environment.SystemDirectory,
            "ping.exe"));
        using var dashboard = StartDisposableProcess(client);

        await DashboardProcessShutdown.CloseOnlyDashboardAsync(
            dashboard.Id,
            _root,
            client);

        Assert.True(dashboard.HasExited);
        Assert.False(unrelated.HasExited);
        unrelated.Kill(entireProcessTree: false);
        await unrelated.WaitForExitAsync();
    }

    [Fact]
    public async Task CloseOnlyDashboard_RejectsMatchingNameOutsideInstallRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var expected = CreateDisposableDashboard();
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            $"1salem-dashboard-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        var outside = Path.Combine(outsideRoot, "1Salem.ServerManager.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), outside);
        using var process = StartDisposableProcess(outside);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DashboardProcessShutdown.CloseOnlyDashboardAsync(
                    process.Id,
                    _root,
                    expected));
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync();
            }

            TryDeleteDirectory(outsideRoot);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            TryDeleteDirectory(_root);
        }
    }

    private string CreateDisposableDashboard()
    {
        var clientDirectory = Path.Combine(_root, "Client");
        Directory.CreateDirectory(clientDirectory);
        var client = Path.Combine(clientDirectory, "1Salem.ServerManager.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), client);
        return client;
    }

    private static Process StartDisposableProcess(string executable)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("127.0.0.1");
        return Process.Start(start) ?? throw new InvalidOperationException(
            "Disposable process did not start.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            // Windows can retain the executable image briefly after process exit.
        }
        catch (IOException)
        {
            // The disposable temp directory is safe to leave for OS cleanup.
        }
    }
}
