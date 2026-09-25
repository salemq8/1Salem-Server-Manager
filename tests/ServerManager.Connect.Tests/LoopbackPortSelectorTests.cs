using System.Net;
using System.Net.Sockets;
using ServerManager.Connect.Core.Networking;

namespace ServerManager.Connect.Tests;

public sealed class LoopbackPortSelectorTests
{
    [Fact]
    public void BindsLoopbackOnlyWithinTheRange()
    {
        var listener = LoopbackPortSelector.Bind();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;

            Assert.Equal(IPAddress.Loopback, endpoint.Address);
            Assert.NotEqual(IPAddress.Any, endpoint.Address);
            Assert.InRange(endpoint.Port, LoopbackPortSelector.FirstPort, LoopbackPortSelector.LastPort);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void OccupiedPreferredPort_FallsBackToAnotherLoopbackPort()
    {
        // Another program's ordinary (non-exclusive) listener on the preferred port.
        using var occupant = OccupyAPortInRange();
        var occupiedPort = ((IPEndPoint)occupant.LocalEndPoint!).Port;

        var listener = LoopbackPortSelector.Bind(occupiedPort);
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;

            Assert.NotEqual(occupiedPort, endpoint.Port);
            Assert.Equal(IPAddress.Loopback, endpoint.Address);
            Assert.InRange(endpoint.Port, LoopbackPortSelector.FirstPort, LoopbackPortSelector.LastPort);
            Assert.True(occupant.IsBound, "The other program's socket must be left alone.");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void TwoSessions_GetDifferentPorts()
    {
        var first = LoopbackPortSelector.Bind();
        try
        {
            var second = LoopbackPortSelector.Bind(((IPEndPoint)first.LocalEndpoint).Port);
            try
            {
                Assert.NotEqual(((IPEndPoint)first.LocalEndpoint).Port, ((IPEndPoint)second.LocalEndpoint).Port);
            }
            finally
            {
                second.Stop();
            }
        }
        finally
        {
            first.Stop();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25565)]
    [InlineData(18210)]
    [InlineData(18300)]
    public void PreferredPortOutsideTheRange_IsRefused(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LoopbackPortSelector.Bind(port));

    private static Socket OccupyAPortInRange()
    {
        for (var port = LoopbackPortSelector.FirstPort + 20; port <= LoopbackPortSelector.LastPort; port++)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                socket.Listen();
                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
        }

        throw new InvalidOperationException("No port in the Connect range was free for the test.");
    }
}
