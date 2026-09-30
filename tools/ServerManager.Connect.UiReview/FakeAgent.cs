using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Connect.UiReview;

/// <summary>
/// A loopback-only stand-in for the Agent's Connect owner API with canned answers. The owner
/// windows under review talk to it (through ONE_SALEM_AGENT_URL) instead of any real Agent.
/// </summary>
internal sealed class FakeAgent : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public FakeAgent()
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "1salem-uireview-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(DataRoot, "security"));
        File.WriteAllText(Path.Combine(DataRoot, "security", "local-agent.key"),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        _listener.Start();
        _ = AcceptAsync();
    }

    public string Url => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string DataRoot { get; }

    public ConnectStatusResponse? Status { get; set; }

    public ServerConnectResponse? Server { get; set; }

    public ConnectInviteCreated? Invite { get; set; }

    public ConnectRevokeResult? Revoke { get; set; }

    public List<string> Requests { get; } = [];

    /// <summary>Optional routes tried first (the Content scenario); may delay to simulate a slow site.</summary>
    public Func<string, string, Task<(int Status, object? Payload)?>>? Extra { get; set; }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { Directory.Delete(DataRoot, recursive: true); } catch (IOException) { }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new StringBuilder();
            var buffer = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) &&
                   await stream.ReadAsync(buffer) == 1)
                header.Append((char)buffer[0]);
            var lines = header.ToString().Split("\r\n");
            var request = lines[0].Split(' ');
            string? Header(string name) => lines.Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2 && parts[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(parts => parts[1].Trim()).FirstOrDefault();
            if (Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
            {
                // JsonContent has no length, so HttpClient sends it chunked; consume it all.
                var tail = new StringBuilder();
                while (!tail.ToString().EndsWith("\r\n0\r\n\r\n", StringComparison.Ordinal) &&
                       !tail.ToString().Equals("0\r\n\r\n", StringComparison.Ordinal) &&
                       await stream.ReadAsync(buffer) == 1)
                    tail.Append((char)buffer[0]);
            }
            else
            {
                var length = int.Parse(Header("Content-Length") ?? "0");
                var body = new byte[length];
                for (var read = 0; read < length;) read += await stream.ReadAsync(body.AsMemory(read));
            }
            var (status, payload) = (Extra is null ? null : await Extra(request[0], request[1])) ?? Route(request[0], request[1]);
            lock (Requests) Requests.Add(request[0] + " " + request[1] + " -> " + status);
            var json = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            var response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} X\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response);
            await stream.WriteAsync(json);
        }
    }

    private (int Status, object? Payload) Route(string method, string path)
    {
        object? found = (method, path) switch
        {
            ("GET", "/api/v1/connect/status") or ("PUT", "/api/v1/connect/credential") or ("POST", "/api/v1/connect/check") => Status,
            ("DELETE", "/api/v1/connect/credential") => OperationResult.Ok(),
            ("GET", _) when path.StartsWith("/api/v1/servers/", StringComparison.Ordinal) && path.EndsWith("/connect", StringComparison.Ordinal) => Server,
            ("POST", _) when path.EndsWith("/connect/invites", StringComparison.Ordinal) => Invite,
            ("POST", _) when path.StartsWith("/api/v1/connect/memberships/", StringComparison.Ordinal) && path.EndsWith("/revoke", StringComparison.Ordinal) => Revoke,
            ("POST" or "PUT", _) when path.StartsWith("/api/v1/connect/", StringComparison.Ordinal) ||
                                      path.EndsWith("/connect/enable", StringComparison.Ordinal) ||
                                      path.EndsWith("/connect/disable", StringComparison.Ordinal) => OperationResult.Ok(),
            _ => null
        };
        return found is null ? (404, OperationResult.Fail("NotFound", "Not part of this review.")) : (200, found);
    }
}
