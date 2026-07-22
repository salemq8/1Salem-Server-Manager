using System.IO;
using System.Net;
using System.Net.Http;
using ServerManager.Contracts;

namespace ServerManager.Setup;

public enum InstalledServiceState
{
    Missing,
    Stopped,
    Running,
    StartPending,
    StopPending,
    MarkedForDeletion,
    Unknown
}

public sealed record InstalledServiceSnapshot(
    InstalledServiceState State,
    string BinaryPath = "",
    string StartType = "auto")
{
    public bool Exists =>
        State is not InstalledServiceState.Missing and
            not InstalledServiceState.MarkedForDeletion;
}

public interface IServiceControlBackend
{
    Task<InstalledServiceSnapshot> QueryAsync(CancellationToken cancellationToken);

    Task<ProcessExecutionResult> CreateAsync(
        string binaryPath,
        CancellationToken cancellationToken);

    Task<ProcessExecutionResult> ConfigureAsync(
        string binaryPath,
        string startType,
        CancellationToken cancellationToken);

    Task<ProcessExecutionResult> StartAsync(CancellationToken cancellationToken);

    Task<ProcessExecutionResult> StopAsync(CancellationToken cancellationToken);

    Task<ProcessExecutionResult> DeleteAsync(CancellationToken cancellationToken);
}

public interface IAgentHealthProbe
{
    Task<bool> WaitUntilHealthyAsync(CancellationToken cancellationToken);
}

public sealed class ScServiceControlBackend(
    IInstallerProcessRunner runner) : IServiceControlBackend
{
    public const string ServiceName = "1SalemServerManagerAgent";
    public const string DisplayName = "1Salem Server Manager Agent";

    public async Task<InstalledServiceSnapshot> QueryAsync(
        CancellationToken cancellationToken)
    {
        var configuration = await runner.RunAsync(
            "Read Agent service configuration",
            "sc.exe",
            ["qc", ServiceName],
            cancellationToken);
        if (IsServiceMissing(configuration))
        {
            return new InstalledServiceSnapshot(InstalledServiceState.Missing);
        }

        if (IsMarkedForDeletion(configuration))
        {
            return new InstalledServiceSnapshot(
                InstalledServiceState.MarkedForDeletion);
        }

        RequireSuccess(configuration);
        var status = await runner.RunAsync(
            "Read Agent service status",
            "sc.exe",
            ["query", ServiceName],
            cancellationToken);
        if (IsServiceMissing(status))
        {
            return new InstalledServiceSnapshot(InstalledServiceState.Missing);
        }

        if (IsMarkedForDeletion(status))
        {
            return new InstalledServiceSnapshot(
                InstalledServiceState.MarkedForDeletion);
        }

        RequireSuccess(status);
        return new InstalledServiceSnapshot(
            ParseState(status.CombinedOutput),
            ReadScValue(configuration.CombinedOutput, "BINARY_PATH_NAME"),
            ParseStartType(
                ReadScValue(configuration.CombinedOutput, "START_TYPE")));
    }

    public Task<ProcessExecutionResult> CreateAsync(
        string binaryPath,
        CancellationToken cancellationToken) =>
        runner.RunAsync(
            "Create Agent service",
            "sc.exe",
            WindowsServiceCommandBuilder.CreateArguments(
                ServiceName,
                DisplayName,
                binaryPath),
            cancellationToken);

    public Task<ProcessExecutionResult> ConfigureAsync(
        string binaryPath,
        string startType,
        CancellationToken cancellationToken) =>
        runner.RunAsync(
            "Configure Agent service",
            "sc.exe",
            WindowsServiceCommandBuilder.ConfigureArguments(
                ServiceName,
                binaryPath,
                startType),
            cancellationToken);

    public Task<ProcessExecutionResult> StartAsync(
        CancellationToken cancellationToken) =>
        runner.RunAsync(
            "Start Agent service",
            "sc.exe",
            ["start", ServiceName],
            cancellationToken);

    public Task<ProcessExecutionResult> StopAsync(
        CancellationToken cancellationToken) =>
        runner.RunAsync(
            "Stop Agent service",
            "sc.exe",
            ["stop", ServiceName],
            cancellationToken);

    public Task<ProcessExecutionResult> DeleteAsync(
        CancellationToken cancellationToken) =>
        runner.RunAsync(
            "Delete Agent service",
            "sc.exe",
            ["delete", ServiceName],
            cancellationToken);

    public static bool IsServiceMissing(ProcessExecutionResult result) =>
        result.ExitCode == 1060 ||
        result.CombinedOutput.Contains(
            "1060",
            StringComparison.OrdinalIgnoreCase) ||
        result.CombinedOutput.Contains(
            "does not exist as an installed service",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsMarkedForDeletion(ProcessExecutionResult result) =>
        result.ExitCode == 1072 ||
        result.CombinedOutput.Contains(
            "1072",
            StringComparison.OrdinalIgnoreCase) ||
        result.CombinedOutput.Contains(
            "marked for deletion",
            StringComparison.OrdinalIgnoreCase);

    public static void RequireSuccess(ProcessExecutionResult result)
    {
        if (!result.Success)
        {
            throw InstallerFailureException.FromProcess(result);
        }
    }

    private static string ReadScValue(string output, string key)
    {
        foreach (var line in output.Split(['\r', '\n']))
        {
            var separator = line.IndexOf(':');
            if (separator < 0 ||
                !line[..separator].Trim().Equals(
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(separator + 1)..].Trim();
        }

        return string.Empty;
    }

    private static InstalledServiceState ParseState(string output)
    {
        var value = ReadScValue(output, "STATE");
        if (value.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return InstalledServiceState.Running;
        }

        if (value.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return InstalledServiceState.Stopped;
        }

        if (value.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
        {
            return InstalledServiceState.StartPending;
        }

        return value.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase)
            ? InstalledServiceState.StopPending
            : InstalledServiceState.Unknown;
    }

    private static string ParseStartType(string value)
    {
        if (value.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        if (value.Contains("DEMAND_START", StringComparison.OrdinalIgnoreCase))
        {
            return "demand";
        }

        return value.Contains("DISABLED", StringComparison.OrdinalIgnoreCase)
            ? "disabled"
            : "auto";
    }
}

public sealed class HttpAgentHealthProbe : IAgentHealthProbe, IDisposable
{
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:5251/"),
        Timeout = TimeSpan.FromSeconds(2)
    };

    public async Task<bool> WaitUntilHealthyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var response = await _client.GetAsync("health", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return false;
    }

    public void Dispose() => _client.Dispose();
}

public sealed record ServiceInstallResult(
    bool RepairedExistingService,
    string BinaryPath,
    InstalledServiceState State,
    bool HealthVerified);

public sealed class WindowsServiceInstaller
{
    private readonly IServiceControlBackend _backend;
    private readonly IAgentHealthProbe _healthProbe;
    private readonly InstallerLog? _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _maximumPollAttempts;

    public WindowsServiceInstaller(
        IServiceControlBackend backend,
        IAgentHealthProbe healthProbe,
        InstallerLog? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maximumPollAttempts = 60)
    {
        _backend = backend;
        _healthProbe = healthProbe;
        _log = log;
        _delay = delay ?? Task.Delay;
        _maximumPollAttempts = maximumPollAttempts > 0
            ? maximumPollAttempts
            : throw new ArgumentOutOfRangeException(nameof(maximumPollAttempts));
    }

    public async Task<ServiceInstallResult> InstallOrRepairAsync(
        string agentExecutable,
        string dataRoot,
        CancellationToken cancellationToken,
        InstalledServiceSnapshot? originalSnapshot = null)
    {
        if (!Path.IsPathFullyQualified(agentExecutable) ||
            !File.Exists(agentExecutable))
        {
            throw new InstallerFailureException(
                "Agent service preflight",
                "The published Agent executable is missing.",
                $"Expected Agent executable: {agentExecutable}");
        }

        var binaryPath = WindowsServiceCommandBuilder.BuildBinaryPath(
            agentExecutable,
            dataRoot);
        var initial = await _backend.QueryAsync(cancellationToken);
        var rollbackSnapshot = originalSnapshot ?? initial;
        var repairedExisting = rollbackSnapshot.Exists;
        var createdDuringAttempt = false;
        await WriteLogAsync(
            "Agent service",
            $"Initial state={initial.State}; startType={initial.StartType}; " +
            $"binaryPath=\"{initial.BinaryPath}\"",
            cancellationToken);

        try
        {
            if (initial.State == InstalledServiceState.MarkedForDeletion)
            {
                await WaitForStateAsync(
                    InstalledServiceState.Missing,
                    "Wait for previously deleted Agent service",
                    cancellationToken);
                initial = new InstalledServiceSnapshot(InstalledServiceState.Missing);
            }

            if (initial.Exists &&
                initial.State is InstalledServiceState.Running or
                    InstalledServiceState.StartPending or
                    InstalledServiceState.StopPending)
            {
                var stop = await _backend.StopAsync(cancellationToken);
                if (!stop.Success &&
                    stop.ExitCode != 1062 &&
                    !ScServiceControlBackend.IsServiceMissing(stop))
                {
                    throw InstallerFailureException.FromProcess(stop);
                }

                await WaitForStateAsync(
                    InstalledServiceState.Stopped,
                    "Wait for Agent service to stop",
                    cancellationToken);
            }

            if (!initial.Exists)
            {
                var create = await _backend.CreateAsync(binaryPath, cancellationToken);
                if (ScServiceControlBackend.IsMarkedForDeletion(create))
                {
                    await WaitForStateAsync(
                        InstalledServiceState.Missing,
                        "Wait for stale Agent service deletion",
                        cancellationToken);
                    create = await _backend.CreateAsync(binaryPath, cancellationToken);
                }

                ScServiceControlBackend.RequireSuccess(create);
                createdDuringAttempt = true;
            }

            var configure = await _backend.ConfigureAsync(
                binaryPath,
                "auto",
                cancellationToken);
            if (ScServiceControlBackend.IsMarkedForDeletion(configure))
            {
                await WaitForStateAsync(
                    InstalledServiceState.Missing,
                    "Wait for Agent service deletion before repair",
                    cancellationToken);
                var recreate = await _backend.CreateAsync(binaryPath, cancellationToken);
                ScServiceControlBackend.RequireSuccess(recreate);
                createdDuringAttempt = true;
                configure = await _backend.ConfigureAsync(
                    binaryPath,
                    "auto",
                    cancellationToken);
            }

            ScServiceControlBackend.RequireSuccess(configure);
            var start = await _backend.StartAsync(cancellationToken);
            if (!start.Success &&
                start.ExitCode != 1056 &&
                !start.CombinedOutput.Contains(
                    "already running",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw InstallerFailureException.FromProcess(start);
            }

            await WaitForStateAsync(
                InstalledServiceState.Running,
                "Wait for Agent service to run",
                cancellationToken);
            if (!await _healthProbe.WaitUntilHealthyAsync(cancellationToken))
            {
                throw new InstallerFailureException(
                    "Verify Agent health",
                    "The Agent service started, but its local health check did not respond.",
                    "GET http://127.0.0.1:5251/health did not return HTTP 200 within 10 seconds.");
            }

            return new ServiceInstallResult(
                repairedExisting,
                binaryPath,
                InstalledServiceState.Running,
                true);
        }
        catch
        {
            await RollBackServiceAsync(
                rollbackSnapshot,
                createdDuringAttempt,
                false,
                cancellationToken);
            throw;
        }
    }

    public async Task<InstalledServiceSnapshot> PrepareForDeploymentAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await _backend.QueryAsync(cancellationToken);
        if (snapshot.State == InstalledServiceState.MarkedForDeletion)
        {
            await WaitForStateAsync(
                InstalledServiceState.Missing,
                "Wait for previously deleted Agent service",
                cancellationToken);
            return snapshot;
        }

        if (snapshot.Exists &&
            snapshot.State is not InstalledServiceState.Stopped)
        {
            var stop = await _backend.StopAsync(cancellationToken);
            if (!stop.Success &&
                stop.ExitCode != 1062 &&
                !ScServiceControlBackend.IsServiceMissing(stop))
            {
                throw InstallerFailureException.FromProcess(stop);
            }

            await WaitForStateAsync(
                InstalledServiceState.Stopped,
                "Wait for Agent service before file deployment",
                cancellationToken);
        }

        return snapshot;
    }

    public Task RestoreAsync(
        InstalledServiceSnapshot snapshot,
        CancellationToken cancellationToken) =>
        RollBackServiceAsync(snapshot, false, true, cancellationToken);

    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _backend.QueryAsync(cancellationToken);
        if (snapshot.State == InstalledServiceState.Missing)
        {
            return;
        }

        if (snapshot.State == InstalledServiceState.MarkedForDeletion)
        {
            await WaitForStateAsync(
                InstalledServiceState.Missing,
                "Wait for Agent service removal",
                cancellationToken);
            return;
        }

        if (snapshot.Exists &&
            snapshot.State != InstalledServiceState.Stopped)
        {
            var stop = await _backend.StopAsync(cancellationToken);
            if (!stop.Success &&
                stop.ExitCode != 1062 &&
                !ScServiceControlBackend.IsServiceMissing(stop))
            {
                throw InstallerFailureException.FromProcess(stop);
            }

            await WaitForStateAsync(
                InstalledServiceState.Stopped,
                "Wait for Agent service to stop",
                cancellationToken);
        }

        var delete = await _backend.DeleteAsync(cancellationToken);
        if (!delete.Success && !ScServiceControlBackend.IsServiceMissing(delete))
        {
            throw InstallerFailureException.FromProcess(delete);
        }

        await WaitForStateAsync(
            InstalledServiceState.Missing,
            "Wait for Agent service removal",
            cancellationToken);
    }

    private async Task RollBackServiceAsync(
        InstalledServiceSnapshot initial,
        bool createdDuringAttempt,
        bool restartOriginal,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteLogAsync(
                "Rollback",
                "Rolling back Agent service changes.",
                cancellationToken);
            if (createdDuringAttempt && !initial.Exists)
            {
                var snapshot = await _backend.QueryAsync(cancellationToken);
                if (snapshot.Exists &&
                    snapshot.State != InstalledServiceState.Stopped)
                {
                    await _backend.StopAsync(cancellationToken);
                }

                await _backend.DeleteAsync(cancellationToken);
                await WaitForStateAsync(
                    InstalledServiceState.Missing,
                    "Wait for rollback service removal",
                    cancellationToken);
                return;
            }

            if (initial.Exists && !string.IsNullOrWhiteSpace(initial.BinaryPath))
            {
                await _backend.ConfigureAsync(
                    initial.BinaryPath,
                    initial.StartType,
                    cancellationToken);
                if (restartOriginal &&
                    initial.State == InstalledServiceState.Running)
                {
                    await _backend.StartAsync(cancellationToken);
                }
            }
        }
        catch (Exception exception)
        {
            await WriteLogAsync(
                "Rollback",
                $"Agent service rollback warning: {exception}",
                CancellationToken.None);
        }
    }

    private async Task WaitForStateAsync(
        InstalledServiceState expected,
        string step,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _maximumPollAttempts; attempt++)
        {
            var snapshot = await _backend.QueryAsync(cancellationToken);
            if (snapshot.State == expected)
            {
                return;
            }

            if (expected == InstalledServiceState.Stopped &&
                snapshot.State == InstalledServiceState.Missing)
            {
                return;
            }

            await _delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new InstallerFailureException(
            step,
            $"{step} timed out.",
            $"Service did not reach {expected} after " +
            $"{_maximumPollAttempts * 500} milliseconds.");
    }

    private Task WriteLogAsync(
        string step,
        string message,
        CancellationToken cancellationToken) =>
        _log?.WriteAsync(step, message, cancellationToken) ?? Task.CompletedTask;
}
