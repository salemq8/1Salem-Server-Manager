[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$buildRevision = [int](Get-Content -LiteralPath (Join-Path $root 'BUILD_REVISION') -Raw).Trim()
$candidate = Join-Path $artifactsRoot "staging\release-candidates\$version-build-$buildRevision"
$canonical = Join-Path $artifactsRoot "release\$version"
$previousBase = Join-Path $artifactsRoot 'staging\release-previous'
$previous = Join-Path $previousBase "$version-previous"
$installRoot = Join-Path $env:ProgramFiles '1Salem Server Manager'
$installedManifestPath = Join-Path $installRoot 'current.json'

function Assert-InsideArtifacts {
    param([Parameter(Mandatory)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    $prefix = $artifactsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the repository artifacts root: $full"
    }
    return $full
}

foreach ($path in @($candidate, $canonical, $previous)) {
    $null = Assert-InsideArtifacts $path
}
if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
    throw "Validated release candidate is missing: $candidate"
}
if (-not (Test-Path -LiteralPath $installedManifestPath -PathType Leaf)) {
    throw "Installed application manifest is missing: $installedManifestPath"
}

$installed = Get-Content -LiteralPath $installedManifestPath -Raw | ConvertFrom-Json
if ($installed.activeVersion -ne $version -or
    [int]$installed.activeBuildRevision -ne $buildRevision -or
    $installed.updateStatus -ne 'Succeeded') {
    throw "Installed application is not verified as Version $version Build $buildRevision."
}
if ([string]::IsNullOrWhiteSpace($installed.rollbackSnapshotPath) -or
    -not (Test-Path -LiteralPath $installed.rollbackSnapshotPath -PathType Container)) {
    throw 'A verified installed rollback snapshot must exist before release promotion.'
}

$manifest = Get-Content -LiteralPath (Join-Path $candidate 'version.json') -Raw |
    ConvertFrom-Json
if ($manifest.version -ne $version -or
    [int]$manifest.buildRevision -ne $buildRevision -or
    $manifest.archiveValidation -ne 'passed') {
    throw 'The release candidate manifest does not match VERSION and BUILD_REVISION.'
}

$required = @(
    'Setup.exe',
    'Portable.zip',
    'Source.zip',
    "1SalemServerManager-Update-$version.zip",
    '1SalemConnect-Setup.exe',
    '1SalemConnect-Portable.zip',
    'version.json',
    'build-info.json',
    'SHA256SUMS.txt',
    'RELEASE_NOTES.md'
)
$expected = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $candidate 'SHA256SUMS.txt')) {
    if ($line -match '^([0-9A-Fa-f]{64}) \*(.+)$') {
        $expected[$Matches[2]] = $Matches[1].ToUpperInvariant()
    }
}
foreach ($name in $required) {
    $path = Join-Path $candidate $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required candidate file is missing: $name"
    }
    if ($name -ne 'SHA256SUMS.txt') {
        if (-not $expected.ContainsKey($name) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected[$name]) {
            throw "Candidate checksum verification failed: $name"
        }
    }
}

New-Item -ItemType Directory -Path $previousBase -Force | Out-Null
if (Test-Path -LiteralPath $previous) {
    Remove-Item -LiteralPath $previous -Recurse -Force
}
if (Test-Path -LiteralPath $canonical) {
    Move-Item -LiteralPath $canonical -Destination $previous
}
try {
    Move-Item -LiteralPath $candidate -Destination $canonical
}
catch {
    if (-not (Test-Path -LiteralPath $canonical) -and
        (Test-Path -LiteralPath $previous)) {
        Move-Item -LiteralPath $previous -Destination $canonical
    }
    throw
}

Write-Host "Rolling Stable release promoted: Version $version Build $buildRevision"
Write-Host "Release directory: $canonical"
