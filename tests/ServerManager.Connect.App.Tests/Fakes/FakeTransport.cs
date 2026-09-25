using ServerManager.Connect.App.Transport;

namespace ServerManager.Connect.App.Tests.Fakes;

internal sealed record OpenCall(string Node, string Ticket, string SessionKey, int PreferredPort);

internal sealed record RefreshCall(string SessionId, string Ticket, string SessionKey);

internal sealed record EnrollCall(string Node, string AuthKey, string Hostname);

/// <summary>An in-memory friend transport that records every operation.</summary>
internal sealed class FakeTransport : ITransportClient
{
    public string Mode { get; set; } = "fake";

    public Exception? HelloFailure { get; set; }

    public Exception? DiagnosticsFailure { get; set; }

    public Exception? StatusFailure { get; set; }

    /// <summary>Thrown by the next closes, one each, after they have been recorded; the session stays open.</summary>
    public Queue<Exception> CloseFailures { get; } = new();

    /// <summary>Closes are recorded and then never complete, whatever their token says: a transport stuck mid-call.</summary>
    public bool CloseNeverAnswers { get; set; }

    /// <summary>Thrown by the next refresh, after it has been recorded.</summary>
    public Exception? NextRefreshFailure { get; set; }

    /// <summary>Thrown by the next enroll, after it has been recorded (the key did reach the pipe).</summary>
    public Exception? NextEnrollFailure { get; set; }

    public List<TransportNode> Nodes { get; } = [];

    public List<TransportSession> Sessions { get; } = [];

    public List<EnrollCall> Enrollments { get; } = [];

    public List<OpenCall> Opens { get; } = [];

    public List<RefreshCall> Refreshes { get; } = [];

    public List<string> Closed { get; } = [];

    public List<string> Log { get; } = [];

    public string NextNodeId { get; set; } = "nFAKE1CNTRL";

    public string LocalAddress { get; set; } = "127.0.0.1:18211";

    public Task<TransportHello> HelloAsync(CancellationToken cancellationToken) =>
        HelloFailure is not null
            ? Task.FromException<TransportHello>(HelloFailure)
            : Task.FromResult(new TransportHello(1, Mode, "test"));

    public Task<TransportStatus> StatusAsync(CancellationToken cancellationToken) =>
        StatusFailure is not null
            ? Task.FromException<TransportStatus>(StatusFailure)
            : Task.FromResult(new TransportStatus([.. Nodes], [.. Sessions]));

    public Task<string> EnrollAsync(string node, string authKey, string hostname, CancellationToken cancellationToken)
    {
        Enrollments.Add(new EnrollCall(node, authKey, hostname));
        if (NextEnrollFailure is { } failure)
        {
            NextEnrollFailure = null;
            return Task.FromException<string>(failure);
        }

        Nodes.Add(new TransportNode(node, NextNodeId, "running"));
        return Task.FromResult(NextNodeId);
    }

    public Task<TransportOpened> OpenAsync(string node, string ticket, string sessionKey, int preferredPort, CancellationToken cancellationToken)
    {
        Opens.Add(new OpenCall(node, ticket, sessionKey, preferredPort));
        var sessionId = $"ses_test{Opens.Count}";
        Sessions.Add(new TransportSession(sessionId, "sid", LocalAddress, "listening"));
        return Task.FromResult(new TransportOpened(sessionId, LocalAddress));
    }

    public Task RefreshAsync(string sessionId, string ticket, string sessionKey, CancellationToken cancellationToken)
    {
        Refreshes.Add(new RefreshCall(sessionId, ticket, sessionKey));
        if (NextRefreshFailure is { } failure)
        {
            NextRefreshFailure = null;
            return Task.FromException(failure);
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync(string sessionId, CancellationToken cancellationToken)
    {
        Closed.Add(sessionId);
        if (CloseNeverAnswers)
        {
            return new TaskCompletionSource().Task;
        }

        if (CloseFailures.TryDequeue(out var failure))
        {
            return Task.FromException(failure);
        }

        Sessions.RemoveAll(session => session.SessionId == sessionId);
        return Task.CompletedTask;
    }

    public Task<TransportDiagnostics> DiagnosticsAsync(CancellationToken cancellationToken) =>
        DiagnosticsFailure is not null
            ? Task.FromException<TransportDiagnostics>(DiagnosticsFailure)
            : Task.FromResult(new TransportDiagnostics(
                Mode,
                "test",
                ["k1"],
                [.. Nodes],
                Sessions.Select(session => new TransportSessionDetail(session.SessionId, session.Local, session.State, "node", "127.0.0.1:7780", 0, 0)).ToList(),
                [.. Log]));
}
