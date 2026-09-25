using System.Diagnostics;
using System.Text.RegularExpressions;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Tests.Fakes;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.Core.Pipes;

namespace ServerManager.Connect.App.Tests;

public sealed class TransportProcessTests
{
    private const string MissingExecutable = @"C:\1salem-connect-test-missing\1Salem.Connect.Transport.exe";
    private const string TestPipe = "1Salem.Connect.Transport.test";

    /// <summary>Bounds a wait for a local child process that should end long before.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>An executable that exists, and runs until it is ended when told to ping 127.0.0.1 thirty times.</summary>
    private static readonly string PingExecutable = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    [Fact]
    public void Tailscale_variables_never_reach_the_transport_environment()
    {
        // Every variable tsnet or the Tailscale client libraries read for credentials or control,
        // in any case, plus any other TS_/TSNET_ setting.
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TS_AUTHKEY"] = "tskey-auth-leaked",
            ["TS_AUTH_KEY"] = "tskey-auth-leaked",
            ["TS_CLIENT_SECRET"] = "tskey-client-leaked",
            ["TS_CONTROL_URL"] = "https://control.example",
            ["TSNET_FORCE_LOGIN"] = "1",
            ["TS_CLIENT_ID"] = "client-id",
            ["TS_ID_TOKEN"] = "id-token",
            ["TS_AUDIENCE"] = "audience",
            ["ts_debug_anything"] = "1",
            ["PATH"] = @"C:\Windows"
        };

        TransportProcess.ScrubEnvironment(environment);

        Assert.Equal(["PATH"], environment.Keys);
    }

    [Fact]
    public void Tsnet_start_passes_an_absolute_state_directory_and_no_secret()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "1salem-connect-app-tests", "transport");
        using var process = new TransportProcess(Settings(TransportMode.Tsnet), new FakeBroker(), new FakeTransport(), dataDirectory, TestPipe, Verifier());

        var startInfo = process.BuildStartInfo(Path.Combine(dataDirectory, "ticket-keys.json"));
        var arguments = startInfo.ArgumentList.ToList();

        Assert.Equal("tsnet", ValueOf(arguments, "--mode"));
        // The transport's --pipe takes the full path (connect/transport/internal/pipe: \\.\pipe\<name>),
        // and it is the very name the client connects to.
        Assert.Equal(@"\\.\pipe\" + TestPipe, ValueOf(arguments, "--pipe"));
        var stateDirectory = ValueOf(arguments, "--state-dir");
        Assert.True(Path.IsPathFullyQualified(stateDirectory));
        Assert.DoesNotContain("--fake-node-id", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("tskey", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(startInfo.Environment.Keys, name => Regex.IsMatch(name, "^TS(NET)?_", RegexOptions.IgnoreCase));
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void Transport_output_is_kept_off_the_apps_own_console()
    {
        using var process = new TransportProcess(Settings(TransportMode.Tsnet), new FakeBroker(), new FakeTransport(), Path.GetTempPath(), TestPipe, Verifier());

        var startInfo = process.BuildStartInfo(Path.Combine(Path.GetTempPath(), "keys.json"));

        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Theory]
    [InlineData(@"\\.\pipe\1Salem.Connect.Transport.x")]
    [InlineData(@"..\1Salem.Connect.Transport.x")]
    [InlineData("1Salem Connect")]
    [InlineData("")]
    public void Pipe_names_the_transport_would_read_differently_are_refused(string pipeName)
    {
        Assert.Throws<ArgumentException>(() => new TransportProcess(Settings(TransportMode.Tsnet), new FakeBroker(), new FakeTransport(), Path.GetTempPath(), pipeName, Verifier()));
        Assert.Throws<ArgumentException>(() => PipeTransportClient.ForPipe(pipeName, Verifier()));
    }

    [Fact]
    public void The_release_pipe_name_is_the_transports_own_default()
    {
        // connect/transport/internal/pipe.FriendPipeName: \\.\pipe\1Salem.Connect.Transport.<user SID>.
        Assert.Equal("1Salem.Connect.Transport." + ConnectPipeSecurity.CurrentUser.Value, FriendPipeName.ForCurrentUser());
        Assert.Equal(FriendPipeName.ForCurrentUser(), FriendPipeName.Validate(FriendPipeName.ForCurrentUser()));
    }

    [Fact]
    public void Fake_start_names_the_fake_node_and_has_no_state_directory()
    {
        using var process = new TransportProcess(Settings(TransportMode.Fake), new FakeBroker(), new FakeTransport(), Path.GetTempPath(), TestPipe, Verifier());

        var arguments = process.BuildStartInfo(Path.Combine(Path.GetTempPath(), "keys.json")).ArgumentList.ToList();

        Assert.Equal("fake", ValueOf(arguments, "--mode"));
        Assert.Equal("fake-friend-1", ValueOf(arguments, "--fake-node-id"));
        Assert.DoesNotContain("--state-dir", arguments);
    }

    [Fact]
    public async Task A_running_transport_in_the_configured_mode_is_reused()
    {
        var transport = new FakeTransport { Mode = "tsnet" };
        using var process = new TransportProcess(Settings(TransportMode.Tsnet), new FakeBroker(), transport, Path.GetTempPath(), TestPipe, Verifier());

        await process.EnsureRunningAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_running_transport_in_the_other_mode_is_not_trusted()
    {
        var transport = new FakeTransport { Mode = "fake" };
        using var process = new TransportProcess(Settings(TransportMode.Tsnet), new FakeBroker(), transport, Path.GetTempPath(), TestPipe, Verifier());

        var failure = await Assert.ThrowsAsync<TransportException>(() => process.EnsureRunningAsync(CancellationToken.None));

        Assert.Equal(TransportErrorCodes.ModeMismatch, failure.Code);
    }

    [Fact]
    public async Task A_missing_transport_executable_fails_clearly()
    {
        var transport = new FakeTransport { HelloFailure = new TransportException(TransportErrorCodes.Unavailable) };
        using var process = new TransportProcess(Settings(TransportMode.Tsnet), new FakeBroker(), transport, Path.GetTempPath(), TestPipe, Verifier());

        var failure = await Assert.ThrowsAsync<TransportException>(() => process.EnsureRunningAsync(CancellationToken.None));

        Assert.Equal(TransportErrorCodes.Missing, failure.Code);
    }

    [Theory]
    [InlineData(TransportErrorCodes.NoAnswer)]
    [InlineData(TransportErrorCodes.Untrusted)]
    public async Task A_transport_that_is_there_is_never_taken_for_one_that_is_not_running(string code)
    {
        // Busy (no answer in time) or not ours: starting another would kill a healthy transport
        // and its sessions, or could not take the pipe anyway. The executable is missing, so any
        // start attempt would surface as "missing" instead.
        var transport = new FakeTransport { HelloFailure = new TransportException(code) };
        using var process = new TransportProcess(Settings(TransportMode.Tsnet), new FakeBroker(), transport, Path.GetTempPath(), TestPipe, Verifier());

        var failure = await Assert.ThrowsAsync<TransportException>(() => process.EnsureRunningAsync(CancellationToken.None));

        Assert.Equal(code, failure.Code);
        Assert.False(process.UsedTransportsExited);
    }

    [Fact]
    public async Task A_close_nothing_answers_is_settled_once_the_reused_transport_that_served_it_has_exited()
    {
        // Another copy of the app (or an earlier run) started the transport this one reuses.
        var reusedId = Environment.ProcessId + 4;
        var inspector = new FakeProcessInspector(ownLevel: 0x2000);
        var reused = new FakeProcess(new InspectedProcess(MissingExecutable, 0x2000));
        inspector.Processes[reusedId] = reused;
        using var verifier = new TransportServerVerifier(MissingExecutable, inspector);
        var transport = new FakeTransport { Mode = "tsnet" };
        using var process = new TransportProcess(Settings(TransportMode.Tsnet), new FakeBroker(), transport, Path.GetTempPath(), TestPipe, verifier);
        var sessions = new SessionService(new FakeBroker(), transport, process, new DiagnosticsLog(new ManualClock()));

        await process.EnsureRunningAsync(CancellationToken.None);
        Assert.False(process.UsedTransportsExited); // nothing has served this app yet

        // What the pipe client does on connecting to it.
        verifier.VerifyServerProcess(reusedId);

        // It stops answering while it still runs: the address may still be open.
        transport.StatusFailure = new TransportException(TransportErrorCodes.Unavailable);
        transport.CloseFailures.Enqueue(new TransportException(TransportErrorCodes.Unavailable));
        Assert.False(await sessions.CloseAsync("ses_test1", CancellationToken.None));

        // Once it has exited, its listeners are gone with it.
        reused.HasExited = true;
        transport.CloseFailures.Enqueue(new TransportException(TransportErrorCodes.Unavailable));
        Assert.True(await sessions.CloseAsync("ses_test1", CancellationToken.None));
    }

    [Fact]
    public async Task A_data_folder_that_cannot_be_written_fails_the_start_as_a_transport_error()
    {
        // A file where the transport's data folder should be.
        var blocked = Path.Combine(Path.GetTempPath(), "1salem-connect-app-tests-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocked, "not a folder");
        try
        {
            var transport = new FakeTransport { HelloFailure = new TransportException(TransportErrorCodes.Unavailable) };
            var starts = 0;
            using var process = new TransportProcess(Settings(TransportMode.Tsnet, PingExecutable), new FakeBroker(), transport, blocked, TestPipe, Verifier(), null, _ =>
            {
                starts++;
                return null;
            });

            var failure = await Assert.ThrowsAsync<TransportException>(() => process.EnsureRunningAsync(CancellationToken.None));

            Assert.Equal(TransportErrorCodes.DataUnwritable, failure.Code);
            Assert.IsAssignableFrom<IOException>(failure.InnerException);
            Assert.Equal(0, starts);
        }
        finally
        {
            File.Delete(blocked);
        }
    }

    [Fact]
    public async Task Once_its_own_transport_has_exited_one_another_copy_started_is_trusted_again()
    {
        // Windows process ids are multiples of four: this is neither the test process nor its child.
        const int otherCopyId = 4097;
        var inspector = new FakeProcessInspector(ownLevel: 0x2000);
        inspector.Processes[otherCopyId] = new FakeProcess(new InspectedProcess(PingExecutable, 0x2000));
        using var verifier = new TransportServerVerifier(PingExecutable, inspector);
        var transport = new FakeTransport { Mode = "tsnet", HelloFailure = new TransportException(TransportErrorCodes.Unavailable) };
        var dataDirectory = Path.Combine(Path.GetTempPath(), "1salem-connect-app-tests", Guid.NewGuid().ToString("N"));
        Process? started = null;
        using var process = new TransportProcess(Settings(TransportMode.Tsnet, PingExecutable), new FakeBroker(), transport, dataDirectory, TestPipe, verifier, null, startInfo =>
        {
            // A local stand-in that runs until it is ended; from now on "it" answers on the pipe.
            startInfo.ArgumentList.Clear();
            foreach (var argument in new[] { "-n", "30", "127.0.0.1" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            transport.HelloFailure = null;
            return started = Process.Start(startInfo);
        });
        try
        {
            await process.EnsureRunningAsync(CancellationToken.None);
            Assert.Throws<TransportServerUntrustedException>(() => verifier.VerifyServerProcess(otherCopyId));

            started!.Kill();
            Assert.True(started.WaitForExit(TestTimeout));
            Assert.True(process.UsedTransportsExited);

            // Another copy of the app started a transport meanwhile. The next use lets go of the
            // exited one first, instead of refusing that one as untrusted until a restart.
            await process.EnsureRunningAsync(CancellationToken.None);

            verifier.VerifyServerProcess(otherCopyId);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Closing_the_job_ends_the_process_placed_in_it()
    {
        // A local child that would otherwise run for about half a minute.
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c ping -n 30 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            using (var job = KillOnCloseJob.Create())
            {
                job.Assign(child);
                Assert.False(child.WaitForExit(TimeSpan.FromMilliseconds(200)));
            }

            // What Windows does when this app ends in any way, a crash or a kill included.
            Assert.True(child.WaitForExit(TestTimeout));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public void Node_names_and_hostnames_fit_the_transport_rules()
    {
        var ownerId = TestIds.NewOwnerId();
        using var device = TestIds.NewDevice();

        Assert.Matches(new Regex("^[A-Za-z0-9_-]{1,64}$"), TransportNodeNames.ForOwner(ownerId));
        Assert.Matches(new Regex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$"), TransportNodeNames.Hostname(device.DeviceId));
        Assert.Throws<ArgumentException>(() => TransportNodeNames.ForOwner("../../etc"));
    }

    [Fact]
    public void Log_signals_follow_the_newest_line_for_the_session_only()
    {
        string[] log =
        [
            "2026-09-24T10:00:00Z friend: session ses_a could not reach the host bridge: timeout",
            "2026-09-24T10:00:05Z friend: session ses_b connection ended (1 bytes up, 1 bytes down)",
            "2026-09-24T10:00:09Z friend: session ses_a refreshed"
        ];

        Assert.Equal(SessionReachability.Unreachable, TransportLogSignals.Latest(log, "ses_a").Reachability);
        Assert.Equal(SessionReachability.Reached, TransportLogSignals.Latest(log, "ses_b").Reachability);
        Assert.Equal(SessionReachability.Unknown, TransportLogSignals.Latest(log, "ses_c").Reachability);
    }

    [Fact]
    public void A_connection_accepted_after_a_failure_counts_as_reached()
    {
        // The transport writes "accepted" as soon as the owner's side lets a connection in, long
        // before a working game's connection ends.
        string[] log =
        [
            "2026-09-24T10:00:00Z friend: session ses_a could not reach the host bridge: timeout",
            "2026-09-24T10:00:05Z friend: session ses_a connection accepted",
            "2026-09-24T10:00:06Z friend: session ses_b connection not accepted: host refused the connection"
        ];

        var signal = TransportLogSignals.Latest(log, "ses_a");

        Assert.Equal(SessionReachability.Reached, signal.Reachability);
        Assert.Equal(log[1], signal.Line);
        Assert.Equal(SessionReachability.Refused, TransportLogSignals.Latest(log, "ses_b").Reachability);
    }

    [Fact]
    public void Poll_backoff_doubles_up_to_its_ceiling()
    {
        var backoff = new Services.PollBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));

        var waits = Enumerable.Range(0, 6).Select(_ => backoff.Next().TotalSeconds).ToList();

        Assert.Equal([5, 10, 20, 40, 60, 60], waits);
    }

    private static TransportServerVerifier Verifier() => TransportServerVerifier.ForTransport(MissingExecutable);

    private static ConnectAppSettings Settings(TransportMode mode, string transportExecutablePath = MissingExecutable) => new()
    {
        BrokerUrl = new Uri("https://broker.test/"),
        DevelopmentMode = mode == TransportMode.Fake,
        TransportMode = mode,
        FakeNodeId = mode == TransportMode.Fake ? "fake-friend-1" : null,
        TransportExecutablePath = transportExecutablePath
    };

    private static string ValueOf(List<string> arguments, string flag)
    {
        var index = arguments.IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"{flag} is missing");
        return arguments[index + 1];
    }
}
