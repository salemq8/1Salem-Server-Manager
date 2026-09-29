using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ServerManager.Contracts;
using ServerManager.Infrastructure.Connect;
using TsnetSmoke;

namespace Phase2Acceptance;

/// <summary>
/// The short SYSTEM-service acceptance: the same isolated Debug Agent, but started by the Service
/// Control Manager as LocalSystem in a disposable, uniquely named service. It enrolls only the
/// host, checks the identities the installed service will have, restarts the service, and removes
/// everything it created. It never touches the installed Agent service or its data.
/// </summary>
internal sealed partial class AcceptanceRun
{
    private const string SystemSid = "S-1-5-18";
    private bool _serviceCreated;

    private string ServiceName => "OneSalemConnectSystemAcceptance" + RunId;

    public async Task ExecuteSystemServiceAsync()
    {
        Require(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            "elevated acceptance driver", "an administrator creates the disposable service");
        var credential = SmokeCredential.Load(_options.Credential);
        _api = new SmokeTailnetApi(_tailnetHttp, credential);
        foreach (var tag in new[] { TailscaleApiProvisioner.HostTag, TailscaleApiProvisioner.FriendTag })
            foreach (var device in await _api.ListDevicesAsync(tag)) _baselineNodes.Add(device.NodeId);
        Require(_baselineNodes.Count == 0, "disposable tailnet baseline", "approved tags have no pre-existing devices");

        await CreateServiceAsync();
        var first = await StartServiceAgentAsync();
        Require(first.OwnerSid == SystemSid, "service runs as LocalSystem", "service process owner SID " + first.OwnerSid);

        await AgentAsync<ConnectStatusResponse>(HttpMethod.Put, "/api/v1/connect/credential",
            new ConnectCredentialRequest(credential.ClientId, credential.ClientSecret));
        credential = null!;
        var ready = await WaitReadyAsync();
        _ownerId = ready.OwnerId ?? throw new InvalidDataException();
        _hostNode.NodeId = ready.HostNodeId;
        var host = await VerifyNodeAsync(_hostNode);
        var hostIp = host.Addresses.First(address => IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork);
        Require(ready.HostBridge == hostIp + ":7780" && ready.BridgeRunning && ready.RevocationChannel,
            "SYSTEM Agent initializes Connect", "credential saved, owner registered, host enrolled with the host tag, bridge and revocation channel up");

        var sidecars = await HostSidecarsAsync();
        Require(sidecars.Length == 1 && sidecars[0].ParentProcessId == first.ProcessId && sidecars[0].OwnerSid == SystemSid &&
                string.Equals(sidecars[0].ExecutablePath, _options.HostTransport, StringComparison.OrdinalIgnoreCase),
            "host sidecar started by the SYSTEM service", $"{sidecars.Length} sidecar(s); parent is the service; owner {sidecars.FirstOrDefault()?.OwnerSid}");

        CheckServicePipes(first.ProcessId);
        CheckProtectedState();

        // Restart recovery: the same SYSTEM identity must reopen its DPAPI credential and owner
        // key, reconcile the existing host node, and mint nothing new.
        ReadAgentJournal();
        var keysBefore = _keys.Count;
        await StopServiceAsync("service stop ends its host sidecar");
        var second = await StartServiceAgentAsync();
        var again = await WaitReadyAsync();
        ReadAgentJournal();
        Require(second.OwnerSid == SystemSid && again.OwnerId == _ownerId && again.HostNodeId == _hostNode.NodeId &&
                again.BridgeRunning && again.RevocationChannel && _keys.Count == keysBefore,
            "restart recovery under SYSTEM", "same owner and host node after a service restart; no new key minted");
        var restarted = await HostSidecarsAsync();
        Require(restarted.Length == 1 && restarted[0].ParentProcessId == second.ProcessId && restarted[0].OwnerSid == SystemSid,
            "host sidecar restarted with the service", "one SYSTEM sidecar under the new service process");

        using (var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/connect/credential"))
        using (var response = await _agentHttp.SendAsync(request, _stop))
            Require(response.IsSuccessStatusCode, "SYSTEM Agent removes its credential", "HTTP " + (int)response.StatusCode);
        await StopServiceAsync("final service stop ends its host sidecar");
    }

    private async Task CreateServiceAsync()
    {
        static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? "\"" + value + "\"" : value;
        var command = string.Join(' ', new[]
        {
            Quote(_options.Agent), "--connect-acceptance", "--data-root", Quote(AgentRoot),
            "--api-url", _options.ApiUrl.AbsoluteUri.TrimEnd('/'), "--pipe-name", PipePrefix + ".Agent",
            "--connect-broker-url", _options.Broker.AbsoluteUri.TrimEnd('/'),
            "--connect-authz-pipe", PipePrefix + ".Authz", "--connect-control-pipe", PipePrefix + ".Control",
            "--connect-transport", Quote(_options.HostTransport)
        });
        // The Debug Agent is framework-dependent and this machine has no machine-wide runtime, so
        // the service gets the repository runtime through its own Environment value, which lives in
        // the service's registry key and disappears with "sc delete".
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(dotnetRoot) || !Path.IsPathFullyQualified(dotnetRoot) || !File.Exists(Path.Combine(dotnetRoot, "dotnet.exe")))
            throw new InvalidOperationException("DOTNET_ROOT must name the repository .NET runtime.");
        await ScAsync("create", ServiceName, "binPath=", command, "start=", "demand", "obj=", "LocalSystem",
            "DisplayName=", "1Salem Connect SYSTEM acceptance (disposable " + RunId + ")");
        _serviceCreated = true;
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName, writable: true)
            ?? throw new InvalidOperationException("The disposable service key is missing."))
            key.SetValue("Environment", new[] { "DOTNET_ROOT=" + Path.GetFullPath(dotnetRoot) }, Microsoft.Win32.RegistryValueKind.MultiString);
        Record("disposable service created", true, ServiceName + ", LocalSystem, manual start");
    }

    private async Task<ServiceProcessInfo> StartServiceAgentAsync()
    {
        await ScAsync("start", ServiceName);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            _stop.ThrowIfCancellationRequested();
            var service = await ServiceAsync();
            if (service is { State: "Running", ProcessId: > 0 } && File.Exists(AgentTransportDefaults.ResolveLocalAgentKeyPath(AgentRoot)))
            {
                var key = (await File.ReadAllTextAsync(AgentTransportDefaults.ResolveLocalAgentKeyPath(AgentRoot), _stop)).Trim();
                if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid isolated Agent bearer.");
                _agentHttp.DefaultRequestHeaders.Authorization = new("Bearer", key);
                try
                {
                    await AgentAsync<ConnectStatusResponse>(HttpMethod.Get, "/api/v1/connect/status");
                    var owner = (await ProcessesAsync($"ProcessId={service.ProcessId}")).Single();
                    Record("service started", true, "PID " + service.ProcessId);
                    return new(service.ProcessId, owner.OwnerSid);
                }
                catch (HttpRequestException) { }
                catch (AgentResponseException) { }
            }
            await Task.Delay(500, _stop);
        }
        throw new TimeoutException("The disposable service did not start the Agent.");
    }

    private async Task StopServiceAsync(string check)
    {
        var running = await ServiceAsync();
        await ScAsync("stop", ServiceName);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while ((await ServiceAsync())?.State != "Stopped")
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("The disposable service did not stop.");
            await Task.Delay(500, _stop);
        }
        var leftovers = (await HostSidecarsAsync()).Where(sidecar => sidecar.ParentProcessId == running?.ProcessId).ToArray();
        var stillRunning = running is { ProcessId: > 0 } && (await ProcessesAsync($"ProcessId={running.ProcessId}")).Length != 0;
        Require(leftovers.Length == 0 && !stillRunning, check, "service stopped; no Agent or host sidecar process left behind");
    }

    private async Task RemoveServiceAsync()
    {
        if (!_serviceCreated) return;
        if ((await ServiceAsync())?.State is { } state && state != "Stopped")
        {
            try { await ScAsync("stop", ServiceName); } catch (InvalidOperationException) { }
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while ((await ServiceAsync())?.State is { } current && current != "Stopped" && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(500);
        }
        await ScAsync("delete", ServiceName);
        var gone = DateTimeOffset.UtcNow.AddSeconds(30);
        while (await ServiceAsync() is not null)
        {
            if (DateTimeOffset.UtcNow > gone) throw new TimeoutException("The disposable service was not removed.");
            await Task.Delay(500);
        }
        _serviceCreated = false;
        Record("disposable service removed", true, ServiceName + " no longer registered");
    }

    /// <summary>
    /// The Agent's own pipe is served by the service process; the authorization and transport
    /// control pipes admit only SYSTEM, so this elevated administrator is refused by both.
    /// </summary>
    private void CheckServicePipes(int serviceProcessId)
    {
        using (var agentPipe = new NamedPipeClientStream(".", PipePrefix + ".Agent", PipeDirection.InOut))
        {
            agentPipe.Connect(5000);
            Require(GetNamedPipeServerProcessId(agentPipe.SafePipeHandle, out var server) && server == serviceProcessId,
                "Agent pipe served by the SYSTEM service", "server PID matches the service process");
        }
        foreach (var (name, label) in new[] { (PipePrefix + ".Authz", "authorization"), (PipePrefix + ".Control", "transport control") })
        {
            var refused = false;
            try
            {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
                pipe.Connect(5000);
            }
            catch (UnauthorizedAccessException) { refused = true; }
            Require(refused, label + " pipe admits only SYSTEM", "an elevated administrator is refused");
        }
    }

    /// <summary>Owner-side Connect state written by SYSTEM must not be readable by anyone else.</summary>
    private void CheckProtectedState()
    {
        var connect = Path.Combine(AgentRoot, "connect");
        static bool Refused(Action read)
        {
            try { read(); return false; }
            catch (UnauthorizedAccessException) { return true; }
        }
        Require(Refused(() => _ = Directory.EnumerateFileSystemEntries(connect).ToArray()) &&
                Refused(() => _ = File.ReadAllBytes(Path.Combine(connect, "oauth-client.dpapi"))) &&
                Refused(() => _ = File.ReadAllBytes(Path.Combine(connect, "identity", "identity.v1.json"))),
            "SYSTEM-only Connect state", "credential, owner identity and folder listing refused to an elevated administrator");
    }

    private async Task ScAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("sc.exe did not start.");
        _ = await process.StandardOutput.ReadToEndAsync();
        _ = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("sc.exe " + arguments[0] + " failed with exit " + process.ExitCode + ".");
    }

    private async Task<ServiceState?> ServiceAsync()
    {
        var rows = await CimAsync<ServiceState>(
            $"Get-CimInstance Win32_Service -Filter \"Name='{ServiceName}'\" | Select-Object @{{n='State';e={{$_.State}}}},@{{n='ProcessId';e={{[int]$_.ProcessId}}}}");
        return rows.SingleOrDefault();
    }

    private Task<ProcessRow[]> HostSidecarsAsync() => ProcessesAsync("Name='1Salem.Connect.Host.Transport.exe'");

    private Task<ProcessRow[]> ProcessesAsync(string filter) => CimAsync<ProcessRow>(
        $"Get-CimInstance Win32_Process -Filter \"{filter}\" | ForEach-Object {{ [pscustomobject]@{{ ProcessId = [int]$_.ProcessId; " +
        "ParentProcessId = [int]$_.ParentProcessId; ExecutablePath = $_.ExecutablePath; " +
        "OwnerSid = (Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid).Sid } }");

    private async Task<T[]> CimAsync<T>(string pipeline)
    {
        // CIM answers with SIDs and state names that do not depend on the Windows display language.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        var script = "ConvertTo-Json -Compress -Depth 3 -InputObject @(" + pipeline + ")";
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CIM query did not start.");
        var output = await process.StandardOutput.ReadToEndAsync();
        _ = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("CIM query failed.");
        output = output.Trim();
        if (output.Length == 0) return [];
        return output.StartsWith('[')
            ? JsonSerializer.Deserialize<T[]>(output, Json) ?? []
            : [JsonSerializer.Deserialize<T>(output, Json)!];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    private sealed record ServiceState(string State, int ProcessId);
    private sealed record ProcessRow(int ProcessId, int ParentProcessId, string? ExecutablePath, string? OwnerSid);
    private sealed record ServiceProcessInfo(int ProcessId, string? OwnerSid);
}
