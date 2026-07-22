[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Patch', 'Minor', 'Major')]
    [string]$Part,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
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
    'Patch' { [version]::new($highest.Major, $highest.Minor, $highest.Build + 1) }
    'Minor' { [version]::new($highest.Major, $highest.Minor + 1, 0) }
    'Major' { [version]::new($highest.Major + 1, 0, 0) }
}
$nextText = '{0}.{1}.{2}' -f $next.Major, $next.Minor, $next.Build

Write-Host "Highest known version: $highestText"
Write-Host "$Part result: $nextText"
if (-not $Apply) {
    Write-Host 'No files changed. Add -Apply to update VERSION and create release-notes scaffolding.'
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
