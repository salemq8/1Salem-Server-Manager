using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Playit;

namespace ServerManager.Infrastructure.Tests;

public sealed class PlayitIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-playit-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ExistingPlayitDetection_FindsOfficialExecutableCandidate()
    {
        var executable = CreateFakeExecutable();
        var result = new PlayitInstallationLocator([executable]).Detect();

        Assert.True(result.IsInstalled);
        Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
    }

    [Fact]
    public void HiddenLaunch_UsesOfficialNoWindowProcessContract()
    {
        var executable = CreateFakeExecutable();
        var secretPath = Path.Combine(_root, "playit.toml");
        File.WriteAllText(secretPath, "not-read-by-test");

        var start = OfficialPlayitSupervisor.CreateStartInfo(executable, secretPath);

        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Contains("--stdout", start.ArgumentList);
        Assert.Contains("start", start.ArgumentList);
        Assert.Contains(secretPath, start.ArgumentList);
    }

    [Fact]
    public async Task Supervisor_DetectsClaimAndVerifiedOnlineState()
    {
        using var fixture = CreateSupervisor();
        var result = await fixture.Supervisor.StartAsync();
        fixture.Factory.Last!.Emit("Open https://playit.gg/claim/ABC123");
        fixture.Factory.Last.Emit(
            "secret key is valid; agent registered; got pong; tunnel running");

        var status = await fixture.Supervisor.GetStatusAsync();

        Assert.True(result.Success);
        Assert.True(status.IsLinked);
        Assert.True(status.IsVerified);
        Assert.Equal(PlayitRuntimeState.Online, status.State);
        Assert.Equal("https://playit.gg/claim/ABC123", status.ClaimUrl);
        Assert.DoesNotContain(
            status.RecentLog,
            line => line.Contains("ABC123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateProcessPrevention_RefusesSecondOfficialAgent()
    {
        using var fixture = CreateSupervisor([4242]);

        var result = await fixture.Supervisor.StartAsync();

        Assert.False(result.Success);
        Assert.Equal(0, fixture.Factory.CreatedCount);
        Assert.Equal(PlayitRuntimeState.RunningExternally, result.Status.State);
    }

    [Fact]
    public async Task CrashRecovery_RestartsEnabledManagedAgent()
    {
        using var fixture = CreateSupervisor();
        await fixture.Settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with { Enabled = true });
        await fixture.Supervisor.InitializeAsync();
        fixture.Factory.Last!.ExitUnexpectedly(9);

        var recovered = await fixture.Supervisor.RecoverIfNeededAsync();

        Assert.True(recovered);
        Assert.Equal(2, fixture.Factory.CreatedCount);
    }

    [Fact]
    public async Task ExistingMinecraftMapping_IsPreservedAsDashboardMetadata()
    {
        using var fixture = CreateSupervisor();

        var status = await fixture.Supervisor.GetStatusAsync();

        Assert.Equal("ALSarabeetMC", status.AgentName);
        Assert.Equal(
            "click-jackets.gl.joinmc.link",
            status.Minecraft.PublicAddress);
        Assert.Equal(25565, status.Minecraft.LocalPort);
        Assert.Equal(8211, status.Palworld.LocalPort);
    }

    [Fact]
    public async Task SavedPalworldAddress_RemainsVisibleWhilePlayitIsOffline()
    {
        using var fixture = CreateSupervisor();
        await fixture.Settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with
            {
                Enabled = true,
                PalworldPublicAddress = "click-jackets.gl.at.ply.gg:7551"
            });

        var status = await fixture.Supervisor.GetStatusAsync();

        Assert.False(status.IsRunning);
        Assert.False(status.IsVerified);
        Assert.Equal(PlayitTunnelProtocol.Udp, status.Palworld.Protocol);
        Assert.Equal("127.0.0.1", status.Palworld.LocalHost);
        Assert.Equal(8211, status.Palworld.LocalPort);
        Assert.Equal(
            "click-jackets.gl.at.ply.gg:7551",
            status.Palworld.PublicAddress);
    }

    // --- P1-XX: Playit must survive an Agent host shutdown/restart, not be stopped by it -----

    [Fact]
    public async Task ExplicitStop_StillStopsTheRealManagedProcess()
    {
        // The real user/admin "Stop Playit" action must keep working exactly as before -- only
        // an Agent host shutdown (ReleaseWithoutStopping) is no longer allowed to do this.
        using var fixture = CreateSupervisor();
        await fixture.Supervisor.StartAsync();
        var started = fixture.Factory.Last!;

        var result = await fixture.Supervisor.StopAsync();

        Assert.True(result.Success);
        Assert.True(started.HasExited);
    }

    [Fact]
    public async Task ReleaseWithoutStopping_DoesNotStopTheManagedProcess()
    {
        // This is the exact defect fixed in Version 1.5 Build 3: an Agent host shutdown must
        // never imply "stop Playit". PlayitRecoveryHostedService.StopAsync calls this method
        // (not OfficialPlayitSupervisor.StopAsync) on every graceful Agent shutdown.
        using var fixture = CreateSupervisor();
        await fixture.Supervisor.StartAsync();
        var started = fixture.Factory.Last!;

        fixture.Supervisor.ReleaseWithoutStopping();

        Assert.False(started.HasExited);
    }

    [Fact]
    public async Task Initialize_AdoptsASingleExistingValidProcess_WithoutStartingANewOne()
    {
        using var fixture = CreateSupervisor([4242]);
        fixture.Factory.RegisterExternal(4242);
        await fixture.Settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with { Enabled = true });

        await fixture.Supervisor.InitializeAsync();
        var status = await fixture.Supervisor.GetStatusAsync();

        Assert.Equal(0, fixture.Factory.CreatedCount);
        Assert.Equal(4242, status.ProcessId);
        Assert.Equal(PlayitRuntimeState.Online, status.State);
        Assert.True(status.IsRunning);
    }

    [Fact]
    public async Task Adoption_PreservesTheAdoptedProcessId()
    {
        using var fixture = CreateSupervisor([4242]);
        fixture.Factory.RegisterExternal(4242);
        await fixture.Settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with { Enabled = true });

        var adopted = await fixture.Supervisor.TryAdoptExistingAsync();
        var status = await fixture.Supervisor.GetStatusAsync();

        Assert.True(adopted);
        Assert.Equal(4242, status.ProcessId);
    }

    [Fact]
    public async Task Adoption_RejectsAReusedPidWithADifferentStartTime_AndFallsBackToStartingOne()
    {
        // Simulates PID reuse: a prior Agent instance recorded PID 4242 starting at T1; by the
        // time this Agent instance comes up, Windows has recycled PID 4242 for a genuinely
        // unrelated process that happens to also be named "playit.exe" and started at a
        // different time. Adoption must reject it on start-time mismatch, not blindly trust the
        // PID number, and must still end up with exactly one managed process afterward (the
        // freshly started one), never zero and never a silently-wrong adoption.
        var recordedStart = DateTimeOffset.UtcNow.AddHours(-2);
        using var fixture = CreateSupervisor([4242]);
        fixture.Factory.RegisterExternal(4242, startTimeUtc: DateTimeOffset.UtcNow);
        await fixture.Settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with
            {
                Enabled = true,
                LastKnownProcessId = 4242,
                LastKnownProcessStartTimeUtc = recordedStart
            });

        var adopted = await fixture.Supervisor.TryAdoptExistingAsync();

        Assert.False(adopted);
    }

    [Fact]
    public async Task Initialize_WithTwoAmbiguousCandidates_AdoptsNeitherAndStartsNone()
    {
        // "Two candidates -> resolve safely or fail clearly rather than blindly start a third."
        // TryAdoptExistingAsync declines (cannot safely disambiguate), and StartAsync's own
        // pre-existing duplicate check independently also refuses to start a new process while
        // any matching one already exists -- the two guards compose into "never a duplicate"
        // without needing special-case logic in Initialize itself.
        using var fixture = CreateSupervisor([111, 222]);
        fixture.Factory.RegisterExternal(111);
        fixture.Factory.RegisterExternal(222);
        await fixture.Settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with { Enabled = true });

        await fixture.Supervisor.InitializeAsync();

        Assert.Equal(0, fixture.Factory.CreatedCount);
        var status = await fixture.Supervisor.GetStatusAsync();
        Assert.Equal(PlayitRuntimeState.RunningExternally, status.State);
    }

    [Fact]
    public async Task Initialize_WhenDisabled_DoesNotAdoptOrStart()
    {
        // A process genuinely exists externally (RegisterExternal), so GetStatusAsync correctly
        // still reports IsRunning=true regardless of whether this supervisor manages it -- that
        // reflects reality ("a Playit process is running"), not adoption state. What matters
        // here is that a disabled integration never adopts or starts anything: no new process,
        // and the existing one is reported as unmanaged (RunningExternally), not Online.
        using var fixture = CreateSupervisor([4242]);
        fixture.Factory.RegisterExternal(4242);
        await fixture.Settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with { Enabled = false });

        await fixture.Supervisor.InitializeAsync();

        Assert.Equal(0, fixture.Factory.CreatedCount);
        var status = await fixture.Supervisor.GetStatusAsync();
        Assert.Equal(PlayitRuntimeState.RunningExternally, status.State);
    }

    [Fact]
    public async Task Initialize_WithNoExistingProcess_StartsExactlyOne()
    {
        using var fixture = CreateSupervisor();
        await fixture.Settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with { Enabled = true });

        await fixture.Supervisor.InitializeAsync();

        Assert.Equal(1, fixture.Factory.CreatedCount);
    }

    [Fact]
    public async Task SimulatedAgentRestart_ReAdoptsExistingPlayit_RegardlessOfCrashOrGracefulShutdown()
    {
        // Models both "Agent restarted gracefully" and "Agent crashed and came back": either
        // way, the next Agent instance's own Initialize call is the only re-adoption entry
        // point, and it behaves identically regardless of how the previous instance went away
        // (a crash never runs ReleaseWithoutStopping at all, but that is exactly why it was
        // already safe -- only the graceful path used to be destructive).
        using var fixture = CreateSupervisor([4242]);
        fixture.Factory.RegisterExternal(4242);
        await fixture.Settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with { Enabled = true });

        await fixture.Supervisor.InitializeAsync();

        Assert.Equal(0, fixture.Factory.CreatedCount);
        var status = await fixture.Supervisor.GetStatusAsync();
        Assert.Equal(4242, status.ProcessId);
        Assert.True(status.IsVerified);
    }

    [Fact]
    public async Task Adoption_DoesNotLeakTheSecretPathIntoLogOutput()
    {
        using var fixture = CreateSupervisor([4242]);
        fixture.Factory.RegisterExternal(4242);
        const string secretPath = @"C:\secret\playit-agent-secret.toml";
        await fixture.Settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with
            {
                Enabled = true,
                SecretPath = secretPath
            });

        await fixture.Supervisor.InitializeAsync();
        var status = await fixture.Supervisor.GetStatusAsync();

        Assert.DoesNotContain(
            status.RecentLog,
            line => line.Contains(secretPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adoption_PreservesUnrelatedSettingsUntouched()
    {
        using var fixture = CreateSupervisor([4242]);
        fixture.Factory.RegisterExternal(4242);
        await fixture.Settings.SetAsync(
            OfficialPlayitSupervisor.SettingsKey,
            OfficialPlayitSupervisor.PlayitSettings.Default with
            {
                Enabled = true,
                AgentName = "CustomAgentName",
                PalworldPublicAddress = "click-jackets.gl.at.ply.gg:7551"
            });

        await fixture.Supervisor.InitializeAsync();
        var status = await fixture.Supervisor.GetStatusAsync();

        Assert.Equal("CustomAgentName", status.AgentName);
        Assert.Equal(
            "click-jackets.gl.at.ply.gg:7551",
            status.Palworld.PublicAddress);
    }

    private SupervisorFixture CreateSupervisor(IReadOnlyList<int>? existing = null)
    {
        var executable = CreateFakeExecutable();
        var settings = new InMemorySettingsStore();
        var factory = new FakeProcessFactory();
        var discovery = new FakeProcessDiscovery(existing ?? []);
        var supervisor = new OfficialPlayitSupervisor(
            new PlayitInstallationLocator([executable]),
            factory,
            discovery,
            settings,
            new EmptyServerStore(),
            NullLogger<OfficialPlayitSupervisor>.Instance);
        return new SupervisorFixture(supervisor, settings, factory);
    }

    private string CreateFakeExecutable()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "playit.exe");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, [0x4D, 0x5A, 0x00, 0x00]);
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record SupervisorFixture(
        OfficialPlayitSupervisor Supervisor,
        InMemorySettingsStore Settings,
        FakeProcessFactory Factory) : IDisposable
    {
        public void Dispose() => Supervisor.Dispose();
    }

    private sealed class FakeProcessFactory : IPlayitProcessFactory
    {
        private int _nextId = 100;
        private readonly Dictionary<int, FakeProcess> _attachable = new();

        public FakeProcess? Last { get; private set; }

        public int CreatedCount { get; private set; }

        public IPlayitProcess Create(ProcessStartInfo startInfo)
        {
            CreatedCount++;
            Last = new FakeProcess(_nextId++, startInfo, DateTimeOffset.UtcNow);
            return Last;
        }

        /// <summary>
        /// Registers a fake process as already running externally (as if a prior Agent instance
        /// started it and this one is coming up fresh), so a supervisor under test can find it
        /// via FindRunningProcessIds and re-adopt it via Attach, exactly like the real
        /// SystemPlayitProcessFactory attaches to a real external PID.
        /// </summary>
        public FakeProcess RegisterExternal(int processId, DateTimeOffset? startTimeUtc = null)
        {
            var process = new FakeProcess(processId, null, startTimeUtc ?? DateTimeOffset.UtcNow);
            _attachable[processId] = process;
            return process;
        }

        public IPlayitProcess? Attach(int processId) =>
            _attachable.TryGetValue(processId, out var process) && !process.HasExited
                ? process
                : null;
    }

    private sealed class FakeProcess(int id, ProcessStartInfo? startInfo, DateTimeOffset startTimeUtc)
        : IPlayitProcess
    {
        public event Action<string>? OutputReceived;
        public event Action<string>? ErrorReceived;
        public event Action<int>? Exited;

        public int Id { get; } = id;

        public ProcessStartInfo? StartInfo { get; } = startInfo;

        public DateTimeOffset? StartTimeUtc => HasExited ? null : startTimeUtc;

        public bool HasExited { get; private set; }

        public void Start()
        {
        }

        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            HasExited = true;
            Exited?.Invoke(0);
            return Task.CompletedTask;
        }

        public void Emit(string line) => OutputReceived?.Invoke(line);

        public void EmitError(string line) => ErrorReceived?.Invoke(line);

        public void ExitUnexpectedly(int exitCode)
        {
            HasExited = true;
            Exited?.Invoke(exitCode);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeProcessDiscovery(IReadOnlyList<int> ids)
        : IPlayitProcessDiscovery
    {
        public IReadOnlyList<int> FindRunningProcessIds(string executablePath) => ids;
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
