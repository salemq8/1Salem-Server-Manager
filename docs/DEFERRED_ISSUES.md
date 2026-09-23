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

## Fresh-install Playit defaults need a cleanup and security review

- **Found:** Version 1.5 review, 2026-09-22.
- **Status:** deferred.
- **What happens today:** the Agent seeds hardcoded default Playit tunnel addresses on a
  fresh installation.
- **Future behaviour:** review what a fresh install should contain. A new installation should
  not carry another deployment's addresses; leave the tunnels unset until the person links
  their own account.
