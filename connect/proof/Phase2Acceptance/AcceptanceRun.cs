using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Identity;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.Core.Identity;
using ServerManager.Connect.Core.Diagnostics;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Connect;
using TsnetSmoke;

namespace Phase2Acceptance;

internal sealed record AcceptanceCheck(string Name, bool Passed, string Detail);
internal sealed record AgentDecision(string NodeId, string Reason, DateTimeOffset ObservedAt);
internal sealed class RunNode(string role, string tag, string hostname, string marker)
{
    public string Role { get; } = role;
    public string Tag { get; } = tag;
    public string Hostname { get; } = hostname;
    public string Marker { get; } = marker;
    public string? NodeId { get; set; }
    public bool SeenByApi { get; set; }
    public bool Removed { get; set; }
}

internal sealed partial class AcceptanceRun : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly AcceptanceOptions _options;
    private readonly CancellationToken _stop;
    private readonly SmokeJob _job = SmokeJob.Create();
    private readonly HttpClient _agentHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromMinutes(3) };
    private readonly HttpClient _tailnetHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentQueue<AgentDecision> _decisions = new();
    private readonly ConcurrentQueue<string> _agentDiagnosticLines = new();
    private readonly List<AcceptanceCheck> _checks = [];
    private readonly Dictionary<string, bool> _keys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deviceReads = new(StringComparer.Ordinal);
    private readonly List<RunNode> _nodes = [];
    private readonly List<Task> _pumps = [];
    private readonly object _ledgerLock = new();
    private readonly Dictionary<int, (DateTime Start, string Image)> _processIdentities = [];
    private Process? _agent;
    private SmokeTailnetApi? _api;
    private DeviceIdentity? _identity;
    private BrokerClient? _broker;
    private TransportServerVerifier? _verifier;
    private PipeTransportClient? _transport;
    private TransportProcess? _friendProcess;
    private EnrollmentCoordinator? _enrollment;
    private SessionService? _sessions;
    private IdentityEndpoint? _gameA;
    private IdentityEndpoint? _gameB;
    private RunNode? _friendNode;
    private readonly RunNode _hostNode;
    private readonly RunNode _probeNode;
    private Guid _serverA;
    private Guid _serverB;
    private string? _ownerId;
    private string? _membershipA;
    private string? _membershipB;

    public AcceptanceRun(AcceptanceOptions options, CancellationToken stop)
    {
        _options = options;
        _stop = stop;
        _agentHttp.BaseAddress = options.ApiUrl;
        var machine = new string(Environment.MachineName.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        var hostname = "1salem-" + (machine.Length == 0 ? "windows-pc" : machine);
        hostname = hostname[..Math.Min(63, hostname.Length)].TrimEnd('-');
        _hostNode = new("host", TailscaleApiProvisioner.HostTag, hostname,
            Path.Combine(AgentRoot, "connect", "host-transport", "nodes", "host", "1salem-node.json"));
        _probeNode = new("probe", TailscaleApiProvisioner.FriendTag, "1salem-p2-probe-" + RunId,
            Work("probe-state", "nodes", "probe", "1salem-node.json"));
        _nodes.AddRange([_hostNode, _probeNode]);
        SaveLedger();
    }

    private string RunId { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    private DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    private string AgentRoot => Work("agent");
    private string PipePrefix => "1Salem.Connect.Acceptance." + RunId;
    private string Work(params string[] parts) => Path.Combine([_options.Work, .. parts]);
    public bool Passed => _checks.Count > 0 && _checks.All(check => check.Passed);

    public void Record(string name, bool passed, string detail)
    {
        // Callers supply bounded metadata, never an exception message, request/response or key.
        _checks.Add(new(name, passed, detail));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
    }

    private void Require(bool condition, string name, string detail)
    {
        Record(name, condition, detail);
        if (!condition) throw new AcceptanceFailureException();
    }

    private async Task<T> AgentAsync<T>(HttpMethod method, string path, object? body = null,
        CancellationToken? cancellation = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _agentHttp.SendAsync(request, cancellation ?? _stop);
        if (!response.IsSuccessStatusCode)
            throw new AgentResponseException((int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellation ?? _stop)
            ?? throw new InvalidDataException("Empty Agent response.");
    }

    private Task<OperationResult> ActionAsync(string path) => AgentAsync<OperationResult>(HttpMethod.Post, path);

    private async Task StartAgentAsync()
    {
        if (_agent is { HasExited: false }) throw new InvalidOperationException("Agent is already running.");
        var args = new[]
        {
            "--connect-acceptance", "--data-root", AgentRoot, "--api-url", _options.ApiUrl.AbsoluteUri.TrimEnd('/'),
            "--pipe-name", PipePrefix + ".Agent", "--connect-broker-url", _options.Broker.AbsoluteUri.TrimEnd('/'),
            "--connect-authz-pipe", PipePrefix + ".Authz", "--connect-control-pipe", PipePrefix + ".Control",
            "--connect-transport", _options.HostTransport
        };
        _agent = StartProcess(_options.Agent, args, agentOutput: true);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            _stop.ThrowIfCancellationRequested();
            if (_agent.HasExited) throw new InvalidOperationException("Isolated Agent exited during startup.");
            var keyPath = AgentTransportDefaults.ResolveLocalAgentKeyPath(AgentRoot);
            if (File.Exists(keyPath))
            {
                var key = (await File.ReadAllTextAsync(keyPath, _stop)).Trim();
                if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid isolated Agent bearer.");
                _agentHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
                try
                {
                    await AgentAsync<ConnectStatusResponse>(HttpMethod.Get, "/api/v1/connect/status");
                    Record("isolated Agent process started", true, "PID " + _agent.Id);
                    return;
                }
                catch (HttpRequestException) { }
                catch (AgentResponseException) { }
            }
            await Task.Delay(500, _stop);
        }
        throw new TimeoutException("Isolated Agent startup timed out.");
    }

    private async Task StopAgentAsync()
    {
        if (_options.SystemService)
        {
            await RemoveServiceAsync();
            ReadAgentJournal();
            SaveLedger();
            return;
        }
        if (_agent is null) return;
        if (!_agent.HasExited)
        {
            await StopOwnedProcessAsync(_agent);
        }
        _agent.Dispose();
        _agent = null;
        ReadAgentJournal();
        RecoverMarkers();
        SaveLedger();
    }

    private Process StartProcess(string executable, IEnumerable<string> args,
        bool agentOutput = false, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = _options.Work
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("TS_", StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith("TSNET_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
        if (environment is not null)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Acceptance process did not start.");
        _job.Assign(process);
        // MainModule can be temporarily null before a newly started Windows process has
        // loaded its image. The exact requested image is checked against the held PID/start
        // identity before termination; never dereference MainModule during startup.
        _processIdentities[process.Id] = (process.StartTime, Path.GetFullPath(executable));
        if (environment is not null)
            foreach (var name in environment.Keys) start.Environment.Remove(name);
        _pumps.Add(PumpAsync(process.StandardOutput, agentOutput));
        _pumps.Add(PumpAsync(process.StandardError, agentOutput));
        return process;
    }

    private async Task StopOwnedProcessAsync(Process process)
    {
        if (process.HasExited) return;
        if (!_processIdentities.TryGetValue(process.Id, out var identity) || process.StartTime != identity.Start ||
            !string.Equals(process.MainModule?.FileName, identity.Image, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Owned process identity changed; refusing termination.");
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    }

    private async Task PumpAsync(StreamReader reader, bool agentOutput)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!agentOutput) continue;
            var safe = SecretRedactor.Redact(line);
            _agentDiagnosticLines.Enqueue(safe[..Math.Min(safe.Length, 2000)]);
            while (_agentDiagnosticLines.Count > 300) _agentDiagnosticLines.TryDequeue(out _);
            // Keep only authorization decision metadata from production Agent logging.
            var match = Regex.Match(line, @"1Salem Connect refused a connection from node ([A-Za-z0-9]{1,64}): ([A-Za-z]{1,64})\.");
            if (match.Success) _decisions.Enqueue(new(match.Groups[1].Value, match.Groups[2].Value, DateTimeOffset.UtcNow));
        }
    }

    private async Task<ConnectStatusResponse> WaitReadyAsync()
    {
        var end = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < end)
        {
            var status = await AgentAsync<ConnectStatusResponse>(HttpMethod.Get, "/api/v1/connect/status");
            ReadAgentJournal();
            RecoverMarkers();
            SaveLedger();
            if (status.State == ConnectSetupState.Ready && status.Policy == ConnectPolicyState.Safe &&
                status.HostNode == ConnectHostNodeState.Enrolled && status.BridgeRunning && status.RevocationChannel)
                return status;
            if (status.HostNode == ConnectHostNodeState.TailnetLockUnsupported ||
                status.Policy is ConnectPolicyState.Unsafe or ConnectPolicyState.NotPermitted or ConnectPolicyState.Unverifiable)
                throw new InvalidOperationException("Agent preflight refused the tailnet.");
            await Task.Delay(1000, _stop);
        }
        throw new TimeoutException("Agent did not become ready.");
    }

    private void StartFriend()
    {
        var root = Work("friend");
        _identity = new DeviceIdentity(new ConnectIdentityStore(Path.Combine(root, "identity"),
            ConnectIdentityKind.Device).LoadOrCreate(), TimeProvider.System);
        var settings = new ConnectAppSettings
        {
            BrokerUrl = _options.Broker, DevelopmentMode = true, TransportMode = TransportMode.Tsnet,
            TransportExecutablePath = _options.FriendTransport
        };
        _broker = new BrokerClient(settings.BrokerUrl, true, _identity);
        _verifier = TransportServerVerifier.ForTransport(settings.TransportExecutablePath);
        var pipe = PipePrefix + ".Friend";
        _transport = PipeTransportClient.ForPipe(pipe, _verifier);
        var log = new DiagnosticsLog(new SystemAppClock());
        _friendProcess = new TransportProcess(settings, _broker, _transport,
            Path.Combine(root, "transport"), pipe, _verifier, log);
        _enrollment = new EnrollmentCoordinator(_broker, _transport, _friendProcess, _identity,
            new ConsumedEnrollments(Path.Combine(root, "consumed-enrollments.json"), log), log);
        _sessions = new SessionService(_broker, _transport, _friendProcess, log);
        _friendNode = new RunNode("friend", TailscaleApiProvisioner.FriendTag,
            TransportNodeNames.Hostname(_identity.DeviceId),
            Path.Combine(root, "transport", "state", "nodes", TransportNodeNames.ForOwner(_ownerId!), "1salem-node.json"));
        _nodes.Add(_friendNode);
        SaveLedger();
    }

    private void ReadAgentJournal()
    {
        var path = Path.Combine(AgentRoot, "connect-acceptance-resources.jsonl");
        if (!File.Exists(path)) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // An append can be in flight; the next scan sees it whole.
            using (document)
            {
                var root = document.RootElement;
                var id = root.GetProperty("id").GetString();
                var at = root.GetProperty("timestamp").GetDateTimeOffset();
                if (id is null || id.Length is < 1 or > 64 || !id.All(char.IsAsciiLetterOrDigit) ||
                    at < StartedAt.AddMinutes(-1) || at > DateTimeOffset.UtcNow.AddMinutes(1))
                    throw new InvalidDataException("Unexpected acceptance resource metadata.");
                switch (root.GetProperty("kind").GetString())
                {
                    case "keyCreated": _keys.TryAdd(id, false); break;
                    case "keyDeleted": if (_keys.ContainsKey(id)) _keys[id] = true; break;
                    case "deviceRead": _deviceReads.Add(id); break;
                    case "deviceDeleted":
                        foreach (var node in _nodes.Where(node => node.NodeId == id)) node.Removed = true;
                        break;
                }
            }
        }
    }

    private void RecoverMarkers()
    {
        // Under SYSTEM the host marker is in SYSTEM-only state; its ID comes from the Agent's status.
        if (_options.SystemService) return;
        foreach (var node in _nodes)
        {
            if (!File.Exists(node.Marker)) continue;
            AcceptanceOptions.AssertNoReparse(node.Marker);
            using var document = JsonDocument.Parse(File.ReadAllBytes(node.Marker));
            var marker = document.RootElement;
            var id = marker.GetProperty("nodeId").GetString();
            var hostname = marker.GetProperty("hostname").GetString();
            if (!string.Equals(hostname, node.Hostname, StringComparison.OrdinalIgnoreCase) ||
                id is null || id.Length is < 1 or > 64 || !id.All(char.IsAsciiLetterOrDigit) ||
                node.NodeId is not null && node.NodeId != id)
                throw new InvalidDataException("An isolated node marker did not match this run.");
            node.NodeId = id;
        }
    }

    private void SaveLedger()
    {
        lock (_ledgerLock)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                runId = RunId, startedAt = StartedAt,
                nodes = _nodes.Select(node => new { node.Role, node.Tag, node.Hostname, node.NodeId, node.SeenByApi, node.Removed }),
                keys = _keys.Select(pair => new { keyId = pair.Key, removed = pair.Value })
            }, Json);
            File.WriteAllBytes(Work("nodes.json.new"), bytes);
            File.Move(Work("nodes.json.new"), Work("nodes.json"), true);
        }
    }

    private async Task<SmokeDevice> VerifyNodeAsync(RunNode node)
    {
        RecoverMarkers();
        SaveLedger();
        if (node.NodeId is null) throw new InvalidDataException("No isolated node ID was recorded.");
        var answer = await _api!.GetDeviceAsync(node.NodeId);
        var device = answer.Device;
        Require(device is not null && IsRunNode(device, node), node.Role + " is the run's real tagged device",
            "Devices API HTTP " + answer.Status);
        node.SeenByApi = true;
        SaveLedger();
        Require(string.IsNullOrEmpty(device!.TailnetLockError), node.Role + " has no Tailnet Lock error", "Devices API field checked");
        return device;
    }

    private bool IsRunNode(SmokeDevice device, RunNode node) => device.NodeId == node.NodeId &&
        string.Equals(device.Hostname, node.Hostname, StringComparison.OrdinalIgnoreCase) &&
        device.Tags.Count == 1 && string.Equals(device.Tags[0], node.Tag, StringComparison.OrdinalIgnoreCase) &&
        device.IsEphemeral != true && device.Created is { } created &&
        created >= StartedAt.AddMinutes(-5) && created <= DateTimeOffset.UtcNow.AddMinutes(1);

    public void WriteResult()
    {
        SaveLedger();
        File.WriteAllBytes(Work("result.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            runId = RunId,
            mode = _options.SystemService ? "Agent-hosted Connect SYSTEM-service acceptance" : "Phase2 actual Agent-hosted real tsnet acceptance",
            startedAt = StartedAt,
            finishedAt = DateTimeOffset.UtcNow, passed = Passed, temporaryNodesRemaining = _temporaryNodesRemaining,
            checks = _checks, agentDecisions = _decisions.ToArray()
        }, Json));
    }

    public async Task CaptureFailureDiagnosticsAsync()
    {
        IReadOnlyList<string> friend = [];
        if (_transport is not null && _friendProcess is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { friend = (await _transport.DiagnosticsAsync(timeout.Token)).Log.Select(SecretRedactor.Redact).ToArray(); }
            catch { /* Diagnostics must never delay or replace resource cleanup. */ }
        }
        File.WriteAllBytes(Work("failure-diagnostics.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { agent = _agentDiagnosticLines.ToArray(), friend }, Json));
    }

    public void Dispose()
    {
        _friendProcess?.Dispose(); _transport?.Dispose(); _verifier?.Dispose(); _broker?.Dispose(); _identity?.Dispose();
        _agentHttp.Dispose(); _tailnetHttp.Dispose(); _job.Dispose();
    }

    private sealed class AcceptanceFailureException : Exception;
    private sealed class AgentResponseException(int status) : Exception { public int Status { get; } = status; }
}
