using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Processes;

namespace ServerManager.Agent.IntegrationTests;

/// <summary>
/// A Minecraft server re-adopted after the Agent restarted (every application update) has no
/// console pipe. Stop and Restart must still stop it the safe way, through Ctrl+C in its own console
/// raised by the real Agent executable's helper, and must never kill it. ping.exe stands in for
/// Minecraft: it keeps running without stdin and ends on Ctrl+C.
/// </summary>
public sealed class ReadoptedServerStopTests
{
    [Fact]
    public async Task ReadoptedServer_StopsThroughCtrlCInItsOwnConsole()
    {
        var (server, spec, processId) = await StartAndOrphanAsync();
        using var supervisor = new ProcessSupervisor(NullLogger<ProcessSupervisor>.Instance, new NullAudit(),
            consoleInterrupt: (pid, ct) => ConsoleInterrupt.RequestAsync(pid, Helper.FileName, Helper.Arguments, ct));
        try
        {
            Assert.NotNull(await supervisor.AdoptAsync(server, spec, processId));
            Assert.Equal(MinecraftConsoleState.NoConsole, supervisor.GetState(server.Id));

            var result = await supervisor.StopAsync(server.Id, force: false);

            Assert.True(result.Success, result.Message);
            Assert.True(HasExited(processId));
        }
        finally
        {
            TryKill(processId);
        }
    }

    [Fact]
    public async Task ReadoptedServer_ThatCannotBeAsked_KeepsRunningAndSaysWhy()
    {
        var (server, spec, processId) = await StartAndOrphanAsync();
        using var supervisor = new ProcessSupervisor(NullLogger<ProcessSupervisor>.Instance, new NullAudit(),
            consoleInterrupt: (_, _) => Task.FromResult(false));
        try
        {
            Assert.NotNull(await supervisor.AdoptAsync(server, spec, processId));

            var result = await supervisor.StopAsync(server.Id, force: false);

            Assert.False(result.Success);
            Assert.Contains("nothing was forced", result.Message, StringComparison.Ordinal);
            Assert.False(HasExited(processId));
            Assert.Equal(ServerState.Running, (await supervisor.GetSnapshotAsync(server.Id))!.State);
        }
        finally
        {
            TryKill(processId);
        }
    }

    [Fact]
    public void Helper_RefusesAnInvalidTarget() =>
        Assert.Equal(2, ConsoleInterrupt.RunHelper("not-a-pid"));

    /// <summary>Started by one supervisor with a console pipe, which then goes away like an Agent restart.</summary>
    private static async Task<(GameServerDefinition Server, ProcessLaunchSpec Spec, int ProcessId)> StartAndOrphanAsync()
    {
        var server = new GameServerDefinition(Guid.NewGuid(), GameType.Minecraft, "Readopted", Path.GetTempPath(), 25565, "test", DateTimeOffset.UtcNow);
        var spec = new ProcessLaunchSpec(
            Path.Combine(Environment.SystemDirectory, "PING.EXE"),
            string.Empty,
            Path.GetTempPath(),
            new Dictionary<string, string>(),
            ArgumentList: ["-n", "600", "127.0.0.1"]);
        var first = new ProcessSupervisor(NullLogger<ProcessSupervisor>.Instance, new NullAudit());
        var started = await first.StartAsync(server, spec);
        first.Dispose();
        Assert.False(HasExited(started.ProcessId));
        return (server, spec, started.ProcessId);
    }

    private static (string FileName, IReadOnlyList<string> Arguments) Helper
    {
        get
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "1Salem.ServerManager.Agent.exe");
            if (File.Exists(exe))
            {
                return (exe, []);
            }

            var dotnet = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!)!)!)!, "dotnet.exe");
            return (dotnet, [Path.Combine(AppContext.BaseDirectory, "1Salem.ServerManager.Agent.dll")]);
        }
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void TryKill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            process.WaitForExit(5_000);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
        }
    }

    private sealed class NullAudit : IAuditLogStore
    {
        public Task WriteAsync(string actor, string action, string target, bool succeeded, string? detail = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AuditLogRecord>> ListRecentAsync(string targetPrefix, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogRecord>>([]);
    }
}
