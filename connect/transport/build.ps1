<#
.SYNOPSIS
Builds the two 1Salem Connect Go sidecars into connect\transport\bin\.

.DESCRIPTION
Produces 1Salem.Connect.Transport.exe (friend) and 1Salem.Connect.Host.Transport.exe
(host bridge) with the repo-local Go toolchain in .tools\go.

Both are built with -tags ts_omit_oauthkey (CONNECT_ARCHITECTURE.md section 11). Without the tag,
tsnet links an OAuth hook that treats any "tskey-client-" auth key as an OAuth client
secret and mints new keys with it. Before building, the script proves the tag removes
that hook (go list -deps must not contain tailscale.com/feature/oauthkey or
golang.org/x/oauth2), and afterwards it checks that each binary records the tag. A
binary built without the tag (plain "go build") refuses to start in tsnet mode.

Builds are offline by default (GOPROXY=off) and use only the module cache in
.tools\go-mod. The caller's environment is restored afterwards.

.PARAMETER Test
Also run go vet and the test suite, with and without the tag, before building.

.PARAMETER AllowDownload
Let Go download missing modules from the default proxy.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File connect\transport\build.ps1 -Test
#>
[CmdletBinding()]
param(
    [switch]$Test,
    [switch]$AllowDownload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$moduleDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $moduleDir '..\..')).Path
$tools = Join-Path $repoRoot '.tools'
$go = Join-Path $tools 'go\bin\go.exe'
if (-not (Test-Path $go)) {
    throw "Go toolchain not found at $go"
}

$tags = 'ts_omit_oauthkey'
# Packages that can mint Tailscale keys. None may be linked into a sidecar.
$forbidden = @(
    'tailscale.com/feature/oauthkey',
    'tailscale.com/feature/identityfederation',
    'golang.org/x/oauth2'
)
$targets = [ordered]@{
    '1Salem.Connect.Transport.exe'      = './cmd/connect-transport'
    '1Salem.Connect.Host.Transport.exe' = './cmd/connect-host-transport'
}

$goEnv = [ordered]@{
    GOPATH      = Join-Path $tools 'gopath'
    GOMODCACHE  = Join-Path $tools 'go-mod'
    GOCACHE     = Join-Path $tools 'go-build-cache'
    GOTOOLCHAIN = 'local'
    GOTELEMETRY = 'off'
    CGO_ENABLED = '0'
    GOOS        = 'windows'
    GOARCH      = 'amd64'
    GOFLAGS     = '-mod=readonly'
}
if (-not $AllowDownload) {
    $goEnv['GOPROXY'] = 'off'
}

function Invoke-Go {
    param([string[]]$GoArgs)
    & $go @GoArgs
    if ($LASTEXITCODE -ne 0) {
        throw "go $($GoArgs -join ' ') failed with exit code $LASTEXITCODE"
    }
}

$saved = @{}
foreach ($name in $goEnv.Keys) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $goEnv[$name], 'Process')
}
Push-Location $moduleDir
try {
    $packages = @($targets.Values)
    $deps = & $go list -deps -tags $tags @packages
    if ($LASTEXITCODE -ne 0) {
        throw 'go list -deps failed'
    }
    foreach ($pkg in $forbidden) {
        if ($deps -contains $pkg) {
            throw "-tags $tags still links $pkg; refusing to build"
        }
    }
    Write-Host "OK: with -tags $tags the sidecars link none of: $($forbidden -join ', ')"

    if ($Test) {
        Invoke-Go @('vet', './...')
        Invoke-Go @('vet', '-tags', $tags, './...')
        Invoke-Go @('test', '-count=1', './...')
        Invoke-Go @('test', '-count=1', '-tags', $tags, './...')
    }

    $version = (Get-Content (Join-Path $repoRoot 'VERSION') -TotalCount 1).Trim()
    $bin = Join-Path $moduleDir 'bin'
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    foreach ($name in $targets.Keys) {
        $out = Join-Path $bin $name
        Invoke-Go @('build', '-tags', $tags, '-trimpath', '-ldflags', "-s -w -X main.version=$version", '-o', $out, $targets[$name])
        $info = & $go version -m $out
        if (-not ($info -match "-tags=$tags")) {
            throw "$name does not record -tags=$tags"
        }
        $size = (Get-Item $out).Length
        Write-Host ('{0}  {1:N0} bytes ({2:N1} MiB)' -f $out, $size, ($size / 1MB))
    }
}
finally {
    Pop-Location
    foreach ($name in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }
}
