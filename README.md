# 1Salem Server Manager

1Salem Server Manager is a native Windows dashboard and background Agent
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
- Unified dark/light/Windows-following design system, responsive navigation,
  readable control states, inline validation, and immediate English/Arabic
  appearance switching.
- Transactional Minecraft and Palworld configuration writes with staged
  validation, unknown-value preservation, protected restore points, and
  verified restart recovery.
- A compact Palworld Overview with status badges, six live metric cards, quick
  actions, bounded activity graphs, recent activity, resource summaries, and
  separate local/Internet connection cards.
- VERSION-driven product metadata, semantic downgrade prevention, immutable
  release folders, per-component installed-version reporting, and a permanent
  Stable launcher with atomic active-version switching.
- 1Salem Connect private friend access for Minecraft: one-time invitations,
  owner approval and revocation, and a separate friend app that reaches only
  the approved server through a local address, with no system VPN, route, DNS
  or proxy change (see [Connect architecture](docs/CONNECT_ARCHITECTURE.md)).

## Release files

The release build reads the current value from `VERSION` and writes:

- Rolling Stable output: `artifacts/release/1.5/`
- Visible product version remains `1.5`; normal releases increment `BUILD_REVISION` only.
- Validated candidates are created under `artifacts/staging/release-candidates/` and promoted only after the installed update succeeds.
- The rolling directory contains Setup.exe, Portable.zip, Source.zip, the update ZIP, the 1Salem Connect friend app (`1SalemConnect-Setup.exe` and `1SalemConnect-Portable.zip`), `1SalemConnect-update.json` (what installed 1Salem Connect copies update themselves from), version.json, build-info.json, SHA256SUMS.txt, and RELEASE_NOTES.md.
- `tools\publish-github-release.ps1` publishes the promoted release for the pushed tag `v<VERSION>-build-<N>`: every file in SHA256SUMS.txt plus SHA256SUMS.txt, verified after upload, then marked latest. Installed 1Salem Connect copies find the new build from that release on their own.

`VERSION` is the only manually edited current product-version source. Use
`tools\next-build.ps1` to calculate the next internal Build without changing VERSION.
`tools\next-version.ps1` is reserved for a future product-version change explicitly authorized by Salem.
Use `tools\build-release.ps1` to enforce the installed/released Build guard.

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
