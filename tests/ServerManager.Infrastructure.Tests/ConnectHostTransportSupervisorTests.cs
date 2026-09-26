using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Connect.Core.Pipes;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// How the Agent starts and keeps <c>1Salem.Connect.Host.Transport.exe</c> running, with a fake
/// process runner: nothing is ever executed. The executable path is an empty temp file, which
/// is all <see cref="ConnectHostTransportSupervisor.Start"/> checks for.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectHostTransportSupervisorTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _executable = Path.Combine(Path.GetTempPath(), $"1salem-connect-test-{Guid.NewGuid():N}.exe");

    public ConnectHostTransportSupervisorTests() => File.WriteAllBytes(_executable, []);

    public void Dispose() => File.Delete(_executable);

    [Fact]
    public void FakeMode_ArgumentsAreExactlyTheSidecarFlags_AndCarryNoSecret()
    {
        var options = new ConnectHostTransportOptions(_executable, ConnectTransportMode.Fake, "127.0.0.1:17780");
        var supervisor = CreateSupervisor(options, new FakeRunner());

        var startInfo = supervisor.CreateStartInfo();

        Assert.Equal(_executable, startInfo.FileName);
        Assert.Equal(Path.GetDirectoryName(_executable), startInfo.WorkingDirectory);
        Assert.Equal(
            new[]
            {
                "--mode", "fake",
                "--pipe", @"\\.\pipe\1Salem.Connect.Host.Transport.Agent.v1",
                "--authz-pipe", @"\\.\pipe\1Salem.Connect.HostAuthz.v1",
                "--expected-authz-owner", ConnectPipeSecurity.CurrentUser.Value,
                "--bridge-listen", "127.0.0.1:17780"
            },
            startInfo.Arguments);
    }

    [Fact]
    public void TsnetMode_AddsTheStateDirectory_AndTheConfiguredPipe()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), "1salem-connect-test-state");
        var options = new ConnectHostTransportOptions(
            _executable,
            ConnectTransportMode.Tsnet,
            ":7780",
            stateDirectory,
            "1Salem.Connect.Test.Authz");
        var supervisor = CreateSupervisor(options, new FakeRunner());

        var startInfo = supervisor.CreateStartInfo();

        Assert.Equal(
            new[]
            {
                "--mode", "tsnet",
                "--pipe", @"\\.\pipe\1Salem.Connect.Host.Transport.Agent.v1",
                "--authz-pipe", @"\\.\pipe\1Salem.Connect.Test.Authz",
                "--expected-authz-owner", ConnectPipeSecurity.CurrentUser.Value,
                "--state-dir", stateDirectory,
                "--bridge-listen", ":7780"
            },
            startInfo.Arguments);
    }

    [Fact]
    public void TheSidecarEnvironment_LosesEveryTailnetCredentialVariable_WhateverTheirCase()
    {
        var parent = new Dictionary<string, string>
        {
            ["Path"] = @"C:\Windows\System32",
            ["SystemRoot"] = @"C:\Windows",
            ["TS_AUTHKEY"] = "tskey-auth-kTEST-not-a-real-key",
            ["ts_auth_key"] = "tskey-auth-kTEST-not-a-real-key",
            ["TS_CLIENT_SECRET"] = "tskey-client-kTEST-not-a-real-secret",
            ["TS_CONTROL_URL"] = "https://control.invalid",
            ["TSNET_FORCE_LOGIN"] = "1",
            ["Ts_Client_Id"] = "client-id",
            ["TS_ID_TOKEN"] = "id-token",
            ["TS_AUDIENCE"] = "audience",
            ["TS_UNRELATED_SETTING"] = "kept"
        };
        var options = new ConnectHostTransportOptions(_executable, ConnectTransportMode.Fake, "127.0.0.1:17780");

        var environment = CreateSupervisor(options, new FakeRunner(), parent).CreateStartInfo().Environment;

        Assert.Equal(
            new[] { "Path", "SystemRoot", "TS_UNRELATED_SETTING" },
            environment.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "TS_AUTHKEY", "TS_AUTH_KEY", "TS_CLIENT_SECRET", "TS_CONTROL_URL", "TSNET_FORCE_LOGIN", "TS_CLIENT_ID", "TS_ID_TOKEN", "TS_AUDIENCE" },
            ConnectSidecarEnvironment.RemovedVariables);
    }

    [Fact]
    public void Start_WithoutTheInstalledExecutable_FailsClearly()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"1salem-connect-missing-{Guid.NewGuid():N}.exe");
        var runner = new FakeRunner();
        var supervisor = CreateSupervisor(new ConnectHostTransportOptions(missing, ConnectTransportMode.Fake, "127.0.0.1:17780"), runner);

        Assert.Throws<FileNotFoundException>(supervisor.Start);
        Assert.Equal(0, runner.StartCount);
    }

    [Fact]
    public async Task ASidecarThatKeepsExiting_IsRestartedWithADoublingDelayThatStopsAtTheCap()
    {
        var logger = new ConnectCapturingLogger<ConnectHostTransportSupervisor>();
        var runner = new FakeRunner(exitImmediately: true);
        var options = new ConnectHostTransportOptions(_executable, ConnectTransportMode.Fake, "127.0.0.1:17780")
        {
            InitialRestartDelay = TimeSpan.FromMilliseconds(10),
            MaximumRestartDelay = TimeSpan.FromMilliseconds(40)
        };
        await using var supervisor = new ConnectHostTransportSupervisor(options, runner, TimeProvider.System, logger);

        supervisor.Start();
        await WaitUntilAsync(() => runner.StartCount >= 6);
        await supervisor.StopAsync();

        var delays = logger.Entries
            .SelectMany(entry => entry.Values)
            .Where(value => value.Key == "Delay")
            .Select(value => (TimeSpan)value.Value!)
            .Take(5)
            .ToArray();
        Assert.Equal(new[] { 10, 20, 40, 40, 40 }, delays.Select(delay => (int)delay.TotalMilliseconds));
        Assert.All(runner.Processes, process => Assert.True(process.Disposed));
    }

    [Fact]
    public async Task Stopping_EndsTheRunningSidecar_AndNeverRestartsIt()
    {
        var runner = new FakeRunner();
        var options = new ConnectHostTransportOptions(_executable, ConnectTransportMode.Fake, "127.0.0.1:17780")
        {
            InitialRestartDelay = TimeSpan.FromMilliseconds(10),
            MaximumRestartDelay = TimeSpan.FromMilliseconds(10)
        };
        var supervisor = CreateSupervisor(options, runner);

        supervisor.Start();
        await WaitUntilAsync(() => runner.StartCount == 1);
        Assert.Equal(1, supervisor.RunningProcessId);
        await supervisor.StopAsync();
        await supervisor.StopAsync();
        await Task.Delay(100);

        var process = Assert.Single(runner.Processes);
        Assert.True(process.Stopped);
        Assert.True(process.Disposed);
        Assert.Equal(1, runner.StartCount);
        Assert.Null(supervisor.RunningProcessId);
        Assert.Throws<ObjectDisposedException>(supervisor.Start);
    }

    [Fact]
    public async Task AFailedStart_IsRetried()
    {
        var runner = new FakeRunner(failFirstStart: true);
        var options = new ConnectHostTransportOptions(_executable, ConnectTransportMode.Fake, "127.0.0.1:17780")
        {
            InitialRestartDelay = TimeSpan.FromMilliseconds(10),
            MaximumRestartDelay = TimeSpan.FromMilliseconds(10)
        };
        await using var supervisor = CreateSupervisor(options, runner);

        supervisor.Start();
        await WaitUntilAsync(() => runner.Processes.Count == 1);
        await supervisor.StopAsync();

        Assert.Equal(2, runner.StartCount);
        Assert.True(Assert.Single(runner.Processes).Stopped);
    }

    [Fact]
    public async Task SidecarOutput_IsRedactedBeforeItIsLogged()
    {
        var logger = new ConnectCapturingLogger<ConnectHostTransportSupervisor>();
        var runner = new FakeRunner(outputLine: "enroll failed for tskey-auth-kTEST123-notarealsecretvalue");
        var options = new ConnectHostTransportOptions(_executable, ConnectTransportMode.Fake, "127.0.0.1:17780");
        await using var supervisor = new ConnectHostTransportSupervisor(options, runner, TimeProvider.System, logger);

        supervisor.Start();
        await WaitUntilAsync(() => runner.StartCount == 1 && logger.Entries.Any(entry => entry.Message.StartsWith("host transport:", StringComparison.Ordinal)));
        await supervisor.StopAsync();

        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("notarealsecretvalue", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("tskey-auth-[REDACTED]", StringComparison.Ordinal));
    }

    [Fact]
    public void Backoff_DoublesUpToTheCap_AndResetsAfterAHealthyRun()
    {
        var backoff = new ConnectRestartBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        var first = Enumerable.Range(0, 6).Select(_ => backoff.Next().TotalSeconds).ToArray();
        backoff.Reset();

        Assert.Equal(new[] { 1d, 2, 4, 5, 5, 5 }, first);
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.Next());
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(20, 10)]
    public void Backoff_RefusesANonPositiveOrInvertedRange(int initialSeconds, int maximumSeconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConnectRestartBackoff(TimeSpan.FromSeconds(initialSeconds), TimeSpan.FromSeconds(maximumSeconds)));

    private static ConnectHostTransportSupervisor CreateSupervisor(
        ConnectHostTransportOptions options,
        FakeRunner runner,
        IEnumerable<KeyValuePair<string, string>>? parentEnvironment = null) =>
        new(
            options,
            runner,
            TimeProvider.System,
            NullLogger<ConnectHostTransportSupervisor>.Instance,
            parentEnvironment ?? new Dictionary<string, string> { ["SystemRoot"] = @"C:\Windows" });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeRunner(bool exitImmediately = false, bool failFirstStart = false, string? outputLine = null)
        : IConnectSidecarProcessRunner
    {
        private readonly ConcurrentQueue<FakeProcess> _processes = new();
        private int _starts;

        public int StartCount => Volatile.Read(ref _starts);

        public IReadOnlyList<FakeProcess> Processes => [.. _processes];

        public IConnectSidecarProcess Start(ConnectSidecarStartInfo startInfo, Action<string> onOutputLine)
        {
            if (Interlocked.Increment(ref _starts) == 1 && failFirstStart)
            {
                throw new InvalidOperationException("The Connect sidecar process could not be started.");
            }

            var process = new FakeProcess(_starts);
            _processes.Enqueue(process);
            if (outputLine is not null)
            {
                onOutputLine(outputLine);
            }

            if (exitImmediately)
            {
                process.Exit(1);
            }

            return process;
        }
    }

    private sealed class FakeProcess(int id) : IConnectSidecarProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id { get; } = id;

        public bool Stopped { get; private set; }

        public bool Disposed { get; private set; }

        public void Exit(int code) => _exit.TrySetResult(code);

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Stopped = true;
            _exit.TrySetResult(-1);
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }
}
