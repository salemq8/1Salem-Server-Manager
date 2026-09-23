using System.Collections.Concurrent;
using System.Net.Sockets;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Processes;

namespace ServerManager.Agent;

public sealed class MinecraftCreationCoordinator(
    IMinecraftVersionCatalog versionCatalog,
    IJavaRuntimeLocator javaRuntimeLocator,
    IMinecraftInstaller minecraftInstaller,
    ISystemResourceReader systemResourceReader,
    IResourceGovernor resourceGovernor,
    INetworkService networkService,
    IFirewallService firewallService,
    IGameServerStore gameServerStore,
    GameServerOrchestrator orchestrator,
    ProcessSupervisor processSupervisor,
    IHostApplicationLifetime applicationLifetime,
    ILogger<MinecraftCreationCoordinator> logger)
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OperationRetention = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<Guid, MinecraftCreationProgress> _operations = new();

    public async Task<MinecraftCreationPlan> PlanAsync(
        MinecraftPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var destination = ValidateDestination(request.DestinationPath);
        var version = await versionCatalog.GetVersionAsync(request.Version, cancellationToken);
        var system = systemResourceReader.Capture(resourceGovernor.ActivePolicy);
        var memory = MinecraftMemoryPolicy.Evaluate(
            system.TotalMemoryBytes,
            system.AvailableMemoryBytes,
            request.MinimumMemoryMb,
            request.MaximumMemoryMb);
        var port = await networkService.TestPortAsync(
            PortPolicy.Validate(request.Port),
            "TCP",
            cancellationToken);
        var network = await networkService.GetSnapshotAsync(cancellationToken);
        var java = !string.IsNullOrWhiteSpace(request.JavaExecutablePath)
            ? await javaRuntimeLocator.InspectAsync(
                request.JavaExecutablePath,
                cancellationToken)
            : await javaRuntimeLocator.FindAsync(
                version.RequiredJavaMajor,
                cancellationToken);
        var warnings = SafePathPolicy.GetRiskWarnings(destination).ToList();
        warnings.AddRange(memory.Warnings);
        if (!port.IsAvailable)
        {
            warnings.Add(port.Message);
        }

        // With several servers on one machine, the port has to be checked against the ones
        // already managed here, not only against what happens to be listening right now.
        var registered = await gameServerStore.ListAsync(cancellationToken);
        if (ServerPortAllocationPolicy.FindConflict(registered, request.Port) is { } conflict)
        {
            var suggestion = ServerPortAllocationPolicy.SuggestPort(registered, request.Port);
            warnings.Add(
                $"Port {conflict.Port} is already used by '{conflict.ServerName}'." +
                (suggestion is { } free ? $" Port {free} is free." : string.Empty));
        }

        if (java is null || java.MajorVersion < version.RequiredJavaMajor)
        {
            warnings.Add(
                $"Minecraft {version.Id} requires Java {version.RequiredJavaMajor}. " +
                "A compatible runtime must be selected or installed.");
        }

        return new MinecraftCreationPlan(
            version,
            destination,
            request.Port,
            port.IsAvailable,
            network.LocalIpv4 is null
                ? null
                : $"{network.LocalIpv4}:{request.Port}",
            system.TotalMemoryBytes,
            system.AvailableMemoryBytes,
            memory.RecommendedMinimumMemoryMb * MinecraftMemoryPolicy.Mebibyte,
            memory.RecommendedMaximumMemoryMb * MinecraftMemoryPolicy.Mebibyte,
            memory.WindowsReserveMemoryMb * MinecraftMemoryPolicy.Mebibyte,
            new JavaRuntimeDescriptor(
                java?.ExecutablePath,
                java?.MajorVersion ?? 0,
                version.RequiredJavaMajor,
                java?.VersionText,
                java is not null && java.MajorVersion >= version.RequiredJavaMajor,
                true),
            warnings);
    }

    public MinecraftCreationStartResponse Start(MinecraftInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PruneCompletedOperations();
        var operationId = Guid.NewGuid();
        Update(
            operationId,
            MinecraftCreationStage.Queued,
            "Queued",
            "Minecraft server creation is queued.",
            0,
            false,
            false);
        _ = Task.Run(
            () => RunAsync(
                operationId,
                request,
                applicationLifetime.ApplicationStopping),
            CancellationToken.None);
        return new MinecraftCreationStartResponse(operationId);
    }

    public MinecraftCreationProgress? Get(Guid operationId) =>
        _operations.TryGetValue(operationId, out var progress)
            ? progress
            : null;

    private async Task RunAsync(
        Guid operationId,
        MinecraftInstallRequest request,
        CancellationToken cancellationToken)
    {
        MinecraftInstallResult? result = null;
        var currentStage = MinecraftCreationStage.Queued;
        var progress = new Progress<MinecraftInstallStep>(step =>
        {
            currentStage = step.Stage;
            Update(
                operationId,
                step.Stage,
                FormatStage(step.Stage),
                step.Message,
                step.Percent,
                false,
                false);
        });

        try
        {
            var plan = await PlanAsync(
                new MinecraftPlanRequest(
                    request.DestinationPath,
                    request.Version,
                    request.Port,
                    request.MinimumMemoryMb,
                    request.MaximumMemoryMb,
                    request.JavaExecutablePath,
                    request.PreferredAdapterId),
                cancellationToken);
            if (!plan.PortAvailable)
            {
                throw new IOException($"TCP port {request.Port} is already in use.");
            }

            // Another managed server may be configured for this port even while it is stopped,
            // which a live port test cannot see. Creating the second one anyway would leave
            // two servers that can never run together.
            var registered = await gameServerStore.ListAsync(cancellationToken);
            if (ServerPortAllocationPolicy.FindConflict(registered, request.Port) is { } conflict)
            {
                var suggestion = ServerPortAllocationPolicy.SuggestPort(registered, request.Port);
                throw new IOException(
                    $"Port {conflict.Port} is already set up for '{conflict.ServerName}'." +
                    (suggestion is { } free ? $" Try port {free}." : string.Empty));
            }

            var memory = MinecraftMemoryPolicy.Evaluate(
                plan.TotalMemoryBytes,
                plan.AvailableMemoryBytes,
                request.MinimumMemoryMb,
                request.MaximumMemoryMb);
            if (!memory.IsSafe)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    string.Join(" ", memory.Warnings));
            }

            result = await minecraftInstaller.InstallWithProgressAsync(
                request,
                progress,
                cancellationToken);

            if (request.CreateFirewallRule)
            {
                currentStage = MinecraftCreationStage.CreatingFirewallRule;
                Update(
                    operationId,
                    currentStage,
                    FormatStage(currentStage),
                    "Creating a Private-network Minecraft firewall rule.",
                    78,
                    false,
                    false);
                var firewall = await firewallService.EnsureRuleAsync(
                    new FirewallRuleSpec(
                        $"Minecraft {result.ServerId:N}",
                        GameType.Minecraft,
                        request.Port,
                        "TCP",
                        result.JavaExecutablePath
                            ?? throw new InvalidOperationException(
                                "The installed Java path was not returned.")),
                    cancellationToken);
                if (!firewall.Success)
                {
                    throw new InvalidOperationException(
                        $"Windows firewall rule creation failed: {firewall.Message}");
                }
            }

            currentStage = MinecraftCreationStage.StartingFirstLaunch;
            Update(
                operationId,
                currentStage,
                FormatStage(currentStage),
                "Starting the first Minecraft server launch.",
                83,
                false,
                false);
            await orchestrator.StartAsync(result.ServerId, cancellationToken);
            await gameServerStore.SetStateAsync(
                result.ServerId,
                ServerState.Starting,
                cancellationToken);

            currentStage = MinecraftCreationStage.WaitingForStartup;
            Update(
                operationId,
                currentStage,
                FormatStage(currentStage),
                "Waiting for Minecraft to finish loading the world.",
                88,
                false,
                false);
            await WaitForReadyAsync(result.ServerId, request.Port, cancellationToken);

            currentStage = MinecraftCreationStage.VerifyingPort;
            Update(
                operationId,
                currentStage,
                FormatStage(currentStage),
                $"Verifying TCP port {request.Port}.",
                96,
                false,
                false);
            if (!await CanConnectAsync(request.Port, cancellationToken))
            {
                throw new IOException(
                    $"Minecraft reported startup completion, but TCP port {request.Port} " +
                    "did not accept a local connection.");
            }

            await gameServerStore.SetStateWithErrorAsync(
                result.ServerId,
                ServerState.Running,
                null,
                cancellationToken);
            var network = await networkService.GetSnapshotAsync(cancellationToken);
            result = result with
            {
                LocalAddress = network.LocalIpv4 is null
                    ? null
                    : $"{network.LocalIpv4}:{request.Port}",
                Started = true,
                PortReady = true
            };
            Update(
                operationId,
                MinecraftCreationStage.Completed,
                FormatStage(MinecraftCreationStage.Completed),
                result.LocalAddress is null
                    ? "Minecraft is running. No active LAN IPv4 address was detected."
                    : $"Minecraft is running at {result.LocalAddress}.",
                100,
                true,
                true,
                result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FailAsync(
                operationId,
                currentStage,
                request,
                result,
                new OperationCanceledException("The Agent stopped during server creation."),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Minecraft creation operation {OperationId} failed at {Stage}.",
                operationId,
                currentStage);
            await FailAsync(
                operationId,
                currentStage,
                request,
                result,
                exception,
                cancellationToken);
        }
    }

    private async Task WaitForReadyAsync(
        Guid serverId,
        int port,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + StartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await processSupervisor.GetSnapshotAsync(
                serverId,
                cancellationToken);
            var logs = processSupervisor.GetRecentLogs(serverId);
            if (snapshot is null)
            {
                throw new InvalidOperationException(
                    "The Minecraft process exited before startup completed.");
            }

            var done = logs.Any(entry =>
                entry.Message.Contains(
                    "Done (",
                    StringComparison.OrdinalIgnoreCase) ||
                entry.Message.Contains(
                    "For help, type",
                    StringComparison.OrdinalIgnoreCase));
            if (done && await CanConnectAsync(port, cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException(
            $"Minecraft did not report startup completion within {StartupTimeout.TotalMinutes:0} minutes.");
    }

    private static async Task<bool> CanConnectAsync(
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch (Exception exception) when (
            exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task FailAsync(
        Guid operationId,
        MinecraftCreationStage stage,
        MinecraftInstallRequest request,
        MinecraftInstallResult? result,
        Exception exception,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> console = result is null
            ? []
            : processSupervisor.GetRecentLogs(result.ServerId)
                .TakeLast(50)
                .Select(entry => $"[{entry.Level}] {entry.Message}")
                .ToArray();
        var failure = StartupFailureClassifier.Classify(
            stage,
            request,
            result,
            exception,
            console);
        if (result is not null)
        {
            try
            {
                await gameServerStore.SetStateWithErrorAsync(
                    result.ServerId,
                    ServerState.Error,
                    failure.Message,
                    cancellationToken);
            }
            catch (Exception stateException)
            {
                logger.LogWarning(
                    stateException,
                    "Could not persist Minecraft creation failure state.");
            }
        }

        Update(
            operationId,
            MinecraftCreationStage.Failed,
            FormatStage(stage),
            failure.Message,
            100,
            true,
            false,
            result,
            failure);
    }

    private void Update(
        Guid operationId,
        MinecraftCreationStage stage,
        string stageLabel,
        string message,
        int percent,
        bool complete,
        bool succeeded,
        MinecraftInstallResult? result = null,
        StartupFailureDetails? failure = null) =>
        _operations[operationId] = new MinecraftCreationProgress(
            operationId,
            stage,
            stageLabel,
            message,
            Math.Clamp(percent, 0, 100),
            complete,
            succeeded,
            result,
            failure,
            DateTimeOffset.UtcNow);

    private void PruneCompletedOperations()
    {
        var cutoff = DateTimeOffset.UtcNow - OperationRetention;
        foreach (var (id, operation) in _operations)
        {
            if (operation.IsComplete && operation.UpdatedAtUtc < cutoff)
            {
                _operations.TryRemove(id, out _);
            }
        }
    }

    private static string ValidateDestination(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A Minecraft destination is required.", nameof(value));
        }

        var destination = Path.GetFullPath(value);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException(
                "The Minecraft destination already exists. Choose a fresh folder or import it.");
        }

        var parent = Directory.GetParent(destination)
            ?? throw new DirectoryNotFoundException(
                "The Minecraft destination parent is invalid.");
        Directory.CreateDirectory(parent.FullName);
        var probe = Path.Combine(parent.FullName, $".1salem-write-test-{Guid.NewGuid():N}");
        try
        {
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            throw new UnauthorizedAccessException(
                $"The Agent cannot write to {parent.FullName}.",
                exception);
        }

        return destination;
    }

    private static string FormatStage(MinecraftCreationStage stage) =>
        stage switch
        {
            MinecraftCreationStage.ValidatingDestination => "Validating destination",
            MinecraftCreationStage.ResolvingVersion => "Resolving official version",
            MinecraftCreationStage.DetectingJava => "Detecting Java",
            MinecraftCreationStage.InstallingJava => "Installing Java",
            MinecraftCreationStage.DownloadingServer => "Downloading server JAR",
            MinecraftCreationStage.VerifyingDownload => "Verifying download",
            MinecraftCreationStage.CreatingFolders => "Creating folders",
            MinecraftCreationStage.WritingProperties => "Writing server.properties",
            MinecraftCreationStage.WritingMemoryArguments => "Writing JVM memory arguments",
            MinecraftCreationStage.AcceptingEula => "Recording EULA acceptance",
            MinecraftCreationStage.CreatingFirewallRule => "Creating firewall rule",
            MinecraftCreationStage.RegisteringServer => "Registering server",
            MinecraftCreationStage.StartingFirstLaunch => "Starting first launch",
            MinecraftCreationStage.WaitingForStartup => "Waiting for startup",
            MinecraftCreationStage.VerifyingPort => "Verifying server port",
            MinecraftCreationStage.Completed => "Completed",
            MinecraftCreationStage.Failed => "Failed",
            _ => "Queued"
        };
}
