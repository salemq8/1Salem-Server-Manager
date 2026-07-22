# Architecture

## Components

```text
ServerManager.Client (native WPF)
    | local: named pipe + loopback HTTP
    | LAN: HTTPS + authenticated SignalR
    v
ServerManager.Agent (ASP.NET Core / Windows Service)
    +-- process supervision and Windows Job Objects
    +-- Minecraft and Palworld providers
    +-- update, backup, restore, files, resources, network
    +-- SQLite and JSONL audit/operational logs
```

`ServerManager.Contracts` contains transport-safe contracts and enums.
`ServerManager.Core` contains models, validation policies, and interfaces.
`ServerManager.Infrastructure` implements storage, games, processes, security,
backup, file, and Windows integrations. The Agent owns long-running and
privileged work. The Client never opens the Agent database directly.

## Data and process ownership

- Default Agent data: `C:\ProgramData\1SalemServerManager`
- UI preferences and protected LAN profile:
  `%LOCALAPPDATA%\1SalemServerManager`
- Game roots are selected during install/import and stored in SQLite.
- Agent-launched game processes are assigned to Windows Job Objects.
- Closing the dashboard does not stop the Agent or game servers.

## Local transport

Named-pipe frames use a four-byte little-endian length prefix and UTF-8 JSON.
Frames larger than 1 MiB are rejected. The pipe dispatcher has a fixed
operation allowlist. The loopback API binds to `127.0.0.1:5251`.

## LAN transport

LAN is disabled by default. `--lan` adds an HTTPS listener (default port 5252)
with an Agent certificate stored as a DPAPI-protected PFX envelope. Pairing
codes are six digits, single use, valid for five minutes, and rate-limited.
The client pins the certificate SHA-256 fingerprint and stores its bearer
credential using DPAPI. The Agent stores only SHA-256 credential hashes.

SignalR exposes whitelisted process status and bounded/redacted log events. It
does not expose a shell terminal.

## Persistence

SQLite uses WAL, foreign keys, parameterized commands, a busy timeout, and
tables for settings, agents, clients, game servers, runtime settings, updates,
backups, schedules, audit events, and crash history.

## Safety boundaries

- Install/update content is staged before finalization.
- Existing destinations are not silently overwritten.
- Updates and restores create safety backups and verify restart outcomes.
- Imports default to Copy; Move requires exact typed confirmation.
- File operations resolve under a registered root and reject reparse points.
- Text edits are capped at 2 MiB and important changes create safety copies.
- Resource profiles reserve memory for Windows and reject Realtime priority.

## Version 1.2 services

`OfficialPlayitSupervisor` runs inside the Agent, locates the official
executable, refuses duplicates, launches without a visible window, redacts
supported output, and applies bounded crash recovery. Its startup gate runs
before game auto-start; failure never blocks local game management.

`ApplicationUpdateCoordinator` checks the approved Stable/Beta manifest and
stages a verified package under Agent ProgramData. The external updater
snapshots only application binaries, swaps Client/Agent directories, restarts
the Agent only when requested, verifies health and versions, and restores the
previous directories on failure. Game roots and Agent ProgramData are never
payload targets.
