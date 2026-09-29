<#
Short SYSTEM-service acceptance for the Agent-hosted 1Salem Connect host (Windows PowerShell 5.1,
run elevated). It runs the isolated Debug acceptance Agent as a disposable, uniquely named
LocalSystem service, enrolls only the host in the disposable test tailnet, restarts and stops the
service, and removes the service, the temporary node, the one-time key and all local state.

It never touches the installed "1SalemServerManagerAgent" service, C:\ProgramData\1SalemServerManager,
production Minecraft/Palworld/Playit, or any Cloudflare account; the broker is a local Wrangler
instance. See connect/proof/PHASE2_ACCEPTANCE.md for the staging credential it expects.
#>
[CmdletBinding()]
param(
    [string]$WorkRoot = (Join-Path $env:TEMP ('1ps-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))),
    [int]$BrokerPort = 8799,
    [int]$AgentPort = 5253
)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this acceptance elevated.' }

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$toolsRoot = Join-Path $repo '.tools'
$toolsItem = Get-Item -LiteralPath $toolsRoot -Force
if ($toolsItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { $toolsRoot = [IO.Path]::GetFullPath(@($toolsItem.Target)[0]) }
$node = Join-Path $toolsRoot 'node\node.exe'
$go = Join-Path $toolsRoot 'go\bin\go.exe'
$brokerRoot = Join-Path $repo 'connect\broker'
$wrangler = Join-Path $brokerRoot 'node_modules\wrangler\bin\wrangler.js'
$agent = Join-Path $repo 'src\ServerManager.Agent\bin\Debug\net8.0-windows\1Salem.ServerManager.Agent.exe'
$driver = Join-Path $PSScriptRoot 'Phase2Acceptance\bin\Debug\net8.0-windows\Phase2Acceptance.exe'
$hostTransport = Join-Path $repo 'connect\transport\bin\1Salem.Connect.Host.Transport.exe'
$credential = Join-Path $env:LOCALAPPDATA '1Salem Connect Phase2 Acceptance\oauth-client.dpapi'
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot).TrimEnd('\')
if ([IO.Path]::GetDirectoryName($WorkRoot) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') -or
    [IO.Path]::GetFileName($WorkRoot) -notmatch '^1ps-[a-f0-9]{8}$' -or (Test-Path -LiteralPath $WorkRoot)) {
    throw 'WorkRoot must be a NEW direct TEMP child named 1ps-<8 hex>.'
}
foreach ($file in $node, $go, $wrangler, $agent, $driver, $hostTransport, $credential) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing prerequisite: $file" }
}
foreach ($port in $BrokerPort, $AgentPort) {
    if ($port -lt 1024 -or $port -in 5251, 5252, 7780, 25565, 8211, 8212) { throw 'Use separate nonproduction ports.' }
    if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { throw "Port $port is already in use." }
}
if ($BrokerPort -eq $AgentPort) { throw 'Broker and Agent ports must differ.' }
if (Get-ChildItem Env: | Where-Object Name -Match '^(TS_|TSNET_)') { throw 'Refused inherited TS_/TSNET_ environment settings.' }
if (-not ((& $go version -m $hostTransport) -match 'ts_omit_oauthkey')) { throw 'The host transport lacks the ts_omit_oauthkey build tag.' }

New-Item -ItemType Directory -Path $WorkRoot | Out-Null
$me = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetOwner($me)
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in $me, (New-Object Security.Principal.SecurityIdentifier('S-1-5-18'))) {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
Set-Acl -LiteralPath $WorkRoot -AclObject $acl
$secrets = Join-Path $WorkRoot 'dev-secrets.env'
$d1 = Join-Path $WorkRoot 'd1'
$driverRoot = Join-Path $WorkRoot 'driver'
$agentRoot = Join-Path $driverRoot 'agent'
$errors = New-Object Collections.Generic.List[string]
$summary = [ordered]@{ mode = 'Agent-hosted Connect SYSTEM-service acceptance'; workRoot = $WorkRoot; driverExit = $null
    networkUnchanged = $false; protectedProcessesUnchanged = $false; protectedServicesUnchanged = $false
    disposableServiceRemoved = $false; localCleanup = $false }
$env:WRANGLER_SEND_METRICS = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
# The Debug driver and Agent are framework-dependent; the driver passes this to the service.
$env:DOTNET_ROOT = Join-Path $toolsRoot 'dotnet'

function Note([string]$Message) {
    $line = "$(Get-Date -Format o) $Message"
    Add-Content -LiteralPath (Join-Path $WorkRoot 'runner.log') -Value $line
    Write-Output $line
}
function Snapshot([string]$Suffix) {
    & (Join-Path $PSScriptRoot 'Get-NetworkIsolationSnapshot.ps1') -OutputPath (Join-Path $WorkRoot "network-$Suffix.txt") | Out-Null
    Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(javaw?|PalServer.*|playit.*|cloudflared.*|ServerManager.*|1Salem.*|tailscale.*)\.exe$' } |
        Sort-Object ProcessId | Select-Object Name, ProcessId, CreationDate, ExecutablePath | ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath (Join-Path $WorkRoot "processes-$Suffix.json")
    Get-CimInstance Win32_Service | Where-Object { $_.Name -match '1Salem|ServerManager|Playit|Palworld|Minecraft|cloudflared|tailscale' } |
        Sort-Object Name | Select-Object Name, State, StartMode, ProcessId | ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath (Join-Path $WorkRoot "services-$Suffix.json")
}
function Remove-Owned([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($WorkRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target escaped the run root.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

$broker = $null
try {
    Snapshot 'before'
    Note 'Captured network, protected-process and service baseline.'
    Push-Location $brokerRoot
    try {
        & $node scripts\new-dev-secrets.mjs --out $secrets | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Disposable broker secrets failed.' }
        & $node $wrangler d1 migrations apply onesalem-connect --local --persist-to $d1 *> (Join-Path $WorkRoot 'migrate.log')
        if ($LASTEXITCODE -ne 0) { throw 'Local D1 migration failed.' }
    } finally { Pop-Location }
    $broker = Start-Process -FilePath $node -WorkingDirectory $brokerRoot -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $WorkRoot 'broker.out.log') -RedirectStandardError (Join-Path $WorkRoot 'broker.err.log') `
        -ArgumentList @("`"$wrangler`"", 'dev', '--local', '--ip', '127.0.0.1', '--port', $BrokerPort, '--inspector-port', '0',
                        '--persist-to', "`"$d1`"", '--env-file', "`"$secrets`"")
    $ready = $false
    for ($i = 0; $i -lt 90 -and -not $ready; $i++) {
        Start-Sleep -Seconds 1
        try { $ready = (Invoke-WebRequest -Uri "http://127.0.0.1:$BrokerPort/v1/keys" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { }
    }
    if (-not $ready) { throw 'The local broker did not start.' }
    Note "Local broker ready; starting the SYSTEM-service driver."
    $arguments = @('--live', '--system-service', '--work', "`"$driverRoot`"", '--agent', "`"$agent`"",
        '--broker', "http://127.0.0.1:$BrokerPort", '--api-url', "http://127.0.0.1:$AgentPort",
        '--host-transport', "`"$hostTransport`"", '--credential', "`"$credential`"")
    $driverProcess = Start-Process -FilePath $driver -ArgumentList $arguments -WorkingDirectory $repo -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $WorkRoot 'driver.out.log') -RedirectStandardError (Join-Path $WorkRoot 'driver.err.log')
    # Windows PowerShell loses ExitCode unless the process handle is opened while the process runs.
    $null = $driverProcess.Handle
    if (-not $driverProcess.WaitForExit(30 * 60 * 1000)) {
        New-Item -ItemType File -Path (Join-Path $driverRoot 'stop.request') -Force | Out-Null
        if (-not $driverProcess.WaitForExit(12 * 60 * 1000)) { throw 'The driver did not finish its cleanup in time.' }
    }
    $summary.driverExit = $driverProcess.ExitCode
    Note "Driver ended with exit $($summary.driverExit)."
} catch {
    $errors.Add($_.Exception.Message)
    Note "Runner stopped: $($_.Exception.Message)"
} finally {
    if ($broker -and -not $broker.HasExited) { & taskkill.exe /PID $broker.Id /T /F 2>$null | Out-Null }
    $leftover = @(Get-CimInstance Win32_Service | Where-Object { $_.Name -like 'OneSalemConnectSystemAcceptance*' })
    $summary.disposableServiceRemoved = $leftover.Count -eq 0
    if ($leftover.Count -ne 0) { $errors.Add('A disposable acceptance service is still registered: ' + ($leftover.Name -join ', ')) }
    try {
        Snapshot 'after'
        $diff = @(Compare-Object (Get-Content (Join-Path $WorkRoot 'network-before.txt')) (Get-Content (Join-Path $WorkRoot 'network-after.txt')))
        $summary.networkUnchanged = $diff.Count -eq 0
        $summary.protectedProcessesUnchanged = (Get-Content (Join-Path $WorkRoot 'processes-before.json') -Raw) -eq (Get-Content (Join-Path $WorkRoot 'processes-after.json') -Raw)
        $summary.protectedServicesUnchanged = (Get-Content (Join-Path $WorkRoot 'services-before.json') -Raw) -eq (Get-Content (Join-Path $WorkRoot 'services-after.json') -Raw)
    } catch { $errors.Add('Could not compare the before/after system snapshots.') }
    try {
        Remove-Owned $secrets
        Remove-Owned $d1
        # SYSTEM-owned Connect state is removed only once the driver proved every cloud resource gone.
        $result = $null
        if (Test-Path -LiteralPath (Join-Path $driverRoot 'result.json')) { $result = Get-Content (Join-Path $driverRoot 'result.json') -Raw | ConvertFrom-Json }
        if ($result -and $result.temporaryNodesRemaining -eq 0 -and (Test-Path -LiteralPath $agentRoot)) {
            if ((Get-UICulture).Name -notlike 'en-*') { throw 'Ownership recovery needs an English display language; SYSTEM state retained.' }
            # Take ownership, then return every entry to the run root's inherited user+SYSTEM ACL.
            & takeown.exe /F $agentRoot /A /R /D Y | Out-Null
            & icacls.exe $agentRoot /reset /T /C /Q | Out-Null
            Remove-Owned $agentRoot
        }
        $summary.localCleanup = -not (Test-Path $secrets) -and -not (Test-Path $d1) -and -not (Test-Path $agentRoot)
    } catch { $errors.Add('Local state cleanup failed: ' + $_.Exception.Message) }
    $summary.errors = @($errors)
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $WorkRoot 'runner-result.json')
    Note "Network unchanged: $($summary.networkUnchanged); protected processes unchanged: $($summary.protectedProcessesUnchanged); services unchanged: $($summary.protectedServicesUnchanged); disposable service removed: $($summary.disposableServiceRemoved); local cleanup: $($summary.localCleanup)."
}
if ($errors.Count -gt 0 -or $summary.driverExit -ne 0 -or -not $summary.networkUnchanged -or -not $summary.protectedProcessesUnchanged -or
    -not $summary.protectedServicesUnchanged -or -not $summary.disposableServiceRemoved -or -not $summary.localCleanup) { exit 1 }
exit 0
