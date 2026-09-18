[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Minor', 'Major')]
    [string]$Part,
    [switch]$Apply,
    [string]$Authorization
)

# VERSION is Salem's fixed two-part visible product version (Major.Minor), enforced by the
# ValidateProductVersion MSBuild target in Directory.Build.props (`^\d+\.\d+$`). There is no
# "Patch" concept at this level any more -- ordinary patch-level changes are the internal
# BUILD_REVISION's job (see tools/next-build.ps1), not a VERSION change. This tool is reserved
# for the rare, explicitly-authorized case where Salem decides the visible product version
# itself should change (e.g. 1.5 -> 1.6 or 1.5 -> 2.0).

$ErrorActionPreference = 'Stop'
if ($Apply -and $Authorization -cne 'Change the product version.') {
    throw 'VERSION is locked by Salem fixed-version policy. Explicit authorization text is required: Change the product version.'
}
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$arguments = @(
    'run',
    '--project', (Join-Path $root 'tools\ServerManager.Versioning\ServerManager.Versioning.csproj'),
    '-c', 'Release',
    '--',
    'audit',
    '--repo', $root,
    '--json'
)
$json = & $dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Installed/released version audit failed with exit code $LASTEXITCODE."
}

$audit = $json | ConvertFrom-Json
$highestText = if ([string]::IsNullOrWhiteSpace($audit.highestKnownVersion)) {
    $audit.sourceVersion
}
else {
    $audit.highestKnownVersion
}
$highest = [version]$highestText
$next = switch ($Part) {
    'Minor' { [version]::new($highest.Major, $highest.Minor + 1) }
    'Major' { [version]::new($highest.Major + 1, 0) }
}
# Two-part only (Major.Minor) -- matches the exact format ValidateProductVersion in
# Directory.Build.props requires. A 3-part value here would pass this script but fail the very
# next build with "VERSION must contain Salem's fixed two-part visible product version".
$nextText = '{0}.{1}' -f $next.Major, $next.Minor

Write-Host "Highest known version: $highestText"
Write-Host "$Part result: $nextText"
if (-not $Apply) {
    Write-Host 'No files changed. This tool is reserved for an explicit Salem product-version change.'
    return
}

Set-Content -LiteralPath (Join-Path $root 'VERSION') -Value $nextText -Encoding ascii
$notes = Join-Path $root "docs\RELEASE_NOTES_$nextText.md"
if (Test-Path -LiteralPath $notes) {
    throw "Release-notes scaffold already exists: $notes"
}

@(
    "# 1Salem Server Manager $nextText",
    '',
    '## Highlights',
    '',
    '- Describe the user-visible changes in this release.',
    '',
    '## Update compatibility',
    '',
    '- Document the minimum supported installed version and rollback behavior.'
) | Set-Content -LiteralPath $notes -Encoding utf8
Write-Host "Updated VERSION and created $notes"
