using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Pipes;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// Plays the broker for the Connect tests: throwaway in-memory keys, a pinned key set and
/// tickets built from editable claims, so each test can break exactly one thing. Nothing here
/// is a real secret, and nothing leaves the process.
/// </summary>
internal sealed class ConnectTestBroker : IDisposable
{
    public const string KeyId = "test-key-1";
    public const string MembershipId = "mem_test0001";
    public const string NodeId = "nTestFriend1CNTRL";
    public const long StartUnix = 1_767_225_600;

    public ConnectTestBroker()
    {
        BrokerKey = Es256.CreateKey();
        SessionKey = Es256.CreateKey();
        OwnerId = NewOwnerId();
        DeviceId = NewDeviceId();
        KeySet = new TicketKeySet([new TicketSigningKey(KeyId, Es256.Algorithm, Es256.ExportPublicKey(BrokerKey))]);
        Clock = new ConnectManualClock(StartUnix);
    }

    public ECDsa BrokerKey { get; }

    /// <summary>The friend transport's in-memory session key; every ticket here names it.</summary>
    public ECDsa SessionKey { get; }

    public string OwnerId { get; }

    public string DeviceId { get; }

    public TicketKeySet KeySet { get; }

    public ConnectManualClock Clock { get; }

    public static string NewOwnerId()
    {
        using var key = Es256.CreateKey();
        return ConnectKeyIds.ForOwner(Es256.ExportPublicKey(key));
    }

    public static string NewDeviceId()
    {
        using var key = Es256.CreateKey();
        return ConnectKeyIds.ForDevice(Es256.ExportPublicKey(key));
    }

    /// <summary>A valid ticket for <paramref name="serverId"/>, issued 10 s ago for 600 s.</summary>
    public string Issue(Guid serverId, Action<JsonObject>? editClaims = null)
    {
        var issuedAt = Clock.UnixSeconds - 10;
        var claims = new JsonObject
        {
            ["iss"] = "1salem-connect-broker",
            ["aud"] = OwnerId,
            ["jti"] = Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
            ["sub"] = DeviceId,
            ["mid"] = MembershipId,
            ["sid"] = serverId.ToString("D"),
            ["proto"] = "tcp",
            ["nid"] = NodeId,
            ["skp"] = Base64Url.Encode(Es256.ExportPublicKey(SessionKey)),
            ["hb"] = "127.0.0.1:7780",
            ["av"] = 1,
            ["iat"] = issuedAt,
            ["nbf"] = issuedAt,
            ["exp"] = issuedAt + 600
        };
        editClaims?.Invoke(claims);
        var header = new JsonObject { ["alg"] = "ES256", ["typ"] = "1salem-ticket+jwt", ["kid"] = KeyId };
        var signingInput = Base64Url.Encode(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." +
            Base64Url.Encode(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        return signingInput + "." + Base64Url.Encode(Es256.Sign(BrokerKey, Encoding.ASCII.GetBytes(signingInput)));
    }

    /// <summary>The <c>{t,n,ts,p}</c> preamble the friend transport would send, with a fresh nonce.</summary>
    public JsonObject Preamble(string ticket)
    {
        var preamble = ConnectionPreamble.Create(ticket, SessionKey, Clock);
        return new JsonObject
        {
            ["t"] = preamble.Ticket,
            ["n"] = preamble.Nonce,
            ["ts"] = preamble.Timestamp,
            ["p"] = preamble.Proof
        };
    }

    /// <summary>The body of an <c>authorize</c> request, as the host transport sends it.</summary>
    public static JsonObject AuthorizeBody(JsonObject preamble, string peerNodeId = NodeId) => new()
    {
        ["preamble"] = preamble,
        ["peer"] = new JsonObject { ["nodeId"] = peerNodeId, ["addr"] = "127.0.0.1:50123" }
    };

    public static string TicketIdOf(string ticket)
    {
        var payload = Base64Url.Decode(ticket.Split('.')[1]);
        return JsonNode.Parse(payload)!["jti"]!.GetValue<string>();
    }

    public void Dispose()
    {
        BrokerKey.Dispose();
        SessionKey.Dispose();
    }
}

/// <summary>
/// A clock the test moves by hand, for dates and for elapsed time alike. Timers still run in
/// real time (the base class's).
/// </summary>
internal sealed class ConnectManualClock(long unixSeconds) : TimeProvider
{
    private long _nowTicks = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcTicks;

    public long UnixSeconds => GetUtcNow().ToUnixTimeSeconds();

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _nowTicks), TimeSpan.Zero);

    public override long GetTimestamp() => Interlocked.Read(ref _nowTicks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _nowTicks, by.Ticks);
}

/// <summary>The Agent's server registrations, in memory; only listing is needed by Connect.</summary>
internal sealed class ConnectTestServerStore : IGameServerStore
{
    public static readonly Guid Minecraft = Guid.Parse("6f1c1d7e-3b1e-4a55-9d0e-2f9a1b7c4d11");
    public static readonly Guid SecondMinecraft = Guid.Parse("9a7b6c5d-4e3f-4a1b-8c9d-0e1f2a3b4c5d");
    public static readonly Guid Palworld = Guid.Parse("0b8e5a3c-6d2f-4c7a-8e19-5a4d3c2b1a00");
    public const int MinecraftPort = 25565;
    public const int SecondMinecraftPort = 25566;
    public const int PalworldPort = 8211;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, GameServerDefinition> _servers = [];

    public ConnectTestServerStore()
    {
        Put(Minecraft, GameType.Minecraft, MinecraftPort);
        Put(SecondMinecraft, GameType.Minecraft, SecondMinecraftPort);
        Put(Palworld, GameType.Palworld, PalworldPort);
    }

    /// <summary>When set, listing fails the way a locked or missing database would.</summary>
    public bool Fail { get; set; }

    /// <summary>
    /// Runs inside every listing after the servers were read and before they are returned: a
    /// slow database, as seen by the caller.
    /// </summary>
    public Func<Task>? Listing { get; set; }

    public void Put(Guid id, GameType game, int port)
    {
        lock (_gate)
        {
            _servers[id] = new GameServerDefinition(id, game, $"server {port}", @"C:\nowhere", port, null, DateTimeOffset.UnixEpoch);
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            _servers.Remove(id);
        }
    }

    public async Task<IReadOnlyList<GameServerDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new IOException("The server store is unavailable.");
        }

        GameServerDefinition[] servers;
        lock (_gate)
        {
            servers = [.. _servers.Values];
        }

        if (Listing is { } listing)
        {
            await listing();
        }

        return servers;
    }

    public Task<GameServerDefinition?> GetAsync(Guid serverId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task UpsertAsync(GameServerDefinition server, ServerState state, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task SetStateAsync(Guid serverId, ServerState state, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>
/// A client of the host authorization pipe that behaves like the Go host transport: it checks
/// the pipe owner before sending anything, connects at Anonymous impersonation level, and
/// speaks one JSON object per line.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ConnectPipeTestClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly JsonLineReader _reader;
    private readonly CancellationToken _cancellation;
    private long _nextId;

    private ConnectPipeTestClient(NamedPipeClientStream pipe, CancellationToken cancellation)
    {
        _pipe = pipe;
        _reader = new JsonLineReader(pipe);
        _cancellation = cancellation;
    }

    public static async Task<ConnectPipeTestClient> ConnectAsync(string pipeName, CancellationToken cancellation, bool hello = true)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await pipe.ConnectAsync(cancellation);
        ConnectPipeSecurity.VerifyServerOwner(pipe, ConnectPipeSecurity.CurrentUser);
        var client = new ConnectPipeTestClient(pipe, cancellation);
        if (hello)
        {
            var response = await client.CallAsync("hello", new JsonObject { ["v"] = 1 });
            Assert.True(response.GetProperty("ok").GetBoolean());
            Assert.Equal(1, response.GetProperty("v").GetInt32());
        }

        return client;
    }

    /// <summary>Sends one request and returns its response, checking the echoed id.</summary>
    public async Task<JsonElement> CallAsync(string operation, JsonObject? body = null)
    {
        var id = await SendAsync(operation, body);
        var response = await ReadAsync();
        Assert.Equal(id, response.GetProperty("id").GetInt64());
        return response;
    }

    /// <summary>Sends one request without waiting, as the Go client does for concurrent calls.</summary>
    public async Task<long> SendAsync(string operation, JsonObject? body = null)
    {
        var id = ++_nextId;
        var request = new JsonObject { ["id"] = id, ["op"] = operation };
        if (body is not null)
        {
            foreach (var (name, value) in body)
            {
                request[name] = value?.DeepClone();
            }
        }

        await JsonLines.WriteAsync(_pipe, Encoding.UTF8.GetBytes(request.ToJsonString()), _cancellation);
        return id;
    }

    public Task<JsonElement> AuthorizeAsync(JsonObject authorizeBody) => CallAsync("authorize", authorizeBody);

    /// <summary>The next line from the Agent: a response, or an event on a subscribed connection.</summary>
    public async Task<JsonElement> ReadAsync()
    {
        var line = await _reader.ReadAsync(_cancellation);
        Assert.True(line.HasValue, "The Agent closed the pipe.");
        return line.Value;
    }

    /// <summary>Reads the next line within <paramref name="window"/>, or null when none came.</summary>
    public async Task<JsonElement?> TryReadAsync(TimeSpan window)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
        timeout.CancelAfter(window);
        try
        {
            return await _reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!_cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}

internal sealed record ConnectCapturedLog(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values);

/// <summary>Keeps every log entry, formatted and structured, so tests can check what was logged.</summary>
internal sealed class ConnectCapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<ConnectCapturedLog> _entries = new();

    public IReadOnlyList<ConnectCapturedLog> Entries => [.. _entries];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        _entries.Enqueue(new ConnectCapturedLog(logLevel, formatter(state, exception), [.. values]));
    }
}
