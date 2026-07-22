# 1Salem Server Manager 1.3.0 Baseline Audit

Date: 2026-07-18

This audit records the state of the installed 1.3.1 application before the
stabilization and UI/UX work. The live Palworld server and Playit configuration
were inspected read-only. No game settings, tunnels, saves, backups, or
processes were changed.

## Build and test baseline

- Repository version: 1.3.1
- Release build: passed with 0 warnings and 0 errors
- Automated tests: 224 passed
  - Core: 81
  - Setup: 17
  - Agent: 15
  - Client: 30
  - Infrastructure: 81
- Existing release artifacts must be preserved before the explicitly requested
  final `artifacts/release/1.3.0` target is rebuilt.

## Live read-only baseline

- Client and Agent: 1.3.1
- Agent: connected locally
- Palworld: running, 0/32 players
- Playit: Online, Linked, Verified
- Local address: `192.168.3.66:8211`
- Public address: `click-jackets.gl.at.ply.gg:7551`
- Palworld build: 24181105
- Observed server FPS: 59
- Observed process-tree RAM: approximately 0.9 GB
- Minecraft: no registered server

Process identifiers and uptime are intentionally not treated as stable audit
values because they change whenever the relevant process restarts.

## Confirmed defects

### Critical

1. Palworld World Settings is blank even though the Agent endpoint is healthy
   and returns the configuration catalog. The control can receive its server ID
   before it is loaded, then skip its first data load.
2. Text boxes and combo boxes use Windows defaults in the dark theme. Their
   white surfaces and low-contrast selected text make settings difficult or
   impossible to read.
3. About and Diagnostics reports `Version 1.0` instead of the installed product
   version.

### High

1. Application Update actions remain enabled before a usable update is
   available.
2. The initial game-update status can be blank and update actions are not
   consistently state-aware.
3. Language and theme are handled in a separate sparse window. Theme choices
   are limited to Dark and Light; Follow Windows is absent.
4. Several ordinary workflows open sparse secondary windows instead of staying
   in the main dashboard.
5. The Palworld page has no dedicated Diagnostics tab.
6. The console lacks timestamp controls, severity filters, command history and
   clear separation between copying selected and visible output.
7. The global backup window exposes long raw paths and an unreadable default
   combo box in the dark theme.

### Medium

1. Navigation omits direct About, Files, Logs, and Network destinations even
   though page content and view-model properties already exist.
2. Multiple pages use rows of equally prominent accent buttons, weakening
   action hierarchy.
3. Remote Access shows a Minecraft tunnel entry even when Minecraft is not
   registered.
4. The main settings page is a launcher rather than an in-page settings
   experience.
5. Some status text is visually concatenated and scan order is inconsistent.

## Preservation rules

- Do not modify unrelated Playit tunnels.
- Do not remove or overwrite ProgramData, worlds, player saves, game
  configuration, backups, SteamCMD data, or previous release artifacts.
- Validate setting and recovery changes against isolated fixtures wherever
  possible before any production operation.
- The final package uses the explicitly requested product version 1.3.0.
