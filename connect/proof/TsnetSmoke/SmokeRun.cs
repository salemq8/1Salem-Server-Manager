using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.App.ViewModels;
using ServerManager.Connect.Core.Diagnostics;
using ServerManager.Infrastructure.Connect;

namespace TsnetSmoke;

/// <summary>The friend app's services as the run built them.</summary>
internal sealed record FriendApp(
    BrokerClient Broker,
    PipeTransportClient Transport,
    TransportProcess Process,
    SmokeClock Clock,
    DiagnosticsLog Log,
    MainViewModel Main,
    EnrollmentCoordinator Enrollment,
    string PipeName);

/// <summary>
/// One tailnet node the run enrolls. The hostname is chosen before the node exists and the node
/// id is recorded the moment it is known, because the cleanup deletes nothing else.
/// </summary>
internal sealed class SmokeNode(string role, string tag)
{
    public string Role { get; } = role;

    /// <summary>The one tag this node's key carries, and so the device's whole tag set.</summary>
    public string Tag { get; } = tag;

    public string? Hostname { get; set; }

    /// <summary>The tsnet node directory: tailscaled.state, the log buffers and the 1salem-node.json marker.</summary>
    public string? StateDirectory { get; set; }

    public string? NodeId { get; set; }

    /// <summary>
    /// The owner's OAuth client read this device once (HTTP 200). Only then does a later 404 mean
    /// the device is gone: whether a client carrying only the host tag may see client-tagged devices
    /// is not documented, so a 404 for a device it never saw could just mean "hidden".
    /// </summary>
    public bool SeenByApi { get; set; }

    /// <summary>The API confirmed it gone (GET 404 after the DELETE, or before it).</summary>
    public bool Removed { get; set; }
}

/// <summary>
/// Everything one run creates that the cleanup must find again, recorded the moment it exists:
/// above all the node ids and key ids it minted, because the cleanup deletes nothing else. The
/// same record is kept in <c>nodes.json</c> in the work directory, rewritten on every change, so
/// a run that was killed before its cleanup finished still says what to remove by hand.
/// </summary>
internal sealed class SmokeRun
{
    private const string LedgerName = "nodes.json";

    private readonly List<IDisposable> _owned = [];
    private readonly List<IAsyncDisposable> _ownedAsync = [];
    private readonly Dictionary<string, HashSet<string>> _evidenceLines = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _evidenceGate = new(1, 1);
    private readonly object _ledgerGate = new();
    private readonly List<string> _keyIds = [];
    private readonly HashSet<string> _removedKeyIds = new(StringComparer.Ordinal);

    public SmokeRun(SmokeOptions options, SmokeJob job)
    {
        Options = options;
        Job = job;
        Host = new SmokeNode("host", options.HostTag)
        {
            Hostname = $"1salem-smoke-host-{RunId}",
            StateDirectory = Path.Combine(Work("host-state"), "nodes", "host"),
        };
        Friend = new SmokeNode("friend", options.ClientTag);
        Probe = new SmokeNode("probe", options.ClientTag)
        {
            Hostname = $"1salem-smoke-probe-{RunId}",
            StateDirectory = Path.Combine(Work("probe-state"), "nodes", "probe"),
        };
        SaveLedger();
    }

    public SmokeOptions Options { get; }

    /// <summary>The host transport and the probe run in it, so neither outlives the driver.</summary>
    public SmokeJob Job { get; }

    /// <summary>Six hex characters. Every hostname the run chooses carries it.</summary>
    public string RunId { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    /// <summary>T0: a device the run may delete was created after this (allowing for clock skew).</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>For the Tailscale API only. It follows no redirects, so the credential and tokens only go to the fixed API host.</summary>
    public HttpClient TailnetHttp { get; } = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public TailscaleApiProvisioner? Provisioner { get; set; }

    public SmokeTailnetApi? Api { get; set; }

    public Process? HostTransport { get; set; }

    public RawPipeClient? HostControl { get; set; }

    public TransportLogCollector? HostLog { get; set; }

    public FriendApp? App { get; set; }

    public TransportLogCollector? FriendLog { get; set; }

    public string? HostIPv4 { get; set; }

    public SmokeNode Host { get; }

    public SmokeNode Friend { get; }

    public SmokeNode Probe { get; }

    /// <summary>Host first: the departed-peer check needs the host gone and the probe still there.</summary>
    public IReadOnlyList<SmokeNode> Nodes => [Host, Friend, Probe];

    public IReadOnlyList<string> KeyIds
    {
        get
        {
            lock (_ledgerGate)
            {
                return _keyIds.ToList();
            }
        }
    }

    public string Work(string name) => Path.Combine(Options.WorkDirectory, name);

    /// <summary>The friend's hostname and node directory follow from its device and owner ids, known only once they exist.</summary>
    public void Name(SmokeNode node, string hostname, string stateDirectory)
    {
        node.Hostname = hostname;
        node.StateDirectory = stateDirectory;
        SaveLedger();
    }

    /// <summary>Records a node id and writes the ledger before anything else happens.</summary>
    public void RecordNodeId(SmokeNode node, string nodeId)
    {
        node.NodeId = nodeId;
        SaveLedger();
    }

    public void MarkRemoved(SmokeNode node)
    {
        node.Removed = true;
        SaveLedger();
    }

    public void RecordKey(string keyId)
    {
        lock (_ledgerGate)
        {
            _keyIds.Add(keyId);
        }

        SaveLedger();
    }

    public void MarkKeyRemoved(string keyId)
    {
        lock (_ledgerGate)
        {
            _removedKeyIds.Add(keyId);
        }

        SaveLedger();
    }

    /// <summary>The console's Machines page filtered to both tags: where to look for anything the run left behind.</summary>
    public string MachinesFilter => $"https://console.tailscale.com/admin/machines?q=managedby:{Options.HostTag},{Options.ClientTag}";

    /// <summary>How to remove <paramref name="node"/> by hand when the run could not.</summary>
    public string ManualRemoval(SmokeNode node) =>
        $"remove it by hand: {MachinesFilter}, find {node.Hostname ?? "the device"} (node {node.NodeId ?? "id unknown"}), " +
        "open its ... menu, select Remove, then Remove machine";

    public T Own<T>(T item)
        where T : IDisposable
    {
        _owned.Add(item);
        return item;
    }

    public T OwnAsync<T>(T item)
        where T : IAsyncDisposable
    {
        _ownedAsync.Add(item);
        return item;
    }

    /// <summary>Releases what the run owns, newest first.</summary>
    public async Task DisposeOwnedAsync()
    {
        for (var i = _ownedAsync.Count - 1; i >= 0; i--)
        {
            await _ownedAsync[i].DisposeAsync();
        }

        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            _owned[i].Dispose();
        }

        _ownedAsync.Clear();
        _owned.Clear();
    }

    /// <summary>
    /// Appends lines to an evidence file in the work directory, each line once. The transports'
    /// logs are redacted already; they are redacted again here for defence in depth. The log
    /// collectors append from their own tasks, so appends are serialized.
    /// </summary>
    public async Task AppendEvidenceAsync(string fileName, IEnumerable<string> lines)
    {
        await _evidenceGate.WaitAsync();
        try
        {
            if (!_evidenceLines.TryGetValue(fileName, out var seen))
            {
                _evidenceLines[fileName] = seen = new HashSet<string>(StringComparer.Ordinal);
            }

            await File.AppendAllLinesAsync(Work(fileName), lines.Where(seen.Add).Select(SecretRedactor.Redact).ToList());
        }
        finally
        {
            _evidenceGate.Release();
        }
    }

    /// <summary>Written to a temporary file and moved into place, so a kill never leaves half a ledger.</summary>
    private void SaveLedger()
    {
        lock (_ledgerGate)
        {
            var ledger = new JsonObject
            {
                ["runId"] = RunId,
                ["startedAt"] = StartedAt.ToString("o"),
                ["note"] = "What this run created on the tailnet. The cleanup deletes each node and key and marks it removed. " +
                    "Anything not marked removed after the run must be removed by hand: nodes on the Machines page (... menu, Remove, Remove machine); " +
                    "unused keys expire by themselves one day after they were minted.",
                ["machinesPage"] = MachinesFilter,
                ["nodes"] = new JsonArray(Nodes.Select(node => (JsonNode)new JsonObject
                {
                    ["role"] = node.Role,
                    ["tag"] = node.Tag,
                    ["hostname"] = node.Hostname,
                    ["nodeId"] = node.NodeId,
                    ["removed"] = node.Removed,
                }).ToArray()),
                ["keys"] = new JsonArray(_keyIds.Select(id => (JsonNode)new JsonObject
                {
                    ["id"] = id,
                    ["removed"] = _removedKeyIds.Contains(id),
                }).ToArray()),
            };
            var path = Work(LedgerName);
            File.WriteAllText(path + ".tmp", ledger.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }
}
