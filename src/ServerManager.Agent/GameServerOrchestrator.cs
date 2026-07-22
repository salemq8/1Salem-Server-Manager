using System.Net.Sockets;
using System.ComponentModel;
using System.Diagnostics;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Processes;

namespace ServerManager.Agent;

public sealed record ManagedProcessIdentity(
    int ProcessId,
    string ExecutablePath,
    DateTimeOffset StartedAtUtc);

public sealed class GameServerOrchestrator(
    IEnumerable<IGameServerProvider> providers,
    IGameServerStore gameServerStore,
    IProcessSupervisor processSupervisor,
    IConsoleService consoleService,
    IProcessResourceController processResourceController,
    ProcessSupervisor processRuntime,
    ISettingsStore settingsStore,
    ISystemResourceReader systemResourceReader,
    IAuditLogStore auditLogStore)
{
    private readonly IReadOnlyDictionary<GameType, IGameServerProvider> _providers =
        providers.ToDictionary(provider => provider.Game);

    public async Task<ProcessSnapshot> StartAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        return await StartAsync(
            serverId,
            false,
            cancellationToken);
    }

    public async Task<ProcessSnapshot> StartAsync(
        Guid serverId,
        bool overrideUnsafeBudget,
        CancellationToken cancellationToken = default)
    {
        var server = await GetRequiredServerAsync(serverId, cancellationToken);
        var budget = await CalculateStartBudgetAsync(server, cancellationToken);
        var usingOverride = !budget.IsSafe && overrideUnsafeBudget;
        if (!budget.IsSafe && !overrideUnsafeBudget)
        {
            throw new ServerBudgetExceededException(budget);
        }

        if (usingOverride && !budget.AllowUnsafeStartupOverride)
        {
            throw new InvalidOperationException(
                "Start Anyway Once is disabled in the active memory policy.");
        }

        await gameServerStore.SetStateWithErrorAsync(
            serverId,
            server.State,
            null,
            cancellationToken);
        var provider = GetRequiredProvider(server.Game);
        await provider.PrepareForStartAsync(server, cancellationToken);
        var spec = provider.CreateLaunchSpec(server);
        await gameServerStore.SetStateWithErrorAsync(
            serverId,
            ServerState.Starting,
            null,
            cancellationToken);
        try
        {
            processSupervisor.ConfigureRestartPolicy(
                serverId,
                server.AutoRestart
                    ? new RestartPolicy(
                        true,
                        TimeSpan.FromSeconds(10),
                        3,
                        TimeSpan.FromMinutes(10))
                    : RestartPolicy.Disabled);
            var snapshot = await processSupervisor.StartAsync(server, spec, cancellationToken);
            await SaveProcessIdentityAsync(serverId, snapshot, spec, cancellationToken);
            var resources = await processResourceController.ApplyResourcesAsync(
                serverId,
                server.Priority,
                server.CpuAffinityMask,
                cancellationToken);
            if (!resources.Success)
            {
                await processSupervisor.StopAsync(
                    serverId,
                    true,
                    CancellationToken.None);
                throw new InvalidOperationException(resources.Message);
            }

            await WaitForReadyAsync(server, cancellationToken);
            await gameServerStore.SetStateWithErrorAsync(
                serverId,
                ServerState.Running,
                null,
                cancellationToken);
            if (usingOverride)
            {
                await auditLogStore.WriteAsync(
                    "ManualStart",
                    "UnsafeBudgetOverride",
                    serverId.ToString(),
                    true,
                    budget.Reason,
                    cancellationToken);
            }

            return snapshot;
        }
        catch (Exception exception)
        {
            await gameServerStore.SetStateWithErrorAsync(
                serverId,
                ServerState.Error,
                exception.Message,
                cancellationToken);
            if (usingOverride)
            {
                await auditLogStore.WriteAsync(
                    "ManualStart",
                    "UnsafeBudgetOverride",
                    serverId.ToString(),
                    false,
                    $"{budget.Reason} Start failed: {exception.Message}",
                    CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<ProcessSnapshot?> AdoptExistingAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await GetRequiredServerAsync(serverId, cancellationToken);
        var provider = GetRequiredProvider(server.Game);
        var spec = provider.CreateLaunchSpec(server);
        var identity = await settingsStore.GetAsync<ManagedProcessIdentity>(
            ProcessIdentityKey(serverId),
            cancellationToken);
        var snapshot = await processSupervisor.AdoptAsync(
            server,
            spec,
            identity?.ProcessId,
            cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        processSupervisor.ConfigureRestartPolicy(
            serverId,
            server.AutoRestart
                ? new RestartPolicy(
                    true,
                    TimeSpan.FromSeconds(10),
                    3,
                    TimeSpan.FromMinutes(10))
                : RestartPolicy.Disabled);
        var resources = await processResourceController.ApplyResourcesAsync(
            serverId,
            server.Priority,
            server.CpuAffinityMask,
            cancellationToken);
        if (!resources.Success)
        {
            throw new InvalidOperationException(
                $"The existing process was re-adopted but its resource policy failed: {resources.Message}");
        }

        await SaveProcessIdentityAsync(serverId, snapshot, spec, cancellationToken);
        await gameServerStore.SetStateWithErrorAsync(
            serverId,
            ServerState.Running,
            null,
            cancellationToken);
        return snapshot;
    }

    public async Task<OperationResult> StopAsync(
        Guid serverId,
        bool force,
        CancellationToken cancellationToken = default)
    {
        var server = await GetRequiredServerAsync(serverId, cancellationToken);
        var provider = GetRequiredProvider(server.Game);
        await gameServerStore.SetStateAsync(serverId, ServerState.Stopping, cancellationToken);
        var result = await processSupervisor.StopAsync(serverId, force, cancellationToken);
        if (result.Success)
        {
            await provider.CleanupAfterStopAsync(server, cancellationToken);
            await settingsStore.SetAsync<ManagedProcessIdentity?>(
                ProcessIdentityKey(serverId),
                null,
                cancellationToken);
        }

        await gameServerStore.SetStateAsync(
            serverId,
            result.Success ? ServerState.Stopped : ServerState.Error,
            cancellationToken);
        return result;
    }

    public async Task<ProcessSnapshot> RestartAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var server = await GetRequiredServerAsync(serverId, cancellationToken);
            var provider = GetRequiredProvider(server.Game);
            await gameServerStore.SetStateAsync(serverId, ServerState.Restarting, cancellationToken);
            var stopped = await StopAsync(serverId, false, cancellationToken);
            if (!stopped.Success)
            {
                throw new InvalidOperationException(stopped.Message);
            }

            var result = await StartAsync(
                serverId,
                false,
                cancellationToken);
            await auditLogStore.WriteAsync(
                "LocalAdministrator",
                "ServerRestarted",
                serverId.ToString(),
                true,
                $"PID {result.ProcessId}",
                cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            await auditLogStore.WriteAsync(
                "LocalAdministrator",
                "ServerRestarted",
                serverId.ToString(),
                false,
                exception.Message,
                cancellationToken);
            throw;
        }
    }

    public Task<OperationResult> SendConsoleAsync(
        Guid serverId,
        string command,
        CancellationToken cancellationToken = default) =>
        consoleService.SendCommandAsync(serverId, command, cancellationToken);

    private async Task<GameServerDefinition> GetRequiredServerAsync(
        Guid serverId,
        CancellationToken cancellationToken) =>
        await gameServerStore.GetAsync(serverId, cancellationToken)
        ?? throw new KeyNotFoundException($"Server {serverId} is not registered.");

    private Task SaveProcessIdentityAsync(
        Guid serverId,
        ProcessSnapshot snapshot,
        ProcessLaunchSpec spec,
        CancellationToken cancellationToken) =>
        settingsStore.SetAsync(
            ProcessIdentityKey(serverId),
            new ManagedProcessIdentity(
                snapshot.ProcessId,
                Path.GetFullPath(spec.FileName),
                snapshot.StartedAtUtc),
            cancellationToken);

    private static string ProcessIdentityKey(Guid serverId) =>
        $"process.identity.{serverId:N}";

    private IGameServerProvider GetRequiredProvider(GameType game) =>
        _providers.TryGetValue(game, out var provider)
            ? provider
            : throw new InvalidOperationException($"No {game} provider is installed.");

    private async Task WaitForReadyAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken)
    {
        var timeout = server.Game == GameType.Minecraft
            ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromSeconds(20);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await processSupervisor.GetSnapshotAsync(
                server.Id,
                cancellationToken);
            if (snapshot is null || snapshot.ExitCode is not null)
            {
                var lastLines = string.Join(
                    Environment.NewLine,
                    processRuntime.GetRecentLogs(server.Id)
                        .TakeLast(10)
                        .Select(line => line.Message));
                throw new InvalidOperationException(
                    $"The {server.Game} process exited during startup." +
                    (string.IsNullOrWhiteSpace(lastLines)
                        ? string.Empty
                        : $"{Environment.NewLine}{lastLines}"));
            }

            if (server.Game == GameType.Palworld)
            {
                if (DateTimeOffset.UtcNow - snapshot.StartedAtUtc >=
                    TimeSpan.FromSeconds(5))
                {
                    return;
                }
            }
            else
            {
                var readyLine = processRuntime.GetRecentLogs(server.Id).Any(line =>
                    line.Message.Contains("Done (", StringComparison.OrdinalIgnoreCase) ||
                    line.Message.Contains(
                        "For help, type",
                        StringComparison.OrdinalIgnoreCase));
                if (readyLine && await CanConnectAsync(server.Port, cancellationToken))
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException(
            $"{server.Game} did not become ready within {timeout.TotalSeconds:0} seconds. " +
            $"Last output: {processRuntime.GetRecentLogs(server.Id).LastOrDefault()?.Message}");
    }

    private static async Task<bool> CanConnectAsync(
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
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

    public async Task<ServerStartBudgetSnapshot> CalculateStartBudgetAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await GetRequiredServerAsync(serverId, cancellationToken);
        return await CalculateStartBudgetAsync(server, cancellationToken);
    }

    private async Task<ServerStartBudgetSnapshot> CalculateStartBudgetAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken)
    {
        var policy = await settingsStore.GetAsync<ResourcePolicy>(
                         "resources.activePolicy",
                         cancellationToken) ??
                     ResourcePolicyCatalog.Create(
                         ResourceMode.Balanced,
                         systemResourceReader.Capture(
                             ResourcePolicyCatalog.Create(
                                 ResourceMode.Balanced,
                                 8 * ResourcePolicyCatalog.Gibibyte))
                             .TotalMemoryBytes);
        var registered = await gameServerStore.ListAsync(cancellationToken);
        var running = (await processSupervisor.GetAllSnapshotsAsync(
                cancellationToken))
            .Where(IsLiveProcessSnapshot)
            .ToArray();
        var system = systemResourceReader.Capture(policy);
        var evaluation = ServerBudgetCalculator.Evaluate(
            server,
            policy,
            system,
            registered,
            running);
        if (policy.Mode == ResourceMode.OneGameAtATime &&
            evaluation.ActiveServers.Count > 0)
        {
            return evaluation with
            {
                IsSafe = false,
                Reason =
                    "Start blocked: One Game at a Time is active and another registered managed server is running.",
                CanStartAnywayOnce = policy.AllowUnsafeStartupOverride
            };
        }

        return evaluation;
    }

    private static bool IsLiveProcessSnapshot(ProcessSnapshot snapshot)
    {
        if (snapshot.State != ServerState.Running ||
            snapshot.ExitCode is not null ||
            snapshot.ProcessId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(snapshot.ProcessId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // Access denied still proves the PID exists. The supervisor's
            // running state remains authoritative for that managed process.
            return true;
        }
    }

    public sealed class ServerBudgetExceededException(
        ServerStartBudgetSnapshot budget) :
        InvalidOperationException(budget.Reason)
    {
        public ServerStartBudgetSnapshot Budget { get; } = budget;
    }
}
