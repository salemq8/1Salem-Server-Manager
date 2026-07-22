using ServerManager.Core;
using ServerManager.Contracts;

namespace ServerManager.Infrastructure.Windows;

public sealed class WindowsServiceManager
{
    public const string ServiceName = "1SalemServerManagerAgent";
    public const string DisplayName = "1Salem Server Manager Agent";

    public async Task<OperationResult> InstallAsync(
        string agentExecutablePath,
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        ValidateAbsoluteFile(agentExecutablePath);
        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException("The Agent data root must be absolute.", nameof(dataRoot));
        }

        var binaryPath = WindowsServiceCommandBuilder.BuildBinaryPath(
            agentExecutablePath,
            dataRoot);
        var create = await WindowsCommandRunner.RunAsync(
            "sc.exe",
            WindowsServiceCommandBuilder.CreateArguments(
                ServiceName,
                DisplayName,
                binaryPath),
            cancellationToken);
        if (!create.Success &&
            !create.Message!.Contains("already exists", StringComparison.OrdinalIgnoreCase) &&
            !create.Message.Contains("1073", StringComparison.Ordinal))
        {
            return create;
        }

        var configure = await WindowsCommandRunner.RunAsync(
            "sc.exe",
            WindowsServiceCommandBuilder.ConfigureArguments(
                ServiceName,
                binaryPath),
            cancellationToken);
        if (!configure.Success)
        {
            return configure;
        }

        await WindowsCommandRunner.RunAsync(
            "sc.exe",
            ["description", ServiceName, "Manages local Minecraft and Palworld servers."],
            cancellationToken);
        return await StartAsync(cancellationToken);
    }

    public Task<OperationResult> StartAsync(CancellationToken cancellationToken = default) =>
        WindowsCommandRunner.RunAsync(
            "sc.exe",
            ["start", ServiceName],
            cancellationToken);

    public Task<OperationResult> StopAsync(CancellationToken cancellationToken = default) =>
        WindowsCommandRunner.RunAsync(
            "sc.exe",
            ["stop", ServiceName],
            cancellationToken);

    public async Task<OperationResult> UninstallAsync(
        CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        return await WindowsCommandRunner.RunAsync(
            "sc.exe",
            ["delete", ServiceName],
            cancellationToken);
    }

    public Task<OperationResult> QueryAsync(CancellationToken cancellationToken = default) =>
        WindowsCommandRunner.RunAsync(
            "sc.exe",
            ["query", ServiceName],
            cancellationToken);

    private static void ValidateAbsoluteFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new FileNotFoundException(
                "Select an existing absolute Agent executable path.",
                path);
        }
    }
}
