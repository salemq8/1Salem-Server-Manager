[CmdletBinding()]
param(
    [switch]$SkipValidation,
    [switch]$RepairSameBuild
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $root 'artifacts'
$releaseVersion = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
if ($releaseVersion -notmatch '^\d+\.\d+$') {
    throw "Fixed-version releases require a two-part VERSION. Found '$releaseVersion'."
}
$buildRevisionText = (Get-Content -LiteralPath (Join-Path $root 'BUILD_REVISION') -Raw).Trim()
if ($buildRevisionText -notmatch '^[1-9]\d*$') {
    throw "BUILD_REVISION must contain a positive integer. Found '$buildRevisionText'."
}
$buildRevision = [int]$buildRevisionText

$releaseBase = Join-Path $artifactsRoot 'release'
$canonicalReleaseRoot = Join-Path $releaseBase $releaseVersion
$candidateBase = Join-Path $artifactsRoot 'staging\release-candidates'
$releaseRoot = Join-Path $candidateBase "$releaseVersion-build-$buildRevision"
$stagingRoot = Join-Path $artifactsRoot "staging\$releaseVersion-build-$buildRevision"
$validationRoot = Join-Path $artifactsRoot "validation\$releaseVersion\build-$buildRevision\palworld-overview"
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
    param([Parameter(Mandatory)][string]$Step)
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

function Assert-InsideArtifacts {
    param([Parameter(Mandatory)][string]$Path)
    $candidate = [System.IO.Path]::GetFullPath($Path)
    $prefix = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside artifacts: $candidate"
    }
    return $candidate
}

function New-EmptyStagingDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $resolved = Assert-InsideArtifacts $Path
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolved | Out-Null
}

function Test-ReleaseIntegrity {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][int]$BuildRevision
    )
    $required = @(
        'Setup.exe',
        'Portable.zip',
        'Source.zip',
        "1SalemServerManager-Update-$Version.zip",
        "1SalemConnect-$Version.zip",
        'version.json',
        'build-info.json',
        'SHA256SUMS.txt',
        'RELEASE_NOTES.md'
    )
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $name) -PathType Leaf)) {
            return $false
        }
    }
    try {
        $manifest = Get-Content -LiteralPath (Join-Path $Path 'version.json') -Raw |
            ConvertFrom-Json
        $packageName = "1SalemServerManager-Update-$Version.zip"
        $packagePath = Join-Path $Path $packageName
        if ($manifest.version -ne $Version -or
            [int]$manifest.buildRevision -ne $BuildRevision -or
            $manifest.packageFileName -ne $packageName -or
            $manifest.releaseChannel -ne 'Stable' -or
            $manifest.archiveValidation -ne 'passed' -or
            $manifest.dashboardShutdownMode -ne 'VerifiedExactProcess' -or
            -not ($manifest.releaseNotes -is [string]) -or
            (Get-Item -LiteralPath (Join-Path $Path 'version.json')).Length -gt 1MB -or
            [int64]$manifest.packageSize -ne (Get-Item -LiteralPath $packagePath).Length -or
            $manifest.sha256 -ne (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash) {
            return $false
        }
        $expected = @{}
        foreach ($line in Get-Content -LiteralPath (Join-Path $Path 'SHA256SUMS.txt')) {
            if ($line -match '^([0-9A-Fa-f]{64}) \*(.+)$') {
                $expected[$Matches[2]] = $Matches[1].ToUpperInvariant()
            }
        }
        foreach ($name in $required | Where-Object { $_ -ne 'SHA256SUMS.txt' }) {
            if (-not $expected.ContainsKey($name)) {
                return $false
            }
            $actual = (Get-FileHash -LiteralPath (Join-Path $Path $name) -Algorithm SHA256).Hash
            if ($actual -ne $expected[$name]) {
                return $false
            }
        }
        foreach ($archive in @(
            (Join-Path $Path 'Portable.zip'),
            (Join-Path $Path 'Source.zip'),
            $packagePath)) {
            Assert-ArchiveSafe $archive
        }
        return $true
    }
    catch {
        return $false
    }
}

function New-ZipFromDirectory {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )
    if (Test-Path -LiteralPath $Destination) {
        throw "Archive destination already exists: $Destination"
    }
    Compress-Archive -Path (Join-Path $Source '*') -DestinationPath $Destination `
        -CompressionLevel Optimal
}

function New-SourceArchive {
    param([Parameter(Mandatory)][string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Destination) {
        throw "Source archive destination already exists: $Destination"
    }
    $excluded = @(
        '\.git\',
        '\.agents\',
        '\.tools\',
        '\.local-data\',
        '\.test-localappdata\',
        '\artifacts\',
        '\bin\',
        '\obj\',
        '\TestResults\',
        '\node_modules\',
        '\.wrangler\',
        '\.claude\'
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
            # -ErrorAction SilentlyContinue (only for this enumeration, not the script-wide
            # 'Stop' preference): a subdirectory that is unreadable by the current account --
            # e.g. leftover, ACL-restricted debris from a prior local installed-update/rollback
            # validation run under an excluded path such as .tools\ -- must not abort the whole
            # source archive; it is skipped like any other excluded path, not fatal.
            Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object {
                $candidate = $_.FullName
                # Match exclusions below the repository root only; the root itself may sit under
                # an excluded name (for example a worktree in .claude\worktrees).
                $inRepo = '\' + $candidate.Substring($root.Length).TrimStart('\', '/')
                -not $candidate.Equals(
                    (Join-Path $root 'build-info.json'),
                    [System.StringComparison]::OrdinalIgnoreCase) -and
                # A local broker secrets file never ships (its .example twin does).
                $_.Name -notin @('.dev.vars', '.env') -and
                -not ($excluded | Where-Object {
                    $inRepo.IndexOf($_, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
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
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $generatedBuildInfo,
                'build-info.json',
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-ArchiveSafe {
    param([Parameter(Mandatory)][string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $isSourceArchive = (Split-Path $Path -Leaf) -eq 'Source.zip'
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $names = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($name) -or
                $name.StartsWith('/') -or
                [System.IO.Path]::IsPathRooted($name) -or
                $name -match '^[A-Za-z]:' -or
                ($name.Split('/') | Where-Object { $_ -in '.', '..' })) {
                throw "Unsafe archive path in $Path`: $name"
            }
            if (-not $names.Add($name)) {
                throw "Duplicate or case-colliding archive path in $Path`: $name"
            }
            $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
            if ($unixType -eq 0xA000) {
                throw "Symbolic link in $Path`: $name"
            }
            $segments = $name.Split('/')
            if ($segments -contains '.local-data' -or
                $segments -contains 'SaveGames' -or
                $segments -contains 'ProgramData' -or
                ((-not $isSourceArchive) -and $segments -contains 'backups') -or
                # Code files named after a credential type (TailscaleOAuthCredential.cs) are source.
                $name -match '(?i)(playit\.toml|credentials?\.(?!(cs|ps1)$)|private[-_]?config|\.pfx$|\.pem$|\.key$|\.sav$|\.db$)') {
                throw "Server data or secret-like content in $Path`: $name"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-ProductVersion {
    param([Parameter(Mandatory)][string]$Path)
    $reported = (Get-Item -LiteralPath $Path).VersionInfo.ProductVersion
    if ($reported -ne $releaseVersion) {
        throw "$(Split-Path $Path -Leaf) reports product version '$reported', expected '$releaseVersion'."
    }
}

New-Item -ItemType Directory -Path $releaseBase -Force | Out-Null
New-Item -ItemType Directory -Path $candidateBase -Force | Out-Null

$preflightArguments = @(
    'run',
    '--project', (Join-Path $root 'tools\ServerManager.Versioning\ServerManager.Versioning.csproj'),
    '-c', 'Release',
    '--',
    'preflight',
    '--repo', $root,
    '--target', $releaseVersion,
    '--build-revision', $buildRevision
)
if ($RepairSameBuild) {
    $preflightArguments += '--repair-same-build'
}
& $dotnet @preflightArguments
Assert-Success 'Release version pre-flight'

if (Test-Path -LiteralPath $releaseRoot) {
    if (-not $RepairSameBuild) {
        throw "Release candidate already exists and will not be overwritten: $releaseRoot"
    }

    $failedCandidate = Join-Path $artifactsRoot (
        "staging\failed\{0}-build-{1}-{2}" -f `
            $releaseVersion,
            $buildRevision,
            [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
    New-Item -ItemType Directory -Path (Split-Path $failedCandidate -Parent) -Force | Out-Null
    Move-Item -LiteralPath $releaseRoot -Destination $failedCandidate
    Write-Host "Preserved previous candidate at $failedCandidate"
}
New-Item -ItemType Directory -Path $releaseRoot | Out-Null
New-EmptyStagingDirectory $stagingRoot
New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null

if (-not $SkipValidation) {
    & $dotnet restore (Join-Path $root '1SalemServerManager.sln') -p:NuGetAudit=false
    Assert-Success 'dotnet restore'
    & $dotnet build (Join-Path $root '1SalemServerManager.sln') -c Release --no-restore `
        -p:NuGetAudit=false
    Assert-Success 'dotnet build'
    & $dotnet test (Join-Path $root '1SalemServerManager.sln') -c Release `
        --no-build --no-restore -p:NuGetAudit=false
    Assert-Success 'dotnet test'
    & $dotnet format (Join-Path $root '1SalemServerManager.sln') `
        --verify-no-changes --no-restore
    Assert-Success 'dotnet format --verify-no-changes'
}

$publishRoot = Join-Path $stagingRoot 'publish'
$clientPublish = Join-Path $publishRoot 'Client'
$agentPublish = Join-Path $publishRoot 'Agent'
$updaterPublish = Join-Path $publishRoot 'Updater'
$launcherPublish = Join-Path $publishRoot 'Launcher'
$maintenancePublish = Join-Path $publishRoot 'Maintenance'
$setupPublish = Join-Path $publishRoot 'SetupHost'
$buildRoot = Join-Path $stagingRoot 'build'
$commit = 'unknown'
$git = Get-Command git -ErrorAction SilentlyContinue
$gitPath = if ($null -ne $git) {
    $git.Source
}
elseif (Test-Path -LiteralPath (Join-Path $root '.tools\mingit\cmd\git.exe')) {
    Join-Path $root '.tools\mingit\cmd\git.exe'
}
else {
    $null
}
if ($null -ne $gitPath) {
    $candidateCommit = & $gitPath -C $root rev-parse --short HEAD
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($candidateCommit)) {
        $commit = $candidateCommit.Trim()
    }
}
$builtUtc = [DateTimeOffset]::UtcNow.ToString('O')
$generatedBuildInfo = Join-Path $stagingRoot 'build-info.json'
[ordered]@{
    productVersion = $releaseVersion
    buildRevision = $buildRevision
    builtUtc = $builtUtc
    commit = $commit
} | ConvertTo-Json | Set-Content -LiteralPath $generatedBuildInfo -Encoding utf8

function Publish-Application {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Output,
        [Parameter(Mandatory)][string]$BuildName,
        [switch]$SingleFile,
        [string]$PayloadArchive
    )
    $arguments = @(
        'publish', $Project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:NuGetAudit=false',
        "-p:BaseOutputPath=$((Join-Path $buildRoot $BuildName) + '/')",
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $Output
    )
    if ($SingleFile) {
        $arguments += @(
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true'
        )
    }
    if (-not [string]::IsNullOrWhiteSpace($PayloadArchive)) {
        $arguments += "-p:PayloadArchive=$PayloadArchive"
    }
    & $dotnet @arguments
    Assert-Success "Publish $BuildName"
}

Publish-Application `
    (Join-Path $root 'src\ServerManager.Client\ServerManager.Client.csproj') `
    $clientPublish 'Client'
Publish-Application `
    (Join-Path $root 'src\ServerManager.Agent\ServerManager.Agent.csproj') `
    $agentPublish 'Agent'
Publish-Application `
    (Join-Path $root 'src\ServerManager.Updater\ServerManager.Updater.csproj') `
    $updaterPublish 'Updater'
Publish-Application `
    (Join-Path $root 'src\ServerManager.Launcher\ServerManager.Launcher.csproj') `
    $launcherPublish 'Launcher' -SingleFile
Publish-Application `
    (Join-Path $root 'tools\ServerManager.Setup\ServerManager.Setup.csproj') `
    $maintenancePublish 'Maintenance' -SingleFile

foreach ($output in @(
    $clientPublish,
    $agentPublish,
    $updaterPublish,
    $launcherPublish,
    $maintenancePublish
)) {
    Copy-Item -LiteralPath $generatedBuildInfo -Destination $output
}

# 1Salem Connect. The Go transports are built with -tags ts_omit_oauthkey, so no value handed to
# them can be used as an OAuth client secret to mint keys; refuse to package them otherwise. The
# Agent starts the host transport from its own directory.
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'connect\transport\build.ps1')
Assert-Success 'Build the 1Salem Connect transports'
$transportBin = Join-Path $root 'connect\transport\bin'
$goTool = Join-Path $root '.tools\go\bin\go.exe'
foreach ($transport in '1Salem.Connect.Host.Transport.exe', '1Salem.Connect.Transport.exe') {
    $metadata = & $goTool version -m (Join-Path $transportBin $transport)
    if ($LASTEXITCODE -ne 0 -or -not ($metadata -match 'ts_omit_oauthkey')) {
        throw "$transport was not built with ts_omit_oauthkey."
    }
}
Copy-Item -LiteralPath (Join-Path $transportBin '1Salem.Connect.Host.Transport.exe') -Destination $agentPublish

Copy-Item -LiteralPath $updaterPublish `
    -Destination (Join-Path $clientPublish 'Updater') -Recurse
New-Item -ItemType Directory -Path (Join-Path $clientPublish 'Launcher') | Out-Null
$launcherHost = Get-ChildItem -LiteralPath $launcherPublish -Filter '*.exe' |
    Where-Object { $_.Name -eq '1Salem.ServerManager.Launcher.exe' } |
    Select-Object -First 1
if ($null -eq $launcherHost) {
    throw 'Published Stable launcher was not found.'
}
Copy-Item -LiteralPath $launcherHost.FullName `
    -Destination (Join-Path $clientPublish 'Launcher\1Salem.ServerManager.Launcher.exe')
Copy-Item -LiteralPath $generatedBuildInfo `
    -Destination (Join-Path $clientPublish 'Launcher\build-info.json')

foreach ($path in @(
    (Join-Path $clientPublish '1Salem.ServerManager.exe'),
    (Join-Path $agentPublish '1Salem.ServerManager.Agent.exe'),
    (Join-Path $updaterPublish '1Salem.ServerManager.Updater.exe'),
    (Join-Path $clientPublish 'Launcher\1Salem.ServerManager.Launcher.exe')
)) {
    Assert-ProductVersion $path
}

$portableRoot = Join-Path $stagingRoot 'Portable'
New-Item -ItemType Directory -Path $portableRoot | Out-Null
Copy-Item -LiteralPath $clientPublish -Destination (Join-Path $portableRoot 'Client') -Recurse
Copy-Item -LiteralPath $agentPublish -Destination (Join-Path $portableRoot 'Agent') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $portableRoot
Copy-Item -LiteralPath (Join-Path $root 'CHANGELOG.md') -Destination $portableRoot
Copy-Item -LiteralPath (Join-Path $root 'VERSION') -Destination $portableRoot
Copy-Item -LiteralPath (Join-Path $root 'BUILD_REVISION') -Destination $portableRoot
Copy-Item -LiteralPath $generatedBuildInfo -Destination $portableRoot
New-ZipFromDirectory $portableRoot (Join-Path $releaseRoot 'Portable.zip')

$payloadRoot = Join-Path $stagingRoot 'Payload'
New-Item -ItemType Directory -Path $payloadRoot | Out-Null
Copy-Item -LiteralPath $clientPublish -Destination (Join-Path $payloadRoot 'Client') -Recurse
Copy-Item -LiteralPath $agentPublish -Destination (Join-Path $payloadRoot 'Agent') -Recurse
New-Item -ItemType Directory -Path (Join-Path $payloadRoot 'Maintenance') | Out-Null
$maintenanceHost = Get-ChildItem -LiteralPath $maintenancePublish -Filter '*.exe' |
    Where-Object { $_.Name -eq '1Salem.ServerManager.Setup.exe' } |
    Select-Object -First 1
if ($null -eq $maintenanceHost) {
    throw 'Published maintenance host was not found.'
}
Assert-ProductVersion $maintenanceHost.FullName
Copy-Item -LiteralPath $maintenanceHost.FullName `
    -Destination (Join-Path $payloadRoot 'Maintenance\Uninstall 1Salem Server Manager.exe')
Copy-Item -LiteralPath $generatedBuildInfo `
    -Destination (Join-Path $payloadRoot 'Maintenance\build-info.json')

$updatePackageName = "1SalemServerManager-Update-$releaseVersion.zip"
$updatePackagePath = Join-Path $releaseRoot $updatePackageName
New-ZipFromDirectory $payloadRoot $updatePackagePath

$payloadZip = Join-Path $stagingRoot 'Payload.zip'
New-ZipFromDirectory $payloadRoot $payloadZip
Publish-Application `
    (Join-Path $root 'tools\ServerManager.Setup\ServerManager.Setup.csproj') `
    $setupPublish 'SetupHost' -SingleFile -PayloadArchive $payloadZip
$setupHost = Get-ChildItem -LiteralPath $setupPublish -Filter '*.exe' |
    Where-Object { $_.Name -eq '1Salem.ServerManager.Setup.exe' } |
    Select-Object -First 1
if ($null -eq $setupHost) {
    throw 'Published Setup.exe was not found.'
}
Assert-ProductVersion $setupHost.FullName
Copy-Item -LiteralPath $setupHost.FullName -Destination (Join-Path $releaseRoot 'Setup.exe')
Copy-Item -LiteralPath $generatedBuildInfo -Destination (Join-Path $releaseRoot 'build-info.json')

# The friend app is its own small download: the app, its transport, and the settings that point it
# at the production broker (without them it reports that it is not configured).
$connectPublish = Join-Path $publishRoot 'Connect'
Publish-Application `
    (Join-Path $root 'src\ServerManager.Connect.App\ServerManager.Connect.App.csproj') `
    $connectPublish 'Connect'
Assert-ProductVersion (Join-Path $connectPublish '1Salem.Connect.exe')
Copy-Item -LiteralPath (Join-Path $transportBin '1Salem.Connect.Transport.exe') -Destination $connectPublish
Copy-Item -LiteralPath $generatedBuildInfo -Destination $connectPublish
$connectSettings = [ordered]@{
    brokerUrl = 'https://onesalem-connect-broker-production.onesalemconnect.workers.dev/'
    developmentMode = $false
    transportMode = 'tsnet'
    transportPath = '1Salem.Connect.Transport.exe'
} | ConvertTo-Json
[System.IO.File]::WriteAllText(
    (Join-Path $connectPublish '1Salem.Connect.settings.json'),
    $connectSettings,
    [System.Text.UTF8Encoding]::new($false))
$connectPackageName = "1SalemConnect-$releaseVersion.zip"
New-ZipFromDirectory $connectPublish (Join-Path $releaseRoot $connectPackageName)

New-SourceArchive (Join-Path $releaseRoot 'Source.zip')
$notesSource = Join-Path $root "docs\RELEASE_NOTES_$releaseVersion.md"
if (-not (Test-Path -LiteralPath $notesSource)) {
    throw "Release notes are missing: $notesSource"
}
Copy-Item -LiteralPath $notesSource -Destination (Join-Path $releaseRoot 'RELEASE_NOTES.md')

$updateHash = Get-FileHash -LiteralPath $updatePackagePath -Algorithm SHA256
$notesText = [System.IO.File]::ReadAllText($notesSource)
$published = [DateTimeOffset]::UtcNow.ToString('O')
$versionDocument = [ordered]@{
    version = $releaseVersion
    productVersion = $releaseVersion
    buildRevision = $buildRevision
    minimumSupportedVersion = '1.2.0'
    packageFileName = $updatePackageName
    packageUrl = "https://updates.1salem.app/server-manager/stable/$releaseVersion/$updatePackageName"
    packageSize = (Get-Item -LiteralPath $updatePackagePath).Length
    sha256 = $updateHash.Hash
    releaseChannel = 'Stable'
    releaseNotes = $notesText
    releaseNotesUrl = "https://updates.1salem.app/server-manager/stable/$releaseVersion/RELEASE_NOTES.md"
    publishedAt = $published
    publishedUtc = $published
    rollbackCompatibility = '1.3.2 migration fallback and the immediately previous validated 1.5 Build'
    agentUpdateMode = 'SafeRestartAndReadopt'
    dashboardShutdownMode = 'VerifiedExactProcess'
    requiresElevation = $true
    requiresServiceRestart = $false
    requiresFullSetup = $false
    validationPath = "artifacts/validation/$releaseVersion/build-$buildRevision/palworld-overview"
    archiveValidation = 'pending'
}
$versionDocument | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $releaseRoot 'version.json') -Encoding utf8

foreach ($archive in @(
    (Join-Path $releaseRoot 'Portable.zip'),
    (Join-Path $releaseRoot 'Source.zip'),
    (Join-Path $releaseRoot $connectPackageName),
    $updatePackagePath
)) {
    Assert-ArchiveSafe $archive
    [System.IO.Compression.ZipFile]::OpenRead($archive).Dispose()
}

$versionDocument.archiveValidation = 'passed'
$versionDocument | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $releaseRoot 'version.json') -Encoding utf8

$checksumFiles = @(
    'Setup.exe',
    'Portable.zip',
    'Source.zip',
    $updatePackageName,
    $connectPackageName,
    'version.json',
    'build-info.json',
    'RELEASE_NOTES.md'
)
$checksumLines = foreach ($name in $checksumFiles) {
    $hash = Get-FileHash -LiteralPath (Join-Path $releaseRoot $name) -Algorithm SHA256
    "$($hash.Hash) *$name"
}
$checksumLines | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') `
    -Encoding ascii

$requiredFiles = @(
    'Setup.exe',
    'Portable.zip',
    'Source.zip',
    $updatePackageName,
    $connectPackageName,
    'version.json',
    'build-info.json',
    'SHA256SUMS.txt',
    'RELEASE_NOTES.md'
)
foreach ($name in $requiredFiles) {
    $path = Join-Path $releaseRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release file is missing: $path"
    }
    $stream = [System.IO.File]::OpenRead($path)
    $stream.Dispose()
}
if (-not (Test-ReleaseIntegrity $releaseRoot $releaseVersion $buildRevision)) {
    throw 'The completed release failed its checksum/integrity verification.'
}

Write-Host "Validated release candidate created in $releaseRoot"
Write-Host "Promote it to $canonicalReleaseRoot only after the installed update succeeds."
Get-ChildItem -LiteralPath $releaseRoot | Select-Object Name, Length
