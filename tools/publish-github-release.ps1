[CmdletBinding()]
param(
    [string]$Repository = 'salemq8/1Salem-Server-Manager'
)

# Publishes the promoted canonical release (artifacts\release\<VERSION>) as the GitHub Release
# "1Salem Server Manager <VERSION> — Build <N>" for the already-pushed tag v<VERSION>-build-<N>.
# The assets are exactly the files SHA256SUMS.txt lists plus SHA256SUMS.txt itself, so every file
# the release pipeline adds (such as 1SalemConnect-update.json, which installed 1Salem Connect
# copies read to update themselves) is published without editing this script. The release is
# created as a draft, every upload is checked against SHA256SUMS.txt, and only then is it
# published and marked latest. Existing releases are never modified.
#
# The GitHub token comes from Git Credential Manager (`git credential fill`), stays in memory and
# is never printed or written anywhere.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$buildRevision = [int](Get-Content -LiteralPath (Join-Path $root 'BUILD_REVISION') -Raw).Trim()
$canonical = Join-Path $root "artifacts\release\$version"
$tag = "v$version-build-$buildRevision"
$title = "1Salem Server Manager $version " + [char]0x2014 + " Build $buildRevision"

$git = Join-Path $root '.tools\mingit\cmd\git.exe'
if (-not (Test-Path -LiteralPath $git)) {
    $git = (Get-Command git -ErrorAction Stop).Source
}

# --- Local checks: the canonical folder is this build, and every file matches SHA256SUMS.txt ---
$info = Get-Content -LiteralPath (Join-Path $canonical 'build-info.json') -Raw | ConvertFrom-Json
if ($info.productVersion -ne $version -or [int]$info.buildRevision -ne $buildRevision) {
    throw "The canonical release is not $version Build $buildRevision. Promote it first (tools\promote-release.ps1)."
}

$expected = [ordered]@{}
foreach ($line in Get-Content -LiteralPath (Join-Path $canonical 'SHA256SUMS.txt')) {
    if ($line -match '^([0-9A-Fa-f]{64}) \*(.+)$') {
        $expected[$Matches[2]] = $Matches[1].ToLowerInvariant()
    }
}
if ($expected.Count -eq 0) {
    throw 'SHA256SUMS.txt lists no files.'
}
foreach ($name in @($expected.Keys)) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $canonical $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $expected[$name]) {
        throw "Canonical file does not match SHA256SUMS.txt: $name"
    }
}
$expected['SHA256SUMS.txt'] = (Get-FileHash -LiteralPath (Join-Path $canonical 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
$assets = @($expected.Keys)

$connectUpdate = Get-Content -LiteralPath (Join-Path $canonical '1SalemConnect-update.json') -Raw | ConvertFrom-Json
if ($connectUpdate.releaseTag -ne $tag -or [int]$connectUpdate.buildRevision -ne $buildRevision -or
    -not $expected.Contains('1SalemConnect-update.json')) {
    throw "1SalemConnect-update.json does not describe $tag."
}

# --- GitHub ---
$env:GIT_TERMINAL_PROMPT = '0'
# Git needs LF-only input without a byte-order mark; Windows PowerShell pipes alter both, so cmd
# redirects a query file (protocol and host only, no secret).
$query = [System.IO.Path]::GetTempFileName()
try {
    [IO.File]::WriteAllBytes($query, [Text.Encoding]::ASCII.GetBytes("protocol=https`nhost=github.com`n`n"))
    $fill = & cmd.exe /d /c "`"$git`" credential fill < `"$query`""
}
finally {
    Remove-Item -LiteralPath $query -Force -ErrorAction SilentlyContinue
}
$token = ($fill | Where-Object { $_ -like 'password=*' } | Select-Object -First 1)
$fill = $null
if (-not $token) {
    throw 'No GitHub credential is available from Git Credential Manager.'
}
$token = $token.Substring(9).Trim()

$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromMinutes(60)
$http.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token)
$token = $null
$http.DefaultRequestHeaders.UserAgent.ParseAdd('1Salem-release-publisher')
$http.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')
$http.DefaultRequestHeaders.Add('X-GitHub-Api-Version', '2022-11-28')

function Send($method, $url, $json) {
    $request = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), $url)
    if ($null -ne $json) {
        $request.Content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
    }
    $response = $http.SendAsync($request).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw "$method $url -> $([int]$response.StatusCode): $body"
    }
    if ($body) { return ($body | ConvertFrom-Json) } else { return $null }
}

try {
    $null = Send 'GET' "https://api.github.com/repos/$Repository/git/ref/tags/$tag" $null
}
catch {
    if ($_.Exception.Message -match '-> 404') {
        throw "The tag $tag is not on GitHub yet. Push it first (git push origin $tag)."
    }
    throw
}

# Drafts are invisible to /releases/tags/<tag>, so the full list is searched (it includes drafts
# for a token that can push). A draft left by an earlier failed run is reported, never duplicated.
$existing = @(Send 'GET' "https://api.github.com/repos/$Repository/releases?per_page=100" $null) |
    Where-Object { $_.tag_name -eq $tag } |
    Select-Object -First 1
if ($existing) {
    $kind = if ($existing.draft) { 'A draft release' } else { 'A release' }
    throw "$kind for $tag already exists and is left untouched: $($existing.html_url)"
}

$notes = [IO.File]::ReadAllText((Join-Path $canonical 'RELEASE_NOTES.md'), [Text.Encoding]::UTF8)
$payload = @{ tag_name = $tag; name = $title; body = $notes; draft = $true; prerelease = $false } | ConvertTo-Json -Depth 3
$release = Send 'POST' "https://api.github.com/repos/$Repository/releases" $payload
Write-Host "draft release id $($release.id)"

$uploaded = @{}
foreach ($asset in $assets) {
    $path = Join-Path $canonical $asset
    $stream = [IO.File]::OpenRead($path)
    try {
        $content = New-Object System.Net.Http.StreamContent($stream)
        $content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue('application/octet-stream')
        $content.Headers.ContentLength = $stream.Length
        $url = "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($asset))"
        $response = $http.PostAsync($url, $content).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "upload $asset -> $([int]$response.StatusCode): $body"
        }
        $uploaded[$asset] = $body | ConvertFrom-Json
        Write-Host ("uploaded {0,-36} {1,12} bytes" -f $asset, $uploaded[$asset].size)
    }
    finally {
        $stream.Dispose()
    }
}

# Verify each upload: GitHub's own digest when present, otherwise download it and hash it.
$failures = @()
$sha = [Security.Cryptography.SHA256]::Create()
foreach ($asset in $assets) {
    $remoteAsset = Send 'GET' "https://api.github.com/repos/$Repository/releases/assets/$($uploaded[$asset].id)" $null
    $remote = $null
    $how = 'digest'
    if ($remoteAsset.digest -and $remoteAsset.digest -like 'sha256:*') {
        $remote = $remoteAsset.digest.Substring(7).ToLowerInvariant()
    }
    else {
        $how = 'download'
        $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $remoteAsset.url)
        $request.Headers.Accept.Clear()
        $request.Headers.Accept.ParseAdd('application/octet-stream')
        $response = $http.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "download $asset -> $([int]$response.StatusCode)"
        }
        $content = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        try { $remote = ([BitConverter]::ToString($sha.ComputeHash($content))).Replace('-', '').ToLowerInvariant() } finally { $content.Dispose() }
    }
    $ok = ($remote -eq $expected[$asset]) -and ([int64]$remoteAsset.size -eq (Get-Item -LiteralPath (Join-Path $canonical $asset)).Length)
    if (-not $ok) { $failures += $asset }
    Write-Host ("verify   {0,-36} {1} ({2})" -f $asset, $(if ($ok) { 'MATCH' } else { 'MISMATCH' }), $how)
}
if ($failures.Count -gt 0) {
    throw "Hash verification failed; the release stays a draft: $($failures -join ', ')"
}

$published = Send 'PATCH' "https://api.github.com/repos/$Repository/releases/$($release.id)" (@{ draft = $false; make_latest = 'true' } | ConvertTo-Json)
Write-Host "published: $($published.html_url)"
Write-Host "tag: $($published.tag_name)  name: $($published.name)  assets: $(@($published.assets).Count)"
