using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Processes;
using System.Diagnostics;

namespace ServerManager.Infrastructure.Tests;

public sealed class ProcessSupervisorTests : IDisposable
{
    private readonly ProcessSupervisor _supervisor =
        new(NullLogger<ProcessSupervisor>.Instance, new InMemoryAuditLogStore());

    [Fact]
    public async Task StartAndGracefulStop_TracksLifecycle()
    {
        var server = CreateServer(GameType.Minecraft);
        var spec = CreatePowerShellStopHost();

        var started = await _supervisor.StartAsync(server, spec);
        var stopped = await _supervisor.StopAsync(server.Id, false);

        Assert.True(started.ProcessId > 0);
        Assert.Equal(ServerState.Running, started.State);
        Assert.True(stopped.Success);
    }

    [Fact]
    public async Task ConsoleCommand_CapturesBoundedOutput()
    {
        var server = CreateServer(GameType.Palworld);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var commandProcessor = Path.Combine(system, "cmd.exe");
        var spec = new ProcessLaunchSpec(
            commandProcessor,
            "/d /q /k",
            Path.GetTempPath(),
            new Dictionary<string, string>());
        await _supervisor.StartAsync(server, spec);

        var result = await _supervisor.SendCommandAsync(server.Id, "echo PHASE2_CONSOLE");
        var found = false;
        for (var attempt = 0; attempt < 20 && !found; attempt++)
        {
            await Task.Delay(50);
            found = _supervisor.GetRecentLogs(server.Id)
                .Any(entry => entry.Message.Contains("PHASE2_CONSOLE", StringComparison.Ordinal));
        }

        await _supervisor.StopAsync(server.Id, true);
        Assert.True(result.Success);
        Assert.True(found);
    }

    [Fact]
    public async Task MinecraftExchange_WaitsForReadyThenReturnsTheAnswerLine()
    {
        var server = CreateServer(GameType.Minecraft);
        var ready = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        _supervisor.ServerReady += (_, id) => ready.TrySetResult(id);

        await _supervisor.StartAsync(server, CreateFakeMinecraftConsole());
        Assert.Equal(server.Id, await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(MinecraftConsoleState.Ready, _supervisor.GetState(server.Id));

        var answer = await _supervisor.ExchangeAsync(
            server.Id,
            "gamerule keepInventory",
            line => ServerManager.Core.Minecraft.MinecraftConsoleReplies.TryParseGameRuleQuery(line, out _, out _),
            TimeSpan.FromSeconds(10));
        var silent = await _supervisor.ExchangeAsync(
            server.Id,
            "say nothing",
            line => line.Contains("never printed", StringComparison.Ordinal),
            TimeSpan.FromMilliseconds(400));
        var stopped = await _supervisor.StopAsync(server.Id, false);

        Assert.True(answer.Result.Success, answer.Result.Message);
        Assert.EndsWith("Gamerule keepInventory is currently set to: false", answer.Answer);
        Assert.Equal("ConsoleTimeout", silent.Result.ErrorCode);
        Assert.True(stopped.Success);
        Assert.Equal(MinecraftConsoleState.NotRunning, _supervisor.GetState(server.Id));
    }

    [Fact]
    public async Task MinecraftExchange_RefusesAServerThatIsNotRunning()
    {
        var result = await _supervisor.ExchangeAsync(Guid.NewGuid(), "list", _ => true, TimeSpan.FromSeconds(1));

        Assert.Equal("ServerNotRunning", result.Result.ErrorCode);
    }

    [Fact]
    public async Task SendCommandAsync_RejectsNewlineInjection()
    {
        var result = await _supervisor.SendCommandAsync(
            Guid.NewGuid(),
            "say hello\r\nstop");

        Assert.False(result.Success);
        Assert.Equal("InvalidConsoleCommand", result.ErrorCode);
    }

    [Fact]
    public void ConfigureRestartPolicy_RejectsZeroAttemptLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _supervisor.ConfigureRestartPolicy(
                Guid.NewGuid(),
                new RestartPolicy(true, TimeSpan.Zero, 0, TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public async Task Snapshot_AggregatesManagedChildProcessCpuAndMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var batchPath = CreateProcessTreeBatch();
        var server = CreateServer(GameType.Palworld);
        try
        {
            await _supervisor.StartAsync(server, CreateBatchSpec(batchPath));
            ProcessSnapshot? first = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(100);
                first = await _supervisor.GetSnapshotAsync(server.Id);
                if (first?.ChildProcessCount > 0 &&
                    first.WorkingSetBytes > 32L * 1024 * 1024)
                {
                    break;
                }
            }

            Assert.NotNull(first);
            Assert.True(first.ChildProcessCount > 0);
            Assert.True(first.WorkingSetBytes > 32L * 1024 * 1024);
            Assert.True(first.PrivateMemoryBytes > 0);
            Assert.True(first.PeakWorkingSetBytes >= first.WorkingSetBytes);
            Assert.NotNull(first.GameProcessId);
            Assert.False(string.IsNullOrWhiteSpace(first.RootExecutableName));
            Assert.False(string.IsNullOrWhiteSpace(first.GameExecutableName));

            var applied = await _supervisor.ApplyResourcesAsync(
                server.Id,
                System.Diagnostics.ProcessPriorityClass.AboveNormal,
                null);
            var verified = await _supervisor.GetSnapshotAsync(server.Id);
            Assert.True(applied.Success, applied.Message);
            Assert.Equal(
                System.Diagnostics.ProcessPriorityClass.AboveNormal,
                verified?.Priority);

            await Task.Delay(700);
            var second = await _supervisor.GetSnapshotAsync(server.Id);
            Assert.NotNull(second);
            Assert.True(second.CpuPercent > 0);
        }
        finally
        {
            await _supervisor.StopAsync(server.Id, true);
            File.Delete(batchPath);
        }
    }

    [Fact]
    public async Task Restart_ReplacesRootAndGameProcessIds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var batchPath = CreateProcessTreeBatch();
        var server = CreateServer(GameType.Palworld);
        var spec = CreateBatchSpec(batchPath);
        try
        {
            var first = await _supervisor.StartAsync(server, spec);
            await Task.Delay(300);
            first = await _supervisor.GetSnapshotAsync(server.Id) ?? first;
            await _supervisor.StopAsync(server.Id, true);
            var second = await _supervisor.StartAsync(server, spec);
            await Task.Delay(300);
            second = await _supervisor.GetSnapshotAsync(server.Id) ?? second;

            Assert.NotEqual(first.ProcessId, second.ProcessId);
            Assert.DoesNotContain(
                first.ProcessId,
                new[] { second.ProcessId, second.GameProcessId ?? 0 });
        }
        finally
        {
            await _supervisor.StopAsync(server.Id, true);
            File.Delete(batchPath);
        }
    }

    [Fact]
    public async Task AgentDispose_DoesNotTerminateManagedGameProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var supervisor = new ProcessSupervisor(
            NullLogger<ProcessSupervisor>.Instance,
            new InMemoryAuditLogStore());
        var server = CreateServer(GameType.Palworld);
        var started = await supervisor.StartAsync(server, CreateIndependentHostSpec());
        supervisor.Dispose();
        try
        {
            using var process = Process.GetProcessById(started.ProcessId);
            Assert.False(process.HasExited);
        }
        finally
        {
            TryKill(started.ProcessId);
        }
    }

    [Fact]
    public async Task ReAdoptExistingProcess_RestoresPidAndPreventsDuplicateStart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var server = CreateServer(GameType.Palworld);
        var spec = CreateIndependentHostSpec();
        var firstSupervisor = new ProcessSupervisor(
            NullLogger<ProcessSupervisor>.Instance,
            new InMemoryAuditLogStore());
        var started = await firstSupervisor.StartAsync(server, spec);
        firstSupervisor.Dispose();
        var secondSupervisor = new ProcessSupervisor(
            NullLogger<ProcessSupervisor>.Instance,
            new InMemoryAuditLogStore());
        try
        {
            var adopted = await secondSupervisor.AdoptAsync(
                server,
                spec,
                started.ProcessId);
            var duplicateAttempt = await secondSupervisor.StartAsync(server, spec);

            Assert.NotNull(adopted);
            Assert.Equal(started.ProcessId, adopted.ProcessId);
            Assert.Equal(started.ProcessId, duplicateAttempt.ProcessId);
            Assert.Single(await secondSupervisor.GetAllSnapshotsAsync());
        }
        finally
        {
            await secondSupervisor.StopAsync(server.Id, true);
            secondSupervisor.Dispose();
            TryKill(started.ProcessId);
        }
    }

    public void Dispose() => _supervisor.Dispose();

    private static GameServerDefinition CreateServer(GameType game) =>
        new(
            Guid.NewGuid(),
            game,
            "Test server",
            Path.GetTempPath(),
            game == GameType.Minecraft ? 25565 : 8211,
            "test",
            DateTimeOffset.UtcNow);

    private static ProcessLaunchSpec CreatePowerShellStopHost()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(
            Directory.GetParent(system)!.FullName,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return new ProcessLaunchSpec(
            powershell,
            "-NoLogo -NoProfile -NonInteractive -Command \"$line=[Console]::ReadLine(); if($line -eq 'stop'){exit 0}; exit 3\"",
            Path.GetTempPath(),
            new Dictionary<string, string>());
    }

    /// <summary>A PowerShell stand-in for a Minecraft console: prints "Done", answers gamerule queries, exits on stop.</summary>
    private static ProcessLaunchSpec CreateFakeMinecraftConsole()
    {
        const string script =
            "[Console]::Out.WriteLine('[00:00:00] [Server thread/INFO]: Done (1.0s)! For help, type \"help\"'); [Console]::Out.Flush(); " +
            "while ($null -ne ($line = [Console]::ReadLine())) { " +
            "if ($line -eq 'stop') { exit 0 }; " +
            "if ($line -like 'gamerule *') { [Console]::Out.WriteLine('[00:00:01] [Server thread/INFO]: Gamerule ' + $line.Substring(9) + ' is currently set to: false'); [Console]::Out.Flush() } }";
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return new ProcessLaunchSpec(
            powershell,
            string.Empty,
            Path.GetTempPath(),
            new Dictionary<string, string>(),
            ArgumentList:
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-EncodedCommand",
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))
            ]);
    }

    private static ProcessLaunchSpec CreateIndependentHostSpec()
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return new ProcessLaunchSpec(
            powershell,
            string.Empty,
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            new Dictionary<string, string>(),
            RedirectStandardInput: false,
            RedirectStandardOutput: false,
            RedirectStandardError: false,
            ArgumentList:
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "while ($true) { Start-Sleep -Milliseconds 250 }"
            ]);
    }

    private static void TryKill(int processId)
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
        catch (ArgumentException)
        {
        }
    }

    private static string CreateProcessTreeBatch()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"1salem-process-tree-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(
            path,
            "@echo off\r\n" +
            "start \"\" /b powershell.exe -NoLogo -NoProfile -NonInteractive " +
            "-Command \"$b=New-Object byte[] 67108864; while($true){" +
            "$b[0]=($b[0]+1)%%255}\"\r\n" +
            "set /p line=\r\n");
        return path;
    }

    private static ProcessLaunchSpec CreateBatchSpec(string batchPath)
    {
        var commandProcessor = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        return new ProcessLaunchSpec(
            commandProcessor,
            string.Empty,
            Path.GetTempPath(),
            new Dictionary<string, string>(),
            ArgumentList: ["/d", "/q", "/c", batchPath]);
    }

    private sealed class InMemoryAuditLogStore : IAuditLogStore
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
