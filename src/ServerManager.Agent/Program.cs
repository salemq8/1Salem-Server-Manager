using Microsoft.AspNetCore.Http.Json;
using System.Net;
using ServerManager.Agent;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Logging;
using ServerManager.Infrastructure.Persistence;
using ServerManager.Infrastructure.Processes;
using ServerManager.Infrastructure.Games.Minecraft;
using ServerManager.Infrastructure.Games.Palworld;
using ServerManager.Infrastructure.Games;
using ServerManager.Infrastructure.Security;
using ServerManager.Infrastructure.Backups;
using ServerManager.Infrastructure.Windows;
using ServerManager.Infrastructure.Files;
using ServerManager.Infrastructure.Playit;
using ServerManager.Infrastructure.Updates;

var agentOptions = AgentOptions.Parse(args);
var storageOptions = new SqliteStorageOptions(agentOptions.DataRoot);
var certificateSecretStore = new WindowsDpapiSecretStore();
var certificateIdentity = AgentCertificateManager.LoadOrCreate(
    agentOptions.DataRoot,
    certificateSecretStore);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "1Salem Server Manager Agent";
});
if (agentOptions.LanEnabled)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(IPAddress.Loopback, 5251);
        options.ListenAnyIP(
            agentOptions.LanPort,
            listenOptions => listenOptions.UseHttps(certificateIdentity.Certificate));
    });
}
else
{
    builder.WebHost.UseUrls(agentOptions.ApiUrl);
}
builder.Logging.AddJsonFile(
    new JsonFileLoggerOptions(Path.Combine(storageOptions.LogsRoot, "agent.jsonl")));
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton<SqliteConnectionFactory>();
builder.Services.AddSingleton<IApplicationDatabase, SqliteApplicationDatabase>();
builder.Services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
builder.Services.AddSingleton<IAuditLogStore, SqliteAuditLogStore>();
builder.Services.AddSingleton<IGameServerStore, SqliteGameServerStore>();
builder.Services.AddSingleton<IBackupStore, SqliteBackupStore>();
builder.Services.AddSingleton<IScheduleStore, SqliteScheduleStore>();
builder.Services.AddSingleton<IClientStore, SqliteClientStore>();
// Hosted services start in registration order. The database must exist before
// polling, Playit recovery, scheduling, or metrics services can query stores.
builder.Services.AddHostedService<DatabaseInitializationService>();
builder.Services.AddSingleton<ISecretStore>(certificateSecretStore);
builder.Services.AddSingleton(certificateIdentity);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPairingService, SecurePairingService>();
builder.Services.AddSingleton<IJavaRuntimeLocator, JavaRuntimeLocator>();
builder.Services.AddSingleton<MinecraftServerProvider>();
builder.Services.AddSingleton<IGameServerProvider>(
    services => services.GetRequiredService<MinecraftServerProvider>());
builder.Services.AddSingleton<PalworldServerProvider>();
builder.Services.AddSingleton<IGameServerProvider>(
    services => services.GetRequiredService<PalworldServerProvider>());
builder.Services.AddSingleton<IFileImportService, SafeFileImportService>();
builder.Services.AddSingleton<IRegisteredFileService, RegisteredFileService>();
builder.Services.AddSingleton<MinecraftJarSwapService>();
builder.Services.AddHttpClient<IMinecraftVersionCatalog, MinecraftVersionCatalog>();
builder.Services.AddHttpClient<IJavaRuntimeInstaller, AdoptiumJavaRuntimeInstaller>();
builder.Services.AddHttpClient<IMinecraftInstaller, MinecraftInstaller>();
builder.Services.AddHttpClient<MinecraftUpdateService>();
builder.Services.AddHttpClient<ISteamCmdService, SteamCmdService>();
builder.Services.AddSingleton<IPalworldInstaller, PalworldInstaller>();
builder.Services.AddSingleton<PalworldUpdateService>();
builder.Services.AddHttpClient<PalworldRestClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddSingleton<PalworldManagementCache>();
builder.Services.AddHostedService<PalworldManagementPollingService>();
builder.Services.AddSingleton<PalworldControlCenterService>();
builder.Services.AddSingleton<IUpdateService, GameUpdateService>();
builder.Services.AddSingleton<IBackupService, BackupService>();
builder.Services.AddSingleton<ProcessSupervisor>();
builder.Services.AddSingleton<IProcessSupervisor>(
    services => services.GetRequiredService<ProcessSupervisor>());
builder.Services.AddSingleton<IProcessResourceController>(
    services => services.GetRequiredService<ProcessSupervisor>());
builder.Services.AddSingleton<IConsoleService>(
    services => services.GetRequiredService<ProcessSupervisor>());
builder.Services.AddSingleton<ILogStreamService>(
    services => services.GetRequiredService<ProcessSupervisor>());
builder.Services.AddSingleton<ISystemResourceReader, WindowsSystemResourceReader>();
builder.Services.AddSingleton<INetworkService, WindowsNetworkService>();
builder.Services.AddSingleton<IFirewallService, WindowsFirewallService>();
builder.Services.AddSingleton<AgentRuntimeState>();
builder.Services.AddSingleton<ProcessMetricsCache>();
builder.Services.AddSingleton<NamedPipeRequestDispatcher>();
builder.Services.AddSingleton<GameServerOrchestrator>();
builder.Services.AddSingleton<ConfigurationRestorePointService>();
builder.Services.AddSingleton<MinecraftCreationCoordinator>();
builder.Services.AddSingleton<ServerConfigurationService>();
builder.Services.AddSingleton<DashboardSnapshotService>();
builder.Services.AddSingleton<PlayitInstallationLocator>();
builder.Services.AddSingleton<IPlayitProcessFactory, SystemPlayitProcessFactory>();
builder.Services.AddSingleton<IPlayitProcessDiscovery, SystemPlayitProcessDiscovery>();
builder.Services.AddSingleton<OfficialPlayitSupervisor>();
builder.Services.AddSingleton<PlayitStartupGate>();
builder.Services.AddHostedService<PlayitRecoveryHostedService>();
builder.Services.AddSingleton(ApplicationUpdateSourceOptions.Production);
builder.Services.AddHttpClient<ApplicationUpdateCoordinator>();
builder.Services.AddHostedService<ApplicationUpdateHostedService>();
builder.Services.AddHostedService<NamedPipeAgentServer>();
builder.Services.AddHostedService<ProcessMetricsService>();
builder.Services.AddHostedService<BackupSchedulerService>();
builder.Services.AddSingleton<ResourceGovernorService>();
builder.Services.AddSingleton<IResourceGovernor>(
    services => services.GetRequiredService<ResourceGovernorService>());
builder.Services.AddHostedService(
    services => services.GetRequiredService<ResourceGovernorService>());
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 32 * 1024;
    options.EnableDetailedErrors = false;
});
builder.Services.AddHostedService<AgentLiveBroadcastService>();
builder.Services.AddHostedService<ServerRecoveryService>();

var app = builder.Build();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseMiddleware<ApiAuthenticationMiddleware>();

app.MapGet(
    "/health",
    (AgentRuntimeState runtime) =>
        Results.Ok(new HealthResponse("Healthy", runtime.Version, DateTimeOffset.UtcNow)));
app.MapGet(
    "/api/v1/installed-versions",
    () => Results.Ok(InstalledVersionDetector.Detect()));
app.MapPost(
    "/api/v1/pairing/challenge",
    async (IPairingService pairingService, CancellationToken cancellationToken) =>
        Results.Ok(await pairingService.CreateChallengeAsync(cancellationToken)));
app.MapPost(
    "/api/v1/pairing/complete",
    async (
        HttpContext context,
        PairingCompleteRequest request,
        IPairingService pairingService,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return Results.Ok(await pairingService.CompleteAsync(
                request.Code,
                request.ClientName,
                address,
                cancellationToken));
        }
        catch (PairingRateLimitException exception)
        {
            return Results.Json(
                new OperationResult(false, "PairingRateLimited", exception.Message),
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Results.Json(
                new OperationResult(false, "PairingRejected", exception.Message),
                statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(
                new OperationResult(false, "InvalidPairingRequest", exception.Message));
        }
    });
app.MapGet(
    "/api/v1/pairing/clients",
    async (IPairingService pairingService, CancellationToken cancellationToken) =>
    {
        var clients = await pairingService.ListClientsAsync(cancellationToken);
        return Results.Ok(clients.Select(client => new PairedClientResponse(
            client.Id,
            client.Name,
            client.CertificateFingerprint,
            client.CreatedAtUtc,
            client.LastConnectedAtUtc,
            client.RevokedAtUtc)));
    });
app.MapPost(
    "/api/v1/pairing/clients/{clientId:guid}/rename",
    async (
        Guid clientId,
        PairingRenameRequest request,
        IPairingService pairingService,
        CancellationToken cancellationToken) =>
    {
        await pairingService.RenameAsync(clientId, request.Name, cancellationToken);
        return Results.Ok(OperationResult.Ok());
    });
app.MapPost(
    "/api/v1/pairing/clients/{clientId:guid}/revoke",
    async (
        Guid clientId,
        IPairingService pairingService,
        CancellationToken cancellationToken) =>
    {
        await pairingService.RevokeAsync(clientId, cancellationToken);
        return Results.Ok(OperationResult.Ok());
    });
app.MapGet(
    "/api/v1/agent/status",
    (AgentRuntimeState runtime, AgentOptions options) => Results.Ok(runtime.ToStatus(options)));
app.MapGet(
    "/api/v1/dashboard",
    async (
        DashboardSnapshotService dashboard,
        CancellationToken cancellationToken) =>
        Results.Ok(await dashboard.GetAsync(cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/activity",
    async (
        Guid serverId,
        int? limit,
        IAuditLogStore auditLog,
        CancellationToken cancellationToken) =>
    {
        var items = await auditLog.ListRecentAsync(
            serverId.ToString(),
            Math.Clamp(limit ?? 40, 1, 50),
            cancellationToken);
        return Results.Ok(items.Select(item => new ServerActivityItem(
            item.TimestampUtc,
            item.Actor,
            item.Action,
            DescribeActivity(item.Action),
            item.Succeeded,
            item.Detail)));
    });
app.MapGet(
    "/api/v1/processes",
    (ProcessMetricsCache metrics) => Results.Ok(metrics.Snapshot()));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/logs",
    (
        Guid serverId,
        ProcessSupervisor processSupervisor) =>
        Results.Ok(processSupervisor.GetRecentLogs(serverId)
            .TakeLast(500)
            .Select(AgentHub.Redact)));
app.MapGet(
    "/api/v1/network",
    async (INetworkService networkService, CancellationToken cancellationToken) =>
        Results.Ok(await networkService.GetSnapshotAsync(cancellationToken)));
app.MapPost(
    "/api/v1/network/preference",
    async (
        NetworkPreferenceRequest request,
        INetworkService networkService,
        CancellationToken cancellationToken) =>
        Results.Ok(await networkService.SetPreferredAdapterAsync(
            request.AdapterId,
            cancellationToken)));
app.MapPost(
    "/api/v1/network/test-port",
    async (
        PortTestRequest request,
        INetworkService networkService,
        CancellationToken cancellationToken) =>
        Results.Ok(await networkService.TestPortAsync(
            request.Port,
            request.Protocol,
            cancellationToken)));
app.MapGet(
    "/api/v1/playit",
    async (
        OfficialPlayitSupervisor playit,
        CancellationToken cancellationToken) =>
        Results.Ok(await playit.GetStatusAsync(cancellationToken)));
app.MapPost(
    "/api/v1/playit/detect",
    async (
        OfficialPlayitSupervisor playit,
        CancellationToken cancellationToken) =>
        Results.Ok(await playit.DetectAsync(cancellationToken)));
app.MapPost(
    "/api/v1/playit/settings",
    async (
        PlayitSettingsRequest request,
        OfficialPlayitSupervisor playit,
        CancellationToken cancellationToken) =>
        Results.Ok(await playit.SaveSettingsAsync(request, cancellationToken)));
app.MapPost(
    "/api/v1/playit/start",
    async (
        OfficialPlayitSupervisor playit,
        CancellationToken cancellationToken) =>
        Results.Ok(await playit.StartAsync(cancellationToken)));
app.MapPost(
    "/api/v1/playit/stop",
    async (
        OfficialPlayitSupervisor playit,
        CancellationToken cancellationToken) =>
        Results.Ok(await playit.StopAsync(cancellationToken)));
app.MapPost(
    "/api/v1/playit/restart",
    async (
        OfficialPlayitSupervisor playit,
        CancellationToken cancellationToken) =>
        Results.Ok(await playit.RestartAsync(cancellationToken)));
app.MapPost(
    "/api/v1/playit/disable",
    async (
        OfficialPlayitSupervisor playit,
        CancellationToken cancellationToken) =>
    {
        var status = await playit.GetStatusAsync(cancellationToken);
        await playit.SaveSettingsAsync(
            new PlayitSettingsRequest(
                false,
                status.AgentName,
                status.Minecraft.PublicAddress,
                status.Palworld.PublicAddress),
            cancellationToken);
        return Results.Ok(await playit.StopAsync(cancellationToken));
    });
app.MapGet(
    "/api/v1/application-updates",
    async (
        ApplicationUpdateCoordinator updates,
        CancellationToken cancellationToken) =>
        Results.Ok(await updates.GetStatusAsync(cancellationToken)));
app.MapPost(
    "/api/v1/application-updates/settings",
    async (
        ApplicationUpdateSettingsRequest request,
        ApplicationUpdateCoordinator updates,
        CancellationToken cancellationToken) =>
        Results.Ok(await updates.SaveSettingsAsync(request, cancellationToken)));
app.MapPost(
    "/api/v1/application-updates/check",
    async (
        ApplicationUpdateCoordinator updates,
        CancellationToken cancellationToken) =>
        Results.Ok(await updates.CheckAsync(cancellationToken)));
app.MapPost(
    "/api/v1/application-updates/download",
    async (
        ApplicationUpdateActionRequest request,
        ApplicationUpdateCoordinator updates,
        CancellationToken cancellationToken) =>
        Results.Ok(await updates.DownloadAsync(request, cancellationToken)));
app.MapPost(
    "/api/v1/application-updates/prepare",
    async (
        ApplicationUpdateActionRequest request,
        ApplicationUpdateCoordinator updates,
        IGameServerStore serverStore,
        IBackupService backupService,
        GameServerOrchestrator orchestrator,
        CancellationToken cancellationToken) =>
    {
        var running = (await serverStore.ListAsync(cancellationToken))
            .Where(server => server.State == ServerState.Running)
            .ToArray();
        if (running.Length > 0 && request.ApproveWhileGameServerBusy)
        {
            foreach (var server in running)
            {
                await backupService.CreateAsync(
                    new BackupRequest(
                        server.Id,
                        BackupDestinationPolicy.GetDefaultRoot(server),
                        false,
                        true),
                    cancellationToken);
                var stopped = await orchestrator.StopAsync(
                    server.Id,
                    force: false,
                    cancellationToken);
                if (!stopped.Success)
                {
                    return Results.Conflict(new ApplicationUpdateLaunchResponse(
                        false,
                        $"The safety backup completed, but {server.Name} did not stop gracefully. The update was postponed.",
                        null,
                        [],
                        await updates.GetStatusAsync(cancellationToken)));
                }
            }
        }

        return Results.Ok(await updates.PrepareInstallAsync(
            request,
            cancellationToken));
    });
app.MapGet(
    "/api/v1/resources",
    async (
        IResourceGovernor governor,
        CancellationToken cancellationToken) =>
        Results.Ok(await governor.GetSnapshotAsync(cancellationToken)));
app.MapPost(
    "/api/v1/resources/profile",
    async (
        ResourceProfileRequest request,
        ISystemResourceReader reader,
        IResourceGovernor governor,
        IGameServerStore serverStore,
        IProcessSupervisor processSupervisor,
        PalworldRestClient palworldRestClient,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var hardLimitChanged =
                request.HardMemoryLimitBytes !=
                governor.ActivePolicy.HardMemoryLimitBytes;
            if (hardLimitChanged && request.HardMemoryLimitBytes is not null)
            {
                if (!string.Equals(
                        request.HardLimitConfirmation,
                        "APPLY HARD LIMIT",
                        StringComparison.Ordinal))
                {
                    return Results.BadRequest(OperationResult.Fail(
                        "HardLimitConfirmationRequired",
                        "Type APPLY HARD LIMIT before enforcing a native-process memory limit."));
                }

                var servers = (await serverStore.ListAsync(cancellationToken))
                    .ToDictionary(server => server.Id);
                var running = await processSupervisor.GetAllSnapshotsAsync(
                    cancellationToken);
                foreach (var process in running.Where(snapshot =>
                             snapshot.State == ServerState.Running &&
                             servers.TryGetValue(snapshot.ServerId, out var server) &&
                             server.Game == GameType.Palworld))
                {
                    var saved = await palworldRestClient.SaveWorldAsync(
                        servers[process.ServerId],
                        cancellationToken);
                    if (!saved.Success)
                    {
                        return Results.BadRequest(OperationResult.Fail(
                            "HardLimitWorldSaveFailed",
                            $"The hard limit was not applied because Palworld Save World failed: {saved.Message}"));
                    }
                }
            }

            var totalMemory = reader.Capture(governor.ActivePolicy).TotalMemoryBytes;
            var policy = ResourcePolicyCatalog.Create(
                request.Mode,
                totalMemory,
                request.WindowsReserveBytes,
                request.MaximumServerBudgetBytes,
                request.CpuAffinityMask,
                request.StopLowerPriorityGame,
                request.MinecraftPriority,
                request.PalworldPriority,
                request.PalworldWarningThresholdBytes,
                request.PalworldCriticalThresholdBytes,
                request.CriticalNotificationEnabled,
                request.AutoSaveAndRestart,
                request.HardMemoryLimitBytes,
                request.RestoreBalancedOnServerStop,
                request.AllowUnsafeStartupOverride);
            await governor.ApplyAsync(policy, cancellationToken);
            return Results.Ok(await governor.GetSnapshotAsync(cancellationToken));
        }
        catch (ResourceGovernorService.ResourcePolicyApplyException exception)
        {
            var permissionDenied = exception.Failures.Any(failure =>
                failure.ErrorCode == "PermissionDenied");
            return Results.Conflict(OperationResult.Fail(
                permissionDenied ? "PermissionDenied" : "ResourcePolicyFailed",
                exception.Message));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new OperationResult(
                false,
                "InvalidResourcePolicy",
                exception.Message));
        }
    });
app.MapPost(
    "/api/v1/resources/prioritize/{game}",
    async (
        GameType game,
        ISystemResourceReader reader,
        IResourceGovernor governor,
        CancellationToken cancellationToken) =>
    {
        var mode = game == GameType.Minecraft
            ? ResourceMode.MinecraftPriority
            : ResourceMode.PalworldPriority;
        var totalMemory = reader.Capture(governor.ActivePolicy).TotalMemoryBytes;
        var policy = ResourcePolicyCatalog.Create(mode, totalMemory);
        try
        {
            await governor.ApplyAsync(policy, cancellationToken);
            return Results.Ok(await governor.GetSnapshotAsync(cancellationToken));
        }
        catch (ResourceGovernorService.ResourcePolicyApplyException exception)
        {
            return Results.Conflict(OperationResult.Fail(
                exception.Failures.Any(failure =>
                    failure.ErrorCode == "PermissionDenied")
                    ? "PermissionDenied"
                    : "ResourcePolicyFailed",
                exception.Message));
        }
    });
app.MapGet(
    "/api/v1/resources/profile/status",
    async (
        IResourceGovernor governor,
        IGameServerStore serverStore,
        IProcessSupervisor processSupervisor,
        CancellationToken cancellationToken) =>
    {
        var policy = governor.ActivePolicy;
        var servers = await serverStore.ListAsync(cancellationToken);
        var snapshots = (await processSupervisor.GetAllSnapshotsAsync(
                cancellationToken))
            .ToDictionary(snapshot => snapshot.ServerId);
        var verification = servers.Select(server =>
        {
            snapshots.TryGetValue(server.Id, out var process);
            var expected = server.Game == GameType.Palworld
                ? policy.PalworldPriority
                : policy.MinecraftPriority;
            var running = process is { State: ServerState.Running };
            var verified = running &&
                           process!.Priority == expected &&
                           (policy.CpuAffinityMask is null ||
                            process.CpuAffinityMask == policy.CpuAffinityMask);
            return new ResourceProcessVerification(
                server.Id,
                server.Game,
                running,
                process?.ProcessId,
                process?.GameProcessId,
                process?.Priority,
                process?.CpuAffinityMask,
                verified,
                !running
                    ? "Server not running"
                    : verified
                        ? "Priority and CPU affinity verified."
                        : "Actual process resources differ from the active profile.");
        }).ToArray();
        var anyRunning = verification.Any(item => item.IsRunning);
        var allVerified = anyRunning &&
                          verification.Where(item => item.IsRunning)
                              .All(item => item.Verified);
        return Results.Ok(new ResourceProfileApplyResponse(
            allVerified,
            !anyRunning
                ? ResourceProfileApplyState.ServerNotRunning
                : allVerified
                    ? ResourceProfileApplyState.Active
                    : ResourceProfileApplyState.Failed,
            !anyRunning
                ? "No managed game server is running."
                : allVerified
                    ? ResourceProfileSummary.Build(
                        policy,
                        servers.Select(server => server.Game)) +
                      " · priority and affinity verified."
                    : "One or more process resource values could not be verified.",
            policy.Mode,
            verification,
            DateTimeOffset.UtcNow));
    });
app.MapGet(
    "/api/v1/resources/memory-performance",
    async (
        IResourceGovernor governor,
        ISystemResourceReader reader,
        IGameServerStore serverStore,
        IProcessSupervisor processSupervisor,
        PalworldManagementCache management,
        CancellationToken cancellationToken) =>
    {
        var system = reader.Capture(governor.ActivePolicy);
        var servers = await serverStore.ListAsync(cancellationToken);
        var processes = (await processSupervisor.GetAllSnapshotsAsync(
                cancellationToken))
            .ToDictionary(snapshot => snapshot.ServerId);
        var palworld = servers.FirstOrDefault(server =>
            server.Game == GameType.Palworld);
        var minecraft = servers.FirstOrDefault(server =>
            server.Game == GameType.Minecraft);
        ProcessSnapshot? palworldProcess = null;
        if (palworld is not null)
        {
            processes.TryGetValue(palworld.Id, out palworldProcess);
        }

        var minecraftConfigured = minecraft?.MaximumMemoryMb is { } maximum
            ? maximum * 1024L * 1024
            : 0;
        var policy = governor.ActivePolicy;
        var registeredIds = servers.Select(server => server.Id).ToHashSet();
        var activeProcesses = processes.Values.Where(process =>
            process.State == ServerState.Running &&
            process.ExitCode is null &&
            process.ProcessId > 0 &&
            registeredIds.Contains(process.ServerId)).ToArray();
        var palworldRunning =
            palworldProcess is
            {
                State: ServerState.Running,
                ExitCode: null
            };
        var minecraftRunning = minecraft is not null &&
            processes.TryGetValue(minecraft.Id, out var minecraftProcess) &&
            minecraftProcess.State == ServerState.Running &&
            minecraftProcess.ExitCode is null;
        var recommendations = new List<string>
        {
            "Palworld is a native application; this policy does not reserve RAM like Minecraft Xms/Xmx.",
            $"Keep at least {policy.WindowsReserveBytes / (double)ResourcePolicyCatalog.Gibibyte:F1} GiB available for Windows."
        };
        if (system.TotalMemoryBytes <= 18 * ResourcePolicyCatalog.Gibibyte &&
            (system.AvailableMemoryBytes < 6 * ResourcePolicyCatalog.Gibibyte ||
             minecraftConfigured + (palworldProcess?.WorkingSetBytes ?? 0) >
             policy.MaximumServerBudgetBytes))
        {
            recommendations.Add(
                "On this approximately 16 GB system, run one heavy game server at a time when available memory is insufficient.");
        }

        return Results.Ok(new MemoryPerformancePolicySnapshot(
            system.TotalMemoryBytes,
            Math.Max(0, system.TotalMemoryBytes - system.AvailableMemoryBytes),
            system.AvailableMemoryBytes,
            policy.WindowsReserveBytes,
            System.Diagnostics.Process.GetCurrentProcess().WorkingSet64,
            palworldProcess?.WorkingSetBytes ?? 0,
            palworldProcess?.PrivateMemoryBytes ?? 0,
            palworldProcess?.PeakWorkingSetBytes ?? 0,
            minecraftConfigured,
            policy.PalworldWarningThresholdBytes,
            policy.PalworldCriticalThresholdBytes,
            policy.MaximumServerBudgetBytes,
            policy.HardMemoryLimitBytes,
            policy.Mode,
            policy.PalworldPriority,
            policy.MinecraftPriority,
            policy.CpuAffinityMask,
            palworldProcess?.ProcessId,
            palworldProcess?.GameProcessId,
            palworld is null
                ? 0
                : management.Get(palworld.Id)?.PlayersOnline ?? 0,
            policy.AutoSaveAndRestart,
            policy.CriticalNotificationEnabled,
            policy.RestoreBalancedOnServerStop,
            recommendations,
            system.Warnings,
            policy.AllowUnsafeStartupOverride,
            palworld is not null,
            minecraft is not null,
            palworldRunning,
            minecraftRunning,
            activeProcesses.Sum(process => process.WorkingSetBytes),
            ResourceProfileSummary.Build(
                policy,
                servers.Select(server => server.Game))));
    });
app.MapGet(
    "/api/v1/minecraft/versions",
    async (IMinecraftVersionCatalog catalog, CancellationToken cancellationToken) =>
        Results.Ok(await catalog.GetReleasesAsync(cancellationToken)));
app.MapPost(
    "/api/v1/minecraft/plan",
    async (
        MinecraftPlanRequest request,
        MinecraftCreationCoordinator coordinator,
        CancellationToken cancellationToken) =>
        Results.Ok(await coordinator.PlanAsync(request, cancellationToken)));
app.MapPost(
    "/api/v1/minecraft/create",
    (
        MinecraftInstallRequest request,
        MinecraftCreationCoordinator coordinator) =>
        Results.Accepted(value: coordinator.Start(request)));
app.MapGet(
    "/api/v1/minecraft/creation/{operationId:guid}",
    (
        Guid operationId,
        MinecraftCreationCoordinator coordinator) =>
        coordinator.Get(operationId) is { } progress
            ? Results.Ok(progress)
            : Results.NotFound(new ApiErrorResponse(
                "CreationOperationNotFound",
                "The Minecraft creation operation was not found.",
                "It may have expired or the Agent may have restarted.",
                operationId.ToString(),
                "Start a new creation operation.",
                false)));
app.MapPost(
    "/api/v1/minecraft/install",
    async (
        MinecraftInstallRequest request,
        IMinecraftInstaller installer,
        CancellationToken cancellationToken) =>
        Results.Ok(await installer.InstallAsync(request, cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/backups",
    async (
        Guid serverId,
        IBackupService backupService,
        CancellationToken cancellationToken) =>
        Results.Ok(await backupService.ListAsync(serverId, cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/backups",
    async (
        Guid serverId,
        IGameServerStore store,
        IBackupService backupService,
        CancellationToken cancellationToken) =>
    {
        var server = await store.GetAsync(serverId, cancellationToken);
        return server is null
            ? Results.NotFound()
            : Results.Ok(await backupService.CreateAsync(
                new BackupRequest(
                    serverId,
                    BackupDestinationPolicy.GetDefaultRoot(server),
                    false,
                    server.Game == GameType.Minecraft),
                cancellationToken));
    });
app.MapPost(
    "/api/v1/servers/{serverId:guid}/backups/create",
    async (
        Guid serverId,
        PalworldBackupCreateRequest request,
        IGameServerStore store,
        IBackupService backupService,
        CancellationToken cancellationToken) =>
    {
        var server = await store.GetAsync(serverId, cancellationToken);
        if (server is null)
        {
            return Results.NotFound();
        }

        var destination = string.IsNullOrWhiteSpace(request.DestinationRoot)
            ? BackupDestinationPolicy.GetDefaultRoot(server)
            : request.DestinationRoot;
        return Results.Ok(await backupService.CreateAsync(
            new BackupRequest(
                serverId,
                destination,
                request.IncludeLogs,
                server.Game == GameType.Minecraft,
                false,
                request.ProtectAfterCreation,
                request.DisplayName,
                request.Notes),
            cancellationToken));
    });
app.MapPost(
    "/api/v1/servers/{serverId:guid}/backups/destination/validate",
    async (
        Guid serverId,
        PalworldBackupCreateRequest request,
        IGameServerStore store,
        CancellationToken cancellationToken) =>
    {
        var server = await store.GetAsync(serverId, cancellationToken);
        if (server is null)
        {
            return Results.NotFound();
        }

        var destination = string.IsNullOrWhiteSpace(request.DestinationRoot)
            ? BackupDestinationPolicy.GetDefaultRoot(server)
            : request.DestinationRoot;
        return Results.Ok(BackupDestinationPolicy.Validate(
            server,
            destination));
    });
app.MapPost(
    "/api/v1/backups/{backupId:guid}/verify",
    async (
        Guid backupId,
        IBackupService backupService,
        CancellationToken cancellationToken) =>
        Results.Ok(await backupService.VerifyAsync(backupId, cancellationToken)));
app.MapPost(
    "/api/v1/backups/{backupId:guid}/restore",
    async (
        Guid backupId,
        IBackupService backupService,
        CancellationToken cancellationToken) =>
        Results.Ok(await backupService.RestoreAsync(backupId, cancellationToken)));
app.MapPost(
    "/api/v1/backups/{backupId:guid}/metadata",
    async (
        Guid backupId,
        BackupMetadataUpdateRequest request,
        IBackupService backupService,
        CancellationToken cancellationToken) =>
        Results.Ok(await backupService.UpdateMetadataAsync(
            backupId,
            request.DisplayName,
            request.Notes,
            request.IsProtected,
            cancellationToken)));
app.MapDelete(
    "/api/v1/backups/{backupId:guid}",
    async (
        Guid backupId,
        IBackupService backupService,
        CancellationToken cancellationToken) =>
        Results.Ok(await backupService.DeleteAsync(
            backupId,
            cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/backup-center",
    async (
        Guid serverId,
        IGameServerStore gameServerStore,
        IScheduleStore scheduleStore,
        ISettingsStore settingsStore,
        CancellationToken cancellationToken) =>
    {
        var server = await gameServerStore.GetAsync(serverId, cancellationToken);
        if (server is null)
        {
            return Results.NotFound();
        }

        var saved = await settingsStore.GetAsync<BackupCenterSettings>(
            $"backup.center.{server.Id:N}",
            cancellationToken);
        var schedule = (await scheduleStore.ListAsync(cancellationToken))
            .FirstOrDefault(item =>
                item.ServerId == server.Id &&
                item.Kind.Equals("Backup", StringComparison.OrdinalIgnoreCase));
        return Results.Ok(saved ?? new BackupCenterSettings(
            server.Id,
            schedule?.Enabled ?? false,
            schedule?.CronExpression ?? "@daily",
            BackupDestinationPolicy.GetDefaultRoot(server),
            20,
            30,
            100L * 1024 * 1024 * 1024,
            true,
            true,
            schedule?.NextRunAtUtc,
            schedule?.LastRunAtUtc,
            null));
    });
app.MapPost(
    "/api/v1/backup-schedules",
    async (
        BackupScheduleRequest request,
        IGameServerStore gameServerStore,
        IScheduleStore scheduleStore,
        ISettingsStore settingsStore,
        CancellationToken cancellationToken) =>
    {
        var server = await gameServerStore.GetAsync(
            request.ServerId,
            cancellationToken);
        if (server is null)
        {
            return Results.NotFound();
        }

        if (request.MaximumCount < 1 ||
            request.MaximumAgeDays < 1 ||
            request.MaximumTotalBytes < BackupDestinationPolicy.MinimumFreeSpaceBytes)
        {
            return Results.BadRequest(OperationResult.Fail(
                "InvalidRetentionPolicy",
                "Retention requires a positive count/age and at least 256 MiB maximum storage."));
        }

        var destination = string.IsNullOrWhiteSpace(request.DestinationRoot)
            ? BackupDestinationPolicy.GetDefaultRoot(server)
            : request.DestinationRoot;
        var destinationResult = BackupDestinationPolicy.Validate(
            server,
            destination);
        if (!destinationResult.IsValid)
        {
            return Results.BadRequest(destinationResult);
        }

        var now = DateTimeOffset.UtcNow;
        var existing = (await scheduleStore.ListAsync(cancellationToken))
            .FirstOrDefault(item =>
                item.ServerId == request.ServerId &&
                item.Kind.Equals("Backup", StringComparison.OrdinalIgnoreCase));
        var schedule = new ScheduleRecord(
            existing?.Id ?? Guid.NewGuid(),
            request.ServerId,
            "Backup",
            request.Expression,
            request.Enabled,
            ScheduleExpression.GetNextRun(request.Expression, now),
            existing?.LastRunAtUtc);
        await scheduleStore.UpsertAsync(schedule, cancellationToken);
        await settingsStore.SetAsync(
            $"backup.center.{server.Id:N}",
            new BackupCenterSettings(
                server.Id,
                request.Enabled,
                request.Expression,
                destinationResult.ResolvedPath,
                request.MaximumCount,
                request.MaximumAgeDays,
                request.MaximumTotalBytes,
                request.KeepDaily,
                request.KeepWeekly,
                schedule.NextRunAtUtc,
                schedule.LastRunAtUtc,
                null),
            cancellationToken);
        return Results.Ok(schedule);
    });
app.MapPost(
    "/api/v1/palworld/install",
    async (
        PalworldInstallRequest request,
        IPalworldInstaller installer,
        CancellationToken cancellationToken) =>
        Results.Ok(await installer.InstallAsync(request, cancellationToken)));
app.MapGet(
    "/api/v1/servers",
    async (IGameServerStore store, CancellationToken cancellationToken) =>
        Results.Ok(await store.ListAsync(cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/minecraft/configuration",
    async (
        Guid serverId,
        ServerConfigurationService configuration,
        CancellationToken cancellationToken) =>
        Results.Ok(await configuration.GetMinecraftAsync(
            serverId,
            cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/configuration-restore-points",
    async (
        Guid serverId,
        ConfigurationRestorePointService restorePoints,
        CancellationToken cancellationToken) =>
        Results.Ok(await restorePoints.ListAsync(
            serverId,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/configuration-restore-points/restore",
    async (
        Guid serverId,
        ConfigurationRestorePointRestoreRequest request,
        ConfigurationRestorePointService restorePoints,
        CancellationToken cancellationToken) =>
        Results.Ok(await restorePoints.RestoreAsync(
            serverId,
            request,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/configuration-restore-points/{restorePointId}/label",
    async (
        Guid serverId,
        string restorePointId,
        ConfigurationRestorePointLabelRequest request,
        ConfigurationRestorePointService restorePoints,
        CancellationToken cancellationToken) =>
        Results.Ok(await restorePoints.SetLabelAsync(
            serverId,
            restorePointId,
            request,
            cancellationToken)));
app.MapDelete(
    "/api/v1/servers/{serverId:guid}/configuration-restore-points/{restorePointId}",
    async (
        Guid serverId,
        string restorePointId,
        ConfigurationRestorePointService restorePoints,
        CancellationToken cancellationToken) =>
        Results.Ok(await restorePoints.DeleteAsync(
            serverId,
            restorePointId,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/minecraft/memory",
    async (
        Guid serverId,
        MinecraftMemoryUpdateRequest request,
        ServerConfigurationService configuration,
        CancellationToken cancellationToken) =>
        Results.Ok(await configuration.UpdateMinecraftMemoryAsync(
            serverId,
            request,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/minecraft/settings",
    async (
        Guid serverId,
        ServerSettingsUpdateRequest request,
        ServerConfigurationService configuration,
        CancellationToken cancellationToken) =>
        Results.Ok(await configuration.UpdateMinecraftSettingsAsync(
            serverId,
            request,
            cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/minecraft/players",
    async (
        Guid serverId,
        ServerConfigurationService configuration,
        CancellationToken cancellationToken) =>
        Results.Ok(await configuration.GetMinecraftPlayersAsync(
            serverId,
            cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/palworld/configuration",
    async (
        Guid serverId,
        ServerConfigurationService configuration,
        CancellationToken cancellationToken) =>
        Results.Ok(await configuration.GetPalworldAsync(
            serverId,
            cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/palworld/management",
    (
        Guid serverId,
        PalworldManagementCache cache) =>
        cache.Get(serverId) is { } snapshot
            ? Results.Ok(snapshot)
            : Results.Ok(PalworldRestClient.Unavailable(
                serverId,
                8212,
                "Palworld management data has not been collected yet.")));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/palworld/management/enable",
    async (
        Guid serverId,
        PalworldRestEnableRequest request,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.EnableManagementAsync(
            serverId,
            request,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/palworld/management/repair",
    async (
        Guid serverId,
        PalworldRestEnableRequest request,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.EnableManagementAsync(
            serverId,
            request,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/palworld/management/retry",
    async (
        Guid serverId,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.RetryManagementAsync(
            serverId,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/palworld/management/test",
    async (
        Guid serverId,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.TestManagementAsync(
            serverId,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/palworld/management/disable",
    async (
        Guid serverId,
        PalworldManagementActionRequest request,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.DisableManagementAsync(
            serverId,
            request,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/palworld/save",
    async (
        Guid serverId,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.SaveWorldNowAsync(
            serverId,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/palworld/announcement",
    async (
        Guid serverId,
        PalworldAnnouncementRequest request,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.AnnounceAsync(
            serverId,
            request,
            cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/palworld/world-settings",
    async (
        Guid serverId,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.GetWorldSettingsAsync(
            serverId,
            cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/palworld/world-settings/presets/{preset}",
    async (
        Guid serverId,
        string preset,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.GetWorldSettingsPresetAsync(
            serverId,
            preset,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/palworld/world-settings",
    async (
        Guid serverId,
        PalworldWorldSettingsUpdateRequest request,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.UpdateWorldSettingsAsync(
            serverId,
            request,
            cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/palworld/configuration-history",
    async (
        Guid serverId,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.GetConfigurationHistoryAsync(
            serverId,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/palworld/configuration-history/restore",
    async (
        Guid serverId,
        PalworldRestoreConfigurationRequest request,
        PalworldControlCenterService controlCenter,
        CancellationToken cancellationToken) =>
        Results.Ok(await controlCenter.RestoreConfigurationAsync(
            serverId,
            request,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/palworld/settings",
    async (
        Guid serverId,
        PalworldSettingsUpdateRequest request,
        ServerConfigurationService configuration,
        CancellationToken cancellationToken) =>
        Results.Ok(await configuration.UpdatePalworldSettingsAsync(
            serverId,
            request,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/automation",
    async (
        Guid serverId,
        ServerAutomationUpdateRequest request,
        ServerConfigurationService configuration,
        CancellationToken cancellationToken) =>
        Results.Ok(await configuration.UpdateAutomationAsync(
            serverId,
            request,
            cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/files",
    async (
        Guid serverId,
        string? relativePath,
        IRegisteredFileService fileService,
        CancellationToken cancellationToken) =>
        Results.Ok(await fileService.ListAsync(
            serverId,
            relativePath ?? string.Empty,
            cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/file-text",
    async (
        Guid serverId,
        string relativePath,
        IRegisteredFileService fileService,
        CancellationToken cancellationToken) =>
        Results.Ok(await fileService.ReadTextAsync(
            serverId,
            relativePath,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/file-text",
    async (
        Guid serverId,
        FileWriteRequest request,
        IRegisteredFileService fileService,
        CancellationToken cancellationToken) =>
        Results.Ok(await fileService.WriteTextAsync(
            serverId,
            request.RelativePath,
            request.Content,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/files/rename",
    async (
        Guid serverId,
        FileRenameRequest request,
        IRegisteredFileService fileService,
        CancellationToken cancellationToken) =>
        Results.Ok(await fileService.RenameAsync(
            serverId,
            request.RelativePath,
            request.NewName,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/files/delete",
    async (
        Guid serverId,
        FileDeleteRequest request,
        IRegisteredFileService fileService,
        CancellationToken cancellationToken) =>
        Results.Ok(await fileService.DeleteAsync(
            serverId,
            request.RelativePath,
            request.ConfirmationText,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/start",
    async (
        Guid serverId,
        GameServerOrchestrator orchestrator,
        CancellationToken cancellationToken) =>
        Results.Ok(await orchestrator.StartAsync(serverId, cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/start-budget",
    async (
        Guid serverId,
        GameServerOrchestrator orchestrator,
        CancellationToken cancellationToken) =>
        Results.Ok(await orchestrator.CalculateStartBudgetAsync(
            serverId,
            cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/start/override",
    async (
        Guid serverId,
        ServerStartRequest request,
        GameServerOrchestrator orchestrator,
        CancellationToken cancellationToken) =>
    {
        if (!request.OverrideUnsafeBudget ||
            !string.Equals(
                request.Confirmation,
                "START ANYWAY",
                StringComparison.Ordinal))
        {
            return Results.BadRequest(OperationResult.Fail(
                "UnsafeBudgetConfirmationRequired",
                "Type START ANYWAY to override the shared server memory budget."));
        }

        return Results.Ok(await orchestrator.StartAsync(
            serverId,
            true,
            cancellationToken));
    });
app.MapPost(
    "/api/v1/servers/{serverId:guid}/stop",
    async (
        Guid serverId,
        ServerStopRequest request,
        GameServerOrchestrator orchestrator,
        CancellationToken cancellationToken) =>
        Results.Ok(await orchestrator.StopAsync(serverId, request.Force, cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/restart",
    async (
        Guid serverId,
        GameServerOrchestrator orchestrator,
        CancellationToken cancellationToken) =>
        Results.Ok(await orchestrator.RestartAsync(serverId, cancellationToken)));
app.MapPost(
    "/api/v1/servers/{serverId:guid}/console",
    async (
        Guid serverId,
        ConsoleCommandRequest request,
        GameServerOrchestrator orchestrator,
        CancellationToken cancellationToken) =>
        Results.Ok(await orchestrator.SendConsoleAsync(
            serverId,
            request.Command,
            cancellationToken)));
app.MapPost(
    "/api/v1/import/plan",
    async (
        ImportRequest request,
        IFileImportService importService,
        CancellationToken cancellationToken) =>
        Results.Ok(await importService.PlanAsync(request, cancellationToken)));
app.MapPost(
    "/api/v1/import/execute",
    async (
        ImportPlan plan,
        IFileImportService importService,
        CancellationToken cancellationToken) =>
        Results.Ok(await importService.ExecuteAsync(plan, cancellationToken)));
app.MapGet(
    "/api/v1/servers/{serverId:guid}/updates",
    async (
        Guid serverId,
        IGameServerStore store,
        IUpdateService updateService,
        CancellationToken cancellationToken) =>
    {
        var server = await store.GetAsync(serverId, cancellationToken);
        return server is null
            ? Results.NotFound()
            : Results.Ok(await updateService.CheckAsync(server, cancellationToken));
    });
app.MapPost(
    "/api/v1/servers/{serverId:guid}/updates",
    async (
        Guid serverId,
        IGameServerStore store,
        IUpdateService updateService,
        IAuditLogStore auditLog,
        CancellationToken cancellationToken) =>
    {
        var server = await store.GetAsync(serverId, cancellationToken);
        if (server is null)
        {
            return Results.NotFound();
        }

        var result = await updateService.UpdateAsync(server, cancellationToken);
        await auditLog.WriteAsync(
            "LocalAdministrator",
            "GameUpdated",
            serverId.ToString(),
            result.Success,
            result.Message ?? result.ErrorCode,
            cancellationToken);
        return Results.Ok(result);
    });
app.MapHub<AgentHub>("/hubs/agent");

static string DescribeActivity(string action) => action switch
{
    "ProcessStarted" => "Server started",
    "ProcessStopped" => "Server stopped",
    "ProcessForceStopped" => "Server force-stopped",
    "ProcessCrashed" => "Server crashed",
    "ProcessExited" => "Server exited",
    "ServerRestarted" => "Server restarted",
    "PalworldSaveWorld" => "World saved",
    "PalworldAnnouncement" => "Announcement sent",
    "BackupCreated" => "Backup created",
    "BackupRestored" => "Backup restored",
    "PalworldWorldSettingChanged" => "World setting changed",
    "PalworldWorldSettingsChanged" => "World settings changed",
    "PalworldManagementEnabled" => "Local management enabled",
    "PalworldManagementDisabled" => "Local management disabled",
    "GameUpdated" => "Server updated",
    _ => System.Text.RegularExpressions.Regex.Replace(
        action,
        "(?<!^)([A-Z])",
        " $1")
};

await app.RunAsync();
