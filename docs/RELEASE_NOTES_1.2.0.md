# 1Salem Server Manager 1.2.0

Version 1.2.0 adds official Playit agent management and transactional
in-application updates.

## Remote Access

- Detects and supervises the official Playit executable without reimplementing
  Playit protocols or using account credentials.
- Starts Playit without a visible terminal, prevents duplicate processes,
  bounds and redacts captured output, detects claim links, reports verified
  connectivity, and automatically recovers from crashes.
- Preserves the existing `ALSarabeetMC` agent and Minecraft mapping
  `click-jackets.gl.joinmc.link` to `127.0.0.1:25565`.
- Guides creation of a separate Palworld UDP tunnel to
  `127.0.0.1:8211` through the official Playit portal.
- Keeps local Minecraft and Palworld management running when Playit is offline.

## Application Update

- Adds Stable and Beta channels, manual and automatic checks, release notes,
  download progress, update history, and rollback status.
- Requires an approved HTTPS manifest and package host, exact package size,
  and SHA-256 verification.
- Rejects ZIP traversal, symbolic links, files outside the Client/Agent roots,
  and executable script payloads.
- Stages updates under protected application data, snapshots current
  application binaries, runs the updater outside the install directory,
  replaces Client and Agent directories, restarts the Agent only when required,
  verifies `/health` and binary versions, and rolls back automatically on
  failure.
- Preserves ProgramData, server roots, worlds, saves, configurations, and
  backups because these locations are never part of the application payload.

## Installation

`Setup.exe` remains the first-install, repair, service-installation, and major
prerequisite bootstrapper. Normal compatible releases use the in-application
updater.

Current locally produced binaries are unsigned. HTTPS, size, SHA-256, strict
payload validation, and rollback are enforced now; Authenticode enforcement is
prepared for a future signing certificate.
