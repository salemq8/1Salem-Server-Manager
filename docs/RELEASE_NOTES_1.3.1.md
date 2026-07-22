# 1Salem Server Manager 1.3.1

Version 1.3.1 is a focused Memory Policy UI and server-budget hotfix.

- Memory policy presets now use one authoritative selected value with exclusive
  radio-button semantics. Selecting Custom changes and persists the actual Agent
  policy instead of only changing a visual state.
- Custom reserve, warning, critical, and total-server-budget values accept
  locale-aware decimal GiB input, retain invalid pending text for correction,
  show inline validation, and remain untouched by the two-second metrics
  refresh. Save, Cancel, and Apply Recommended operate on separate pending and
  active values.
- The startup gate now recalculates immediately before every start from physical
  memory, Windows reserve, actual live registered-server process usage, and a
  startup allowance. Absent or stopped games, stopped targets, exited processes,
  and stale process IDs consume no budget.
- Profile summaries only name registered games. Palworld-only installations no
  longer display or budget Minecraft.
- An optional, confirmed Start Anyway Once action is limited to a single manual
  attempt and is audited. It does not affect Auto Start or native hard limits.
- Resource summaries are consistent across Performance, Home, the tray, audit
  events, diagnostics, and priority confirmations.

The update preserves the schema and all existing ProgramData, Palworld and
Minecraft data, backups, settings, credentials, Playit data, and tunnels. It
supports the normal transactional in-application update flow from 1.3.0 and
1.2.1.

Current local builds are unsigned and should be code signed before broad
distribution.
