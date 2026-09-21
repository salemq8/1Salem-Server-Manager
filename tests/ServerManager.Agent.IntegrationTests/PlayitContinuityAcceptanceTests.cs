using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Playit;

namespace ServerManager.Agent.IntegrationTests;

/// <summary>
/// End-to-end regression coverage for the Version 1.5 Build 3 Playit continuity repair, using
/// the REAL OfficialPlayitSupervisor, SystemPlayitProcessFactory, SystemPlayitProcessDiscovery,
/// and PlayitRecoveryHostedService -- the exact production classes involved in the original
/// defect -- against a real, disposable OS process (never the real playit.exe). This is the
/// most direct possible proof that PlayitRecoveryHostedService.StopAsync, called by the .NET
/// generic host on every graceful Agent shutdown, no longer terminates a live Playit process.
/// </summary>
public sealed class PlayitContinuityAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-playit-continuity-{Guid.NewGuid():N}");
    private readonly List<Process> _disposableProcesses = [];

    [Fact]
    public async Task AgentHostShutdown_DoesNotStopAnAdoptedPlayitProcess()
    {
        // This is the exact scenario that was broken: an Agent instance has a real, live Playit
        // process adopted/managed, and the Agent's own hosted-service shutdown (a binary
        // update, a Windows Service restart) must leave it running.
        var (supervisor, _) = CreateRealSupervisor();
        var disposable = StartDisposableProcess();
        Assert.True(await supervisor.TryAdoptExistingAsync());
        var statusBefore = await supervisor.GetStatusAsync();
        Assert.Equal(disposable.Id, statusBefore.ProcessId);

        var hostedService = new PlayitRecoveryHostedService(
            supervisor,
            new PlayitStartupGate(),
            NullLogger<PlayitRecoveryHostedService>.Instance);
        await hostedService.StopAsync(CancellationToken.None);

        Assert.False(disposable.HasExited);
    }

    [Fact]
    public async Task AgentHostShutdown_ThenNewSupervisor_ReAdoptsTheSameProcess_NoDuplicate()
    {
        // Models the full production sequence: Agent A has Playit adopted, the Agent host
        // shuts down (must not stop Playit), and a brand-new Agent instance (a fresh
        // OfficialPlayitSupervisor, exactly as DI would construct for the next Agent process)
        // comes up and must re-adopt the SAME still-running process rather than starting a
        // second one.
        var (supervisorA, settings) = CreateRealSupervisor();
        var disposable = StartDisposableProcess();
        Assert.True(await supervisorA.TryAdoptExistingAsync());

        var hostedServiceA = new PlayitRecoveryHostedService(
            supervisorA,
            new PlayitStartupGate(),
            NullLogger<PlayitRecoveryHostedService>.Instance);
        await hostedServiceA.StopAsync(CancellationToken.None);
        Assert.False(disposable.HasExited);

        var (supervisorB, _) = CreateRealSupervisor(settings);
        var adoptedByB = await supervisorB.TryAdoptExistingAsync();
        var statusB = await supervisorB.GetStatusAsync();

        Assert.True(adoptedByB);
        Assert.Equal(disposable.Id, statusB.ProcessId);
        Assert.False(disposable.HasExited);
        supervisorB.Dispose();
    }

    [Fact]
    public async Task ExplicitStopAsync_StillTerminatesTheRealProcess()
    {
        // The real user/admin "Stop Playit" action must be unaffected by this repair.
        var (supervisor, _) = CreateRealSupervisor();
        var disposable = StartDisposableProcess();
        Assert.True(await supervisor.TryAdoptExistingAsync());

        await supervisor.StopAsync();

        disposable.WaitForExit(5000);
        Assert.True(disposable.HasExited);
    }

    private (OfficialPlayitSupervisor Supervisor, InMemorySettingsStore Settings) CreateRealSupervisor(
        InMemorySettingsStore? settings = null)
    {
        Directory.CreateDirectory(_root);
        var executable = Path.Combine(_root, "playit.exe");
        if (!File.Exists(executable))
        {
            File.Copy(
                Path.Combine(Environment.SystemDirectory, "ping.exe"),
                executable);
        }

        settings ??= new InMemorySettingsStore();
        _ = settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with { Enabled = true });

        var supervisor = new OfficialPlayitSupervisor(
            new PlayitInstallationLocator([executable]),
            new SystemPlayitProcessFactory(),
            new SystemPlayitProcessDiscovery(),
            settings,
            new EmptyServerStore(),
            NullLogger<OfficialPlayitSupervisor>.Instance);
        return (supervisor, settings);
    }

    private Process StartDisposableProcess()
    {
        var executable = Path.Combine(_root, "playit.exe");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("127.0.0.1");
        var process = Process.Start(start) ??
            throw new InvalidOperationException("Disposable process did not start.");
        _disposableProcesses.Add(process);
        WaitUntilDiscoverable(process);
        return process;
    }

    /// <summary>
    /// Windows reports an empty MainModule for a process whose loader has not finished
    /// initialising, so a test that adopts immediately after Process.Start races it. The
    /// production discovery path already retries around this; the test waits for the same
    /// readiness explicitly instead of assuming the process is visible straight away, which
    /// is what made this test fail intermittently under load. Test harness only — no
    /// production behaviour is involved.
    /// </summary>
    private static void WaitUntilDiscoverable(Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "Disposable process exited before it could be adopted.");
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(process.MainModule?.FileName))
                {
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Still initialising; fall through to the bounded retry below.
            }
            catch (InvalidOperationException)
            {
            }

            Thread.Sleep(25);
        }

        throw new InvalidOperationException(
            "Disposable process never became discoverable within 15s.");
    }

    public void Dispose()
    {
        foreach (var process in _disposableProcesses)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(2000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        private readonly ConcurrentDictionary<string, object?> _values =
            new(StringComparer.Ordinal);

        public Task<T?> GetAsync<T>(
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _values.TryGetValue(key, out var value) ? (T?)value : default);

        public Task SetAsync<T>(
            string key,
            T value,
            CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyServerStore : IGameServerStore
    {
        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameServerDefinition>>([]);

        public Task<GameServerDefinition?> GetAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GameServerDefinition?>(null);

        public Task UpsertAsync(
            GameServerDefinition server,
            ServerState state,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetStateAsync(
            Guid serverId,
            ServerState state,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
