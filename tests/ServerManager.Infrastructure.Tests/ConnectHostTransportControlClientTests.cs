using System.Runtime.Versioning;
using System.Text.Json;
using ServerManager.Connect.Core.Pipes;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConnectHostTransportControlClientTests
{
    [Fact]
    public async Task Hello_VerifiesTheServingProcess_AndParsesTheResponse()
    {
        var pipeName = Name();
        await using var server = ConnectPipeSecurity.CreateFirstInstance(pipeName);
        var serving = ServeOnceAsync(server, request =>
            $"{{\"id\":{request.GetProperty("id").GetInt64()},\"ok\":true,\"v\":1,\"mode\":\"tsnet\",\"version\":\"test\"}}");
        var client = Create(pipeName, Environment.ProcessId);

        var hello = await client.HelloAsync(CancellationToken.None);

        Assert.Equal(new ConnectHostTransportHello(1, "tsnet", "test"), hello);
        var request = await serving;
        Assert.Equal("hello", request.GetProperty("op").GetString());
    }

    [Fact]
    public async Task Enroll_UsesTheLongLane_AndReturnsTheNodeId()
    {
        var pipeName = Name();
        await using var server = ConnectPipeSecurity.CreateFirstInstance(pipeName);
        var serving = ServeOnceAsync(server, request =>
            $"{{\"id\":{request.GetProperty("id").GetInt64()},\"ok\":true,\"nodeId\":\"nHost1CNTRL\"}}");
        var client = Create(pipeName, Environment.ProcessId);

        var nodeId = await client.EnrollAsync("tskey-auth-kTest-notreal", "owner-pc", CancellationToken.None);

        Assert.Equal("nHost1CNTRL", nodeId);
        var request = await serving;
        Assert.Equal("enroll", request.GetProperty("op").GetString());
        Assert.Equal("owner-pc", request.GetProperty("hostname").GetString());
    }

    [Fact]
    public async Task AServerOtherThanTheSupervisedPid_IsRefusedBeforeAnyRequest()
    {
        var pipeName = Name();
        await using var server = ConnectPipeSecurity.CreateFirstInstance(pipeName);
        // Listen before the client connects: it disconnects at once, and a late listen fails.
        var connecting = server.WaitForConnectionAsync();
        var serving = Task.Run(async () =>
        {
            await connecting;
            return await new JsonLineReader(server).ReadAsync(CancellationToken.None);
        });
        var client = Create(pipeName, Environment.ProcessId + 1);

        var error = await Assert.ThrowsAsync<ConnectHostTransportException>(() => client.HelloAsync(CancellationToken.None));

        Assert.Equal("untrusted", error.Code);
        Assert.Null(await serving);
    }

    private static ConnectHostTransportControlClient Create(string pipeName, int processId) =>
        new(pipeName, () => processId, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    private static async Task<JsonElement> ServeOnceAsync(
        System.IO.Pipes.NamedPipeServerStream server,
        Func<JsonElement, string> response)
    {
        await server.WaitForConnectionAsync();
        var request = await new JsonLineReader(server).ReadAsync(CancellationToken.None) ?? throw new InvalidOperationException();
        await JsonLines.WriteAsync(server, System.Text.Encoding.UTF8.GetBytes(response(request)), CancellationToken.None);
        return request;
    }

    private static string Name() => "1Salem.Connect.HostControl.Test." + Guid.NewGuid().ToString("N");
}
