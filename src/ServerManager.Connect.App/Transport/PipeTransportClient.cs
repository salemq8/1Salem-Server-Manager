using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerManager.Connect.Core.Pipes;

namespace ServerManager.Connect.App.Transport;

/// <summary>
/// JSON-lines client for the friend pipe (§11). Requests on one connection are serialized: the
/// protocol answers in order. Enroll, which in tsnet mode waits for a new node to come up, gets
/// a fresh connection of its own for each call, so status, hello and close are never queued
/// behind it (the transport serves several clients at once) and the one-time auth key only ever
/// goes down a connection verified moments before. A connection whose framing is in doubt (I/O
/// error, timeout, garbled or mismatched answer) is dropped and the next call reconnects.
/// </summary>
public sealed partial class PipeTransportClient : ITransportClient, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(30);

    // In tsnet mode enroll brings a new node up against the control server before it answers.
    private static readonly TimeSpan DefaultEnrollTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _enrollTimeout;
    private readonly Lane _calls = new(keepOpen: true);
    private readonly Lane _enrollments = new(keepOpen: false);
    private long _nextId;

    /// <param name="connect">Opens a connection that is already verified; see <see cref="ForPipe"/>.</param>
    public PipeTransportClient(Func<CancellationToken, Task<Stream>> connect)
        : this(connect, DefaultCallTimeout, DefaultEnrollTimeout)
    {
    }

    internal PipeTransportClient(Func<CancellationToken, Task<Stream>> connect, TimeSpan callTimeout, TimeSpan enrollTimeout)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _callTimeout = callTimeout;
        _enrollTimeout = enrollTimeout;
    }

    /// <summary>
    /// The transport serving <paramref name="pipeName"/> (see <see cref="FriendPipeName"/>). Every
    /// connection is checked to be owned by this Windows user and served by this app's transport
    /// (<paramref name="server"/>) before anything is written to it.
    /// </summary>
    public static PipeTransportClient ForPipe(string pipeName, TransportServerVerifier server)
    {
        var name = FriendPipeName.Validate(pipeName);
        ArgumentNullException.ThrowIfNull(server);
        return new PipeTransportClient(cancellationToken => ConnectToFriendPipeAsync(name, server, cancellationToken));
    }

    public async Task<TransportHello> HelloAsync(CancellationToken cancellationToken) =>
        TransportResponses.Hello(await CallAsync(_calls, TransportRequests.Hello, _callTimeout, cancellationToken).ConfigureAwait(false));

    public async Task<TransportStatus> StatusAsync(CancellationToken cancellationToken) =>
        TransportResponses.Status(await CallAsync(_calls, TransportRequests.Status, _callTimeout, cancellationToken).ConfigureAwait(false));

    public async Task<string> EnrollAsync(string node, string authKey, string hostname, CancellationToken cancellationToken) =>
        TransportResponses.EnrolledNodeId(
            await CallAsync(_enrollments, id => TransportRequests.Enroll(id, node, authKey, hostname), _enrollTimeout, cancellationToken).ConfigureAwait(false));

    public Task ForgetAsync(string node, CancellationToken cancellationToken) =>
        CallAsync(_calls, id => TransportRequests.Forget(id, node), _callTimeout, cancellationToken);

    public async Task<TransportOpened> OpenAsync(string node, string ticket, string sessionKey, int preferredPort, CancellationToken cancellationToken) =>
        TransportResponses.Opened(
            await CallAsync(_calls, id => TransportRequests.Open(id, node, ticket, sessionKey, preferredPort), _callTimeout, cancellationToken).ConfigureAwait(false));

    public Task RefreshAsync(string sessionId, string ticket, string sessionKey, CancellationToken cancellationToken) =>
        CallAsync(_calls, id => TransportRequests.Refresh(id, sessionId, ticket, sessionKey), _callTimeout, cancellationToken);

    public Task CloseAsync(string sessionId, CancellationToken cancellationToken) =>
        CallAsync(_calls, id => TransportRequests.Close(id, sessionId), _callTimeout, cancellationToken);

    public async Task<TransportDiagnostics> DiagnosticsAsync(CancellationToken cancellationToken) =>
        TransportResponses.Diagnostics(await CallAsync(_calls, TransportRequests.Diag, _callTimeout, cancellationToken).ConfigureAwait(false));

    public void Dispose()
    {
        _calls.Dispose();
        _enrollments.Dispose();
    }

    private static async Task<Stream> ConnectToFriendPipeAsync(string pipeName, TransportServerVerifier server, CancellationToken cancellationToken)
    {
        var user = ConnectPipeSecurity.CurrentUser;
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);

            // Before one byte is written: whoever serves this pipe receives auth keys and session
            // keys. The owner check shuts out other accounts; the server check shuts out this
            // user's own sandboxed processes, whose pipes are owned by this user as well.
            ConnectPipeSecurity.VerifyServerOwner(pipe, user);
            server.Verify(pipe);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<JsonElement> CallAsync(Lane lane, Func<long, byte[]> buildRequest, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Waiting for the calls ahead is not part of this call's time: each of them is bounded by
        // its own deadline, and a call that timed out before it was even sent would report a
        // healthy transport as gone.
        await lane.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var connected = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            lane.Stream ??= await _connect(deadline.Token).ConfigureAwait(false);
            lane.Reader ??= new JsonLineReader(lane.Stream);
            connected = true;
            var id = Interlocked.Increment(ref _nextId);
            var request = buildRequest(id);
            try
            {
                await JsonLines.WriteAsync(lane.Stream, request, deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                // The line may carry an auth key or a session key.
                CryptographicOperations.ZeroMemory(request);
            }

            var response = await lane.Reader.ReadAsync(deadline.Token).ConfigureAwait(false) ??
                throw new TransportException(TransportErrorCodes.Unavailable);
            return Interpret(response, id);
        }
        catch (Exception exception) when (exception is ConnectPipeUntrustedException or TransportServerUntrustedException)
        {
            lane.Drop();
            throw new TransportException(TransportErrorCodes.Untrusted, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            // A pipe of that name exists but will not let this user in: someone else holds it.
            lane.Drop();
            throw new TransportException(TransportErrorCodes.Untrusted, exception);
        }
        catch (TransportException exception) when (exception.Code is TransportErrorCodes.Unavailable or TransportErrorCodes.InvalidResponse)
        {
            lane.Drop();
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lane.Drop();
            throw;
        }
        catch (OperationCanceledException exception) when (connected)
        {
            // Our deadline, on a verified connection: the transport is there but did not answer.
            // The framing is unknown now, so the connection goes; the transport is not "gone".
            lane.Drop();
            throw new TransportException(TransportErrorCodes.NoAnswer, exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException or TimeoutException or OperationCanceledException)
        {
            lane.Drop();
            throw new TransportException(TransportErrorCodes.Unavailable, exception);
        }
        finally
        {
            if (!lane.KeepOpen)
            {
                lane.Drop();
            }

            lane.Gate.Release();
        }
    }

    /// <summary>
    /// Returns the response of an <c>ok:true</c> answer to request <paramref name="id"/>, or throws
    /// the transport's error code. A code that is not a plain token is replaced, so nothing the
    /// transport sends is ever shown verbatim.
    /// </summary>
    private static JsonElement Interpret(JsonElement response, long id)
    {
        if (!response.TryGetProperty("id", out var echoed) ||
            !echoed.TryGetInt64(out var echoedId) ||
            echoedId != id ||
            !response.TryGetProperty("ok", out var ok) ||
            ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new TransportException(TransportErrorCodes.InvalidResponse);
        }

        if (ok.ValueKind == JsonValueKind.True)
        {
            return response;
        }

        var code = response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : null;
        throw new TransportException(code is not null && ErrorCodePattern().IsMatch(code) ? code : TransportErrorCodes.InvalidResponse);
    }

    [GeneratedRegex("^[a-z_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodePattern();

    /// <summary>One pipe connection and the gate that keeps its requests and answers in order.</summary>
    private sealed class Lane(bool keepOpen) : IDisposable
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>False: the connection is closed after every call.</summary>
        public bool KeepOpen { get; } = keepOpen;

        public Stream? Stream { get; set; }

        public JsonLineReader? Reader { get; set; }

        public void Drop()
        {
            Stream?.Dispose();
            Stream = null;
            Reader = null;
        }

        public void Dispose()
        {
            Drop();
            Gate.Dispose();
        }
    }
}
