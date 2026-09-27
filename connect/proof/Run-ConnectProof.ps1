[CmdletBinding()]
param(
    # Where the run's disposable state lives. Kept short on purpose: local D1 nests its SQLite
    # file several folders deep and fails with an opaque error past MAX_PATH.
    [string]$WorkRoot = (Join-Path $env:TEMP ('1cp-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))),
    [int]$BrokerPort = 8797,
    [switch]$KeepState,
    # Use an already built proof driver (for example while another build is running).
    [switch]$SkipBuild
)

# Disposable end-to-end fake-mode proof of 1Salem Connect Build 8, entirely on this PC:
#   local broker (wrangler dev --local, D1 in a temp folder, throwaway secrets)
#   + the real Go host and friend transports in FAKE network mode (loopback stands in for the
#     tailnet; this is NOT a tsnet test)
#   + the real Agent-side host authorization pipe server
#   + a second friend driven through the friend app's own code (src/ServerManager.Connect.App:
#     broker client, page view models, enrollment and session services, and the transport
#     process it starts itself), with its identity and state in the work folder
#   + two throwaway TCP "game servers".
# It proves the control plane, the ServerId mapping, the attack cases and that no Windows network
# setting changed. It never touches the installed Server Manager, its data, Palworld, Playit, a
# real tailnet or a Cloudflare account, and everything it creates is deleted afterwards.

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$node = Join-Path $repo '.tools\node\node.exe'
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
$broker = Join-Path $repo 'connect\broker'
$wrangler = Join-Path $broker 'node_modules\wrangler\bin\wrangler.js'
$transportBin = Join-Path $repo 'connect\transport\bin'
$hostExe = Join-Path $transportBin '1Salem.Connect.Host.Transport.exe'
$friendExe = Join-Path $transportBin '1Salem.Connect.Transport.exe'
$driverProject = Join-Path $PSScriptRoot 'ConnectProof\ConnectProof.csproj'

New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
$log = Join-Path $WorkRoot 'proof.log'
function Note($m) { $line = "$((Get-Date).ToString('HH:mm:ss'))  $m"; Add-Content -LiteralPath $log -Value $line; Write-Host $line }

foreach ($required in $node, $dotnet, $wrangler, $hostExe, $friendExe, $driverProject) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing prerequisite: $required" }
}

$env:WRANGLER_SEND_METRICS = 'false'
$wranglerProcess = $null
$summary = [ordered]@{ workRoot = $WorkRoot; brokerPort = $BrokerPort }
try {
    # ---- 1. network settings before -----------------------------------------------------------
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Get-NetworkIsolationSnapshot.ps1') `
        -OutputPath (Join-Path $WorkRoot 'network-before.txt') | Out-Null
    Note 'network snapshot taken (before)'

    # ---- 2. local broker with throwaway secrets ------------------------------------------------
    $secrets = Join-Path $WorkRoot 'dev-secrets.env'
    $state = Join-Path $WorkRoot 'd1'
    Push-Location $broker
    try {
        $published = & $node scripts\new-dev-secrets.mjs --out $secrets | ConvertFrom-Json
        Note "throwaway ticket key generated (kid $($published.kid)); secrets live only in $secrets"
        & $node $wrangler d1 migrations apply onesalem-connect --local --persist-to $state 2>&1 |
            Out-File (Join-Path $WorkRoot 'migrate.log')
        if ($LASTEXITCODE -ne 0) { throw 'local D1 migration failed (see migrate.log)' }
        Note 'local D1 migrated'
    }
    finally { Pop-Location }

    # The fake transports use a loopback host bridge, which only a local broker may accept. It is
    # passed as a --var: because wrangler.jsonc declares secrets.required, wrangler loads only those
    # names from the env file and silently drops everything else in it.
    $wranglerProcess = Start-Process -FilePath $node -WorkingDirectory $broker -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $WorkRoot 'wrangler.out.log') `
        -RedirectStandardError (Join-Path $WorkRoot 'wrangler.err.log') `
        -ArgumentList @("`"$wrangler`"", 'dev', '--local', '--ip', '127.0.0.1', '--port', $BrokerPort,
                        '--persist-to', "`"$state`"", '--env-file', "`"$secrets`"",
                        '--var', 'CONNECT_DEV_LOOPBACK_BRIDGE:allow')
    $brokerUrl = "http://127.0.0.1:$BrokerPort"
    $ready = $false
    for ($i = 0; $i -lt 90 -and -not $ready; $i++) {
        Start-Sleep -Seconds 1
        try { $ready = (Invoke-WebRequest -Uri "$brokerUrl/v1/keys" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch {}
    }
    if (-not $ready) { throw 'the local broker did not start (see wrangler.*.log)' }
    Note "local broker listening on $brokerUrl (pid $($wranglerProcess.Id))"

    # ---- 3. the proof itself ---------------------------------------------------------------
    if (-not $SkipBuild) {
        & $dotnet build $driverProject -c Debug --nologo -v q | Out-File (Join-Path $WorkRoot 'driver-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'proof driver build failed (see driver-build.log)' }
    }
    $driverDll = Join-Path $PSScriptRoot 'ConnectProof\bin\Debug\net8.0-windows\ConnectProof.dll'
    $resultPath = Join-Path $WorkRoot 'proof-result.json'
    & $dotnet $driverDll --broker $brokerUrl --host-transport $hostExe --friend-transport $friendExe `
        --work (Join-Path $WorkRoot 'driver') --result $resultPath 2>&1 | Tee-Object -FilePath (Join-Path $WorkRoot 'driver.log')
    $summary.driverExit = $LASTEXITCODE
    if (Test-Path -LiteralPath $resultPath) { $summary.result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json }
}
finally {
    if ($wranglerProcess -and -not $wranglerProcess.HasExited) {
        # wrangler starts workerd as a child; stop the whole tree it owns.
        & taskkill.exe /PID $wranglerProcess.Id /T /F 2>$null | Out-Null
        Note 'local broker stopped'
    }

    # ---- 4. network settings after -----------------------------------------------------------
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Get-NetworkIsolationSnapshot.ps1') `
        -OutputPath (Join-Path $WorkRoot 'network-after.txt') | Out-Null
    $diff = Compare-Object (Get-Content (Join-Path $WorkRoot 'network-before.txt')) (Get-Content (Join-Path $WorkRoot 'network-after.txt'))
    $summary.networkUnchanged = -not $diff
    $summary.networkDifferences = @($diff | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
    Note "network settings unchanged: $($summary.networkUnchanged)"

    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $WorkRoot 'summary.json') -Encoding UTF8
    if (-not $KeepState) {
        # Secrets and the local database are deleted; the logs and summary are copied out first.
        $keep = Join-Path $env:TEMP ('1cp-last')
        New-Item -ItemType Directory -Force -Path $keep | Out-Null
        foreach ($f in 'summary.json', 'proof.log', 'driver.log', 'network-before.txt', 'network-after.txt', 'proof-result.json') {
            $p = Join-Path $WorkRoot $f
            if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination $keep -Force }
        }
        Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "state deleted; summary kept in $keep"
    }
}
if ($summary.driverExit -ne 0 -or -not $summary.networkUnchanged) { exit 1 }
exit 0
