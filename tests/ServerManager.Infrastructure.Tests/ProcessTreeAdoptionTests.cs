using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Processes;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// Covers re-adoption of a game server that was already running -- with its own child
/// process -- before this supervisor (and therefore its Job Object) existed. Job Object
/// membership cannot describe that tree, because the child was created long before the Job
/// was, so discovery has to come from the real OS process table instead.
/// </summary>
public sealed class ProcessTreeAdoptionTests : IDisposable
{
    private readonly List<int> _disposableProcessIds = [];
    private readonly string _disposableRoot = Path.Combine(
        Path.GetTempPath(),
        $"1salem-adoption-{Guid.NewGuid():N}");

    private static string SystemPingPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "PING.EXE");

    private string RootExecutablePath => Path.Combine(_disposableRoot, "PalServer.exe");

    private string GameExecutablePath =>
        Path.Combine(_disposableRoot, "PalServer-Win64-Shipping-Cmd.exe");

    private void EnsureDisposableExecutables()
    {
        if (File.Exists(RootExecutablePath))
        {
            return;
        }

        Directory.CreateDirectory(_disposableRoot);
        File.Copy(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe"),
            RootExecutablePath,
            overwrite: true);
        File.Copy(SystemPingPath, GameExecutablePath, overwrite: true);
    }

    [Fact]
    public async Task AdoptedRoot_DiscoversChildThatExistedBeforeTheSupervisor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Started directly, NOT through a supervisor: nothing here has ever been inside a
        // Job Object, exactly like a server left running by a previous Agent instance.
        var (rootProcessId, childProcessId) = await StartRootWithChildAsync();

        var supervisor = new ProcessSupervisor(
            NullLogger<ProcessSupervisor>.Instance,
            new NoOpAuditLogStore());
        try
        {
            var server = CreateServer();
            var adopted = await supervisor.AdoptAsync(
                server,
                CreateRootSpec(),
                rootProcessId);

            Assert.NotNull(adopted);
            Assert.Equal(rootProcessId, adopted.ProcessId);

            // A re-adopted process has no pipes: live controls say so instead of throwing.
            Assert.Equal(MinecraftConsoleState.NoConsole, supervisor.GetState(server.Id));
            var command = await supervisor.SendCommandAsync(server.Id, "list");
            Assert.Equal("ConsoleUnavailable", command.ErrorCode);
            var exchange = await supervisor.ExchangeAsync(server.Id, "list", _ => true, TimeSpan.FromSeconds(1));
            Assert.Equal("ConsoleUnavailable", exchange.Result.ErrorCode);

            var snapshot = await supervisor.GetSnapshotAsync(server.Id);
            Assert.NotNull(snapshot);

            // The defect this test exists for: the pre-existing child was invisible, so the
            // root was reported as the game process and the child count was zero.
            Assert.True(
                snapshot.ChildProcessCount > 0,
                "The pre-existing child process was not discovered after re-adoption.");
            Assert.NotEqual(snapshot.ProcessId, snapshot.GameProcessId);
            Assert.Equal(childProcessId, snapshot.GameProcessId);
            Assert.Equal("PalServer-Win64-Shipping-Cmd", snapshot.GameExecutableName);
            Assert.Equal("PalServer", snapshot.RootExecutableName);
        }
        finally
        {
            supervisor.Dispose();
        }
    }

    [Fact]
    public async Task AdoptedMinecraft_GracefulStopFailsInsteadOfKillingTheServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (rootProcessId, _) = await StartRootWithChildAsync();
        var supervisor = new ProcessSupervisor(
            NullLogger<ProcessSupervisor>.Instance,
            new NoOpAuditLogStore());
        try
        {
            // Without a console there is no way to ask Minecraft to save and stop, so the stop
            // is refused rather than ending in a kill after the timeout.
            var server = CreateServer() with { Game = GameType.Minecraft };
            await supervisor.AdoptAsync(server, CreateRootSpec() with { RedirectStandardInput = true }, rootProcessId);

            var stopped = await supervisor.StopAsync(server.Id, force: false);

            Assert.False(stopped.Success);
            Assert.Equal("ProcessStopFailed", stopped.ErrorCode);
            using var root = Process.GetProcessById(rootProcessId);
            Assert.False(root.HasExited);
        }
        finally
        {
            supervisor.Dispose();
        }
    }

    [Fact]
    public async Task AdoptedRoot_ReportsTelemetryFromTheWholeTreeNotJustTheRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (rootProcessId, childProcessId) = await StartRootWithChildAsync();
        var supervisor = new ProcessSupervisor(
            NullLogger<ProcessSupervisor>.Instance,
            new NoOpAuditLogStore());
        try
        {
            var server = CreateServer();
            await supervisor.AdoptAsync(server, CreateRootSpec(), rootProcessId);
            var snapshot = await supervisor.GetSnapshotAsync(server.Id);

            using var child = Process.GetProcessById(childProcessId);
            child.Refresh();

            Assert.NotNull(snapshot);
            // Aggregate telemetry has to include the child, which holds essentially all of
            // this tree's threads and memory -- the root is a thin command processor.
            Assert.True(
                snapshot.ThreadCount > 1,
                $"Thread count {snapshot.ThreadCount} looks like the root wrapper alone.");
            Assert.True(
                snapshot.WorkingSetBytes >= child.WorkingSet64,
                "Aggregate working set excluded the pre-existing child process.");
        }
        finally
        {
            supervisor.Dispose();
        }
    }

    [Fact]
    public async Task AdoptedRoot_DoesNotKillEitherProcessWhenTheSupervisorGoesAway()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (rootProcessId, childProcessId) = await StartRootWithChildAsync();
        var supervisor = new ProcessSupervisor(
            NullLogger<ProcessSupervisor>.Instance,
            new NoOpAuditLogStore());
        await supervisor.AdoptAsync(
            CreateServer(),
            CreateRootSpec(),
            rootProcessId);

        supervisor.Dispose();
        await Task.Delay(300);

        Assert.True(
            IsAlive(rootProcessId),
            "Disposing the supervisor terminated the adopted root.");
        Assert.True(
            IsAlive(childProcessId),
            "Disposing the supervisor terminated the adopted child.");
    }

    [Fact]
    public async Task Discovery_ExcludesProcessesThatAreNotDescendants()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (rootProcessId, childProcessId) = await StartRootWithChildAsync();
        var unrelatedProcessId = StartIndependentProcess();
        var discovery = new WindowsProcessTreeDiscovery();

        var discovered = discovery.DescendantsOf(rootProcessId);

        Assert.Contains(rootProcessId, discovered);
        Assert.Contains(childProcessId, discovered);
        Assert.DoesNotContain(unrelatedProcessId, discovered);
    }

    [Fact]
    public void Discovery_AlwaysIncludesTheRootItself()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var processId = StartIndependentProcess();
        var discovery = new WindowsProcessTreeDiscovery();

        var discovered = discovery.DescendantsOf(processId);

        Assert.Equal(processId, discovered[0]);
        Assert.Equal(discovered.Distinct().Count(), discovered.Count);
    }

    [Fact]
    public void BuildDescendantList_ReturnsOnlyTheRootWhenItHasNoChildren()
    {
        var discovered = WindowsProcessTreeDiscovery.BuildDescendantList(
            42,
            [new WindowsProcessTreeDiscovery.ProcessEntry(7, 6)],
            _ => null);

        Assert.Equal([42], discovered);
    }

    [Fact]
    public void BuildDescendantList_RejectsAChildThatPredatesItsClaimedParent()
    {
        // A PID-reuse artifact: PID 200 still lists 100 as its parent, but 200 has been
        // running since long before the process currently holding PID 100 started, so the
        // relationship cannot be real. This cannot be provoked against a live system, which
        // is why the walk is exercised directly here.
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var startTimes = new Dictionary<int, DateTime?>
        {
            [100] = start,
            [200] = start.AddMinutes(-30),
            [300] = start.AddMinutes(5)
        };

        var discovered = WindowsProcessTreeDiscovery.BuildDescendantList(
            100,
            [
                new WindowsProcessTreeDiscovery.ProcessEntry(200, 100),
                new WindowsProcessTreeDiscovery.ProcessEntry(300, 100)
            ],
            processId => startTimes.GetValueOrDefault(processId));

        Assert.Contains(300, discovered);
        Assert.DoesNotContain(200, discovered);
    }

    [Fact]
    public void BuildDescendantList_WalksGrandchildrenAndIgnoresCycles()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var startTimes = new Dictionary<int, DateTime?>
        {
            [10] = start,
            [11] = start.AddSeconds(1),
            [12] = start.AddSeconds(2),
            [99] = start
        };

        var discovered = WindowsProcessTreeDiscovery.BuildDescendantList(
            10,
            [
                new WindowsProcessTreeDiscovery.ProcessEntry(11, 10),
                new WindowsProcessTreeDiscovery.ProcessEntry(12, 11),
                new WindowsProcessTreeDiscovery.ProcessEntry(10, 12),
                new WindowsProcessTreeDiscovery.ProcessEntry(99, 98)
            ],
            processId => startTimes.GetValueOrDefault(processId));

        Assert.Equal([10, 11, 12], discovered);
    }

    [Fact]
    public void BuildDescendantList_KeepsChildrenWhoseStartTimeCannotBeRead()
    {
        // An unreadable start time must not silently drop a real descendant; the sanity
        // check only rejects a relationship it can positively disprove.
        var discovered = WindowsProcessTreeDiscovery.BuildDescendantList(
            1,
            [new WindowsProcessTreeDiscovery.ProcessEntry(2, 1)],
            _ => null);

        Assert.Equal([1, 2], discovered);
    }

    public void Dispose()
    {
        foreach (var processId in _disposableProcessIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
            }
        }

        try
        {
            if (Directory.Exists(_disposableRoot))
            {
                Directory.Delete(_disposableRoot, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Starts a disposable root that immediately spawns a long-lived child and then idles,
    /// mirroring how a Palworld launcher leaves a separate shipping process running
    /// underneath it. Returns once the child is actually visible to the OS.
    /// </summary>
    private async Task<(int RootProcessId, int ChildProcessId)> StartRootWithChildAsync()
    {
        // Disposable stand-ins named exactly like the real Palworld pair, so the production
        // game-process selection (which prefers the shipping executable's name) is the code
        // actually under test. Nothing here is a real game binary.
        EnsureDisposableExecutables();
        var root = Process.Start(new ProcessStartInfo
        {
            FileName = RootExecutablePath,
            WorkingDirectory = _disposableRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/d", "/q", "/c", GameExecutablePath, "-t", "127.0.0.1" }
        }) ?? throw new InvalidOperationException("The disposable root process did not start.");
        _disposableProcessIds.Add(root.Id);

        // Windows also gives cmd.exe a conhost.exe child, so the intended child is matched by
        // name rather than by "whatever descendant turned up first".
        var discovery = new WindowsProcessTreeDiscovery();
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(100);
            foreach (var processId in discovery.DescendantsOf(root.Id))
            {
                if (processId == root.Id)
                {
                    continue;
                }

                try
                {
                    using var candidate = Process.GetProcessById(processId);
                    if (candidate.ProcessName.Equals(
                            "PalServer-Win64-Shipping-Cmd",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // Tracked separately: `start /b` detaches it, so it can outlive the
                        // root and escape a tree kill rooted at cmd.exe.
                        _disposableProcessIds.Add(processId);
                        return (root.Id, processId);
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException or InvalidOperationException)
                {
                }
            }
        }

        throw new InvalidOperationException(
            "The disposable child process never appeared; the test cannot proceed.");
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private int StartIndependentProcess()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = PowerShellPath,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "while ($true) { Start-Sleep -Milliseconds 250 }"
            }
        }) ?? throw new InvalidOperationException("The disposable process did not start.");
        _disposableProcessIds.Add(process.Id);
        return process.Id;
    }

    private static GameServerDefinition CreateServer() =>
        new(
            Guid.NewGuid(),
            GameType.Palworld,
            "Adoption test server",
            Path.GetTempPath(),
            8211,
            "test",
            DateTimeOffset.UtcNow);

    private static string PowerShellPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    /// <summary>
    /// The adopted root's own executable, which AdoptAsync verifies the target PID against.
    /// </summary>
    private ProcessLaunchSpec CreateRootSpec() =>
        new(
            RootExecutablePath,
            string.Empty,
            _disposableRoot,
            new Dictionary<string, string>(),
            RedirectStandardInput: false,
            RedirectStandardOutput: false,
            RedirectStandardError: false);

    private sealed class NoOpAuditLogStore : IAuditLogStore
    {
        public Task WriteAsync(
            string actor,
            string action,
            string target,
            bool succeeded,
            string? detail = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
