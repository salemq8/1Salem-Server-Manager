using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.Core.Broker;
using ServerManager.Connect.Core.Diagnostics;
using ServerManager.Connect.Core.Pipes;
using ServerManager.Contracts;
using ServerManager.Core;

// Small helpers copied from connect/proof/ConnectProof rather than referenced, so the two
// development drivers stay independent of each other.
namespace TsnetSmoke;

/// <summary>Signed calls to the broker (contract §5) and the parsed answers.</summary>
internal sealed class BrokerCaller(HttpClient http, SignedRequestSigner signer)
{
    public async Task<BrokerAnswer> SendAsync(HttpMethod method, string path, object? body)
    {
        // The signer signs the exact path of an absolute URI, so the request is built absolute.
        using var request = new HttpRequestMessage(method, new Uri(http.BaseAddress!, path));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        await signer.SignAsync(request, CancellationToken.None);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new BrokerAnswer((int)response.StatusCode, text);
    }
}

internal sealed record BrokerAnswer(int Status, string? Raw)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    private JsonElement? Root
    {
        get
        {
            try
            {
                return string.IsNullOrWhiteSpace(Raw) ? null : JsonDocument.Parse(Raw).RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public string? Text(string name) =>
        Root is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public IEnumerable<JsonElement> Items(string name) =>
        Root is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : [];
}

/// <summary>
/// A raw JSON-lines client for a transport pipe (the friend pipe, or the host transport's control
/// pipe), checking the pipe's owner first. The driver plays an attacker or the Agent with it.
/// </summary>
internal sealed class RawPipeClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(30);

    private readonly NamedPipeClientStream _pipe;
    private readonly JsonLineReader _reader;
    private int _nextId;

    private RawPipeClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new JsonLineReader(pipe);
    }

    public static async Task<RawPipeClient> ConnectAsync(string pipeName)
    {
        for (var attempt = 0; ; attempt++)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(2000);
                ConnectPipeSecurity.VerifyServerOwner(pipe, ConnectPipeSecurity.CurrentUser);
                return new RawPipeClient(pipe);
            }
            catch (TimeoutException) when (attempt < 15)
            {
                await pipe.DisposeAsync();
            }
        }
    }

    public async Task<PipeAnswer> CallAsync(JsonObject request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        request["id"] = ++_nextId;
        var line = Encoding.UTF8.GetBytes(request.ToJsonString());
        try
        {
            await JsonLines.WriteAsync(_pipe, line, CancellationToken.None);
        }
        finally
        {
            // An enroll line carries an auth key, an open line a session key.
            CryptographicOperations.ZeroMemory(line);
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancel.CancelAfter(timeout ?? DefaultCallTimeout);
        var reply = await _reader.ReadAsync(cancel.Token) ?? throw new IOException("the transport closed the pipe");
        var body = JsonNode.Parse(reply.GetRawText())?.AsObject();
        var ok = body?["ok"]?.GetValue<bool>() == true;
        return new PipeAnswer(ok, ok ? null : body?["error"]?.GetValue<string>(), body);
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}

internal sealed record PipeAnswer(bool Ok, string? Error, JsonObject? Body)
{
    public string? Text(string name) => Body?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>A throwaway "game server": it says its name, then echoes lines back.</summary>
internal sealed class FakeGameServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    private FakeGameServer(string name)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptAsync(name);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static FakeGameServer Start(string name) => new(name);

    private async Task AcceptAsync(string name)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(name + "\n"), _stop.Token);
                        var buffer = new byte[4096];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                        {
                            await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                    }
                }
            });
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The Agent's server registrations, in memory, for the smoke run only.</summary>
internal sealed class InMemoryServerStore(params GameServerDefinition[] servers) : IGameServerStore
{
    public Task<IReadOnlyList<GameServerDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GameServerDefinition>>(servers);

    public Task<GameServerDefinition?> GetAsync(Guid serverId, CancellationToken cancellationToken = default) =>
        Task.FromResult(servers.FirstOrDefault(server => server.Id == serverId));

    public Task UpsertAsync(GameServerDefinition server, ServerState state, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The smoke run's server list is fixed.");

    public Task SetStateAsync(Guid serverId, ServerState state, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Redacted log lines on the console, and optionally kept, so the run can show why the host
/// refused something.
/// </summary>
internal sealed class ConsoleLogger<T>(string label, ConcurrentQueue<string>? kept = null) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = SecretRedactor.Redact(formatter(state, exception));
        Console.WriteLine($"  [{label} {logLevel}] {message}");
        kept?.Enqueue(message);
    }
}

/// <summary>
/// The app's clock for the run. Time is real, but a page's wait between polls lasts until the
/// driver calls <see cref="Release"/>, so a monitor step happens exactly when the run has set up
/// what it should see, not after the app's 5-second intervals.
/// </summary>
internal sealed class SmokeClock : IAppClock
{
    private readonly object _gate = new();
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waits;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <summary>How many waits the pages have started so far.</summary>
    public int Waits
    {
        get
        {
            lock (_gate)
            {
                return _waits;
            }
        }
    }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        Task released;
        lock (_gate)
        {
            _waits++;
            released = _release.Task;
        }

        return released.WaitAsync(cancellationToken);
    }

    public void Release()
    {
        TaskCompletionSource released;
        lock (_gate)
        {
            released = _release;
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        released.SetResult();
    }
}

/// <summary>Stands in for the Windows clipboard, which needs an STA thread the console driver does not have.</summary>
internal sealed class SmokeClipboard : IClipboardService
{
    public bool TrySetText(string text) => true;
}
