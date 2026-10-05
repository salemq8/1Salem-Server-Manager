# 1Salem Server Manager 1.5 — Build 15

Current release notes: [docs/RELEASE_NOTES_1.5.md](docs/RELEASE_NOTES_1.5.md).

Build 15 fixes private friend access checks on running Paper/Purpur 26.x servers ("The last
Connect check did not finish" while Network said Ready): `server.properties` is now read without
blocking the server that keeps it open. Connect also recovers on its own when the broker is not
reachable yet after Windows starts, and the server's Connect card shows what failed with a Retry
check button.

Application updates never needed a Windows restart: they restart only the 1Salem service, and
Minecraft and Playit keep running. A server running through an update loses its console pipe,
and Stop/Restart used to refuse it, which left rebooting Windows as the only way out. Stop and
Restart now ask such a server to save and stop with Ctrl+C in its own console; it is never forced.
Live console controls (gamerules applied live, player actions) return once 1Salem starts it again.

Build 14 fixed Gameplay gamerules on Minecraft 26.3 (Vanilla, Paper and Purpur). Keep
Inventory and the other rules no longer fail with "The server did not answer in time" or show
"Not known until the world exists": the Agent reads 26.3's `System chat: Game rule …` answers,
applies the real `minecraft:keep_inventory` rule, reads it back and reports success only when the
server confirms it. Saved values come from 26.3's overworld `game_rules.dat`, and an unknown rule
is never shown or sent as Off. The same answer fix restores the live player list.

A server already running when this update restarts the Agent keeps running; gamerule changes made
before you next start it from 1Salem are saved and applied, then verified, at that start.
This update does not restart servers, reset worlds or change gameplay values.

Build 13 fixed Minecraft software changes failing after the replacement server starts:
the Agent no longer rewrites `server.properties` or restores configuration while the new
runtime is using those files. A successful start can complete its migration journal;
an actual post-start failure keeps the recovery guard and current data intact.

Three focused disposable lock tests and a real Java disposable acceptance passed.
This application update does not reset worlds, switch server software, restore deleted
servers or overwrite existing backups.

Build 12 added server-software management with explicitly confirmed fresh-world reset for
Vanilla → Paper/Purpur, truthful plugin compatibility, independent provider status, a shared
player counter, UUID Players dashboard and real moderation, read-only inventory, and the
1Salem Connect self-updater. Settings and existing backups are preserved. Normal application
updates do not reset worlds. Older Connect users install Build 12 manually once, then update in-app.

## Historical 1.3.2 notes

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
