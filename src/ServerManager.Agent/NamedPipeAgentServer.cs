using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ServerManager.Contracts;
using ServerManager.Infrastructure.Transport;

namespace ServerManager.Agent;

public sealed class NamedPipeAgentServer(
    AgentOptions options,
    NamedPipeRequestDispatcher dispatcher,
    ILogger<NamedPipeAgentServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Named-pipe transport listening on {PipeName}.", options.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreateLocalPipe(
                options.PipeName,
                NamedPipeServerStream.MaxAllowedServerInstances);

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                var request = await PipeMessageSerializer.ReadAsync<PipeRequest>(pipe, stoppingToken);
                var response = dispatcher.Dispatch(request);
                await PipeMessageSerializer.WriteAsync(pipe, response, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "A named-pipe request failed validation or transport.");
            }
        }
    }

    public static NamedPipeServerStream CreateLocalPipe(
        string pipeName,
        int maximumInstances = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var security = new PipeSecurity();
        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(
                    WellKnownSidType.BuiltinUsersSid,
                    null),
                PipeAccessRights.ReadWrite |
                PipeAccessRights.Synchronize |
                PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(
                new PipeAccessRule(
                    currentUser,
                    PipeAccessRights.ReadWrite |
                    PipeAccessRights.Synchronize |
                    PipeAccessRights.CreateNewInstance,
                    AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maximumInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }
}
