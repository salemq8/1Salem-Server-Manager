[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseRoot,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][int]$BuildRevision,
    [Parameter(Mandatory)][string]$PublishedUtc,
    [string]$Repository = 'salemq8/1Salem-Server-Manager'
)

# Writes <ReleaseRoot>\1SalemConnect-update.json, what installed copies of 1Salem Connect read to
# update themselves (src/ServerManager.Connect.App/Updates). tools/build-release.ps1 runs this for
# every release, so nothing in the app changes per release: the URLs name this release's own
# GitHub assets (tag v<VERSION>-build-<N>), and the sizes and SHA-256 values are those of
# 1SalemConnect-Setup.exe and 1SalemConnect-Portable.zip beside it. UTF-8 without a BOM.

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+$') {
    throw "VERSION must be two numbers, such as 1.5: $Version"
}
if ($BuildRevision -lt 1) {
    throw "BUILD_REVISION must be a positive number: $BuildRevision"
}

$tag = "v$Version-build-$BuildRevision"

function Get-ReleaseFile {
    param([Parameter(Mandatory)][string]$Name)
    $path = Join-Path $ReleaseRoot $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release file is missing: $path"
    }
    [ordered]@{
        fileName = $Name
        url = "https://github.com/$Repository/releases/download/$tag/$Name"
        size = (Get-Item -LiteralPath $path).Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}

$manifest = [ordered]@{
    schema = 1
    product = '1Salem Connect'
    channel = 'Stable'
    productVersion = $Version
    buildRevision = $BuildRevision
    releaseTag = $tag
    releaseUrl = "https://github.com/$Repository/releases/tag/$tag"
    publishedUtc = $PublishedUtc
    installer = Get-ReleaseFile '1SalemConnect-Setup.exe'
    portable = Get-ReleaseFile '1SalemConnect-Portable.zip'
}

$target = Join-Path $ReleaseRoot '1SalemConnect-update.json'
[System.IO.File]::WriteAllText($target, ($manifest | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))
Write-Output $target
