using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.Core.Pipes;

namespace ServerManager.Connect.App.Tests;

public sealed class TransportClientTests
{
    // Everything the friend pipe accepts from the app (§11): the envelope plus each operation's own inputs.
    private static readonly HashSet<string> PermittedFields =
        ["id", "op", "v", "node", "authKey", "hostname", "ticket", "sessionKey", "preferredPort", "sessionId"];

    private static readonly string[] DestinationLikeFields =
        ["destination", "dest", "dst", "host", "hostBridge", "hb", "address", "addr", "target", "ip", "port", "endpoint", "url", "remote"];

    [Fact]
    public void No_request_the_app_can_build_carries_a_destination()
    {
        var requests = new[]
        {
            TransportRequests.Hello(1),
            TransportRequests.Status(2),
            TransportRequests.Enroll(3, "own_node", "tskey-auth-k1", "1salem-x"),
            TransportRequests.Open(4, "own_node", "eyJ.t.s", "key", 0),
            TransportRequests.Refresh(5, "ses_1", "eyJ.t.s", "key"),
            TransportRequests.Close(6, "ses_1"),
            TransportRequests.Diag(7)
        };

        var fields = requests.SelectMany(FieldNames).ToHashSet();

        Assert.Subset(PermittedFields, fields);
        foreach (var forbidden in DestinationLikeFields)
        {
            Assert.DoesNotContain(fields, field => string.Equals(field, forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Every_line_written_to_the_pipe_is_one_of_the_permitted_shapes()
    {
        // Enroll runs on a connection of its own, so every connection is recorded.
        var pipes = new List<ScriptedPipe>();
        using var client = new PipeTransportClient(_ =>
        {
            var pipe = new ScriptedPipe(Respond);
            pipes.Add(pipe);
            return Task.FromResult<Stream>(pipe);
        });

        await client.HelloAsync(CancellationToken.None);
        await client.StatusAsync(CancellationToken.None);
        await client.EnrollAsync("own_node", "tskey-auth-k1", "1salem-x", CancellationToken.None);
        var opened = await client.OpenAsync("own_node", "eyJ.t.s", "key", 0, CancellationToken.None);
        await client.RefreshAsync(opened.SessionId, "eyJ.t.s", "key", CancellationToken.None);
        await client.CloseAsync(opened.SessionId, CancellationToken.None);
        await client.DiagnosticsAsync(CancellationToken.None);

        var lines = pipes.SelectMany(pipe => pipe.Lines).ToList();
        var requests = lines.Select(Parse).OrderBy(request => request.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(["hello", "status", "enroll", "open", "refresh", "close", "diag"], requests.Select(request => request.GetProperty("op").GetString()));
        Assert.Equal([1L, 2, 3, 4, 5, 6, 7], requests.Select(request => request.GetProperty("id").GetInt64()));
        Assert.Subset(PermittedFields, lines.SelectMany(line => FieldNames(Encoding.UTF8.GetBytes(line))).ToHashSet());
        Assert.Equal("127.0.0.1:18211", opened.Local);
    }

    [Fact]
    public async Task Transport_error_codes_surface_as_transport_exceptions()
    {
        var pipe = new ScriptedPipe(request => $$"""{"id":{{request.GetProperty("id").GetInt64()}},"ok":false,"error":"ticket_rejected"}""");
        using var client = new PipeTransportClient(_ => Task.FromResult<Stream>(pipe));

        var failure = await Assert.ThrowsAsync<TransportException>(() => client.OpenAsync("own_node", "eyJ.t.s", "key", 0, CancellationToken.None));

        Assert.Equal("ticket_rejected", failure.Code);
    }

    [Fact]
    public async Task A_local_address_that_is_not_loopback_is_never_shown()
    {
        var pipe = new ScriptedPipe(request => $$"""{"id":{{request.GetProperty("id").GetInt64()}},"ok":true,"sessionId":"ses_1","local":"192.168.1.20:18211"}""");
        using var client = new PipeTransportClient(_ => Task.FromResult<Stream>(pipe));

        var failure = await Assert.ThrowsAsync<TransportException>(() => client.OpenAsync("own_node", "eyJ.t.s", "key", 0, CancellationToken.None));

        Assert.Equal(TransportErrorCodes.InvalidResponse, failure.Code);
    }

    [Fact]
    public async Task An_answer_to_another_request_is_refused()
    {
        var pipe = new ScriptedPipe(_ => """{"id":99,"ok":true,"v":1,"mode":"fake","version":"x"}""");
        using var client = new PipeTransportClient(_ => Task.FromResult<Stream>(pipe));

        var failure = await Assert.ThrowsAsync<TransportException>(() => client.HelloAsync(CancellationToken.None));

        Assert.Equal(TransportErrorCodes.InvalidResponse, failure.Code);
    }

    [Fact]
    public async Task A_missing_pipe_is_reported_as_unavailable()
    {
        using var client = new PipeTransportClient(_ => Task.FromException<Stream>(new TimeoutException()));

        var failure = await Assert.ThrowsAsync<TransportException>(() => client.StatusAsync(CancellationToken.None));

        Assert.Equal(TransportErrorCodes.Unavailable, failure.Code);
    }

    [Fact]
    public async Task A_call_queued_behind_a_slow_one_gets_its_whole_time_once_it_is_sent()
    {
        // Each answer takes 60% of a call's time. The second call waits for the first before it is
        // sent; had its clock started while it waited, it would run out before its answer came.
        var pipe = new ScriptedPipe(async request =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            return Respond(request);
        });
        using var client = new PipeTransportClient(_ => Task.FromResult<Stream>(pipe), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        var first = client.StatusAsync(CancellationToken.None);
        var second = client.DiagnosticsAsync(CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Equal(["status", "diag"], Ops(pipe));
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_in_the_queue_is_cancelled_without_disturbing_the_call_ahead()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipe = new ScriptedPipe(async request =>
        {
            await answer.Task;
            return Respond(request);
        });
        using var client = new PipeTransportClient(_ => Task.FromResult<Stream>(pipe));
        using var giveUp = new CancellationTokenSource();

        var ahead = client.StatusAsync(CancellationToken.None);
        var queued = client.HelloAsync(giveUp.Token);
        giveUp.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        answer.SetResult();
        await ahead;
        Assert.Equal(["status"], Ops(pipe));
        Assert.False(pipe.IsDisposed);
    }

    [Fact]
    public async Task Enroll_has_a_connection_of_its_own_so_no_other_call_waits_behind_it()
    {
        // tsnet enroll waits for a new node to come up (up to 90 s); status, hello and close
        // must still answer meanwhile. The transport serves several connections at once.
        var enrolled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipes = new List<ScriptedPipe>();
        using var client = new PipeTransportClient(_ =>
        {
            var pipe = new ScriptedPipe(async request =>
            {
                if (request.GetProperty("op").GetString() == "enroll")
                {
                    await enrolled.Task;
                }

                return Respond(request);
            });
            pipes.Add(pipe);
            return Task.FromResult<Stream>(pipe);
        });

        var enroll = client.EnrollAsync("own_node", "tskey-auth-k1", "1salem-x", CancellationToken.None);
        await client.StatusAsync(CancellationToken.None);
        await client.HelloAsync(CancellationToken.None);
        Assert.False(enroll.IsCompleted);

        enrolled.SetResult();
        Assert.Equal("nFAKE1", await enroll);

        // The auth key's connection is closed with its call; the others share one that stays open.
        Assert.Equal(2, pipes.Count);
        Assert.Equal(["enroll"], Ops(pipes[0]));
        Assert.True(pipes[0].IsDisposed);
        Assert.Equal(["status", "hello"], Ops(pipes[1]));
        Assert.False(pipes[1].IsDisposed);

        await client.EnrollAsync("own_node", "tskey-auth-k2", "1salem-x", CancellationToken.None);
        Assert.Equal(3, pipes.Count);
        Assert.True(pipes[2].IsDisposed);
    }

    [Fact]
    public async Task A_connected_transport_that_does_not_answer_in_time_is_busy_not_gone()
    {
        var pipes = new List<ScriptedPipe>();
        using var client = new PipeTransportClient(
            _ =>
            {
                // The first connection never answers; the next one does.
                var silent = pipes.Count == 0;
                var pipe = new ScriptedPipe(request => Task.FromResult<string?>(silent ? null : Respond(request)));
                pipes.Add(pipe);
                return Task.FromResult<Stream>(pipe);
            },
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(300));

        var failure = await Assert.ThrowsAsync<TransportException>(() => client.StatusAsync(CancellationToken.None));

        Assert.Equal(TransportErrorCodes.NoAnswer, failure.Code);

        // Whether an answer is still on its way is unknown, so that connection is not reused.
        Assert.True(pipes[0].IsDisposed);
        await client.StatusAsync(CancellationToken.None);
        Assert.Equal(2, pipes.Count);
    }

    [Fact]
    public async Task A_pipe_that_fails_its_checks_is_untrusted()
    {
        using var foreignOwner = new PipeTransportClient(_ => Task.FromException<Stream>(new ConnectPipeUntrustedException()));
        using var foreignServer = new PipeTransportClient(_ => Task.FromException<Stream>(new TransportServerUntrustedException("test")));

        var owner = await Assert.ThrowsAsync<TransportException>(() => foreignOwner.HelloAsync(CancellationToken.None));
        var server = await Assert.ThrowsAsync<TransportException>(() => foreignServer.EnrollAsync("own_node", "tskey-auth-k1", "1salem-x", CancellationToken.None));

        Assert.Equal(TransportErrorCodes.Untrusted, owner.Code);
        Assert.Equal(TransportErrorCodes.Untrusted, server.Code);
    }

    private static List<string?> Ops(ScriptedPipe pipe) =>
        pipe.Lines.Select(line => Parse(line).GetProperty("op").GetString()).ToList();

    private static string Respond(JsonElement request)
    {
        var id = request.GetProperty("id").GetInt64();
        return request.GetProperty("op").GetString() switch
        {
            "hello" => $$"""{"id":{{id}},"ok":true,"v":1,"mode":"fake","version":"test"}""",
            "status" => $$"""{"id":{{id}},"ok":true,"nodes":[{"node":"own_node","nodeId":"nFAKE1","state":"fake"}],"sessions":[]}""",
            "enroll" => $$"""{"id":{{id}},"ok":true,"nodeId":"nFAKE1"}""",
            "open" => $$"""{"id":{{id}},"ok":true,"sessionId":"ses_1","local":"127.0.0.1:18211"}""",
            "diag" => $$"""{"id":{{id}},"ok":true,"mode":"fake","version":"test","kids":["k1"],"nodes":[],"sessions":[],"log":[]}""",
            _ => $$"""{"id":{{id}},"ok":true}"""
        };
    }

    private static IEnumerable<string> FieldNames(byte[] line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
    }

    private static JsonElement Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// A duplex stream standing in for one pipe connection: it records each request line and
    /// answers it with the script's line once the script's task completes (null: never).
    /// </summary>
    private sealed class ScriptedPipe(Func<JsonElement, Task<string?>> respond) : Stream
    {
        private readonly Channel<byte[]> _answers = Channel.CreateUnbounded<byte[]>();
        private readonly List<byte> _partial = [];
        private ReadOnlyMemory<byte> _current;

        public ScriptedPipe(Func<JsonElement, string> respond)
            : this(request => Task.FromResult<string?>(respond(request)))
        {
        }

        public List<string> Lines { get; } = [];

        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            foreach (var value in buffer)
            {
                if (value != (byte)'\n')
                {
                    _partial.Add(value);
                    continue;
                }

                var line = Encoding.UTF8.GetString(_partial.ToArray());
                _partial.Clear();
                Lines.Add(line);
                _ = AnswerAsync(Parse(line));
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current.IsEmpty)
            {
                // Ends (0 bytes) once the connection is disposed with no answer left to read.
                if (!await _answers.Reader.WaitToReadAsync(cancellationToken) || !_answers.Reader.TryRead(out var next))
                {
                    return 0;
                }

                _current = next;
            }

            var count = Math.Min(buffer.Length, _current.Length);
            _current[..count].CopyTo(buffer);
            _current = _current[count..];
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            _answers.Writer.TryComplete();
            base.Dispose(disposing);
        }

        private async Task AnswerAsync(JsonElement request)
        {
            if (await respond(request) is { } answer)
            {
                _answers.Writer.TryWrite(Encoding.UTF8.GetBytes(answer + "\n"));
            }
        }
    }
}
