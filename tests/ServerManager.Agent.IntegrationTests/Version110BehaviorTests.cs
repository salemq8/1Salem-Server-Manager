using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Agent;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Agent.IntegrationTests;

public sealed class Version110BehaviorTests : IDisposable
{
    private const long Gibibyte = 1024L * 1024 * 1024;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.Agent.V110",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MinecraftCreationPlan_ReportsMemoryJavaPortAndAddress()
    {
        Directory.CreateDirectory(_root);
        var version = new MinecraftVersionDescriptor(
            "1.21.8",
            "release",
            new Uri("https://example.test/version.json"),
            new Uri("https://example.test/server.jar"),
            "abcdef",
            123,
            21);
        var coordinator = new MinecraftCreationCoordinator(
            new StaticCatalog(version),
            new StaticJavaLocator(),
            null!,
            new StaticSystemReader(),
            new StaticGovernor(),
            new StaticNetworkService(),
            null!,
            null!,
            null!,
            null!,
            new StaticLifetime(),
            NullLogger<MinecraftCreationCoordinator>.Instance);

        var plan = await coordinator.PlanAsync(
            new MinecraftPlanRequest(
                Path.Combine(_root, "Server With Spaces"),
                "1.21.8",
                25565,
                2048,
                4096));

        Assert.Equal("1.21.8", plan.Version.Id);
        Assert.True(plan.PortAvailable);
        Assert.Equal("192.168.1.50:25565", plan.LocalAddress);
        Assert.True(plan.Java?.IsCompatible);
        Assert.Equal(21, plan.Java?.InstalledMajorVersion);
        Assert.True(plan.WindowsReserveBytes >= 2 * Gibibyte);
        Assert.DoesNotContain(
            plan.Warnings,
            warning =>
                warning.Contains("port", StringComparison.OrdinalIgnoreCase) ||
                warning.Contains("requires Java", StringComparison.OrdinalIgnoreCase) ||
                warning.Contains("Xmx must", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Java 21 is required and java.exe is missing", "JavaMissingOrIncompatible")]
    [InlineData("You need to agree to the EULA", "EulaNotAccepted")]
    [InlineData("Failed to bind to port: Address already in use", "PortAlreadyInUse")]
    [InlineData("Invalid maximum heap size -Xmx", "InvalidMemory")]
    [InlineData("server jar hash mismatch", "InvalidServerJar")]
    [InlineData("Access denied to folder", "FolderPermission")]
    [InlineData("Failed to lock world; already running", "WorldLockOrExistingProcess")]
    [InlineData("HTTP download network failure", "DownloadFailure")]
    public void StartupFailureClassification_ReturnsActionableKnownError(
        string message,
        string expectedCode)
    {
        var request = CreateRequest();

        var failure = StartupFailureClassifier.Classify(
            MinecraftCreationStage.StartingFirstLaunch,
            request,
            null,
            new InvalidOperationException(message),
            [$"[Error] {message}"]);

        Assert.Equal(expectedCode, failure.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(failure.SuggestedFix));
        Assert.NotEmpty(failure.ConsoleLines);
        Assert.True(failure.CanRetry);
        Assert.Equal("Xms=2048 MB; Xmx=4096 MB", failure.MemoryAllocation);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private MinecraftInstallRequest CreateRequest() =>
        new(
            "Failure Test",
            Path.Combine(_root, "Failure"),
            "1.21.8",
            25565,
            2048,
            4096,
            new MinecraftServerSettings(
                "Test",
                20,
                "normal",
                "survival",
                true,
                10,
                10,
                false),
            true);

    private sealed class StaticCatalog(MinecraftVersionDescriptor version) :
        IMinecraftVersionCatalog
    {
        public Task<IReadOnlyList<MinecraftVersionDescriptor>> GetReleasesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MinecraftVersionDescriptor>>([version]);

        public Task<MinecraftVersionDescriptor> GetVersionAsync(
            string requestedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(version);
    }

    private sealed class StaticJavaLocator : IJavaRuntimeLocator
    {
        public Task<JavaRuntimeInfo?> FindAsync(
            int minimumMajorVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaRuntimeInfo?>(
                new JavaRuntimeInfo(@"C:\Java 21\bin\java.exe", 21, "Java 21"));
    }

    private sealed class StaticSystemReader : ISystemResourceReader
    {
        public SystemResourceSnapshot Capture(ResourcePolicy activePolicy) =>
            new(
                DateTimeOffset.UtcNow,
                16 * Gibibyte,
                12 * Gibibyte,
                10,
                100 * Gibibyte,
                activePolicy,
                []);
    }

    private sealed class StaticGovernor : IResourceGovernor
    {
        public ResourcePolicy ActivePolicy { get; } =
            new(
                ResourceMode.Balanced,
                ProcessPriorityClass.Normal,
                ProcessPriorityClass.Normal,
                3 * Gibibyte,
                10 * Gibibyte);

        public Task ApplyAsync(
            ResourcePolicy policy,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SystemResourceSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new StaticSystemReader().Capture(ActivePolicy));
    }

    private sealed class StaticNetworkService : INetworkService
    {
        public Task<NetworkSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new NetworkSnapshot(
                    "TEST",
                    "192.168.1.50",
                    false,
                    DateTimeOffset.UtcNow,
                    []));

        public Task<PortTestResponse> TestPortAsync(
            int port,
            string protocol,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new PortTestResponse(port, protocol, true, "available"));
    }

    private sealed class StaticLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
