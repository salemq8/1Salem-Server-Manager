# Changelog

## 1.5 — Build 11

- Content: the content type list offers Plugin, Modpack, Data Pack and Resource Pack for every
  Minecraft server again, and Plugin is the default wherever plugins can run. On a Vanilla server
  plugins can be browsed with a note that installing one needs a plugin server. The subtitle
  follows the selected type. Search behaviour (debounce, Enter, stale-reply protection, kept text
  and results) is unchanged, with no loading loop.
- Minecraft: a new Gameplay and players page (Settings → Game). Real gamerules (Keep Inventory,
  daylight and weather cycles, mob spawning and griefing, fire, damage and drop rules, and more)
  are asked of the running server and read back after each change, or read from the world's
  level.dat while the server is not answering; changes made then are applied and verified the
  next time 1Salem starts the server. Nothing on the page restarts a server.
- Fall damage offers Normal (100%) and Disabled (0%) only, through the fallDamage gamerule;
  Minecraft has no real percentage setting. Rules a server's version lacks are not offered.
- Minecraft 26.x: gamerules are read from data/minecraft/game_rules.dat under their registry
  names (advance_time, spawn_mobs and the rest); PvP is shown as the gamerule that replaced the
  server.properties value, and the numeric fire-spread rule is not presented as a switch.
- server.properties settings (PvP, hardcore, difficulty, game mode, forced game mode, flight,
  spawn protection, view and simulation distance, max players, whitelist, command blocks, online
  mode and operator level) are saved with a restore point; difficulty and the whitelist switch
  also apply live, the rest at the next start.
- Players: who is online (only when the server says so), operators, whitelist and bans, with
  Make operator, Remove operator, whitelist add and remove, Kick, Ban and Unban for valid Java
  names. Removing, kicking and banning are confirmed. A server re-adopted after an Agent restart
  says its live controls need a restart from 1Salem instead of failing.

## 1.5 — Build 10

- Content: the Discover page no longer reloads on every dashboard refresh, which had reset it to
  "Loading" every few seconds and interrupted typing. Search is debounced; Enter searches at
  once; stale replies are dropped; results stay visible while loading; one provider failing keeps
  the other's results. The search hint follows the content type.
- Content: a server-software filter. Plugins: Automatic, Paper, Purpur, Spigot, Bukkit, Folia or
  all. Modpacks: Fabric, Forge, NeoForge, Quilt or all. It maps onto Modrinth loader facets, and
  Hangar is asked only where its Paper files apply. Per-card compatibility is unchanged.
- 1Salem Connect: `1SalemConnect-Setup.exe`, a normal Windows installer. It installs to Program
  Files and adds Start menu and desktop shortcuts, an Installed apps entry with an uninstaller and
  a non-elevated launch. `1SalemConnect-Portable.zip` replaces `1SalemConnect-1.5.zip`.
- The invite page offers "Download 1Salem Connect", pointing at the Build 10 installer.

## 1.5 — Build 9

- Create Minecraft Server gives each new server its own unique folder,
  `ProgramData\1SalemServerManager\MinecraftServers\<server name>` with `-2`, `-3`… when taken,
  instead of the first server's `…\Minecraft` folder. A second Minecraft server can now be created
  without choosing a folder by hand. The folder follows the server name until one is chosen with
  Browse; existing folders are never reused.

## 1.5 — Build 8

- Added 1Salem Connect private friend access for Minecraft: owner setup with the owner's own
  Tailscale OAuth client, one-time invitations, approval, nicknames and revocation in Server
  Manager, and the separate 1Salem Connect friend app (`1SalemConnect-1.5.zip`).
- The Agent hosts the Connect host transport; friends reach only the approved server through a
  loopback address, with no system VPN, route, DNS or proxy change.
- Added the Connect broker (Cloudflare Worker) with its invite landing page, live on Cloudflare
  workers.dev at `https://onesalem-connect-broker-production.onesalemconnect.workers.dev`; the
  Agent, invite links and the packaged friend app use that address.
- Release packaging builds the Go transports with `ts_omit_oauthkey` and keeps local broker
  state, secrets and dependencies out of `Source.zip`.

Builds 2 to 7 are described in `docs/RELEASE_NOTES_1.5.md`.

## 1.5 — Build 1

- Completed the one-time visible product-version transition from 1.3.2 to 1.5.
- Added a separate monotonic `BUILD_REVISION` and generated `build-info.json` identity.
- Changed normal update ordering to compare product version, internal Build revision, and package hash.
- Added rolling 1.5 release candidates and post-install promotion while preserving historical 1.3.2 artifacts.
- Added Build-aware installed component reporting, rollback metadata, and stable-launcher validation.
- Locked normal build preparation to `tools/next-build.ps1`; `VERSION` changes now require Salem's explicit authorization phrase.

## 1.3.2 - 2026-07-22

- Redesigned the Palworld Overview as a responsive live dashboard with compact
  health badges, six metric cards, explicit quick actions, bounded server
  activity graphs, recent activity, resource summaries, and separate local and
  Playit connection cards.
- Added loading, stale, unavailable, stopped, REST-offline, Playit-offline,
  minimum-width, dark/light, and Arabic RTL dashboard behavior and validation.
- Made `VERSION` the single manually maintained product-version source for all
  assemblies, Setup, Updater, manifests, release paths, and validation output.
- Added installed Client, Agent, and Updater version detection, mismatch and
  pending-restart reporting, Stable/Preview/Development channels, and a
  read-only installed-version history view.
- Added semantic release pre-flight and next-version tools that reject equal or
  lower targets, preserve invalid builds under timestamped failed folders, and
  never overwrite immutable prior releases.
- Added a permanent Stable launcher, versioned installations, atomic
  `current.json` switching, rollback snapshots, corrupt-package rejection, and
  safe Agent staging while a game process is live.
- Removed Agent-exit game termination for new processes and added existing-game
  process re-adoption to prevent duplicate starts after a safe Agent restart.
- Strengthened archive checks for traversal, absolute paths, symbolic links,
  case collisions, scripts, server data, saves, backups, and secret-like files.

## 1.3.0 - 2026-07-21

- Added a centralized WPF design system with readable disabled states, dark and
  light input templates, responsive navigation, focus states, and inline
  validation.
- Expanded the professional Home and per-game dashboards with independent
  health indicators, process-tree metrics, structured backup/update state, and
  distinct local and public addresses.
- Reworked Palworld and Minecraft settings around active, pending, and unsaved
  states while preserving unknown configuration keys and real JVM arguments.
- Added staged, atomic, verified configuration writes with restart validation
  and protected per-server configuration restore points.
- Added per-server diagnostics, bounded console filtering/history, toast
  notifications, dynamic product versions, and immediate English/Arabic and
  theme application.
- Added regression tests for configuration recovery, expanded Minecraft
  settings, UI resources, navigation, versioning, and data preservation.
- Fixed Palworld management activation by editing and validating the exact live
  `PalWorldSettings.ini`, preserving unknown values and encoding, atomically
  replacing it, and rolling back failed REST activation.
- Added localhost-only REST enable, retry, repair, test, and disable workflows
  with DPAPI-protected credentials and explicit activation-stage errors.
- Added categorized bilingual world settings, official defaults, presets,
  validation, compare/import/export, configuration history, and verified
  apply/restart.
- Added native Palworld memory thresholds, Windows reserve, shared game-server
  budget, optional save-before-enforcement hard limit, and verified process-tree
  priority/affinity profiles.
- Added Save World Now, save-before-backup, destination validation, SHA-256 and
  manifest verification, schedules, storage retention, protected backups, and
  staged restore with safety backup and rollback.

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
