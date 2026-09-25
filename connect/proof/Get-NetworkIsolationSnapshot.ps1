[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputPath
)

# READ-ONLY snapshot of every piece of Windows network configuration 1Salem Connect promises not
# to touch: network adapters (a VPN adapter would appear here), the default routes and the whole
# route table, DNS servers, the per-user WinINet proxy, the machine WinHTTP proxy and proxy
# environment variables. It is taken before and after the Connect proof and the two files must be
# identical. Nothing here changes any setting: only Get-* cmdlets, a registry read and
# "netsh winhttp show".

$ErrorActionPreference = 'Stop'
$lines = New-Object System.Collections.Generic.List[string]

# Adapters, including hidden ones: a Wintun/TUN adapter created by a transport would show up.
foreach ($adapter in Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Sort-Object InterfaceGuid) {
    $lines.Add("ADAPTER $($adapter.InterfaceGuid)|$($adapter.InterfaceDescription)|$($adapter.Name)")
}

# Default gateways (IPv4 and IPv6) with their next hops and interfaces.
foreach ($route in Get-NetRoute -DestinationPrefix '0.0.0.0/0', '::/0' -ErrorAction SilentlyContinue |
             Sort-Object DestinationPrefix, InterfaceIndex, NextHop) {
    $lines.Add("DEFAULTROUTE $($route.DestinationPrefix)|$($route.NextHop)|if$($route.InterfaceIndex)|metric$($route.RouteMetric)")
}

# The whole active route table. Routes an OS renews on its own (for example a DHCP lease) keep
# the same destination, next hop and interface, so they do not appear as differences.
foreach ($route in Get-NetRoute -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
             Sort-Object DestinationPrefix, InterfaceIndex, NextHop) {
    $lines.Add("ROUTE $($route.DestinationPrefix)|$($route.NextHop)|if$($route.InterfaceIndex)")
}

# DNS servers per interface.
foreach ($dns in Get-DnsClientServerAddress -ErrorAction SilentlyContinue |
             Sort-Object InterfaceIndex, AddressFamily) {
    $lines.Add("DNS if$($dns.InterfaceIndex)|$($dns.InterfaceAlias)|$($dns.AddressFamily)|$(($dns.ServerAddresses | Sort-Object) -join ',')")
}

# Per-user WinINet proxy (what browsers and most apps use).
$internet = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$settings = Get-ItemProperty -Path $internet -ErrorAction SilentlyContinue
foreach ($name in 'ProxyEnable', 'ProxyServer', 'ProxyOverride', 'AutoConfigURL', 'AutoDetect') {
    $value = if ($settings -and $settings.PSObject.Properties[$name]) { $settings.$name } else { '<unset>' }
    $lines.Add("WININET $name=$value")
}

# Machine-wide WinHTTP proxy (services, Windows Update). "show advproxy" replaces the deprecated
# "show proxy" on current Windows; both are read-only.
$winhttp = & netsh.exe winhttp show advproxy 2>$null
if ($LASTEXITCODE -ne 0 -or -not $winhttp) { $winhttp = & netsh.exe winhttp show proxy 2>$null }
foreach ($line in $winhttp) {
    $trimmed = "$line".Trim()
    if ($trimmed) { $lines.Add("WINHTTP $trimmed") }
}

# Proxy environment variables for this user and the machine.
foreach ($scope in 'User', 'Machine') {
    foreach ($name in 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY') {
        $value = [Environment]::GetEnvironmentVariable($name, $scope)
        $lines.Add("ENV $scope $name=$(if ($value) { $value } else { '<unset>' })")
    }
}

$directory = Split-Path -Parent $OutputPath
if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
[IO.File]::WriteAllLines($OutputPath, $lines, (New-Object System.Text.UTF8Encoding $false))
Write-Output "$($lines.Count) lines -> $OutputPath"
