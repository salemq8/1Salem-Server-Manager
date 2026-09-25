using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using ServerManager.Connect.App.Tests.Fakes;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.Core.Pipes;

namespace ServerManager.Connect.App.Tests;

public sealed class TransportServerVerifierTests
{
    private const string TransportPath = @"C:\Program Files\1Salem Connect\1Salem.Connect.Transport.exe";
    private const string MissingExecutable = @"C:\1salem-connect-test-missing\1Salem.Connect.Transport.exe";
    private const string AuthKey = "tskey-auth-kVerify1CNTRL-abcdef0123456789";
    private const int Low = 0x1000;
    private const int Medium = 0x2000;
    private const int High = 0x3000;
    private static readonly TimeSpan PipeTestTimeout = TimeSpan.FromSeconds(15);

    // Never this test process's own id, which the verifier inspects for its own integrity level.
    private static readonly int ServerId = Environment.ProcessId + 4;
    private static readonly int OtherId = Environment.ProcessId + 8;

    [Theory]
    [InlineData(TransportPath, Medium)]
    [InlineData(TransportPath, High)]
    [InlineData(@"c:\PROGRAM FILES\1salem connect\.\1SALEM.CONNECT.TRANSPORT.EXE", Medium)]
    [InlineData(@"C:\Program Files\1Salem Connect\sub\..\1Salem.Connect.Transport.exe", Medium)]
    public void A_running_transport_is_reused_when_it_is_the_configured_executable_at_no_lower_integrity(string imagePath, int integrityLevel)
    {
        var inspector = new FakeProcessInspector(ownLevel: Medium);
        inspector.Processes[ServerId] = new FakeProcess(new InspectedProcess(imagePath, integrityLevel));
        using var verifier = new TransportServerVerifier(TransportPath, inspector);

        verifier.VerifyServerProcess(ServerId);
    }

    [Theory]
    [InlineData(@"C:\Users\Friend\Downloads\1Salem.Connect.Transport.exe", Medium)] // another copy
    [InlineData(@"C:\Program Files\1Salem Connect\1Salem.Connect.Transport.exe.old", Medium)]
    [InlineData(@"C:\Windows\System32\cmd.exe", High)]
    [InlineData(TransportPath, Low)] // the real executable, started from a sandbox: it runs at the sandbox's level
    [InlineData("", Medium)]
    public void Any_other_server_is_refused(string imagePath, int integrityLevel)
    {
        var inspector = new FakeProcessInspector(ownLevel: Medium);
        inspector.Processes[ServerId] = new FakeProcess(new InspectedProcess(imagePath, integrityLevel));
        using var verifier = new TransportServerVerifier(TransportPath, inspector);

        Assert.Throws<TransportServerUntrustedException>(() => verifier.VerifyServerProcess(ServerId));
    }

    [Fact]
    public void A_medium_transport_is_refused_by_an_app_running_elevated()
    {
        var inspector = new FakeProcessInspector(ownLevel: High);
        inspector.Processes[ServerId] = new FakeProcess(new InspectedProcess(TransportPath, Medium));
        using var verifier = new TransportServerVerifier(TransportPath, inspector);

        Assert.Throws<TransportServerUntrustedException>(() => verifier.VerifyServerProcess(ServerId));
    }

    [Fact]
    public void A_process_that_cannot_be_inspected_is_refused()
    {
        var unopenable = new FakeProcessInspector(ownLevel: Medium);
        var unreadable = new FakeProcessInspector(ownLevel: Medium);
        unreadable.Processes[ServerId] = new FakeProcess(null);
        var unreadableSelf = new FakeProcessInspector(ownLevel: null);
        unreadableSelf.Processes[ServerId] = new FakeProcess(new InspectedProcess(TransportPath, High));

        foreach (var inspector in new[] { unopenable, unreadable, unreadableSelf })
        {
            using var verifier = new TransportServerVerifier(TransportPath, inspector);

            Assert.Throws<TransportServerUntrustedException>(() => verifier.VerifyServerProcess(ServerId));
            Assert.Null(verifier.ReusedTransportsExited);
            Assert.All(inspector.Opened, opened => Assert.True(opened.Disposed));
        }
    }

    [Fact]
    public void While_its_own_transport_runs_only_that_process_may_serve_the_pipe()
    {
        var inspector = new FakeProcessInspector(ownLevel: Medium);
        inspector.Processes[ServerId] = new FakeProcess(new InspectedProcess(TransportPath, Medium));
        inspector.Processes[OtherId] = new FakeProcess(new InspectedProcess(TransportPath, Medium));
        using var verifier = new TransportServerVerifier(TransportPath, inspector);

        verifier.TransportStarted(ServerId);

        verifier.VerifyServerProcess(ServerId);

        // Even the right executable at the right level: it is not the process this app started.
        Assert.Throws<TransportServerUntrustedException>(() => verifier.VerifyServerProcess(OtherId));

        // The started process is known by the handle its owner holds; the verifier opens nothing.
        Assert.Empty(inspector.Opened);
        Assert.Null(verifier.ReusedTransportsExited);

        // Only the started process's own stop releases the rule.
        verifier.TransportStopped(OtherId);
        Assert.Throws<TransportServerUntrustedException>(() => verifier.VerifyServerProcess(OtherId));
        verifier.TransportStopped(ServerId);
        verifier.VerifyServerProcess(OtherId);
    }

    [Fact]
    public void A_reused_transport_is_read_and_kept_through_the_one_handle_opened_at_verification()
    {
        var inspector = new FakeProcessInspector(ownLevel: Medium);
        var server = new FakeProcess(new InspectedProcess(TransportPath, Medium));
        inspector.Processes[ServerId] = server;
        var verifier = new TransportServerVerifier(TransportPath, inspector);

        verifier.VerifyServerProcess(ServerId);

        // Opened once, and what was checked is what is kept; this app's own handle is released.
        var kept = Assert.Single(inspector.Opened, opened => opened.ProcessId == ServerId);
        Assert.Equal(1, kept.Inspections);
        Assert.False(kept.Disposed);
        Assert.All(inspector.Opened.Where(opened => opened.ProcessId == Environment.ProcessId), own => Assert.True(own.Disposed));
        Assert.False(verifier.ReusedTransportsExited);

        server.HasExited = true;
        Assert.True(verifier.ReusedTransportsExited);

        verifier.Dispose();
        Assert.True(kept.Disposed);
        Assert.Null(verifier.ReusedTransportsExited);
        Assert.Throws<ObjectDisposedException>(() => verifier.VerifyServerProcess(ServerId));
        Assert.Empty(inspector.OpenHandles(ServerId));
    }

    [Fact]
    public void Reused_transports_count_as_exited_only_once_every_one_has()
    {
        var thirdId = Environment.ProcessId + 12;
        var inspector = new FakeProcessInspector(ownLevel: Medium);
        var first = new FakeProcess(new InspectedProcess(TransportPath, Medium));
        var second = new FakeProcess(new InspectedProcess(TransportPath, Medium));
        inspector.Processes[ServerId] = first;
        inspector.Processes[OtherId] = second;
        inspector.Processes[thirdId] = new FakeProcess(new InspectedProcess(TransportPath, Medium));
        using var verifier = new TransportServerVerifier(TransportPath, inspector);

        // Each new connection to the same process is verified again, but it is kept once.
        verifier.VerifyServerProcess(ServerId);
        verifier.VerifyServerProcess(ServerId);
        Assert.Single(inspector.OpenHandles(ServerId));

        // A session may still listen in the first while the second serves the pipe.
        verifier.VerifyServerProcess(OtherId);
        first.HasExited = true;
        Assert.False(verifier.ReusedTransportsExited);

        second.HasExited = true;
        Assert.True(verifier.ReusedTransportsExited);

        // Those that have exited are released once another is kept, which the answer then covers.
        verifier.VerifyServerProcess(thirdId);
        Assert.Empty(inspector.OpenHandles(ServerId));
        Assert.Empty(inspector.OpenHandles(OtherId));
        Assert.Single(inspector.OpenHandles(thirdId));
        Assert.False(verifier.ReusedTransportsExited);
    }

    [Fact]
    public void The_windows_inspector_reads_a_process_and_sees_it_exit_through_the_handle_it_opened()
    {
        var inspector = new WindowsProcessInspector();

        using (var self = inspector.Open(Environment.ProcessId))
        {
            Assert.NotNull(self);
            var own = self.Inspect();
            Assert.NotNull(own);
            Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), own.ImagePath, ignoreCase: true);
            Assert.InRange(own.IntegrityLevel, Medium, 0x4000);
            Assert.False(self.HasExited);
        }

        // A short-lived local child; its id stays reserved while Process holds its handle.
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        using var opened = inspector.Open(child.Id);
        Assert.NotNull(opened);
        Assert.True(child.WaitForExit(PipeTestTimeout));
        Assert.True(opened.HasExited);
    }

    [Fact]
    public async Task A_pipe_served_by_another_program_receives_nothing()
    {
        // This test process serves the pipe as the same user at the same integrity level, like a
        // sandboxed look-alike would after passing the owner check: only its image differs.
        var pipeName = TestPipeName();
        await using var server = ConnectPipeSecurity.CreateFirstInstance(pipeName);
        var received = ReceiveAllAsync(server);
        using var verifier = TransportServerVerifier.ForTransport(MissingExecutable);
        using var client = PipeTransportClient.ForPipe(pipeName, verifier);

        var failure = await Assert.ThrowsAsync<TransportException>(() => client.EnrollAsync("own_node", AuthKey, "1salem-x", CancellationToken.None))
            .WaitAsync(PipeTestTimeout);

        Assert.Equal(TransportErrorCodes.Untrusted, failure.Code);
        Assert.IsType<TransportServerUntrustedException>(failure.InnerException);
        Assert.Empty(await received.WaitAsync(PipeTestTimeout));
    }

    [Fact]
    public async Task A_pipe_served_by_another_process_than_the_started_transport_receives_nothing()
    {
        var pipeName = TestPipeName();
        await using var server = ConnectPipeSecurity.CreateFirstInstance(pipeName);
        var received = ReceiveAllAsync(server);
        using var verifier = TransportServerVerifier.ForTransport(Environment.ProcessPath!);
        verifier.TransportStarted(OtherId);
        using var client = PipeTransportClient.ForPipe(pipeName, verifier);

        var failure = await Assert.ThrowsAsync<TransportException>(() => client.HelloAsync(CancellationToken.None)).WaitAsync(PipeTestTimeout);

        Assert.Equal(TransportErrorCodes.Untrusted, failure.Code);
        Assert.Empty(await received.WaitAsync(PipeTestTimeout));
    }

    [Fact]
    public async Task A_pipe_served_by_the_configured_executable_is_used_and_its_server_kept()
    {
        var pipeName = TestPipeName();
        await using var server = ConnectPipeSecurity.CreateFirstInstance(pipeName);
        var serving = AnswerHelloAsync(server);
        using var verifier = TransportServerVerifier.ForTransport(Environment.ProcessPath!);
        using var client = PipeTransportClient.ForPipe(pipeName, verifier);

        var hello = await client.HelloAsync(CancellationToken.None).WaitAsync(PipeTestTimeout);

        Assert.Equal("fake", hello.Mode);
        await serving.WaitAsync(PipeTestTimeout);

        // The server (this test process) is held and known to be running.
        Assert.False(verifier.ReusedTransportsExited);
    }

    [Fact]
    public async Task A_pipe_served_by_the_started_transport_is_used_by_process_id()
    {
        var pipeName = TestPipeName();
        await using var server = ConnectPipeSecurity.CreateFirstInstance(pipeName);
        var serving = AnswerHelloAsync(server);
        using var verifier = TransportServerVerifier.ForTransport(MissingExecutable);
        verifier.TransportStarted(Environment.ProcessId);
        using var client = PipeTransportClient.ForPipe(pipeName, verifier);

        var hello = await client.HelloAsync(CancellationToken.None).WaitAsync(PipeTestTimeout);

        Assert.Equal("fake", hello.Mode);
        await serving.WaitAsync(PipeTestTimeout);
    }

    private static string TestPipeName() => "1Salem.Connect.Transport.test." + Guid.NewGuid().ToString("N");

    /// <summary>Everything the client writes before it hangs up.</summary>
    private static async Task<byte[]> ReceiveAllAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync();
        using var received = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await server.ReadAsync(buffer)) > 0)
        {
            received.Write(buffer, 0, read);
        }

        return received.ToArray();
    }

    private static async Task AnswerHelloAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync();
        var request = await new JsonLineReader(server).ReadAsync(CancellationToken.None) ??
            throw new InvalidOperationException("The client hung up without a request.");
        var id = request.GetProperty("id").GetInt64();
        await JsonLines.WriteAsync(server, Encoding.UTF8.GetBytes($$"""{"id":{{id}},"ok":true,"v":1,"mode":"fake","version":"test"}"""), CancellationToken.None);
    }
}
