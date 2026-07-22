[CmdletBinding()]
param(
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
    throw "Installed/released build audit failed with exit code $LASTEXITCODE."
}

$audit = $json | ConvertFrom-Json
$currentProductVersion = [string]$audit.sourceVersion
$knownBuilds = @([int]$audit.sourceBuildRevision)
$knownBuilds += @(
    $audit.sources |
        Where-Object { $_.version -eq $currentProductVersion } |
        ForEach-Object { [int]$_.buildRevision }
)
$installedBuild = if ($knownBuilds.Count -gt 0) {
    ($knownBuilds | Measure-Object -Maximum).Maximum
}
else {
    0
}
$nextBuild = [int]$installedBuild + 1

Write-Host "Current product version: $currentProductVersion"
Write-Host "Installed/released build: $installedBuild"
Write-Host "Next internal build: $nextBuild"
if (-not $Apply) {
    Write-Host 'No files changed. Add -Apply to update BUILD_REVISION only.'
    return
}

Set-Content -LiteralPath (Join-Path $root 'BUILD_REVISION') `
    -Value $nextBuild `
    -Encoding ascii
Write-Host "BUILD_REVISION updated to $nextBuild. VERSION remains $currentProductVersion."
