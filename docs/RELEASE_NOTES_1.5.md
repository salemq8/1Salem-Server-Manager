# 1Salem Server Manager 1.5 — Build 2

## Safety and reliability repairs (Build 2)

An independent review of Build 1 and a follow-up independent re-audit of the resulting
fixes found and corrected the following, entirely internal to the Agent, Client, and
update tooling — no change to the visible Version 1.5 identity:

- Backup restore is now transactional across its full pre-restore safety-backup step,
  not only its file-swap loop: a failure creating that safety backup (for example, disk
  exhaustion) is caught, rolled back, and reported instead of leaving the server stuck in
  a "Restoring" state. Disk-space pre-checks now budget for the safety backup as well as
  the restored content.
- The Agent's local HTTP API requires authentication on every privileged endpoint,
  including from the same machine; a wrong LAN pairing credential is now correctly
  reported as 403 Forbidden rather than 401 Unauthorized.
- Concurrent operations against the same game server (start, stop, restart, backup,
  restore) are serialized through one required, shared coordinator, replacing a
  same-process fallback that could have silently masked a future wiring mistake.
- Diagnostics redaction now covers the "Copy Diagnostics" button on the game server page,
  which previously copied the server's raw last-error text and console output to the
  clipboard unredacted.
- Applying and rolling back an application update is atomic on both the forward and the
  recovery path: rollback now stages and swaps the previous Client and Agent directories
  the same way a forward update does, instead of copying files directly over the live
  directories.
- The fixed Stable AppUserModelID is now stamped onto every shortcut a normal install or
  in-app update actually produces (Desktop, Start Menu, and Administrator), not only a
  shortcut created through the Admin Tools window — this is what lets a pinned taskbar
  icon merge with the running application's taskbar button.
- A secret-storage scope repair (DPAPI, ACL) and a diagnostics-redaction pattern repair
  from Build 1 were independently re-verified against the actual committed code with no
  further defects found.
- Fixed two clean-checkout portability gaps (a test-only path collision and a hard-coded
  build output path) and one pre-existing, low-probability race condition in
  update-time dashboard-process verification, none of which affect any installed system.

## Fixed visible version policy (Build 1)

- Migrates the visible product version once from 1.3.2 to 1.5.
- Keeps the normal interface and About page at Version 1.5.
- Uses a separate monotonically increasing internal Build revision for future updates.
- Orders updates by product version, Build revision, and verified package hash.
- Rejects older Builds and requires explicit repair mode for an identical Build.

## Installed updates and rollback

- Continues to update through the permanent installed launcher without Setup.exe.
- Stores Version 1.5 Builds in build-specific installed directories behind the stable launcher.
- Preserves the immediately previous Build as the operational rollback target.
- Preserves the historical 1.3.2 release as the pre-1.5 migration fallback.
- Keeps game servers, Playit, user data, saves, backups, tunnels, and configurations outside the update transaction.

## Release output

- Uses the rolling `artifacts/release/1.5` release directory.
- Builds a validated candidate first and promotes it only after the installed update and rollback snapshot are verified.
- Adds `BUILD_REVISION` and `build-info.json` without presenting Build 1 as product version 1.5.1.
