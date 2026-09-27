using System.ComponentModel;
using System.Buffers;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerManager.Connect.Core.Pipes;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// Agent client for the host transport control pipe. The pipe owner and the exact process the
/// supervisor still holds are verified before a request containing an auth key is written.
/// </summary>
[SupportedOSPlatform("windows")]
public interface IConnectHostTransportControlClient
{
    Task<ConnectHostTransportHello> HelloAsync(CancellationToken cancellationToken);
    Task<ConnectHostTransportStatus> StatusAsync(CancellationToken cancellationToken);
    Task<string> EnrollAsync(string authKey, string hostname, CancellationToken cancellationToken);
    Task<JsonElement> DiagnosticsAsync(CancellationToken cancellationToken);
}

[SupportedOSPlatform("windows")]
public sealed partial class ConnectHostTransportControlClient : IConnectHostTransportControlClient
{
    private readonly string _pipeName;
    private readonly Func<int?> _runningProcessId;
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _enrollTimeout;
    private long _nextId;

    public ConnectHostTransportControlClient(ConnectHostTransportOptions options, IConnectHostTransportSupervisor supervisor)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).ControlPipeName,
            ProcessIdGetter(supervisor),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMinutes(2))
    {
    }

    private static Func<int?> ProcessIdGetter(IConnectHostTransportSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        return () => supervisor.RunningProcessId;
    }

    internal ConnectHostTransportControlClient(string pipeName, Func<int?> runningProcessId, TimeSpan callTimeout, TimeSpan enrollTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _runningProcessId = runningProcessId ?? throw new ArgumentNullException(nameof(runningProcessId));
        _callTimeout = callTimeout;
        _enrollTimeout = enrollTimeout;
    }

    public async Task<ConnectHostTransportHello> HelloAsync(CancellationToken cancellationToken)
    {
        var response = await CallAsync((id, writer) =>
        {
            writer.WriteNumber("id", id);
            writer.WriteString("op", "hello");
            writer.WriteNumber("v", 1);
        }, _callTimeout, cancellationToken).ConfigureAwait(false);
        return new ConnectHostTransportHello(
            RequiredInt(response, "v"),
            RequiredString(response, "mode"),
            RequiredString(response, "version"));
    }

    public async Task<ConnectHostTransportStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var response = await SimpleAsync("status", _callTimeout, cancellationToken).ConfigureAwait(false);
        if (!response.TryGetProperty("nodes", out var nodesValue) || nodesValue.ValueKind != JsonValueKind.Array ||
            !response.TryGetProperty("bridge", out var bridge) || bridge.ValueKind != JsonValueKind.Object)
        {
            throw new ConnectHostTransportException("invalid_response");
        }

        var nodes = nodesValue.EnumerateArray().Select(node => new ConnectHostTransportNode(
            RequiredString(node, "node"),
            OptionalString(node, "nodeId"),
            RequiredString(node, "state"))).ToArray();
        return new ConnectHostTransportStatus(
            nodes,
            new ConnectHostBridgeStatus(
                RequiredString(bridge, "state"),
                OptionalString(bridge, "listen"),
                OptionalBoolean(bridge, "revocationChannel"),
                OptionalInt64(bridge, "live") ?? 0));
    }

    public async Task<string> EnrollAsync(string authKey, string hostname, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        var response = await CallAsync((id, writer) =>
        {
            writer.WriteNumber("id", id);
            writer.WriteString("op", "enroll");
            writer.WriteString("authKey", authKey);
            writer.WriteString("hostname", hostname);
        }, _enrollTimeout, cancellationToken).ConfigureAwait(false);
        return RequiredString(response, "nodeId");
    }

    public Task<JsonElement> DiagnosticsAsync(CancellationToken cancellationToken) =>
        SimpleAsync("diag", _callTimeout, cancellationToken);

    private Task<JsonElement> SimpleAsync(string operation, TimeSpan timeout, CancellationToken cancellationToken) =>
        CallAsync((id, writer) =>
        {
            writer.WriteNumber("id", id);
            writer.WriteString("op", operation);
        }, timeout, cancellationToken);

    private async Task<JsonElement> CallAsync(Action<long, Utf8JsonWriter> write, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var request = BuildRequest(Interlocked.Increment(ref _nextId), write, out var id);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            VerifyServer(pipe);
            await JsonLines.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
            var response = await new JsonLineReader(pipe).ReadAsync(deadline.Token).ConfigureAwait(false) ??
                throw new ConnectHostTransportException("unavailable");
            return Interpret(response, id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConnectHostTransportException("no_answer");
        }
        catch (ConnectPipeUntrustedException exception)
        {
            throw new ConnectHostTransportException("untrusted", exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or Win32Exception or UnauthorizedAccessException)
        {
            throw new ConnectHostTransportException("unavailable", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(request);
        }
    }

    private void VerifyServer(NamedPipeClientStream pipe)
    {
        ConnectPipeSecurity.VerifyServerOwner(pipe, ConnectPipeSecurity.CurrentUser);
        var expected = _runningProcessId();
        if (expected is null || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) || actual != expected)
        {
            throw new ConnectHostTransportException("untrusted");
        }
    }

    private static byte[] BuildRequest(long nextId, Action<long, Utf8JsonWriter> write, out long id)
    {
        id = nextId;
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(id, writer);
            writer.WriteEndObject();
        }

        var request = buffer.WrittenSpan.ToArray();
        buffer.Clear();
        return request;
    }

    private static JsonElement Interpret(JsonElement response, long id)
    {
        if (!response.TryGetProperty("id", out var echoed) || !echoed.TryGetInt64(out var echoedId) || echoedId != id ||
            !response.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ConnectHostTransportException("invalid_response");
        }

        if (ok.GetBoolean())
        {
            return response;
        }

        var code = response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : null;
        throw new ConnectHostTransportException(code is not null && ErrorPattern().IsMatch(code) ? code : "invalid_response");
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text
            ? text : throw new ConnectHostTransportException("invalid_response");

    private static string? OptionalString(JsonElement value, string name) =>
        !value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null
            ? null : property.ValueKind == JsonValueKind.String ? property.GetString() : throw new ConnectHostTransportException("invalid_response");

    private static int RequiredInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result)
            ? result : throw new ConnectHostTransportException("invalid_response");

    private static long? OptionalInt64(JsonElement value, string name) =>
        !value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null
            ? null : property.TryGetInt64(out var result) ? result : throw new ConnectHostTransportException("invalid_response");

    private static bool OptionalBoolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : throw new ConnectHostTransportException("invalid_response");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int serverProcessId);

    [GeneratedRegex("^[a-z_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorPattern();
}

public sealed record ConnectHostTransportHello(int ProtocolVersion, string Mode, string Version);
public sealed record ConnectHostTransportNode(string Node, string? NodeId, string State);
public sealed record ConnectHostBridgeStatus(string State, string? Listen, bool RevocationChannel, long LiveConnections);
public sealed record ConnectHostTransportStatus(IReadOnlyList<ConnectHostTransportNode> Nodes, ConnectHostBridgeStatus Bridge);

public sealed class ConnectHostTransportException : Exception
{
    public ConnectHostTransportException(string code, Exception? inner = null)
        : base("The 1Salem Connect host transport call failed: " + code + ".", inner) => Code = code;
    public string Code { get; }
}
