# 1Salem Server Manager

1Salem Server Manager 1.3.1 is a native Windows dashboard and background Agent
for vanilla Minecraft Java and vanilla Palworld dedicated servers.

## Highlights

- Native .NET 8 WPF dashboard; no Electron or browser runtime.
- Windows Service Agent that continues when the dashboard closes.
- All-in-One, Client-only, and Agent-only installation modes.
- Official Minecraft server downloads with published SHA-1 and size checks.
- Isolated SteamCMD Palworld installation using app ID `2394010`.
- Graceful start, stop, restart, force-stop confirmation, crash detection, and
  bounded console/log capture.
- Verified ZIP backups, schedules, retention policy, safe restore, and rollback.
- Safe update staging, pre-update backup, restart verification, and rollback
  where supported.
- Balanced, Minecraft Priority, Palworld Priority, One Game at a Time, and
  Custom resource profiles. Realtime priority is always rejected.
- Opt-in HTTPS LAN Agent with five-minute pairing codes, SHA-256 certificate
  pinning, hashed credentials, client revocation, and authenticated SignalR.
- English and Arabic navigation with RTL support, dark/light themes, high
  contrast behavior, and redacted diagnostic exports.
- Root-restricted file manager with reparse-point blocking and safety copies.
- Official Playit agent supervision for Minecraft and Palworld Remote Access.
- Transactional in-application updates with HTTPS manifests, SHA-256 package
  verification, health checks, and automatic rollback.
- Palworld Control Center with verified localhost REST activation, live player
  and FPS status, safe world-setting presets, and configuration history.
- Save World Now, verified ZIP backups, selectable destinations, schedules,
  protected retention, SHA-256 validation, and rollback-safe restore.
- Native-process memory thresholds, shared server budget, optional hard limit,
  and verified root/child process priority and affinity profiles.

## Release files

The release build writes:

- `artifacts/release/1.3.1/Setup.exe`
- `artifacts/release/1.3.1/Portable.zip`
- `artifacts/release/1.3.1/Source.zip`
- `artifacts/release/1.3.1/1SalemServerManager-Update-1.3.1.zip`
- `artifacts/release/1.3.1/version.json`
- `artifacts/release/1.3.1/SHA256SUMS.txt`

Version 1.3.1 makes memory-policy presets authoritative and persistent, enables
validated Custom editing without refresh-time data loss, removes absent games
from resource summaries and budgets, and adds a recalculated manual one-time
startup override.

See [Installation](docs/INSTALLATION.md), the
[English guide](docs/USER_GUIDE_EN.md), or the
[Arabic guide](docs/USER_GUIDE_AR.md).

## Build from source

Windows 10/11 x64 and the .NET 8 SDK are required.

```powershell
dotnet restore .\1SalemServerManager.sln
dotnet build .\1SalemServerManager.sln -c Release --no-restore
dotnet test .\1SalemServerManager.sln -c Release --no-build --no-restore
```

For an interactive development run:

```powershell
dotnet .\src\ServerManager.Agent\bin\Release\net8.0\1Salem.ServerManager.Agent.dll --data-root .\.local-data
```

In another terminal:

```powershell
.\src\ServerManager.Client\bin\Release\net8.0-windows\1Salem.ServerManager.exe
```

The Agent binds only to `127.0.0.1:5251` unless it is explicitly started with
`--lan`. Existing game folders are never scanned, moved, or deleted without a
user-selected import operation and its required confirmation.

## Official upstream sources

- Minecraft server: <https://www.minecraft.net/en-us/download/server>
- Minecraft version metadata: <https://piston-meta.mojang.com/mc/game/version_manifest_v2.json>
- Palworld dedicated server documentation:
  <https://docs.palworldgame.com/getting-started/deploy-dedicated-server/>

## License and support

This repository does not redistribute Minecraft or Palworld game server
content. Server content is downloaded from official upstream services during
the user-initiated installation flow.
