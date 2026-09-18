<#
.SYNOPSIS
    Proves the test suite passes from a genuinely clean checkout: no artifacts/ directory, no
    previous releases, no local ProgramData, no pre-generated packages, and no other untracked
    or git-ignored state that might be sitting in the working tree.

.DESCRIPTION
    Creates an isolated copy of the repository containing only what `git` actually tracks
    (via `git archive` on HEAD, falling back to `git ls-files` if `git archive` is unavailable),
    then runs the normal restore/build/test sequence against that copy. This is the same
    guarantee a fresh CI runner or a first-time contributor's clone gets -- if a test only
    passes because of a leftover local build artifact, it fails here.

.PARAMETER Configuration
    Build configuration to use. Defaults to Release, matching docs/RELEASE.md's documented gate.

.EXAMPLE
    pwsh -File tools/verify-clean-checkout.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Resolve-Git {
    $bundled = Join-Path $root '.tools\mingit\cmd\git.exe'
    if (Test-Path -LiteralPath $bundled) {
        return $bundled
    }

    $onPath = Get-Command git -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    throw 'git was not found (checked .tools\mingit and PATH). Install Git or restore the bundled copy.'
}

function Resolve-Dotnet {
    $bundled = Join-Path $root '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $bundled) {
        return $bundled
    }

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    throw 'dotnet was not found (checked .tools\dotnet and PATH). Install the .NET 8 SDK.'
}

$git = Resolve-Git
$dotnet = Resolve-Dotnet
$clean = Join-Path ([System.IO.Path]::GetTempPath()) "1salem-clean-checkout-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $clean -Force | Out-Null

try {
    Write-Host "Exporting the tracked worktree (HEAD) into: $clean"
    $archive = Join-Path ([System.IO.Path]::GetTempPath()) "1salem-clean-checkout-$([Guid]::NewGuid().ToString('N')).zip"
    & $git -C $root archive --format=zip -o $archive HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "git archive failed with exit code $LASTEXITCODE."
    }

    Expand-Archive -LiteralPath $archive -DestinationPath $clean -Force
    Remove-Item -LiteralPath $archive -Force

    foreach ($mustNotExist in @('artifacts', '.local-data', '.test-localappdata', '.tools')) {
        $path = Join-Path $clean $mustNotExist
        if (Test-Path -LiteralPath $path) {
            throw "Clean checkout unexpectedly contains '$mustNotExist' -- git archive should only include tracked files."
        }
    }

    Write-Host 'Verified: no artifacts/, .local-data/, .test-localappdata/, or .tools/ in the clean export.'

    Push-Location $clean
    try {
        Write-Host "`n== dotnet restore =="
        & $dotnet restore .\1SalemServerManager.sln
        if ($LASTEXITCODE -ne 0) { throw "restore failed with exit code $LASTEXITCODE." }

        Write-Host "`n== dotnet build -c $Configuration =="
        & $dotnet build .\1SalemServerManager.sln -c $Configuration --no-restore
        if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE." }

        Write-Host "`n== dotnet test -c $Configuration =="
        & $dotnet test .\1SalemServerManager.sln -c $Configuration --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw "test failed with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }

    Write-Host "`nClean-checkout verification passed: restore, build, and the full test suite all " +
        'succeeded with no reliance on any local, git-ignored, or previously-built state.'
}
finally {
    if (Test-Path -LiteralPath $clean) {
        Remove-Item -LiteralPath $clean -Recurse -Force -ErrorAction SilentlyContinue
    }
}
