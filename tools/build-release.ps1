[CmdletBinding()]
param(
    [switch]$SkipValidation
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $root 'artifacts'
$releaseVersion = '1.3.1'
$releaseRoot = Join-Path $artifactsRoot "release\$releaseVersion"
$stagingRoot = Join-Path $artifactsRoot "staging\$releaseVersion"
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget-packages'
$env:APPDATA = Join-Path $root '.tools\appdata'
$env:LOCALAPPDATA = Join-Path $root '.tools\localappdata'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Assert-Success {
    param([string]$Step)
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

function Reset-ArtifactDirectory {
    param([string]$Path)
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $allowedRoot = [System.IO.Path]::GetFullPath($artifactsRoot) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside the repository artifacts root: $resolved"
    }

    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolved | Out-Null
}

function New-ZipFromDirectory {
    param(
        [string]$Source,
        [string]$Destination
    )
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Force
    }

    Compress-Archive -Path (Join-Path $Source '*') -DestinationPath $Destination `
        -CompressionLevel Optimal
}

function New-SourceArchive {
    param([string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Force
    }

    $excludedSegments = @(
        '\.git\',
        '\.tools\',
        '\.local-data\',
        '\artifacts\',
        '\bin\',
        '\obj\',
        '\TestResults\'
    )
    $stream = [System.IO.File]::Open(
        $Destination,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $true)
        try {
            Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
                $candidate = $_.FullName
                -not ($excludedSegments | Where-Object {
                    $candidate.IndexOf(
                        $_,
                        [System.StringComparison]::OrdinalIgnoreCase) -ge 0
                })
            } | ForEach-Object {
                $relative = $_.FullName.Substring($root.Length).TrimStart(
                    [char[]]@(
                        [System.IO.Path]::DirectorySeparatorChar,
                        [System.IO.Path]::AltDirectorySeparatorChar))
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive,
                    $_.FullName,
                    $relative.Replace('\', '/'),
                    [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

Reset-ArtifactDirectory -Path $releaseRoot
Reset-ArtifactDirectory -Path $stagingRoot

if (-not $SkipValidation) {
    & $dotnet restore (Join-Path $root '1SalemServerManager.sln') `
        -p:NuGetAudit=false
    Assert-Success 'dotnet restore'
    & $dotnet build (Join-Path $root '1SalemServerManager.sln') -c Release --no-restore `
        -p:NuGetAudit=false
    Assert-Success 'dotnet build'
    & $dotnet test (Join-Path $root '1SalemServerManager.sln') -c Release `
        --no-build --no-restore -p:NuGetAudit=false
    Assert-Success 'dotnet test'
}

$publishRoot = Join-Path $stagingRoot 'publish'
$clientPublish = Join-Path $publishRoot 'Client'
$agentPublish = Join-Path $publishRoot 'Agent'
$updaterPublish = Join-Path $publishRoot 'Updater'
$setupPublish = Join-Path $publishRoot 'SetupHost'

& $dotnet publish (Join-Path $root 'src\ServerManager.Client\ServerManager.Client.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:NuGetAudit=false `
    -p:DebugType=None -p:DebugSymbols=false -o $clientPublish
Assert-Success 'Client publish'

& $dotnet publish (Join-Path $root 'src\ServerManager.Agent\ServerManager.Agent.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:NuGetAudit=false `
    -p:DebugType=None -p:DebugSymbols=false -o $agentPublish
Assert-Success 'Agent publish'

& $dotnet publish (Join-Path $root 'src\ServerManager.Updater\ServerManager.Updater.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:NuGetAudit=false `
    -p:DebugType=None -p:DebugSymbols=false -o $updaterPublish
Assert-Success 'Updater publish'

Copy-Item -LiteralPath $updaterPublish `
    -Destination (Join-Path $clientPublish 'Updater') -Recurse

$portableRoot = Join-Path $stagingRoot 'Portable'
New-Item -ItemType Directory -Path $portableRoot | Out-Null
Copy-Item -LiteralPath $clientPublish -Destination (Join-Path $portableRoot 'Client') `
    -Recurse
Copy-Item -LiteralPath $agentPublish -Destination (Join-Path $portableRoot 'Agent') `
    -Recurse
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $portableRoot
Copy-Item -LiteralPath (Join-Path $root 'CHANGELOG.md') -Destination $portableRoot
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $portableRoot -Recurse
New-ZipFromDirectory -Source $portableRoot `
    -Destination (Join-Path $releaseRoot 'Portable.zip')

$payloadRoot = Join-Path $stagingRoot 'Payload'
New-Item -ItemType Directory -Path $payloadRoot | Out-Null
Copy-Item -LiteralPath $clientPublish -Destination (Join-Path $payloadRoot 'Client') `
    -Recurse
Copy-Item -LiteralPath $agentPublish -Destination (Join-Path $payloadRoot 'Agent') `
    -Recurse
$payloadZip = Join-Path $stagingRoot 'Payload.zip'
New-ZipFromDirectory -Source $payloadRoot -Destination $payloadZip
$updatePackageName = "1SalemServerManager-Update-$releaseVersion.zip"
Copy-Item -LiteralPath $payloadZip `
    -Destination (Join-Path $releaseRoot $updatePackageName)

& $dotnet publish (Join-Path $root 'tools\ServerManager.Setup\ServerManager.Setup.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:NuGetAudit=false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    "-p:PayloadArchive=$payloadZip" `
    -p:DebugType=None -p:DebugSymbols=false -o $setupPublish
Assert-Success 'Embedded-payload Setup.exe publish'

$setupHost = Get-ChildItem -LiteralPath $setupPublish -Filter '*.exe' |
    Select-Object -First 1
if ($null -eq $setupHost) {
    throw 'Published Setup.exe was not found.'
}
Copy-Item -LiteralPath $setupHost.FullName `
    -Destination (Join-Path $releaseRoot 'Setup.exe')

New-SourceArchive -Destination (Join-Path $releaseRoot 'Source.zip')
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $releaseRoot
Copy-Item -LiteralPath (Join-Path $root 'CHANGELOG.md') -Destination $releaseRoot
Copy-Item -LiteralPath (Join-Path $root 'docs\RELEASE_NOTES_1.3.1.md') `
    -Destination (Join-Path $releaseRoot 'RELEASE_NOTES.md')

$updatePackagePath = Join-Path $releaseRoot $updatePackageName
$updatePackageHash = Get-FileHash -LiteralPath $updatePackagePath -Algorithm SHA256
$versionDocument = [ordered]@{
    version = $releaseVersion
    minimumSupportedVersion = '1.2.1'
    releaseChannel = 'Stable'
    packageUrl = "https://updates.1salem.app/server-manager/stable/$releaseVersion/$updatePackageName"
    packageSize = (Get-Item -LiteralPath $updatePackagePath).Length
    sha256 = $updatePackageHash.Hash
    releaseNotesUrl = "https://updates.1salem.app/server-manager/stable/$releaseVersion/RELEASE_NOTES.md"
    publishedAt = [DateTimeOffset]::UtcNow.ToString('O')
    requiresElevation = $true
    requiresServiceRestart = $true
    requiresFullSetup = $false
}
$versionPath = Join-Path $releaseRoot 'version.json'
$versionDocument | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $versionPath -Encoding utf8

$checksumFiles = @(
    'Setup.exe',
    'Portable.zip',
    'Source.zip',
    $updatePackageName,
    'version.json',
    'RELEASE_NOTES.md'
)
$checksumLines = foreach ($name in $checksumFiles) {
    $hash = Get-FileHash -LiteralPath (Join-Path $releaseRoot $name) -Algorithm SHA256
    "$($hash.Hash) *$name"
}
$checksumLines | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') `
    -Encoding ascii

Write-Host "Release artifacts created in $releaseRoot"
Get-ChildItem -LiteralPath $releaseRoot | Select-Object Name, Length
