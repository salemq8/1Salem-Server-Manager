#requires -Version 7.0
<#
Development-only Phase 2 acceptance. Never invokes the old Phase 1 smoke runner.
Uses prebuilt, focused-test-verified binaries; no packaging or deployment.
The driver owns live-tailnet cleanup. This runner owns only its local broker and disposable state.
#>
[CmdletBinding()]
param(
    [switch]$Live,
    [Parameter(Mandatory)][string]$PrecheckEvidence,
    [string]$WorkRoot = (Join-Path $env:TEMP ('1p2-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))),
    [int]$BrokerPort = 8799,
    [int]$AgentPort = 5253
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$toolsRoot = Join-Path $repo '.tools'
# This checkout shares the repository's toolchain via a junction. Resolve that one
# read-only toolchain link; disposable state and all driver paths remain reparse-free.
$toolchainItem = Get-Item -LiteralPath $toolsRoot
if ($toolchainItem.LinkType -eq 'Junction') { $toolsRoot = [IO.Path]::GetFullPath($toolchainItem.Target) }
$dotnet = Join-Path $toolsRoot 'dotnet\dotnet.exe'
$node = Join-Path $toolsRoot 'node\node.exe'
$go = Join-Path $toolsRoot 'go\bin\go.exe'
$brokerRoot = Join-Path $repo 'connect\broker'
$wrangler = Join-Path $brokerRoot 'node_modules\wrangler\bin\wrangler.js'
$agent = Join-Path $repo 'src\ServerManager.Agent\bin\Debug\net8.0-windows\1Salem.ServerManager.Agent.exe'
$driver = Join-Path $PSScriptRoot 'Phase2Acceptance\bin\Debug\net8.0-windows\Phase2Acceptance.exe'
$hostTransport = Join-Path $repo 'connect\transport\bin\1Salem.Connect.Host.Transport.exe'
$friendTransport = Join-Path $repo 'connect\transport\bin\1Salem.Connect.Transport.exe'
$probe = Join-Path $toolsRoot 'phase2-probe.test.exe'
$credential = Join-Path $env:LOCALAPPDATA '1Salem Connect Phase2 Acceptance\oauth-client.dpapi'
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot).TrimEnd('\')
$temporaryParent = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
if ([IO.Path]::GetDirectoryName($WorkRoot) -ne $temporaryParent -or
    [IO.Path]::GetFileName($WorkRoot) -notmatch '^1p2-[a-f0-9]{8}$' -or
    (Test-Path -LiteralPath $WorkRoot)) { throw 'WorkRoot must be a NEW direct TEMP child named 1p2-<8 hex>.' }

function Assert-NoReparse([string]$Path) {
    for ($part = [IO.Path]::GetFullPath($Path); $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if ((Test-Path -LiteralPath $part) -and
            ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Refused a reparse point in an acceptance path.'
        }
    }
}
Assert-NoReparse $WorkRoot
Assert-NoReparse $PrecheckEvidence
$precheck = Get-Content -LiteralPath $PrecheckEvidence -Raw | ConvertFrom-Json
$precheckAge = [DateTimeOffset]::UtcNow - [DateTimeOffset]$precheck.checkedAtUtc
if (-not $precheck.tailnetLockOff -or -not $precheck.policySafe -or -not $precheck.requiredScopesPresent -or
    $precheckAge.TotalMinutes -gt 30 -or $precheckAge.TotalSeconds -lt -30) {
    throw 'A fresh read-only OAuth/policy/Tailnet Lock precheck is required.'
}
foreach ($executable in $dotnet, $node, $go, $wrangler, $agent, $driver, $hostTransport, $friendTransport, $probe) {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing focused-build output: $executable" }
}
if (-not (Test-Path -LiteralPath $credential -PathType Leaf)) { throw 'The Phase 2 DPAPI credential staging file is missing.' }
foreach ($port in $BrokerPort, $AgentPort) {
    if ($port -lt 1024 -or $port -gt 65535 -or $port -in 5251, 5252, 7780, 25565, 8211, 8212) {
        throw 'Acceptance endpoint must use a separate nonproduction port.'
    }
    if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { throw "Acceptance port $port is already occupied." }
}
if ($BrokerPort -eq $AgentPort) { throw 'Broker and Agent ports must differ.' }
if (Get-ChildItem Env: | Where-Object Name -Match '^(TS_|TSNET_)') { throw 'Refused inherited TS_/TSNET_ environment settings.' }
foreach ($image in $hostTransport, $friendTransport, $probe) {
    $buildMetadata = & $go version -m $image
    if ($LASTEXITCODE -ne 0 -or -not ($buildMetadata -match 'ts_omit_oauthkey')) { throw 'Transport/probe lacks ts_omit_oauthkey build tag.' }
}
if (-not $Live) {
    Write-Output 'Phase 2 runner arguments and prebuilt outputs verified; no process launched, credential opened, or API contacted.'
    exit 0
}

New-Item -ItemType Directory -Path $WorkRoot | Out-Null
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$directoryAcl = [Security.AccessControl.DirectorySecurity]::new()
$directoryAcl.SetOwner($currentSid)
$directoryAcl.SetAccessRuleProtection($true, $false)
foreach ($sid in $currentSid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18')) {
    $directoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -LiteralPath $WorkRoot -AclObject $directoryAcl
Copy-Item -LiteralPath $PrecheckEvidence -Destination (Join-Path $WorkRoot 'precheck.json')
$driverRoot = Join-Path $WorkRoot 'driver'
$secrets = Join-Path $WorkRoot 'dev-secrets.env'
$d1 = Join-Path $WorkRoot 'd1'
$brokerUrl = "http://127.0.0.1:$BrokerPort"
$runnerErrors = [Collections.Generic.List[string]]::new()
$savedEnvironment = @{}
$runtimeEnvironment = @{
    DOTNET_ROOT = Join-Path $toolsRoot 'dotnet'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
    WRANGLER_SEND_METRICS = 'false'
    WRANGLER_LOG_SANITIZE = 'true'
    WRANGLER_LOG_PATH = Join-Path $WorkRoot 'wrangler-debug.log'
    CI = 'true'
}
foreach ($entry in $runtimeEnvironment.GetEnumerator()) {
    $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
}

function Note([string]$Message) {
    $line = "$(Get-Date -Format o) $Message"
    Add-Content -LiteralPath (Join-Path $WorkRoot 'runner.log') -Value $line
    Write-Output $line
}
function Snapshot([string]$Suffix) {
    & (Join-Path $PSScriptRoot 'Get-NetworkIsolationSnapshot.ps1') -OutputPath (Join-Path $WorkRoot "network-$Suffix.txt") | Out-Null
    $network = Get-Content -LiteralPath (Join-Path $WorkRoot "network-$Suffix.txt")
    foreach ($prefix in 'ADAPTER ', 'ROUTE ', 'DNS ', 'WINHTTP ') {
        if (-not ($network | Where-Object { $_.StartsWith($prefix) })) { throw "Incomplete network snapshot: $prefix" }
    }
    $protectedProcesses = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match '^(javaw?|PalServer.*|playit.*|cloudflared.*|ServerManager.*|1Salem.*|tailscale.*)\.exe$'
    } | Sort-Object ProcessId | Select-Object Name, ProcessId, CreationDate, ExecutablePath
    ConvertTo-Json -InputObject @($protectedProcesses) -Depth 3 | Set-Content -LiteralPath (Join-Path $WorkRoot "processes-$Suffix.json")
    $protectedServices = Get-CimInstance Win32_Service | Where-Object {
        $_.Name -match '1Salem|ServerManager|Playit|Palworld|Minecraft|cloudflared|tailscale'
    } | Sort-Object Name | Select-Object Name, State, StartMode, ProcessId
    ConvertTo-Json -InputObject @($protectedServices) -Depth 3 | Set-Content -LiteralPath (Join-Path $WorkRoot "services-$Suffix.json")
}
function Start-Tracked([string]$File, [string[]]$Arguments, [string]$Directory, [string]$Label) {
    $quotedArguments = @($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' })
    $process = Start-Process -FilePath $File -ArgumentList $quotedArguments -WorkingDirectory $Directory -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $WorkRoot "$Label.out.log") -RedirectStandardError (Join-Path $WorkRoot "$Label.err.log")
    $null = $process.Handle
    $job.Assign($process)
    return $process
}
function Remove-OwnedState([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($WorkRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target escaped the disposable run root.' }
    Assert-NoReparse $full
    if (Test-Path -LiteralPath $full) {
        if (Get-ChildItem -LiteralPath $full -Recurse -Force -ErrorAction Stop | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            throw 'Cleanup refuses reparse-point contents.'
        }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

$brokerProcess = $null
$driverProcess = $null
$job = $null
$summary = [ordered]@{ mode = 'Phase 2 Agent-hosted real-tailnet acceptance'; workRoot = $WorkRoot; driverExit = $null; networkUnchanged = $false; protectedProcessesUnchanged = $false; protectedServicesUnchanged = $false; localCleanup = $false }
try {
    # Reuse only the existing kill-on-close helper; this is not the old smoke driver.
    $jobSource = "using System;`n" + (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'TsnetSmoke\SmokeJob.cs') -Raw).
        Replace('namespace TsnetSmoke;', 'namespace Phase2Runner;').Replace('internal sealed class SmokeJob', 'public sealed class SmokeJob')
    Add-Type -TypeDefinition $jobSource
    $job = [Phase2Runner.SmokeJob]::Create()
    Snapshot 'before'
    Note 'Captured network, production-process and service baseline.'
    Push-Location $brokerRoot
    try {
        $publicKeyMetadata = & $node scripts\new-dev-secrets.mjs --out $secrets | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Disposable broker secrets generation failed.' }
        & $node $wrangler d1 migrations apply onesalem-connect --local --persist-to $d1 *> (Join-Path $WorkRoot 'migrate.log')
        if ($LASTEXITCODE -ne 0) { throw 'Disposable LOCAL D1 migration failed.' }
    } finally { Pop-Location }
    $brokerProcess = Start-Tracked $node @($wrangler, 'dev', '--local', '--ip', '127.0.0.1', '--port', "$BrokerPort", '--inspector-port', '0', '--persist-to', $d1, '--env-file', $secrets) $brokerRoot 'broker'
    $brokerStartTime = $brokerProcess.StartTime
    $brokerImagePath = $brokerProcess.MainModule.FileName
    $ready = $false
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(90)
    while (-not $ready -and [DateTime]::UtcNow -lt $readyDeadline -and -not $brokerProcess.HasExited) {
        try { $ready = (Invoke-WebRequest -Uri "$brokerUrl/v1/keys" -NoProxy -TimeoutSec 2).StatusCode -eq 200 } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'Disposable loopback broker did not become ready.' }
    Note 'Disposable local broker ready. Starting the isolated Agent acceptance driver.'
    $driverArguments = @('--live', '--work', $driverRoot, '--agent', $agent, '--broker', $brokerUrl, '--api-url', "http://127.0.0.1:$AgentPort", '--host-transport', $hostTransport, '--friend-transport', $friendTransport, '--probe', $probe, '--credential', $credential, '--tailnet-lock-off-evidence', (Join-Path $WorkRoot 'precheck.json'))
    $driverProcess = Start-Tracked $driver $driverArguments $repo 'driver'
    $runDeadline = [DateTime]::UtcNow.AddMinutes(35)
    while (-not $driverProcess.WaitForExit(1000) -and [DateTime]::UtcNow -lt $runDeadline) { }
    if (-not $driverProcess.HasExited) { throw 'Driver exceeded its budget; requesting cleanup.' }
    $summary.driverExit = $driverProcess.ExitCode
    Note "Driver ended with exit $($summary.driverExit)."
} catch {
    $runnerErrors.Add($_.Exception.Message)
    Note "Runner stopped: $($_.Exception.Message)"
} finally {
    try {
        if ($driverProcess -and -not $driverProcess.HasExited) {
            New-Item -ItemType File -Path (Join-Path $driverRoot 'stop.request') -Force | Out-Null
            Note 'Waiting for driver cleanup; no additional live tests will start.'
            $cleanupDeadline = [DateTime]::UtcNow.AddMinutes(12)
            while (-not $driverProcess.WaitForExit(1000) -and [DateTime]::UtcNow -lt $cleanupDeadline) { }
            if (-not $driverProcess.HasExited) { $runnerErrors.Add('Driver cleanup timed out; inspect nodes.json before any further run.') }
            else { $summary.driverExit = $driverProcess.ExitCode }
        }
    } catch { $runnerErrors.Add('Driver cleanup request failed; inspect nodes.json before any further run.') }
    try {
        if ($brokerProcess -and -not $brokerProcess.HasExited -and $brokerProcess.StartTime -eq $brokerStartTime -and
            $brokerProcess.MainModule.FileName -eq $brokerImagePath) {
            $brokerProcess.Kill($true)
            $null = $brokerProcess.WaitForExit(10000)
        }
    } catch { $runnerErrors.Add('Broker shutdown failed; closing the owned process job next.') }
    finally { if ($job) { $job.Dispose() } }
    try {
        Snapshot 'after'
        $networkDiff = @(Compare-Object (Get-Content -LiteralPath (Join-Path $WorkRoot 'network-before.txt')) (Get-Content -LiteralPath (Join-Path $WorkRoot 'network-after.txt')))
        $networkDiff | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $WorkRoot 'network-diff.json')
        $summary.networkUnchanged = $networkDiff.Count -eq 0
        $summary.protectedProcessesUnchanged = (Get-Content -LiteralPath (Join-Path $WorkRoot 'processes-before.json') -Raw) -eq (Get-Content -LiteralPath (Join-Path $WorkRoot 'processes-after.json') -Raw)
        $summary.protectedServicesUnchanged = (Get-Content -LiteralPath (Join-Path $WorkRoot 'services-before.json') -Raw) -eq (Get-Content -LiteralPath (Join-Path $WorkRoot 'services-after.json') -Raw)
    } catch { $runnerErrors.Add('Could not verify complete before/after system snapshot.') }
    try {
        Remove-OwnedState $secrets
        Remove-OwnedState $d1
        # Driver state is its own responsibility: never erase an interrupted run's recovery data.
        $summary.localCleanup = -not (Test-Path -LiteralPath $secrets) -and -not (Test-Path -LiteralPath $d1)
    } catch { $runnerErrors.Add('Local broker state cleanup failed; retained under the recorded disposable root.') }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    $summary.errors = @($runnerErrors)
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $WorkRoot 'runner-result.json')
    Note "Network unchanged: $($summary.networkUnchanged); production processes unchanged: $($summary.protectedProcessesUnchanged); services unchanged: $($summary.protectedServicesUnchanged)."
    Note "Evidence retained at $WorkRoot; disposable broker secrets and D1 removed: $($summary.localCleanup)."
}
if ($runnerErrors.Count -gt 0 -or $summary.driverExit -ne 0 -or -not $summary.networkUnchanged -or
    -not $summary.protectedProcessesUnchanged -or -not $summary.protectedServicesUnchanged -or -not $summary.localCleanup) { exit 1 }
exit 0
