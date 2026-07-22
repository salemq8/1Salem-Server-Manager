# Changelog

## 1.3.1 - 2026-07-18

- Fixed Custom memory-policy selection so the domain policy activates,
  persists, and controls editability through one authoritative selected value.
- Added locale-aware decimal GiB editing, inline validation, pending-versus-
  active values, Save, Cancel, and Apply Recommended without refresh-time input
  loss.
- Corrected startup budgeting to use current physical memory, Windows reserve,
  actual live registered-server memory, and a startup allowance while ignoring
  absent, stopped, exited, and stale processes.
- Removed absent Minecraft from resource summaries, priority controls, audit
  events, diagnostics, confirmations, and server budgets.
- Added a confirmed, audited, manual-only one-time unsafe start override.
- Added regression coverage for selection, editing, persistence, locale input,
  validation, stale metrics, budget calculations, conditional summaries, and
  distinct selected and hover states.

## 1.3.0 - 2026-07-18

- Fixed Palworld management activation by editing and validating the exact live
  `PalWorldSettings.ini`, preserving unknown values and encoding, atomically
  replacing it, and rolling back failed REST activation.
- Added localhost-only REST enable, retry, repair, test, and disable workflows
  with DPAPI-protected credentials and explicit activation-stage errors.
- Added categorized bilingual world settings, official defaults, presets,
  validation, compare/import/export, configuration history, save/restart
  verification, and rollback.
- Added native Palworld memory thresholds, Windows reserve, shared game-server
  budget, optional save-before-enforcement hard limit, and verified process-tree
  priority/affinity profiles.
- Added Save World Now, save-before-backup, destination validation, SHA-256 and
  manifest verification, schedules, storage retention, protected backups, and
  staged restore with safety backup and rollback.
- Redesigned navigation and Palworld controls with calm navy/teal styling,
  focused actions, readable states, high-DPI behavior, and Arabic RTL support.
- Migrated the application database to schema version 3 without removing
  existing game, tunnel, backup, credential, or ProgramData records.

## 1.2.1 - 2026-07-18

- Corrected Palworld CPU and memory metrics by aggregating the complete managed
  Windows Job Object process tree, with separate root/game PIDs and child count.
- Added localhost-only official Palworld REST polling for players, FPS, uptime,
  server identity, explicit unavailable states, backoff, and a confirmed guided
  enable/restart/verification flow.
- Added separate local and Internet/Playit addresses across Palworld, Home,
  Network, creation results, players, and tray while preserving honest offline
  and unverified tunnel states.
- Fixed global tab states, disabled button labels, keyboard focus, PerMonitorV2
  high-DPI behavior, and wrapped backup/update status.
- Added process-tree, REST parsing, Playit offline, address, layout, and
  accessibility regression coverage.

## 1.2.0 - 2026-07-18

- Added official Playit detection, hidden supervision, account-claim link
  handling, verified status, crash recovery, duplicate prevention, and
  Minecraft/Palworld tunnel guidance.
- Added Stable/Beta application update channels, HTTPS manifest validation,
  verified staging, transactional replacement, Agent health checks, protected
  rollback snapshots, and automatic rollback.
- Added `1SalemServerManager-Update-1.2.0.zip` to release output.
- Added comprehensive Playit and application updater security/recovery tests.

## 1.1.0 - 2026-07-17

- Replaced the foundation-only dashboard with live Home, Minecraft, Palworld,
  network, resource, console, settings, players, files, backups, and update
  experiences.
- Added a five-step Minecraft creation wizard with official metadata, verified
  JAR download, compatible Java detection/installation, EULA gating, firewall,
  first launch, readiness, and port verification.
- Added persisted Xms/Xmx memory controls using `ProcessStartInfo.ArgumentList`.
- Added local adapter selection and IP/port display throughout the app and tray.
- Added structured startup diagnostics, state-aware actions, Agent recovery,
  Palworld configuration, and SQLite schema migration version 2.
- Expanded automated coverage beyond the previous 102 tests.

## 1.0.1 - 2026-07-17

### Fixed

- Corrected `sc.exe create` and `sc.exe config` argument tokenization. Options
  such as `binPath=`, `start=`, and `DisplayName=` are now separate
  `ProcessStartInfo.ArgumentList` tokens and work with Program Files paths.
- Added idempotent repair for stopped, running, stale, partially created, and
  deletion-pending Agent services, including bounded state waits and local
  health verification.
- Added safe application-file rollback that never includes ProgramData,
  Minecraft worlds, Palworld saves, server configurations, or backups.

### Changed

- Setup now records structured per-operation logs, shows concise failure
  details, reaches a clear failed state, and provides Retry, Open Log, Copy
  Details, Close, and Finish actions.
- Firewall rule failures are reported independently as warnings.
- Applied the official 1Salem Server Manager icon to executables, WPF windows,
  taskbar, tray, shortcuts, About, Setup branding, and Apps & Features.

## 1.0.0 - 2026-07-17

### Added

- Native WPF Client, Windows Service Agent, SQLite persistence, JSONL logs, and
  bounded named-pipe transport.
- Minecraft vanilla install, import, configuration, Java detection, version
  selection, official download verification, JAR swap, update, and rollback.
- Palworld vanilla install/import using isolated SteamCMD, official settings,
  DPAPI-protected passwords, update validation, and save/config preservation.
- Windows Job Object process supervision, console, logs, metrics, graceful
  stop, crash-loop protection, and optional restart policies.
- Verified backups with per-file SHA-256 manifests, schedules, retention,
  restore staging, safety backup, restart verification, and rollback.
- Resource Governor with safe priority profiles, CPU affinity, Windows memory
  reserve, total server budget, and warning-only critical-memory defaults.
- Opt-in HTTPS LAN access, protected certificate identity, short-lived one-use
  pairing codes, rate limiting, certificate pinning, token hashing, revocation,
  and authenticated SignalR status/log streaming.
- Native setup host, service/firewall/startup administration, normal and
  Administrator shortcuts, UAC relaunch, and data-preserving uninstall.
- English/Arabic UI resources, RTL flow, dark/light themes, high contrast,
  accessibility cues, network display, and redacted diagnostic ZIP export.
- Root-restricted file manager and unified game control, update, console, and
  bounded log windows.

### Security

- No Realtime process priority.
- No plaintext credentials in SQLite.
- No public listener by default.
- No arbitrary shell API.
- No game-data deletion during uninstall.
- Typed confirmation and safety copy before file deletion.
