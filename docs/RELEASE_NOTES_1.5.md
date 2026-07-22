# 1Salem Server Manager 1.5 — Build 1

## Fixed visible version policy

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
