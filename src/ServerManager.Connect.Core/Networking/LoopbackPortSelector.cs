using System.Net;
using System.Net.Sockets;

namespace ServerManager.Connect.Core.Networking;

/// <summary>
/// Binds the friend's local game endpoint (contract §11). The listener is on 127.0.0.1 only,
/// preferring 18211 and otherwise taking the next free port in 18211-18299, wrapping around
/// once. A port that is in use is skipped, never taken over: the listener is exclusive, so it
/// cannot share a port with another program's socket. Nothing is ever killed and 0.0.0.0 is
/// never bound.
/// It returns the bound listener rather than a port number, so there is no window in which
/// another process can take the port between choosing and binding.
/// </summary>
public static class LoopbackPortSelector
{
    public const int FirstPort = 18211;
    public const int LastPort = 18299;
    public const int PreferredPort = FirstPort;

    public static TcpListener Bind(int preferredPort = PreferredPort)
    {
        if (preferredPort is < FirstPort or > LastPort)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preferredPort),
                $"The local port must be within {FirstPort}-{LastPort}.");
        }

        var count = LastPort - FirstPort + 1;
        for (var offset = 0; offset < count; offset++)
        {
            var port = FirstPort + (preferredPort - FirstPort + offset) % count;
            var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException exception) when (exception.SocketErrorCode is
                SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                // AccessDenied is what Windows reports for a port another socket holds
                // exclusively, or one inside an excluded port range.
                listener.Stop();
            }
        }

        throw new InvalidOperationException(
            $"No free loopback port in {FirstPort}-{LastPort}. Close the program using those ports and try again.");
    }
}
