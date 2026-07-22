namespace ServerManager.Setup.Tests;

public sealed class WindowsServiceInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-service-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExistingStoppedService_IsUpdatedAndStartedWithoutDuplicateCreate()
    {
        var backend = new FakeServiceBackend(
            new InstalledServiceSnapshot(
                InstalledServiceState.Stopped,
                "\"C:\\Old Agent.exe\" --service",
                "demand"));
        var installer = CreateInstaller(backend, healthy: true);

        var result = await installer.InstallOrRepairAsync(
            CreateAgent(),
            Path.Combine(_root, "data"),
            CancellationToken.None);

        Assert.True(result.RepairedExistingService);
        Assert.Equal(0, backend.CreateCalls);
        Assert.Equal(1, backend.ConfigureCalls);
        Assert.Equal("auto", backend.LastStartType);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(InstalledServiceState.Running, backend.State.State);
    }

    [Fact]
    public async Task FailedHealthCheck_CleansUpServiceCreatedByAttempt()
    {
        var backend = new FakeServiceBackend(
            new InstalledServiceSnapshot(InstalledServiceState.Missing));
        var installer = CreateInstaller(backend, healthy: false);

        await Assert.ThrowsAsync<InstallerFailureException>(
            () => installer.InstallOrRepairAsync(
                CreateAgent(),
                Path.Combine(_root, "data"),
                CancellationToken.None));

        Assert.Equal(1, backend.CreateCalls);
        Assert.Equal(1, backend.DeleteCalls);
        Assert.Equal(InstalledServiceState.Missing, backend.State.State);
    }

    [Fact]
    public async Task ServiceMarkedForDeletion_HasBoundedTimeout()
    {
        var backend = new FakeServiceBackend(
            new InstalledServiceSnapshot(
                InstalledServiceState.MarkedForDeletion))
        {
            KeepMarkedForDeletion = true
        };
        var installer = CreateInstaller(
            backend,
            healthy: true,
            maximumPollAttempts: 2);

        var exception = await Assert.ThrowsAsync<InstallerFailureException>(
            () => installer.InstallOrRepairAsync(
                CreateAgent(),
                Path.Combine(_root, "data"),
                CancellationToken.None));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(backend.QueryCalls, 3, 4);
    }

    [Fact]
    public async Task ScNonZeroExitCode_IsReportedWithStepAndExitCode()
    {
        var backend = new FakeServiceBackend(
            new InstalledServiceSnapshot(
                InstalledServiceState.Stopped,
                "\"C:\\Old Agent.exe\" --service"))
        {
            ConfigureExitCode = 5
        };
        var installer = CreateInstaller(backend, healthy: true);

        var exception = await Assert.ThrowsAsync<InstallerFailureException>(
            () => installer.InstallOrRepairAsync(
                CreateAgent(),
                Path.Combine(_root, "data"),
                CancellationToken.None));

        Assert.Equal(5, exception.ExitCode);
        Assert.Contains("Configure Agent service", exception.Message);
    }

    [Fact]
    public async Task MissingAgentExecutable_FailsBeforeServiceMutation()
    {
        var backend = new FakeServiceBackend(
            new InstalledServiceSnapshot(InstalledServiceState.Missing));
        var installer = CreateInstaller(backend, healthy: true);

        await Assert.ThrowsAsync<InstallerFailureException>(
            () => installer.InstallOrRepairAsync(
                Path.Combine(_root, "missing.exe"),
                Path.Combine(_root, "data"),
                CancellationToken.None));

        Assert.Equal(0, backend.QueryCalls);
        Assert.Equal(0, backend.CreateCalls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private WindowsServiceInstaller CreateInstaller(
        FakeServiceBackend backend,
        bool healthy,
        int maximumPollAttempts = 5) =>
        new(
            backend,
            new FakeHealthProbe(healthy),
            delay: (_, _) => Task.CompletedTask,
            maximumPollAttempts: maximumPollAttempts);

    private string CreateAgent()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "Agent With Spaces.exe");
        File.WriteAllText(path, "test");
        return path;
    }

    private sealed class FakeHealthProbe(bool healthy) : IAgentHealthProbe
    {
        public Task<bool> WaitUntilHealthyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(healthy);
    }

    private sealed class FakeServiceBackend(
        InstalledServiceSnapshot initial) : IServiceControlBackend
    {
        public InstalledServiceSnapshot State { get; private set; } = initial;

        public int QueryCalls { get; private set; }

        public int CreateCalls { get; private set; }

        public int ConfigureCalls { get; private set; }

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public int ConfigureExitCode { get; init; }

        public bool KeepMarkedForDeletion { get; init; }

        public string? LastStartType { get; private set; }

        public Task<InstalledServiceSnapshot> QueryAsync(
            CancellationToken cancellationToken)
        {
            QueryCalls++;
            return Task.FromResult(State);
        }

        public Task<ProcessExecutionResult> CreateAsync(
            string binaryPath,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            State = new InstalledServiceSnapshot(
                InstalledServiceState.Stopped,
                binaryPath,
                "auto");
            return Task.FromResult(Result("Create Agent service", 0));
        }

        public Task<ProcessExecutionResult> ConfigureAsync(
            string binaryPath,
            string startType,
            CancellationToken cancellationToken)
        {
            ConfigureCalls++;
            LastStartType = startType;
            if (ConfigureExitCode == 0)
            {
                State = State with
                {
                    BinaryPath = binaryPath,
                    StartType = startType
                };
            }

            return Task.FromResult(
                Result("Configure Agent service", ConfigureExitCode));
        }

        public Task<ProcessExecutionResult> StartAsync(
            CancellationToken cancellationToken)
        {
            StartCalls++;
            State = State with { State = InstalledServiceState.Running };
            return Task.FromResult(Result("Start Agent service", 0));
        }

        public Task<ProcessExecutionResult> StopAsync(
            CancellationToken cancellationToken)
        {
            StopCalls++;
            State = State with { State = InstalledServiceState.Stopped };
            return Task.FromResult(Result("Stop Agent service", 0));
        }

        public Task<ProcessExecutionResult> DeleteAsync(
            CancellationToken cancellationToken)
        {
            DeleteCalls++;
            if (!KeepMarkedForDeletion)
            {
                State = new InstalledServiceSnapshot(
                    InstalledServiceState.Missing);
            }

            return Task.FromResult(Result("Delete Agent service", 0));
        }

        private static ProcessExecutionResult Result(string step, int exitCode) =>
            new(
                step,
                "sc.exe",
                [],
                Environment.CurrentDirectory,
                exitCode,
                exitCode == 0 ? "SUCCESS" : string.Empty,
                exitCode == 0 ? string.Empty : "Access is denied.",
                TimeSpan.FromMilliseconds(1));
    }
}
