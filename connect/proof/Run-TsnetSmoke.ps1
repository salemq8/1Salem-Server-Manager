<#
.SYNOPSIS
Runs the real tsnet smoke test of 1Salem Connect, or (by default) only its offline preflight.

.DESCRIPTION
Preflight (the default) is offline and contacts nothing: it checks the tools, that both sidecars
record -tags=ts_omit_oauthkey, builds the driver (connect\proof\TsnetSmoke, restored from the local
package cache only) and the Go probe test binary, dry-runs the driver's argument checks (which
refuse a client tag other than the production one and warn about tag names that do not start with
a letter), reports whether an OAuth client is stored and refuses TS_*/TSNET_* variables in this
environment. It then prints what -Live would create.

-Live additionally needs the OAuth client stored by Set-TsnetSmokeCredential.ps1. It snapshots the
Windows network settings, starts a local broker (wrangler dev --local on 127.0.0.1, throwaway
secrets, local D1), samples the sidecars' and the probe's TCP connections and the Windows DNS cache
every 3 s, runs the driver against the owner's real tailnet with temporary tagged nodes (all
deleted again), then compares the network settings and collects the evidence into
$env:TEMP\1ts-last. The work root, with its throwaway secrets, D1 and node state, is deleted.
Ctrl+C is safe: the driver stops at its next step and still deletes every node whose id it
recorded, and this script waits for that. A node cut off in the middle of its enrollment may never
report its id; it is then listed, not deleted, and must be removed by hand. See
connect\proof\TSNET_SMOKE.md.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File connect\proof\Run-TsnetSmoke.ps1
#>
[CmdletBinding()]
param(
    # Offline checks only; this is also what runs when -Live is absent.
    [switch]$Preflight,
    # Run on the owner's real tailnet.
    [switch]$Live,
    # A folder under %TEMP% that does not exist yet: the runner deletes its work root at the end,
    # so it only ever takes one it created itself. Kept short on purpose: local D1 and tsnet state
    # nest several folders deep and fail with opaque errors past MAX_PATH.
    [string]$WorkRoot = (Join-Path $env:TEMP ('1ts-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))),
    [int]$BrokerPort = 8798,
    # Keep the logs in the work root as well. The throwaway secrets, D1 and node state are always deleted.
    [switch]$KeepEvidence,
    # The host node's tag. Empty means the driver's default, tag:onesalem-host.
    [string]$HostTag = '',
    # The friend and probe nodes' tag. Empty means the production provisioner's
    # TailscaleApiProvisioner.FriendTag, the only value the driver accepts.
    [string]$ClientTag = ''
)

$ErrorActionPreference = 'Stop'
if ($Preflight -and $Live) {
    Write-Host 'refused: choose -Preflight or -Live, not both.'
    exit 1
}

$lastEvidence = Join-Path $env:TEMP '1ts-last'
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
if (-not $WorkRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $WorkRoot.StartsWith($lastEvidence, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "refused: -WorkRoot must be a new folder under $tempRoot (and not under $lastEvidence); the runner deletes it at the end."
    exit 1
}
if (Test-Path -LiteralPath $WorkRoot) {
    Write-Host "refused: -WorkRoot $WorkRoot already exists. The runner deletes its work root at the end, so it only takes a folder it creates itself."
    exit 1
}

# A live run replaces the previous run's evidence. If that run left nodes on the tailnet, its
# nodes.json may be the only list of them, so it must be dealt with first.
$previousLedger = Join-Path $lastEvidence 'nodes.json'
if ($Live -and (Test-Path -LiteralPath $previousLedger)) {
    try {
        $previous = Get-Content -LiteralPath $previousLedger -Raw | ConvertFrom-Json
        $leftNodes = @($previous.nodes | Where-Object { $_.nodeId -and -not $_.removed })
        $leftKeys = @($previous.keys | Where-Object { -not $_.removed })
    }
    catch {
        Write-Host "refused: the previous run's $previousLedger cannot be read; check it and the Machines page, then delete it."
        exit 1
    }
    if ($leftNodes.Count -gt 0) {
        Write-Host "refused: the previous run (run $($previous.runId)) left these nodes on the tailnet:"
        foreach ($leftNode in $leftNodes) { Write-Host "  $($leftNode.role) $($leftNode.nodeId) ($($leftNode.hostname))" }
        Write-Host "Remove them by hand ($($previous.machinesPage): ... menu, Remove, Remove machine), then delete $previousLedger."
        exit 1
    }
    if ($leftKeys.Count -gt 0) {
        Write-Host "note: the previous run could not delete $($leftKeys.Count) auth key(s); they expire by themselves one day after they were minted."
    }
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$tools = Join-Path $repo '.tools'
$dotnet = Join-Path $tools 'dotnet\dotnet.exe'
$go = Join-Path $tools 'go\bin\go.exe'
$node = Join-Path $tools 'node\node.exe'
$brokerDir = Join-Path $repo 'connect\broker'
$wrangler = Join-Path $brokerDir 'node_modules\wrangler\bin\wrangler.js'
$transportDir = Join-Path $repo 'connect\transport'
$hostExe = Join-Path $transportDir 'bin\1Salem.Connect.Host.Transport.exe'
$friendExe = Join-Path $transportDir 'bin\1Salem.Connect.Transport.exe'
$driverProject = Join-Path $PSScriptRoot 'TsnetSmoke\TsnetSmoke.csproj'
$driverDll = Join-Path $PSScriptRoot 'TsnetSmoke\bin\Debug\net8.0-windows\TsnetSmoke.dll'
$credential = Join-Path $env:LOCALAPPDATA '1Salem Connect Smoke\oauth-client.dpapi'
$probeExe = Join-Path $WorkRoot 'probe.test.exe'
$driverWork = Join-Path $WorkRoot 'driver'
$driverOut = Join-Path $WorkRoot 'driver.log'
$brokerUrl = "http://127.0.0.1:$BrokerPort"
$driverArgs = @('--broker', $brokerUrl, '--host-transport', $hostExe, '--friend-transport', $friendExe, '--probe', $probeExe,
                '--work', $driverWork, '--result', (Join-Path $WorkRoot 'result.json'), '--credential', $credential)
if ($HostTag) { $driverArgs += @('--host-tag', $HostTag) }
if ($ClientTag) { $driverArgs += @('--client-tag', $ClientTag) }

# Tailscale's log service. Looked up only once the run is over: a lookup before would put the
# names into the Windows DNS cache that the run is checked against.
$logHostNames = @('log.tailscale.com', 'log.tailscale.io')

# How long the runner waits for the driver: its whole run (every step has its own limit), and its
# cleanup after Ctrl+C (which includes up to 6 minutes of departed-peer probe).
$driverBudget = [TimeSpan]::FromMinutes(45)
$cleanupBudget = [TimeSpan]::FromMinutes(15)

# Offline builds with the repository's own toolchain. The Go variables are the ones
# connect\transport\build.ps1 sets. All are restored when the script ends.
$toolEnvironment = [ordered]@{
    DOTNET_CLI_HOME             = Join-Path $tools 'dotnet-home'
    NUGET_PACKAGES              = Join-Path $tools 'nuget-packages'
    DOTNET_NOLOGO               = '1'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    # The SDK's own background checks (workload manifests, first-run setup) would reach the network.
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE         = '1'
    GOPATH                      = Join-Path $tools 'gopath'
    GOMODCACHE                  = Join-Path $tools 'go-mod'
    GOCACHE                     = Join-Path $tools 'go-build-cache'
    GOTOOLCHAIN                 = 'local'
    GOTELEMETRY                 = 'off'
    CGO_ENABLED                 = '0'
    GOOS                        = 'windows'
    GOARCH                      = 'amd64'
    GOFLAGS                     = '-mod=readonly'
    GOPROXY                     = 'off'
    WRANGLER_SEND_METRICS       = 'false'
}

New-Item -ItemType Directory -Path $WorkRoot | Out-Null
$runnerLog = Join-Path $WorkRoot 'runner.log'
$failures = New-Object System.Collections.Generic.List[string]

function Note([string]$Message) {
    $line = "$((Get-Date).ToString('HH:mm:ss'))  $Message"
    Add-Content -LiteralPath $runnerLog -Value $line
    Write-Host $line
}

function Fail([string]$Message) {
    $failures.Add($Message)
    Note "FAIL  $Message"
}

# Runs a native command with stdout and stderr in a log file and returns its exit code. Stop is
# lifted meanwhile: Windows PowerShell turns a native command's stderr line into a terminating error.
function Invoke-Logged([string]$LogName, [scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command 2>&1 | ForEach-Object { "$_" } | Out-File -LiteralPath (Join-Path $WorkRoot $LogName) -Encoding UTF8
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return $LASTEXITCODE
}

# rmdir /s removes a junction or link itself and never what it points to, unlike a recursive
# Remove-Item in Windows PowerShell.
function Remove-Tree([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        & cmd.exe /c rmdir /s /q $Path 2>$null | Out-Null
    }
    return -not (Test-Path -LiteralPath $Path)
}

function Test-Tag([string]$Binary, [string]$Tag) {
    return [bool]((& $go version -m $Binary) -match "-tags=\S*\b$Tag\b")
}

function Get-LogHostCacheEntries {
    return @(Get-DnsClientCache -ErrorAction SilentlyContinue |
        Where-Object { $logHostNames -contains $_.Entry } | ForEach-Object { "$($_.Entry) $($_.Data)" })
}

# Shows what the driver wrote since the last call. Its output is a file in the console's code
# page, read by byte offset so a line is shown once and only when complete.
$script:driverShown = 0L
function Show-DriverOutput {
    if (-not (Test-Path -LiteralPath $driverOut)) { return }
    $stream = [IO.File]::Open($driverOut, 'Open', 'Read', 'ReadWrite')
    try {
        $null = $stream.Seek($script:driverShown, 'Begin')
        $bytes = New-Object byte[] ([Math]::Max(0, $stream.Length - $script:driverShown))
        $read = $stream.Read($bytes, 0, $bytes.Length)
        if ($read -le 0) { return }
        $end = [Array]::LastIndexOf($bytes, [byte]10, $read - 1)
        if ($end -ge 0) {
            Write-Host -NoNewline ([Console]::OutputEncoding.GetString($bytes, 0, $end + 1))
            $script:driverShown += $end + 1
        }
    }
    finally { $stream.Dispose() }
}

# Waits for the driver while showing its output. True once it has exited; false if $Budget ran out.
function Wait-Driver([Diagnostics.Process]$Process, [TimeSpan]$Budget) {
    $deadline = (Get-Date) + $Budget
    while (-not $Process.WaitForExit(1000)) {
        Show-DriverOutput
        if ((Get-Date) -ge $deadline) { return $false }
    }
    Show-DriverOutput
    return $true
}

# What the runner itself can observe about log upload and port mapping, written to
# log-evidence.txt. The driver's own checks (log buffer sizes, expected and forbidden log lines)
# are in result.json; no log line proves that uploads are off, and none is claimed to.
function Write-EvidenceReport {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('== supplementary: lines in the sidecar and probe logs about log upload or port mapping (logtail, portmap, upnp, nat-pmp, pcp) ==')
    $logs = @('host-transport.log', 'host-transport-diag.log', 'friend-transport-diag.log', 'probe.log', 'probe-departed.log' |
        ForEach-Object { Join-Path $driverWork $_ } | Where-Object { Test-Path -LiteralPath $_ })
    $hits = @(if ($logs.Count -gt 0) { Select-String -LiteralPath $logs -Pattern '(?i)logtail|portmap|upnp|nat-?pmp|\bpcp\b' })
    if ($hits.Count -eq 0) { $lines.Add('(none)') }
    foreach ($hit in $hits) { $lines.Add("$($hit.Filename):$($hit.LineNumber): $($hit.Line.Trim())") }

    # The DNS cache is shared by the whole PC. An installed Tailscale client uploads its own logs
    # and looks these names up itself, so with one running (or an entry cached before the run)
    # the check cannot say who looked them up: it is then INCONCLUSIVE, which is not a pass.
    $lines.Add('')
    $lines.Add('== Windows DNS cache: log.tailscale.com / log.tailscale.io (dns-cache-before.txt, dns-cache.csv, dns-cache-after.txt) ==')
    $before = @(Get-Content -LiteralPath (Join-Path $WorkRoot 'dns-cache-before.txt') -ErrorAction SilentlyContinue | Where-Object { $_ })
    $during = @(@(Import-Csv -LiteralPath (Join-Path $WorkRoot 'dns-cache.csv') -ErrorAction SilentlyContinue | ForEach-Object { "$($_.entry) $($_.data)" }) +
        @(Get-Content -LiteralPath (Join-Path $WorkRoot 'dns-cache-after.txt') -ErrorAction SilentlyContinue | Where-Object { $_ }) | Sort-Object -Unique)
    $otherClients = @(Get-Process -Name 'tailscaled', 'tailscale-ipn' -ErrorAction SilentlyContinue | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" })
    if ($before.Count -gt 0 -or $otherClients.Count -gt 0) {
        $summary.logHostDnsCheck = 'inconclusive'
        $lines.Add("INCONCLUSIVE: cached before the run: $(if ($before.Count -gt 0) { $before -join ', ' } else { 'nothing' }); other Tailscale clients running: $(if ($otherClients.Count -gt 0) { $otherClients -join ', ' } else { 'none' }). Stop the installed Tailscale client (and wait for the entries to expire) for a conclusive check.")
    }
    elseif ($during.Count -gt 0) {
        $summary.logHostDnsCheck = 'failed'
        $lines.Add("FAIL: looked up during the run: $($during -join ', ')")
        Fail "a Tailscale log host was looked up during the run and no other Tailscale client was running (see log-evidence.txt)"
    }
    else {
        $summary.logHostDnsCheck = 'passed'
        $lines.Add('PASS: not in the cache before, during (sampled every 3 s) or after the run, and no other Tailscale client was running')
    }

    # Resolved only now, after the last look at the cache.
    $lines.Add('')
    $lines.Add('== remote TCP endpoints of the sidecars and probe (connections.csv) against the log hosts, resolved after the run (log-hosts.txt) ==')
    $logHosts = foreach ($name in $logHostNames) {
        try { Resolve-DnsName -Name $name -ErrorAction Stop | Where-Object { $_.IPAddress } | ForEach-Object { "$name $($_.IPAddress)" } }
        catch { "$name unresolved: $($_.Exception.Message)" }
    }
    [IO.File]::WriteAllLines((Join-Path $WorkRoot 'log-hosts.txt'), [string[]]@($logHosts))
    $logHostIps = @($logHosts | Where-Object { $_ -match '^\S+ [0-9A-Fa-f.:]+$' } | ForEach-Object { ($_ -split ' ')[1] })
    $rows = @(Import-Csv -LiteralPath (Join-Path $WorkRoot 'connections.csv') -ErrorAction SilentlyContinue)
    $remote = @($rows | Where-Object { $_.remoteAddress -and $_.remoteAddress -notin '0.0.0.0', '::', '127.0.0.1', '::1' })
    $remotes = @($remote | ForEach-Object { $_.remoteAddress } | Sort-Object -Unique)
    $logHostHits = @($remotes | Where-Object { $logHostIps -contains $_ })
    $lines.Add("log host addresses: $(if ($logHostIps.Count -gt 0) { $logHostIps -join ', ' } else { 'none resolved' })")
    $lines.Add("remote endpoints seen: $(if ($remotes.Count -gt 0) { $remotes -join ', ' } else { 'none' })")
    $lines.Add("remote TCP ports seen: $(@($remote | ForEach-Object { $_.remotePort } | Sort-Object -Unique) -join ', ') (expected: 80 and 443 for the control plane, 443 for DERP; UDP is not sampled)")
    $lines.Add("endpoints that are log hosts: $(if ($logHostHits.Count -gt 0) { $logHostHits -join ', ' } else { 'none' })")
    if ($logHostIps.Count -eq 0) { Fail 'the log-host comparison could not run: neither log host resolved (see log-hosts.txt)' }
    foreach ($exe in $hostExe, $friendExe, $probeExe) {
        $image = [IO.Path]::GetFileNameWithoutExtension($exe)
        if (-not ($rows | Where-Object { $_.process -eq $image })) { Fail "the log-host comparison could not run for $image`: the sampler saw none of its TCP connections" }
    }
    if ($logHostHits.Count -gt 0) { Fail 'a sidecar or the probe had a TCP connection to a Tailscale log host (see log-evidence.txt)' }
    $lines.Add('Only sampled TCP connections are compared, every 3 s, against one resolution after the run: a match fails the run, no match is not proof.')

    $lines.Add('')
    $lines.Add('== not observed ==')
    $lines.Add('UDP port-mapping probes (NAT-PMP and PCP to UDP 5351, SSDP to UDP 1900) cannot be seen without an elevated packet capture, which this harness does not do; TSNET_SMOKE.md has an optional pktmon command.')
    [IO.File]::WriteAllLines((Join-Path $WorkRoot 'log-evidence.txt'), $lines)
    foreach ($line in $lines) { Note $line }
}

$saved = @{}
foreach ($name in $toolEnvironment.Keys) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $toolEnvironment[$name], 'Process')
}
$wranglerProcess = $null
$sampler = $null
$driverProcess = $null
$reachedEnd = $false
$caught = $false
$summary = [ordered]@{ mode = $(if ($Live) { 'live' } else { 'preflight' }); workRoot = $WorkRoot }
try {
    # ---- preflight: offline, contacts nothing ----------------------------------------------
    Note "work root $WorkRoot"
    foreach ($tool in $dotnet, $go, $node, $wrangler) {
        if (-not (Test-Path -LiteralPath $tool)) { Fail "missing tool $tool" }
    }
    if ($failures.Count -gt 0) { throw 'tools are missing; nothing else was checked' }

    foreach ($exe in $hostExe, $friendExe) {
        if (-not (Test-Path -LiteralPath $exe)) { Fail "missing $exe (build it with connect\transport\build.ps1)" }
        elseif (-not (Test-Tag $exe 'ts_omit_oauthkey')) { Fail "$exe does not record -tags=ts_omit_oauthkey" }
        else { Note "OK    $(Split-Path -Leaf $exe) records -tags=ts_omit_oauthkey" }
    }

    # Restored from the local package cache only, so preflight never goes online; a package
    # missing from the cache fails here instead of being downloaded.
    $build = Invoke-Logged 'driver-build.log' {
        & $dotnet restore $driverProject --source $env:NUGET_PACKAGES --nologo -v q
        if ($LASTEXITCODE -eq 0) { & $dotnet build $driverProject -c Debug --no-restore --nologo -v q }
    }
    if ($build -ne 0) { Fail 'the driver did not build (see driver-build.log)' }
    else { Note 'OK    driver built (restored from the local package cache only)' }

    # The probe is built with the sidecars' tag too, so it cannot mint keys either.
    Push-Location $transportDir
    try { $probeBuild = Invoke-Logged 'probe-build.log' { & $go test -c -tags 'tsnetsmoke,ts_omit_oauthkey' -o $probeExe ./internal/transport } }
    finally { Pop-Location }
    if ($probeBuild -ne 0) { Fail 'the probe did not build (see probe-build.log)' }
    elseif (-not ((Test-Tag $probeExe 'tsnetsmoke') -and (Test-Tag $probeExe 'ts_omit_oauthkey'))) { Fail 'the probe does not record -tags=tsnetsmoke,ts_omit_oauthkey' }
    else { Note "OK    probe built: $probeExe" }

    # The driver owns the tag rules (the client tag must be the production one; a name that does
    # not start with a letter gets a warning), so its dry run is shown as it is.
    if (Test-Path -LiteralPath $driverDll) {
        $dryRun = Invoke-Logged 'driver-arguments.log' { & $dotnet $driverDll @driverArgs }
        Get-Content -LiteralPath (Join-Path $WorkRoot 'driver-arguments.log') | ForEach-Object { Note "driver: $_" }
        if ($dryRun -ne 0) { Fail 'the driver refused its arguments (see above)' }
        else { Note 'OK    driver accepts its arguments (dry run: nothing started, nothing contacted)' }
    }

    $tailscaleVariables = @(Get-ChildItem env: | Where-Object { $_.Name -match '^(TS_|TSNET_)' } | ForEach-Object { $_.Name })
    if ($tailscaleVariables.Count -gt 0) { Fail "Tailscale variables are set in this environment: $($tailscaleVariables -join ', ') (names only)" }
    else { Note 'OK    no TS_*/TSNET_* variables in this environment' }

    if (Test-Path -LiteralPath $credential) { Note "credential: an OAuth client is stored at $credential (the runner never reads it)" }
    else { Note 'credential: none stored (expected until Salem runs Set-TsnetSmokeCredential.ps1)' }

    Note '-Live would create, and delete again at the end:'
    Note "  a local broker on $brokerUrl (wrangler dev --local, throwaway secrets, local D1 in the work root)"
    Note '  3 one-off, pre-authorized auth keys, 1 day expiry: 1 with the host tag, 2 with the client tag (tags as the driver printed them)'
    Note '  3 tailnet nodes: 1salem-smoke-host-<run id> (host tag), the friend app''s own node 1salem-<device id> and 1salem-smoke-probe-<run id> (client tag)'
    Note "  logs and results in $lastEvidence"
    $summary.preflightFailures = @($failures)

    if ($Live) {
        # ---- live --------------------------------------------------------------------------
        if (-not (Test-Path -LiteralPath $credential)) { Fail 'no OAuth client is stored; run Set-TsnetSmokeCredential.ps1 first' }
        if ($failures.Count -gt 0) { throw 'preflight failed; nothing online was started' }

        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Get-NetworkIsolationSnapshot.ps1') `
            -OutputPath (Join-Path $WorkRoot 'network-before.txt') | Out-Null
        Note 'network snapshot taken (before)'

        # A local broker with throwaway secrets. There is no CONNECT_DEV_LOOPBACK_BRIDGE: the host
        # bridge is a real tailnet address, which the broker accepts as it would in production.
        $secrets = Join-Path $WorkRoot 'dev-secrets.env'
        $d1 = Join-Path $WorkRoot 'd1'
        Push-Location $brokerDir
        try {
            $published = & $node scripts\new-dev-secrets.mjs --out $secrets | ConvertFrom-Json
            Note "throwaway ticket key generated (kid $($published.kid))"
            if ((Invoke-Logged 'migrate.log' { & $node $wrangler d1 migrations apply onesalem-connect --local --persist-to $d1 }) -ne 0) {
                throw 'local D1 migration failed (see migrate.log)'
            }
        }
        finally { Pop-Location }
        $wranglerProcess = Start-Process -FilePath $node -WorkingDirectory $brokerDir -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $WorkRoot 'wrangler.out.log') `
            -RedirectStandardError (Join-Path $WorkRoot 'wrangler.err.log') `
            -ArgumentList @("`"$wrangler`"", 'dev', '--local', '--ip', '127.0.0.1', '--port', $BrokerPort,
                            '--persist-to', "`"$d1`"", '--env-file', "`"$secrets`"")
        $ready = $false
        for ($i = 0; $i -lt 90 -and -not $ready; $i++) {
            Start-Sleep -Seconds 1
            try { $ready = (Invoke-WebRequest -Uri "$brokerUrl/v1/keys" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { $ready = $false }
        }
        if (-not $ready) { throw 'the local broker did not start (see wrangler.*.log)' }
        Note "local broker listening on $brokerUrl (pid $($wranglerProcess.Id))"

        [IO.File]::WriteAllLines((Join-Path $WorkRoot 'dns-cache-before.txt'), [string[]]@(Get-LogHostCacheEntries))

        # Every 3 s: the TCP connections owned by the sidecars and the probe (found by executable
        # path), and any log-host entry in the Windows DNS cache.
        $samplerStop = Join-Path $WorkRoot 'sampler.stop'
        $sampler = Start-Job -ArgumentList @($hostExe, $friendExe, $probeExe), (Join-Path $WorkRoot 'connections.csv'), $logHostNames, (Join-Path $WorkRoot 'dns-cache.csv'), $samplerStop -ScriptBlock {
            param([string[]]$Images, [string]$Csv, [string[]]$Names, [string]$DnsCsv, [string]$Stop)
            Set-Content -LiteralPath $Csv -Value 'time,pid,process,localAddress,localPort,remoteAddress,remotePort,state' -Encoding UTF8
            Set-Content -LiteralPath $DnsCsv -Value 'time,entry,data' -Encoding UTF8
            $deadline = (Get-Date).AddHours(1)
            while (-not (Test-Path -LiteralPath $Stop) -and (Get-Date) -lt $deadline) {
                foreach ($process in Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $Images -contains $_.Path }) {
                    foreach ($c in Get-NetTCPConnection -OwningProcess $process.Id -ErrorAction SilentlyContinue) {
                        $row = '{0},{1},{2},{3},{4},{5},{6},{7}' -f (Get-Date).ToString('o'), $process.Id, $process.ProcessName,
                            $c.LocalAddress, $c.LocalPort, $c.RemoteAddress, $c.RemotePort, $c.State
                        Add-Content -LiteralPath $Csv -Value $row -Encoding UTF8
                    }
                }
                foreach ($entry in Get-DnsClientCache -ErrorAction SilentlyContinue | Where-Object { $Names -contains $_.Entry }) {
                    Add-Content -LiteralPath $DnsCsv -Value ('{0},{1},{2}' -f (Get-Date).ToString('o'), $entry.Entry, $entry.Data) -Encoding UTF8
                }
                Start-Sleep -Seconds 3
            }
        }
        Note 'connection and DNS-cache sampler started'

        # The driver shares this console, so Ctrl+C reaches it too and it runs its own cleanup
        # (Program.OnCancelKeyPress). Its output goes to files, not through a pipeline: stopping
        # this script stops a native command in a pipeline, which would cut that cleanup short.
        $quoted = @(@($driverDll) + $driverArgs + @('--live') | ForEach-Object { '"{0}"' -f $_ })
        $driverProcess = Start-Process -FilePath $dotnet -ArgumentList $quoted -NoNewWindow -PassThru `
            -RedirectStandardOutput $driverOut -RedirectStandardError (Join-Path $WorkRoot 'driver.err.log')
        # Windows PowerShell reports no ExitCode for a -PassThru process whose handle was not opened early.
        $null = $driverProcess.Handle
        Note "driver started (pid $($driverProcess.Id)); Ctrl+C stops it at its next step and it still cleans up"
        if (-not (Wait-Driver $driverProcess $driverBudget)) {
            Fail "the driver was still running after $($driverBudget.TotalMinutes) minutes"
            # Not a kill: the driver treats this file like Ctrl+C and still runs its cleanup.
            New-Item -ItemType File -Force -Path (Join-Path $driverWork 'stop.request') | Out-Null
            Note 'asked the driver to stop and clean up'
        }
    }
    $reachedEnd = $true
}
catch {
    $caught = $true
    Fail $_.Exception.Message
}
finally {
    if (-not $reachedEnd -and -not $caught) { Fail 'interrupted (Ctrl+C)' }

    # Before anything else: the driver may still be deleting its nodes, and it needs its work
    # directory for that. A driver that overruns is stopped; its job then ends the host transport
    # and the probe, and nodes.json says which nodes to remove by hand.
    if ($driverProcess) {
        if (-not $driverProcess.HasExited) {
            # Idempotent: whatever ended this script early, the driver is asked to stop and clean up.
            New-Item -ItemType File -Force -Path (Join-Path $driverWork 'stop.request') -ErrorAction SilentlyContinue | Out-Null
            Note "waiting up to $($cleanupBudget.TotalMinutes) minutes for the driver to finish; it deletes the nodes it enrolled"
            if (-not (Wait-Driver $driverProcess $cleanupBudget)) {
                Fail "the driver did not finish and was stopped; nodes it enrolled may remain on the tailnet (see nodes.json in $lastEvidence)"
                Stop-Process -Id $driverProcess.Id -Force -ErrorAction SilentlyContinue
                $null = $driverProcess.WaitForExit(10000)
            }
        }
        $summary.driverExit = if ($driverProcess.HasExited) { $driverProcess.ExitCode } else { 'still running' }
        Note "driver exited with $($summary.driverExit)"
    }
    if ($sampler) {
        New-Item -ItemType File -Force -Path $samplerStop | Out-Null
        Wait-Job -Job $sampler -Timeout 15 | Out-Null
        Stop-Job -Job $sampler
        Remove-Job -Job $sampler -Force
        Note 'sampler stopped'
    }
    if ($wranglerProcess -and -not $wranglerProcess.HasExited) {
        # wrangler starts workerd as a child; stop the whole tree it owns.
        & taskkill.exe /PID $wranglerProcess.Id /T /F 2>$null | Out-Null
        Note 'local broker stopped'
    }
    if (Test-Path -LiteralPath (Join-Path $WorkRoot 'network-before.txt')) {
        [IO.File]::WriteAllLines((Join-Path $WorkRoot 'dns-cache-after.txt'), [string[]]@(Get-LogHostCacheEntries))
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Get-NetworkIsolationSnapshot.ps1') `
            -OutputPath (Join-Path $WorkRoot 'network-after.txt') | Out-Null
        $diff = @(Compare-Object (Get-Content (Join-Path $WorkRoot 'network-before.txt')) (Get-Content (Join-Path $WorkRoot 'network-after.txt')) |
            ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
        [IO.File]::WriteAllLines((Join-Path $WorkRoot 'network-diff.txt'), [string[]]$diff)
        $summary.networkUnchanged = $diff.Count -eq 0
        Note "network settings unchanged: $($summary.networkUnchanged)"
        if (-not $summary.networkUnchanged) { Fail 'Windows network settings changed during the run (see network-diff.txt)' }
        Write-EvidenceReport
    }
    foreach ($name in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }

    if ($Live) {
        # Everything worth reading, already redacted, in one place outside the work root.
        if (-not (Remove-Tree $lastEvidence)) { Fail "could not clear $lastEvidence" }
        New-Item -ItemType Directory -Force -Path $lastEvidence | Out-Null
        $evidence = @('runner.log', 'driver.log', 'driver.err.log', 'result.json', 'driver-build.log', 'probe-build.log', 'driver-arguments.log',
                      'network-before.txt', 'network-after.txt', 'network-diff.txt', 'connections.csv', 'dns-cache-before.txt', 'dns-cache.csv',
                      'dns-cache-after.txt', 'log-hosts.txt', 'log-evidence.txt', 'migrate.log', 'wrangler.out.log', 'wrangler.err.log' |
                          ForEach-Object { Join-Path $WorkRoot $_ })
        $evidence += @('nodes.json', 'host-transport.log', 'host-transport-diag.log', 'friend-transport-diag.log', 'probe.log', 'probe-departed.log',
                       'probe-result.json', 'probe-departed.json' | ForEach-Object { Join-Path $driverWork $_ })
        foreach ($file in $evidence) {
            if (-not (Test-Path -LiteralPath $file)) { continue }
            # The driver's raw stderr holds only what the runtime itself writes (an unhandled
            # exception) and is not redacted, so it is kept as evidence only when it is empty.
            if ((Split-Path -Leaf $file) -eq 'driver.err.log' -and (Get-Item -LiteralPath $file).Length -gt 0) {
                Fail "the driver wrote $((Get-Item -LiteralPath $file).Length) bytes to its raw stderr (an unhandled failure); not copied, because it is not redacted"
                continue
            }
            Copy-Item -LiteralPath $file -Destination $lastEvidence -Force
        }
    }

    # Secrets and state always go. The work root as a whole goes unless the logs are to stay in it,
    # or a preflight failed and its build logs are the only way to see why.
    $keepWorkRoot = $KeepEvidence -or (-not $Live -and $failures.Count -gt 0)
    if ($keepWorkRoot) {
        foreach ($stateItem in 'dev-secrets.env', 'd1', 'driver\host-state', 'driver\probe-state', 'driver\friend-app', 'driver\owner') {
            $path = Join-Path $WorkRoot $stateItem
            $gone = if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force; -not (Test-Path -LiteralPath $path) } else { Remove-Tree $path }
            if (-not $gone) { Fail "could not delete $path" }
        }
        Note "work root kept (logs only): $WorkRoot"
    }
    elseif (-not (Remove-Tree $WorkRoot)) {
        $failures.Add("could not delete the work root $WorkRoot")
        Write-Host "FAIL  could not delete the work root $WorkRoot"
    }

    $summary.failures = @($failures)
    $summaryJson = $summary | ConvertTo-Json -Depth 4
    if ($Live) { Set-Content -LiteralPath (Join-Path $lastEvidence 'summary.json') -Value $summaryJson -Encoding UTF8 }
    if ($keepWorkRoot) { Set-Content -LiteralPath (Join-Path $WorkRoot 'summary.json') -Value $summaryJson -Encoding UTF8 }
    if (-not $reachedEnd -and -not $caught) { Write-Host "RESULT: INTERRUPTED; see $lastEvidence" }
}

$dnsNote = if ($summary.logHostDnsCheck -eq 'inconclusive') { ' (the DNS-cache check was INCONCLUSIVE, see log-evidence.txt)' } else { '' }
if ($failures.Count -gt 0 -or ($Live -and $summary.driverExit -ne 0)) {
    $driverNote = if ($Live) { "; driver exit $($summary.driverExit)" } else { '' }
    Write-Host "RESULT: FAILED ($($failures.Count) runner failure(s)$driverNote)$dnsNote"
    exit 1
}
Write-Host $(if ($Live) { "RESULT: PASSED; evidence in $lastEvidence$dnsNote" } else { 'RESULT: preflight passed; nothing online was contacted' })
exit 0
