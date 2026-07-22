# 1Salem Server Manager 1.3.2

Version 1.3.2 repairs the product-version and installed-update workflow while
shipping the redesigned Palworld Overview dashboard.

## Highlights

- Modern Palworld Overview hierarchy with compact health badges, six live
  metrics, explicit quick actions, bounded activity graphs, recent activity,
  resource summaries, and distinct local/Internet connection cards.
- Responsive minimum-width behavior, English LTR and Arabic RTL ordering,
  dark/light/Follow Windows themes, and explicit loading, stale, unavailable,
  REST-offline, Playit-offline, running, and stopped states.
- One manually edited product-version source: `VERSION`. Assembly, file,
  product, package, Setup, Updater, manifest, release, and validation versions
  are derived from it.
- Semantic downgrade prevention and an automatic next-version tool that audits
  installed components, running processes, local manifests, update packages,
  and immutable release history.
- Separate Client, Agent, and Updater version reporting with incomplete-update,
  rollback, previous-version, channel, and pending-restart states.
- Permanent Stable launcher identity, version-specific application directories,
  atomic active-version selection, rollback snapshots, and original shortcut
  compatibility across updates.
- A narrow privileged migration path verifies the exact installed dashboard
  PID and executable before closing only that process; Agent, game, and Playit
  executables are refused.
- Safer Agent lifetime behavior: game processes started by 1.3.2 survive Agent
  disposal and can be re-adopted without duplicate starts.

## Update compatibility

- Supports verified Stable updates from 1.2.x, 1.3.0, and 1.3.1.
- Rejects equal-version rebuilds unless the developer-only override is explicit.
- Rejects downgrades and treats semantic versions numerically, including
  `1.3.10 > 1.3.9`.
- When an existing Agent owns a live game through the legacy kill-on-close job
  policy, Client and Updater are activated while Agent 1.3.2 is staged until a
  naturally safe service restart. No live game process is stopped for an
  application update.

## Data safety

ProgramData, Palworld and Minecraft worlds/saves, player data, backups, server
configuration, SteamCMD data, Playit data, tunnels, firewall rules, and old
release folders are outside the application swap and are never removed by the
normal updater.

`Setup.exe` is for first installation or repair. Existing installations use the
built-in updater and retain the same launcher, shortcut target, application
identity, icon, tray identity, and taskbar-pin compatibility.
