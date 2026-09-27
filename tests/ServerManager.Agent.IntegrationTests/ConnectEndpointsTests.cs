using Microsoft.AspNetCore.Http;

namespace ServerManager.Agent.IntegrationTests;

public sealed class ConnectEndpointsTests
{
    [Fact]
    public void LocalClientMarker_IsRequired()
    {
        var anonymous = new DefaultHttpContext();
        var paired = new DefaultHttpContext();
        paired.Items["PairedClient"] = new object();
        var local = new DefaultHttpContext();
        local.Items["LocalClient"] = true;

        Assert.False(ConnectEndpoints.IsLocalClient(anonymous));
        Assert.False(ConnectEndpoints.IsLocalClient(paired));
        Assert.True(ConnectEndpoints.IsLocalClient(local));
    }
}
