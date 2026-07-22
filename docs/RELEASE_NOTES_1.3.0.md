# 1Salem Server Manager 1.3.0

Version 1.3.0 adds a complete Palworld Control Center while preserving existing
servers and application data.

## Palworld management

Management activation resolves the registered server root and edits only
`Pal\Saved\Config\WindowsServer\PalWorldSettings.ini`. It creates a timestamped
history copy, preserves unknown parameters and the original text encoding,
validates a temporary file, atomically replaces the live file, protects the
admin password with Windows DPAPI, restarts gracefully, waits for the real game
child, and verifies the official localhost REST API for up to 120 seconds.
Failures retain exact stage/error information and restore the previous
configuration when appropriate. REST is never published through Playit.

## World, memory, priority, and backup controls

- Categorized, searchable Palworld world settings with bilingual labels,
  official parameter names, defaults, ranges, warnings, presets, compare,
  undo/reset, import/export, configuration history, and verified apply/restart.
- Native Palworld memory reporting and warning/critical thresholds, Windows
  reserve, a shared Minecraft/Palworld budget, One Game at a Time mode, and an
  optional typed-confirmation hard Job Object limit that saves first.
- Verified root/child process priority and affinity changes with persistent
  Active/Failed/Permission denied/Server not running status and optional
  Balanced restoration.
- Official REST Save World, verified ZIP backup with per-file manifest and
  SHA-256, destination and free-space checks, schedules, retention, protected
  backups, metadata, export, and rollback-safe staged restore.

## Safety and compatibility

The schema version 3 migration is additive. Setup, update, and uninstall do not
remove game roots, saves, worlds, backups, Playit state, Agent ProgramData, or
DPAPI credentials. The update ZIP is compatible with the in-app updater used by
version 1.2.1 and does not require running Setup manually.

Automated validation covers 203 tests across Core, Infrastructure, Agent,
Client, and Setup. The package is self-contained for Windows 10/11 x64. Local
release binaries are unsigned.
