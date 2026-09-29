# Deferred issues

Issues found during release acceptance that were accepted for now and left for a future
build. Each entry says where it was found, what happens today, and what should happen
instead.

## AutoStart runs whenever the Agent service starts, not only when Windows starts

- **Found:** Version 1.5 Build 6 local installation acceptance, 2026-09-22.
- **Status:** accepted as existing AutoStart behaviour, not a Build 6 regression. Deferred.
- **What happens today:** `ServerRecoveryService`
  (`src/ServerManager.Agent/ServerRecoveryService.cs`) runs every time the Agent service
  starts. Any server with AutoStart on that is not already running, and cannot be
  re-adopted, is started. The setting is labelled "Start this server when the computer
  starts", but it also fires after any Agent service restart, including the restart an
  installed update performs. During the Build 6 update the stopped Palworld server was
  started by the new Agent about seven seconds after the service came back. The logic has
  not changed since before 1.5 (last touched in `b2ebf8b`).
- **Future behaviour:** distinguish a Windows boot from an Agent service restart. Auto-start
  servers only when the Agent starts as part of the computer starting, never after an
  update or a manual service restart. Re-adopting a server that is already running after
  any restart stays as it is today.
- **Until then:** installing an update starts every stopped server that has AutoStart on.
  Turn AutoStart off first if a server must stay stopped through an update.

## Updates page can show "The last update step didn't work" after a successful update

- **Found:** Version 1.5 Build 6 local installation acceptance, 2026-09-22.
- **Status:** observed, cause not fully confirmed. Deferred.
- **What happens today:** after the successful Build 6 update (`updates\last-result.json`:
  `success: true`, `rolledBack: false`), Settings → Updates showed "The last update step
  didn't work. Try checking again." The page reports the Agent's saved update-check state.
  `ApplicationUpdateCoordinator` keeps `Stage = Failed` and `LastError` from an earlier
  failed update check, and only replaces them with `Succeeded` when a manifest from a
  successful check is also stored.
- **Future behaviour:** a successful installed update should clear a stale check failure,
  or the page should say that the last check failed rather than the last update step.

## Resource Governor profile and priority actions can clear Palworld memory safeguards

- **Found:** Version 1.5 review and local acceptance, 2026-09-22.
- **Status:** existing Agent behaviour, unchanged in Build 6. Deferred.
- **What happens today:** applying a Resource Governor profile, or using the priority
  shortcuts, rewrites the process policy for the server and drops the Palworld memory
  safeguards that were in place.
- **Future behaviour:** keep the memory safeguards when a profile or priority is applied, or
  say plainly in the UI which safeguards an action will clear before it runs.

## A mods marketplace is not part of the Content Hub

- **Found:** Build 7 scope, 2026-09-23.
- **Status:** deliberately out of scope. Deferred.
- **What happens today:** the Content Hub browses plugins, modpacks, data packs and resource
  packs. Individual mods are not browsable, installable or advertised anywhere in the UI:
  `ContentKind` has no Mods member and the type selector has no Mods entry.
- **Future behaviour:** if mods are added, they need their own compatibility rules (loader and
  loader version per file, client-vs-server sides, and dependency resolution across a mod set
  rather than one file at a time) before anything is shown. Adding them to the existing plugin
  flow would offer files that cannot load.

## Palworld is still one server per machine

- **Found:** Build 7 multi-server work, 2026-09-23.
- **Status:** deliberate for now. Deferred.
- **What happens today:** the database no longer treats a game type as an identity, so nothing
  in storage stops a second Palworld server. The Palworld paths above it were not changed:
  the resources endpoint, the management cache and the Palworld pages still resolve "the"
  Palworld server as the first one registered.
- **Future behaviour:** give Palworld the same treatment Minecraft now has — every lookup by
  `ServerId`, and port checks across all registered servers — before a second Palworld server
  is offered anywhere in the UI.

## A modpack that needs a different Minecraft version is detected, not migrated

- **Found:** Build 7 modpack lifecycle work, 2026-09-23.
- **Status:** deliberate. Deferred.
- **What happens today:** when a newer release of an installed modpack targets a different
  Minecraft version or a different loader, the Installed row says "Requires server migration",
  names the version, and the update action is disabled. Nothing is applied automatically.
- **Future behaviour:** an explicit, reviewed migration flow — build the new server from the
  new pack, show what would carry over (world, player data, server properties) and what would
  not, and let the person decide, with the old server left intact until they say otherwise.
  Applying such a release in place would break the world it was not built for.

## Playit still maps one tunnel per game, not per server

- **Found:** Build 7 multi-server work, 2026-09-23.
- **Status:** deliberate for now. Deferred.
- **What happens today:** several Minecraft servers can be registered, but Playit's settings
  hold one Minecraft tunnel and one Palworld tunnel. `PlayitSettings` now carries
  `MinecraftServerId` and `PalworldServerId` so a tunnel can say which server it belongs to,
  and `OfficialPlayitSupervisor.PortFor` uses it; when it is unset the supervisor falls back
  to the first server of that game, which is exactly what it did before. Nothing about an
  existing tunnel changes.
- **Future behaviour:** let a person add a tunnel per server and choose which server each
  tunnel points at. Until then, a second Minecraft server is reachable on the LAN and
  through whatever the person sets up themselves, but not through the managed tunnel.

## Home's old two-tile dashboard control is dead code

- **Found:** Build 7 multi-server UI check, 2026-09-23.
- **Status:** harmless. Deferred.
- **What happens today:** `src/ServerManager.Client/Controls/HomeDashboardControl.xaml(.cs)`
  is a leftover from an earlier Home design with one tile per game. `MainWindow` uses
  `HomePageControl`, which lists every registered server as its own card, so the old control
  is compiled but never shown.
- **Future behaviour:** delete the control and its XAML once nothing else is expected to need
  it, so nobody edits a page that is not on screen.

## The Agent's named pipe lets any local user add server instances

- **Found:** Build 8 (1Salem Connect) research into named-pipe security, 2026-09-23.
- **Status:** security finding in existing Build 7 code, outside the Connect phase. Deferred.
- **What happens today:** `src/ServerManager.Agent/NamedPipeAgentServer.cs` grants
  `BUILTIN\Users` `CreateNewInstance`, and allows `MaxAllowedServerInstances` instances. It never
  sets first-instance or reject-remote-clients, and it does not deny NETWORK. .NET never sets
  `PIPE_REJECT_REMOTE_CLIENTS` on its own (Microsoft Learn; runtime source). Any local account can
  therefore create a competing instance of the pipe and may be handed a client's connection. If
  the Server service is running, network logons by those accounts are checked only against this
  ACL. On a single-user PC the exposure is small, but the ACL is wider than it needs to be.
- **Future behaviour:** grant only SYSTEM, Administrators and the interactive user the rights
  they need. Never grant `CreateNewInstance` to Users. Deny `NT AUTHORITY\NETWORK`. Create the
  first instance with `PipeOptions.FirstPipeInstance`. Have the client verify the pipe server's
  identity. 1Salem Connect's pipes follow this pattern from the start.

## 1Salem Connect: owner-confirmed friend node bindings (resolved locally in Phase 2)

- **Found:** Build 8 (1Salem Connect) Phase 1 security review, finding BRK-4, 2026-09-24.
- **Status:** implemented in Phase 2, accepted on a real tailnet, and deployed with the broker on
  workers.dev (2026-09-29). Full write-up: `docs/CONNECT_ARCHITECTURE.md` §21, D-1.
- **Phase 1 behavior:** the broker recorded the node id the friend's device reported. It refused a
  node id another friend's device already holds on a live membership of the same owner
  (`node_in_use`), binds once, and tickets only work from the WhoIs-verified node, so a wrong
  binding gives no access. The owner cannot yet confirm, see or clear a binding.
- **Phase 2 behavior:** the Agent checks the reported node through the Tailscale API
  (`tag:onesalem-client`, created after the enrollment key, not bound elsewhere) and confirms it with
  an owner-signed call; tickets are issued only for a confirmed binding. Revocation never deletes a
  device without that tag check.

## 1Salem Connect: the broker needs an outer rate limiter before any deployment (Phase 2)

- **Found:** Build 8 (1Salem Connect) Phase 1 security review, finding BRK-2, 2026-09-24.
- **Status:** D1 budgets and the fail-closed production outer limiter are implemented and locally
  tested, and the broker is deployed on workers.dev with the `FLOOD` binding active (2026-09-29,
  per Salem's deployment check). Full write-up: `docs/CONNECT_ARCHITECTURE.md` §21, D-2.
- **What happens today:** exact per-key, per-network and global budgets in D1; over-limit,
  malformed, forged and replayed requests add no counter write. The production `FLOOD` Workers
  Rate Limiting binding rejects excess traffic before D1. Production refuses requests if that
  binding or a usable client address is absent.
- **Custom domain (optional, later):** on the zone, ensure Pseudo IPv4 is not set to "Overwrite
  headers". The workers.dev hostname has no such zone setting.

## 1Salem Connect: a low-integrity local process can fill the friend pipe's slots (Phase 2)

- **Found:** Build 8 (1Salem Connect) Phase 1 friend-app review, finding F7, 2026-09-25.
- **Status:** local denial of service only; no secret exposure. Deferred to Connect Phase 2. Full
  write-up: `docs/CONNECT_ARCHITECTURE.md` §21, D-3.
- **What happens today:** the friend transport's pipe serves at most 8 clients and refuses the
  rest by accepting and closing. A sandboxed, lower-integrity process of the same Windows user can
  hold all 8, because the pipe's default label does not block reads. The next time the app needs
  a new pipe connection, it is closed without an answer and reads as `unavailable`. At the start
  of a Connect or an enrollment the app then restarts a transport it started, which ends the
  friend's live sessions, or fails the call when it reused one. An `enroll` refused this way has
  already used up the one-time blob, so the owner must approve the PC again. A refused status or
  close call leaves the session open, with the page saying it could not be closed yet, until a
  slot frees. Nothing is ever sent to the squatter: the app verifies the serving process before
  writing.
- **Future behaviour:** restart only when the transport this app started has exited; treat a
  verified connection that closes without answering as `no_answer`; label the pipe no-read-up.

## 1Salem Connect: a server card can show a stale connection state for up to about 60 s, or longer during a broker outage (Phase 2)

- **Found:** Build 8 (1Salem Connect) Phase 1 friend-app review, finding F8, 2026-09-25.
- **Status:** UI polish; no effect on access. Deferred to Connect Phase 2. Full write-up:
  `docs/CONNECT_ARCHITECTURE.md` §21, D-3.
- **What happens today:** each card on the friend app's Servers page is a snapshot taken at the
  last successful refresh, and the page refreshes every 60 s when nothing is pending. While the
  broker answers, a card can therefore still say "Connected" or "Server offline" for up to about a
  minute after the session ended. While the broker cannot be reached or rate-limits the refresh,
  the page shows its error line but keeps the last cards, so a card can stay stale for the whole
  outage. The Connection page follows the session itself (every 5 s).
- **Future behaviour:** bind each card to its connection's state changes.

## Fresh-install Playit defaults need a cleanup and security review

- **Found:** Version 1.5 review, 2026-09-22.
- **Status:** deferred.
- **What happens today:** the Agent seeds hardcoded default Playit tunnel addresses on a
  fresh installation.
- **Future behaviour:** review what a fresh install should contain. A new installation should
  not carry another deployment's addresses; leave the tunnels unset until the person links
  their own account.
