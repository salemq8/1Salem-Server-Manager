<#
.SYNOPSIS
Stores, reports or removes the Tailscale OAuth client the tsnet smoke test uses.

.DESCRIPTION
Run it yourself, in your own PowerShell; the smoke harness never asks for the secret. Without a
switch it prompts for the OAuth client ID and secret (the secret is read hidden and never shown)
and stores both, encrypted with DPAPI for the current Windows user, in
%LOCALAPPDATA%\1Salem Connect Smoke\oauth-client.dpapi. That folder is limited to the current user
and SYSTEM. Only connect\proof\TsnetSmoke (started by Run-TsnetSmoke.ps1 -Live) reads it. Nothing
here contacts Tailscale.

.PARAMETER Status
Says whether a stored client exists. It never prints it.

.PARAMETER Remove
Deletes the stored client and its folder.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File connect\proof\Set-TsnetSmokeCredential.ps1 -Status
#>
[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(ParameterSetName = 'Status')][switch]$Status,
    [Parameter(ParameterSetName = 'Remove')][switch]$Remove
)

$ErrorActionPreference = 'Stop'
$directory = Join-Path $env:LOCALAPPDATA '1Salem Connect Smoke'
$file = Join-Path $directory 'oauth-client.dpapi'
# Must match SmokeCredential.Entropy in TsnetSmoke. It ties the blob to this purpose; not a secret.
$entropyText = '1Salem.TsnetSmoke.OAuthClient.v1'
$secretPrefix = 'tskey-client-'

# A folder that is a junction or link, or that another account owns, is never used: whoever
# controls it could read or replace the stored client.
function Assert-OwnDirectory {
    $item = Get-Item -LiteralPath $directory -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "$directory is a junction or link; refusing to use it."
    }
    $owner = (Get-Acl -LiteralPath $directory).GetOwner([Security.Principal.SecurityIdentifier])
    if ($owner -ne [Security.Principal.WindowsIdentity]::GetCurrent().User) {
        throw "$directory is owned by $owner, not by you; refusing to use it."
    }
}

# Only the current user and SYSTEM, nothing inherited, so the file created inside gets the same.
function Set-PrivateDirectory {
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    Assert-OwnDirectory
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $system = New-Object Security.Principal.SecurityIdentifier ([Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    foreach ($sid in [Security.Principal.WindowsIdentity]::GetCurrent().User, $system) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule ($sid, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $directory -AclObject $acl
}

# Windows PowerShell 5.1 finds DPAPI in System.Security; PowerShell 7 ships it as its own assembly.
function Import-Dpapi {
    foreach ($assembly in 'System.Security', 'System.Security.Cryptography.ProtectedData') {
        if ('System.Security.Cryptography.ProtectedData' -as [type]) { return }
        try { Add-Type -AssemblyName $assembly } catch { Write-Verbose "$assembly not loaded: $($_.Exception.Message)" }
    }
    if (-not ('System.Security.Cryptography.ProtectedData' -as [type])) {
        throw 'DPAPI (System.Security.Cryptography.ProtectedData) is not available in this PowerShell.'
    }
}

function Test-Token([string]$Value) {
    return $Value.Length -gt 0 -and $Value.Length -le 256 -and $Value -notmatch '[\s\p{Cc}]'
}

if ($Status) {
    if (Test-Path -LiteralPath $file) {
        Write-Host "An OAuth client is stored at $file (not shown)."
    } else {
        Write-Host "No OAuth client is stored ($file does not exist)."
    }
    exit 0
}

if ($Remove) {
    if (-not (Test-Path -LiteralPath $directory)) {
        Write-Host 'Nothing to remove.'
        exit 0
    }
    Assert-OwnDirectory
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    # Anything else in the folder is not ours to delete, so it is reported and the folder stays.
    # (Remove-Item on a folder with children would offer to delete them all.)
    $others = @(Get-ChildItem -LiteralPath $directory -Force | ForEach-Object { $_.Name })
    if ($others.Count -gt 0) {
        Write-Host "Removed $file. The folder $directory also holds $($others -join ', '), which this script did not create, so it was left in place."
        exit 1
    }
    # Not recursive: this throws rather than delete anything that appeared in the meantime.
    [IO.Directory]::Delete($directory, $false)
    Write-Host "Removed $file and its folder."
    Write-Host 'Also revoke the OAuth client itself: https://console.tailscale.com/admin/settings/trust-credentials, Revoke. It does not expire on its own.'
    exit 0
}

# Read hidden like the secret: a secret pasted into the wrong prompt must not appear on screen.
$secureId = Read-Host 'Tailscale OAuth client ID' -AsSecureString
$idPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureId)
try {
    $clientId = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($idPointer).Trim()
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($idPointer)
}
if ($clientId.StartsWith('tskey-', [StringComparison]::OrdinalIgnoreCase)) {
    # Never echoed: it is most likely the secret itself.
    throw 'That looks like a secret, not a client ID. Nothing was stored.'
}
if (-not (Test-Token $clientId)) {
    throw 'That is not an OAuth client ID. Nothing was stored.'
}

$secure = Read-Host 'Tailscale OAuth client secret (tskey-client-...)' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
$plaintext = $null
try {
    $secret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    # The value is never echoed, even when it is wrong: it may be a real secret of another kind.
    if (-not (Test-Token $secret) -or $secret.Length -le $secretPrefix.Length -or -not $secret.StartsWith($secretPrefix, [StringComparison]::Ordinal)) {
        throw 'That is not an OAuth client secret (tskey-client-...). Nothing was stored.'
    }

    Import-Dpapi
    Set-PrivateDirectory
    $json = ConvertTo-Json -Compress -InputObject ([ordered]@{ clientId = $clientId; clientSecret = $secret })
    $plaintext = [Text.Encoding]::UTF8.GetBytes($json)
    $entropy = [Text.Encoding]::UTF8.GetBytes($entropyText)
    $protected = [Security.Cryptography.ProtectedData]::Protect($plaintext, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($file, $protected)
    Write-Host "Stored the OAuth client at $file (DPAPI, current user only)."
}
finally {
    # The unmanaged copy and the byte buffer are wiped; .NET strings cannot be, so they are dropped.
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    if ($plaintext) { [Array]::Clear($plaintext, 0, $plaintext.Length) }
    $secret = $null
    $json = $null
}
