using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ServerManager.Connect.Setup;
using AppProtocol = ServerManager.Connect.App.Updates.UpdateProtocol;

namespace ServerManager.Connect.Setup.Tests;

/// <summary>
/// Setup's update mode: the new build replaces the installed one only when it starts, the
/// previous build comes back when it does not, nothing is half-installed, older or unexpected
/// installers change nothing, and the friend's own files are never touched.
/// </summary>
public sealed class ConnectUpdateTests : IDisposable
{
    private static readonly UpdateTimings Fast = new(
        TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "1salem-connect-setup-tests", Guid.NewGuid().ToString("N"));
    private readonly string _installRoot;
    private readonly string _userData;
    private readonly FakeUpdateHost _host = new();

    public ConnectUpdateTests()
    {
        _installRoot = Path.Combine(_root, "Program Files", "1Salem Connect");
        _userData = Path.Combine(_root, "Users", "friend", "AppData", "Local", "1Salem Connect");
        WriteApp(_installRoot, "1.5", 11, "old app");
        foreach (var (name, text) in UserFiles)
        {
            var path = Path.Combine(_userData, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }

    private static IReadOnlyDictionary<string, string> UserFiles { get; } = new Dictionary<string, string>
    {
        [Path.Combine("identity", "identity.v1.json")] = "{\"protected\":\"device key\"}",
        ["consumed-enrollments.json"] = "{\"version\":2,\"memberships\":[\"mem_a\"]}",
        ["preferences.json"] = "{\"theme\":\"Dark\"}",
        [Path.Combine("transport", "state", "tailscaled.state")] = "node state"
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void The_new_build_replaces_the_old_one_once_it_starts_and_user_data_is_untouched()
    {
        var before = Snapshot(_userData);
        _host.SignalOnLaunch = true;

        var result = Run(Request(12), Payload("1.5", 12, "new app"));

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal((11, 12), (result.FromBuild, result.ToBuild));
        Assert.Equal("new app", File.ReadAllText(Path.Combine(_installRoot, "1Salem.Connect.exe")));
        Assert.Equal(("1.5", 12), ConnectInstallation.ReadBuild(_installRoot)!.Value);
        Assert.Equal([Path.Combine(_installRoot, "1Salem.Connect.exe")], _host.Launches);
        Assert.Equal(1, _host.Registrations);
        Assert.Empty(Leftovers());
        Assert.Equal(before, Snapshot(_userData));
        Assert.Equal("Updated", _host.Results.Single().Outcome.ToString());
    }

    [Fact]
    public void A_new_build_that_does_not_start_is_replaced_by_the_previous_one()
    {
        var before = Snapshot(_installRoot);
        _host.SignalOnLaunch = false;

        var result = Run(Request(12), Payload("1.5", 12, "new app"));

        Assert.Equal(UpdateOutcome.RolledBack, result.Outcome);
        Assert.Equal(before, Snapshot(_installRoot));
        Assert.Empty(Leftovers());
        Assert.Equal(2, _host.Registrations);

        // The result is written before the old app opens again, so the app can say why.
        Assert.Equal(["launch", "result", "launch"], _host.Order);
        Assert.Equal(UpdateOutcome.RolledBack, _host.Results.Single().Outcome);
    }

    [Fact]
    public void An_older_installer_changes_nothing()
    {
        var before = Snapshot(_installRoot);

        var result = Run(Request(10), Payload("1.5", 10, "older app"));

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(before, Snapshot(_installRoot));
        Assert.Empty(Leftovers());
        Assert.Equal(0, _host.Registrations);
        Assert.Equal([Path.Combine(_installRoot, "1Salem.Connect.exe")], _host.Launches);
    }

    [Fact]
    public void An_installer_that_is_not_the_build_the_app_verified_changes_nothing()
    {
        var before = Snapshot(_installRoot);

        var result = Run(Request(12), Payload("1.5", 13, "unexpected app"));

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(before, Snapshot(_installRoot));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void An_incomplete_installer_changes_nothing()
    {
        var before = Snapshot(_installRoot);

        var result = Run(Request(12), Payload("1.5", 12, "new app", leaveOut: "1Salem.Connect.Transport.exe"));

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(before, Snapshot(_installRoot));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void Another_open_window_blocks_the_update_without_changes()
    {
        var before = Snapshot(_installRoot);
        _host.Running.Add(4243);

        var result = Run(Request(12), Payload("1.5", 12, "new app"));

        Assert.Equal(UpdateOutcome.Blocked, result.Outcome);
        Assert.Equal(before, Snapshot(_installRoot));
        Assert.Empty(Leftovers());
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public void A_crash_between_the_two_renames_is_put_right_next_time()
    {
        var installation = new ConnectInstallation(_installRoot);
        var previous = _installRoot + ".old-1a2b3c4d";
        Directory.Move(_installRoot, previous);
        Directory.CreateDirectory(_installRoot + ".new-99999999");

        installation.RecoverInterrupted();

        Assert.Equal(("1.5", 11), ConnectInstallation.ReadBuild(_installRoot)!.Value);
        Assert.Empty(Leftovers());
    }

    [Theory]
    [InlineData("1.5", 12, "1.5", 9, 1)]
    [InlineData("1.5", 12, "1.5", 12, 0)]
    [InlineData("1.6", 1, "1.5", 99, 1)]
    [InlineData("1.5", 10, "1.5", 11, -1)]
    public void Builds_are_compared_by_version_then_build(string leftVersion, int leftBuild, string rightVersion, int rightBuild, int expected) =>
        Assert.Equal(expected, Math.Sign(ConnectInstallation.Compare((leftVersion, leftBuild), (rightVersion, rightBuild))));

    [Fact]
    public void The_update_request_is_read_strictly()
    {
        var token = AppProtocol.NewToken();
        var arguments = AppProtocol.Arguments(4242, ServerManager.Connect.App.Updates.ConnectBuild.Create("1.5", 12), token).Split(' ');

        Assert.Equal(new UpdateRequest(4242, "1.5", 12, token), UpdateRequest.Parse(arguments));
        Assert.Null(UpdateRequest.Parse(["--install"]));
        Assert.Null(UpdateRequest.Parse(["--update", "--wait-pid", "1", "--expected-version", "1.5", "--expected-build", "12", "--token", "..\\x"]));
        Assert.Null(UpdateRequest.Parse(["--update", "--wait-pid", "1", "--expected-version", "1.5.12", "--expected-build", "12", "--token", token]));
        Assert.Null(UpdateRequest.Parse(["--update", "--wait-pid", "1", "--expected-version", "1.5", "--token", token]));
    }

    [Fact]
    public void The_app_and_Setup_speak_the_same_protocol()
    {
        Assert.Equal(AppProtocol.UpdateArgument, UpdateProtocol.UpdateArgument);
        Assert.Equal(AppProtocol.WaitPidArgument, UpdateProtocol.WaitPidArgument);
        Assert.Equal(AppProtocol.ExpectedVersionArgument, UpdateProtocol.ExpectedVersionArgument);
        Assert.Equal(AppProtocol.ExpectedBuildArgument, UpdateProtocol.ExpectedBuildArgument);
        Assert.Equal(AppProtocol.TokenArgument, UpdateProtocol.TokenArgument);
        Assert.Equal(AppProtocol.EventPrefix, UpdateProtocol.EventPrefix);
        Assert.Equal(AppProtocol.ResultDirectory, UpdateProtocol.ResultDirectory);
        Assert.Equal(
            ServerManager.Connect.App.Updates.InstallationDetector.UninstallKeyPath,
            ConnectInstaller.UninstallKeyPath);
        Assert.Equal(
            ServerManager.Connect.App.Updates.InstallationDetector.UninstallerFileName,
            ConnectInstaller.UninstallerExecutable);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private UpdateRunResult Run(UpdateRequest request, byte[] payload) =>
        new ConnectUpdate(new ConnectInstallation(_installRoot), _host, Fast)
            .Run(request, () => new MemoryStream(payload));

    private static UpdateRequest Request(int build) => new(4242, "1.5", build, AppProtocol.NewToken());

    private static void WriteApp(string folder, string version, int build, string marker)
    {
        Directory.CreateDirectory(Path.Combine(folder, "ar"));
        File.WriteAllText(Path.Combine(folder, "1Salem.Connect.exe"), marker);
        File.WriteAllText(Path.Combine(folder, "1Salem.Connect.Transport.exe"), marker + " transport");
        File.WriteAllText(Path.Combine(folder, "1Salem.Connect.settings.json"), "{\"brokerUrl\":\"https://broker.test/\"}");
        File.WriteAllText(Path.Combine(folder, "1Salem.Connect.Setup.exe"), marker + " uninstaller");
        File.WriteAllText(Path.Combine(folder, "ar", "1Salem.Connect.resources.dll"), marker + " arabic");
        File.WriteAllText(
            Path.Combine(folder, "build-info.json"),
            "﻿{\"productVersion\":\"" + version + "\",\"buildRevision\":" + build + "}",
            new UTF8Encoding(false));
    }

    private byte[] Payload(string version, int build, string marker, string? leaveOut = null)
    {
        var folder = Path.Combine(_root, "payload-" + Guid.NewGuid().ToString("N"));
        WriteApp(folder, version, build, marker);
        if (leaveOut is not null)
        {
            File.Delete(Path.Combine(folder, leaveOut));
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                // Windows PowerShell's Compress-Archive writes backslashes, as the real payload has.
                zip.CreateEntryFromFile(file, Path.GetRelativePath(folder, file));
            }
        }

        Directory.Delete(folder, recursive: true);
        return buffer.ToArray();
    }

    private IEnumerable<string> Leftovers() =>
        Directory.EnumerateDirectories(Path.GetDirectoryName(_installRoot)!)
            .Where(path => !string.Equals(path, _installRoot, StringComparison.OrdinalIgnoreCase));

    private static string Snapshot(string folder) =>
        string.Join(
            "\n",
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.GetRelativePath(folder, path) + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));

    private sealed class FakeUpdateHost : IUpdateHost
    {
        public bool SignalOnLaunch { get; set; } = true;

        public List<int> Running { get; } = [];

        public List<string> Launches { get; } = [];

        public List<string> Order { get; } = [];

        public List<UpdateRunResult> Results { get; } = [];

        public int Registrations { get; private set; }

        private bool _signalled;

        public bool WaitForExit(int processId, TimeSpan timeout) => true;

        public IReadOnlyList<int> ProcessesUnder(string folder) => Running;

        public void Kill(int processId) => Running.Remove(processId);

        public void LaunchAsUser(string executable)
        {
            Launches.Add(executable);
            Order.Add("launch");
            _signalled = SignalOnLaunch;
        }

        public void Register() => Registrations++;

        public bool PrepareStartSignal(string token) => true;

        public bool WaitForStartSignal(TimeSpan timeout) => _signalled;

        public void WriteResult(string token, UpdateRunResult result)
        {
            Order.Add("result");
            Results.Add(result);
        }
    }
}
