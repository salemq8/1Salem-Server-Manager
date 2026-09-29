using System.Net;
using System.Net.Sockets;
using System.Text;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.ViewModels;

namespace Phase2Acceptance;

/// <summary>Disposable loopback identity/echo target, never a production Minecraft process.</summary>
internal sealed class IdentityEndpoint : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    public IdentityEndpoint(string banner) { Banner = banner; _listener.Start(); _accept = AcceptAsync(); }
    public string Banner { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    private async Task AcceptAsync()
    {
        try { while (!_stop.IsCancellationRequested) _ = ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)); }
        catch (Exception exception) when (_stop.IsCancellationRequested && exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes(Banner + "\n"), _stop.Token);
                var buffer = new byte[4096];
                int count;
                while ((count = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                    await stream.WriteAsync(buffer.AsMemory(0, count), _stop.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException) { }
        }
    }
    public void Dispose() { _stop.Cancel(); _listener.Stop(); _accept.GetAwaiter().GetResult(); _stop.Dispose(); }
}

internal sealed class NoClipboard : IClipboardService { public bool TrySetText(string text) => false; }
internal sealed class TestNavigator : INavigator
{
    public InviteRedemption? Redemption { get; private set; }
    public void ShowWaiting(InviteRedemption redemption) => Redemption = redemption;
    public void ShowWelcome() { } public void ShowInvite() { } public void ShowServers() { }
    public void ShowConnection(Membership membership) { } public void ShowDiagnostics() { }
    public ConnectionViewModel? FindConnection(string membershipId) => null;
}
