using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ServerManager.Agent;

namespace ServerManager.Agent.IntegrationTests;

public sealed class NamedPipeSecurityTests
{
    [Fact]
    public void ServicePipe_AllowsLocalUsersAndDoesNotAllowNetworkIdentity()
    {
        var pipeName = $"1Salem.ServerManager.SecurityTest.{Guid.NewGuid():N}";
        using var pipe = NamedPipeAgentServer.CreateLocalPipe(pipeName);
        var security = pipe.GetAccessControl();
        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));

        Assert.Contains(
            rules.Cast<PipeAccessRule>(),
            rule =>
                rule.IdentityReference.Equals(
                    new SecurityIdentifier(
                        WellKnownSidType.BuiltinUsersSid,
                        null)) &&
                rule.AccessControlType == AccessControlType.Allow &&
                rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite) &&
                rule.PipeAccessRights.HasFlag(
                    PipeAccessRights.CreateNewInstance));
        Assert.DoesNotContain(
            rules.Cast<PipeAccessRule>(),
            rule =>
                rule.IdentityReference.Equals(
                    new SecurityIdentifier(WellKnownSidType.NetworkSid, null)) &&
                rule.AccessControlType == AccessControlType.Allow &&
                rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
    }

    [Fact]
    public async Task ServicePipe_AcceptsCurrentNonAdministratorClient()
    {
        var pipeName = $"1Salem.ServerManager.ConnectionTest.{Guid.NewGuid():N}";
        await using var server = NamedPipeAgentServer.CreateLocalPipe(pipeName);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        var waiting = server.WaitForConnectionAsync();
        await client.ConnectAsync(3000);
        await waiting;

        Assert.True(server.IsConnected);
        Assert.True(client.IsConnected);
    }
}
