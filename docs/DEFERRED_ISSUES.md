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

## Fresh-install Playit defaults need a cleanup and security review

- **Found:** Version 1.5 review, 2026-09-22.
- **Status:** deferred.
- **What happens today:** the Agent seeds hardcoded default Playit tunnel addresses on a
  fresh installation.
- **Future behaviour:** review what a fresh install should contain. A new installation should
  not carry another deployment's addresses; leave the tunnels unset until the person links
  their own account.
