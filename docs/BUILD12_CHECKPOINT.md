# Build 12 checkpoint — 2026-10-03

Continue the existing `claude/build12` working tree. Do not restart planning, review, or repeat passed suites. Implementation is complete; final candidate and installed/release acceptance remain. Percentage estimates are scope estimates, not measured test coverage.

## Owner decision implemented

The owner explicitly removed world-preservation requirements for incompatible Vanilla → Paper/Purpur changes. The implementation stops the server, displays one destructive confirmation, and only after confirmation deletes the configured world folders and starts the new runtime with a fresh world.

The confirmation says: "Changing server software will delete the current world and create a new world." Server properties, whitelist, ops, bans, registration/settings, and existing backups are preserved. Unsafe world paths or backups inside world deletion targets fail closed. No world conversion, symlinks, reverse migration, or automatic world-data rollback is implemented. Before-start failure restores previous runtime/configuration only. Cancel leaves the server stopped and the world untouched.

Installed acceptance must never change Salem's real software or delete its real world. No previous scope blocker remains.

## Implemented locally

- Reused Connect self-updater commits `43dce0a` and `3bf906c`, already ancestors of the current branch. No updater regression rerun.
- Preserved Claude's three uncommitted invite-link changes; permanent latest-download URL is prepared, not deployed.
- Active-JAR software detection, exact-version official catalog, same-layout runtime migration, protected backup, staged checksum validation, startup verification, runtime/configuration rollback, and interrupted-migration startup guard. See `BUILD12_SERVER_SOFTWARE.md` for limits.
- Plugin browsing on Vanilla stays available without claiming install compatibility. Paper/Purpur choices require real matching plugin releases and exact-version software evidence. Safety blocks remain mandatory; successful migration preserves plugin/search context and requires explicit installation.
- Independent provider statuses retain successful results. Empty/unattempted/canceled providers are not conflated with failures. Vanilla project-detail access no longer fails locally because installation is unsupported.
- Canonical UUID-based Minecraft player service wired to Agent dashboard, Home, Servers, Overview, Players, and legacy gameplay controls. Verified counts are retained and marked stale after failures.
- Players dashboard: name/UUID search; all/online/offline/operator/whitelist/ban filters; online/name/last/first sorts; persistent query/filter selection during refresh; real per-server history and saved metadata. Unavailable values remain unknown. Saved gameplay facts are labeled last saved; status samples are not presented as complete rosters.
- Validated UUID/name moderation through real server commands and readback; destructive confirmations; no moderation restart.
- Re-adopted servers use a bounded, read-only Minecraft status handshake for counts. Anonymous redirected console handles cannot safely be recovered; command limitation remains explicit without restarting the server.
- Read-only inventory grid (27 main + 9 hotbar + armor + offhand), genuine UUID-bound live data when available, otherwise explicitly timestamped saved NBT. Modern/legacy storage support, bounded shared reads, path/handle validation, no invented durability limits or bundled Mojang assets.
- English/Arabic RTL and existing theme resources used by new UI. Installed visual acceptance is still pending.
- Existing unreadable/unsafe/oversized server.properties no longer silently select another world's saved player data.

## Completed focused verification — do not repeat without an affected change

| Check | Result |
| --- | --- |
| ContentProviderStatusTests | 4 passed |
| MinecraftPlayerStateServiceTests + MinecraftGameplayServiceTests | 56 passed |
| MinecraftInventoryTests | 18 passed |
| MinecraftInventoryPresentationTests | 3 passed |
| MinecraftSoftwareTests | 25 passed |
| Custom-level-name backup + existing backup round-trip boundary | 2 passed |
| PluginSoftwarePresentationTests + ContentProviderPresentationTests + ContentSearchSessionTests + ContentHubUiTests + MinecraftPlayersPresentationTests | 57 passed |
| ContentProfileRuntimeIntegrationTests | 2 passed |
| MinecraftPlayerWorldResolutionTests | 1 passed |
| Invite landing page focused Vitest file | 4 passed |

Earlier verification: 172 focused tests passed across these runs. The invite run emitted sandbox log/static-analysis warnings but exited successfully with all four tests passed; no deployment or remote bindings were used. Two backup fixtures needed an approved sandbox escalation for their existing LocalAppData fixture root.

Owner-decision delta: 18 new fresh-world backend cases passed (16 initially, then only the 2 affected backup-safety cases after their exception handling fix). All 3 new fresh-world UI cases passed; WPF compilation succeeded. The 2 changed release identity pinning checks passed after the revision bump. These passed checks must not be repeated.

Client WPF compilation succeeded in the focused Client run. Final Agent Release build succeeded with zero warnings and zero errors (`dotnet build src/ServerManager.Agent/ServerManager.Agent.csproj -c Release --no-restore`). `git diff --check` found no whitespace errors. No full historical regression, old multi-agent review, or Connect tailnet acceptance was rerun.

## Handoff status

- VERSION: 1.5 (unchanged).
- BUILD_REVISION: 12; VERSION remains 1.5. Candidate generation replaces source build metadata with the committed source identity and actual build timestamp.
- Reused Claude work: preserved; local HEAD remains `3bf906c1f215cc9304dff5590165129665a94015` plus uncommitted Build 12 source/tests/docs.
- Server software changes: compatible-layout path retained; incompatible Vanilla transitions use the explicit fresh-world reset described above.
- Existing-backup/configuration preservation and runtime-only recovery: focused checks passed. No real world migrated or deleted.
- Plugin-on-Vanilla / provider status: implemented and focused checks passed.
- Player counter / dashboard / history / administration / inventory: implemented and focused checks passed; installed acceptance pending.
- Connect updater: reused; new release manifest generation deferred to final candidate.
- Invite latest-download link: local change and four tests passed; production deployment deferred.
- Installed Build 12: not attempted.
- Agent / Minecraft / Playit / real server files: not changed or restarted by this work. Installed health/file-preservation acceptance has not been performed.
- Canonical promotion / main commit / push / release / tag / remote asset hash verification: not attempted.
- Working tree: existing branch, intentionally uncommitted implementation changes retained; no resets, rebases, branch replacements, or discarded work.

Next: create one final Build 12 candidate, normal-updater installation with owner UAC approval, short installed acceptance without real migration, then promote/push/publish/hash-check and deploy the invite-link change in the requested order. Preserve Build 11 rollback and all historical releases.

STATUS: IMPLEMENTATION READY — final candidate, installation, short acceptance, and release actions pending.
