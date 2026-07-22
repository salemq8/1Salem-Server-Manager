# 1Salem Server Manager 1.3.1

Version 1.3.1 is a focused Memory Policy UI and server-budget hotfix.

- Memory policy presets now activate and persist through one authoritative
  selection with exclusive radio-button behavior.
- Custom GiB fields are locale-aware, editable, validated inline, and protected
  from two-second metrics refreshes while changes are pending.
- Save Changes, Cancel, and Apply Recommended keep active and pending values
  explicit.
- Startup capacity is recalculated before every start and ignores absent,
  stopped, exited, and stale managed-server processes.
- Minecraft is omitted from summaries, priority controls, and budgets when it
  is not registered.
- A confirmed and audited Start Anyway Once option is available only for one
  manual attempt when enabled by the active Custom policy.

The update preserves the existing database and all server data, saves, backups,
settings, credentials, Playit data, and tunnels. Transactional in-application
updates are supported from 1.3.0 and 1.2.1.

Current local builds are unsigned and should be code signed before broad
distribution.
