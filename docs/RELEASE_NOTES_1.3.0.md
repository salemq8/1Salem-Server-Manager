# 1Salem Server Manager 1.3.0

Version 1.3.0 is a desktop experience, configuration reliability, and product
stabilization release.

## Highlights

- Consistent dark, light, and Follow Windows styling for buttons, tabs, inputs,
  tables, focus, validation, loading, warning, and disabled states.
- Responsive twelve-section navigation, a structured live Home dashboard, and
  clearer per-game overview, console, settings, players, files, backup, update,
  performance, network, and diagnostics tabs.
- Palworld settings load from and write to the registered live
  `PalWorldSettings.ini` with unknown-field preservation, staged validation,
  atomic replacement, active/pending/unsaved state, and verified restart flow.
- Expanded Minecraft `server.properties`, Java path, JVM memory, priority, and
  affinity editing while preserving unknown properties and JVM arguments.
- Protected configuration restore points are created before settings changes,
  retain the latest ten per server, redact secrets, and support compare, label,
  delete, verified restore, and automatic failure recovery.
- Independent server diagnostics, process-tree resource details, bounded
  console tools, and consistent in-app notifications.
- Complete Palworld localhost management, world, memory, priority, Save World,
  verified backup, scheduling, retention, and rollback-safe restore controls.

## Compatibility and safety

- Normal upgrades from compatible 1.2.x installations remain supported by the
  in-application updater.
- Existing ProgramData, worlds, saves, player data, backups, SteamCMD data,
  Playit data, tunnels, and previous release artifacts are not removed.
- The Agent API remains loopback-only by default. This release adds no VPN,
  relay, cloud, or port-forwarding features.
- The existing additive database schema remains at version 3.

## Installation

Use the in-application updater for a normal upgrade. `Setup.exe` remains
available for first installation and repair. Local release binaries are
unsigned.
