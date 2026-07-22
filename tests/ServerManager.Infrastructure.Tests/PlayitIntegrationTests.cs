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

        public FakeProcess? Last { get; private set; }

        public int CreatedCount { get; private set; }

        public IPlayitProcess Create(ProcessStartInfo startInfo)
        {
            CreatedCount++;
            Last = new FakeProcess(_nextId++, startInfo);
            return Last;
        }
    }

    private sealed class FakeProcess(int id, ProcessStartInfo startInfo) : IPlayitProcess
    {
        public event Action<string>? OutputReceived;
        public event Action<string>? ErrorReceived;
        public event Action<int>? Exited;

        public int Id { get; } = id;

        public ProcessStartInfo StartInfo { get; } = startInfo;

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
