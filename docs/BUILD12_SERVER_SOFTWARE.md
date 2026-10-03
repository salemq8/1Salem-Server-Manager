# Build 12 server software management

The new server-software endpoint and window detect the active `server.jar`, not a spare JAR or stale version-history file. Successful managed changes record the platform/version/build bound to the active JAR's SHA-256. An unidentified Paperclip bootstrap is not assumed to be Paper; several forks share it.

## Exact-version safety boundary

Minecraft versions must match exactly. There is no fallback to a different version. The official Paper/Folia catalog uses stable builds only, Purpur uses its official exact-version successful build endpoint, and Vanilla uses the existing official Mojang version catalog. Download destinations are allowlisted, provider checksums and executable-JAR structure are checked, and downloads are staged separately.

The shared server-operation lease serializes software changes. Startup must report the new run's actual console readiness. A process with no live console is not force-killed or restarted to enable a change.

## Owner-approved fresh-world change

The October 3 owner decision supersedes the earlier Vanilla world-preservation blocker. Vanilla → Paper or Purpur is available when an official artifact exists for the exact installed Minecraft version. No world-layout conversion is implemented.

1. The preparation endpoint safely stops the server without deleting files or restarting it.
2. The Client then presents one destructive confirmation: **"Changing server software will delete the current world and create a new world."** Cancellation leaves the server stopped and its world untouched.
3. Only an explicitly confirmed request can proceed. The backend independently verifies that the server is stopped, stages and validates the runtime, and validates the exact deletion targets again immediately before deletion.
4. Only the configured `level-name` directory and its same-root `_nether` and `_the_end` companions are deleted. Minecraft then starts with the new runtime and generates a fresh world.

The configured name must be a safe single directory name. Reserved server-state names, escaping paths, Windows device names, reparse points, and backup/archive conflicts are refused. Existing registered backups and recognizable backups/archives inside a deletion target cause refusal rather than deletion. Existing backups elsewhere are neither replaced nor deleted; this path does not create an automatic world backup.

`server.properties`, whitelist, operators, bans, server registration/settings, and existing backups are preserved. Download, checksum, or JAR-validation failure happens before world deletion. If installation fails before startup is attempted, only the previous runtime/configuration is retained or restored; the server stays stopped. Once new-runtime startup is attempted, a failure requires manual attention: no automatic reverse runtime change, world restoration, world conversion, or retry occurs. The pending journal blocks ordinary start and another software change until resolved. Deleted worlds cannot be recovered by this flow; independently existing backups remain available for a separate restore operation.

For compatible Paper ↔ Purpur changes, the already-implemented same-layout path retains its protected backup and runtime/configuration recovery. It never rolls back world data. Custom `level-name` worlds and their named Nether/End directories are included in that normal backup pipeline.

## Deliberately unavailable transitions

- **Paper/Purpur → Vanilla:** unavailable; no reverse migration or conversion is implemented.
- **Spigot/CraftBukkit → modern (26.x) Paper-family:** unavailable; no layout-conversion path is implemented.
- **Spigot automatic download:** unavailable; official Spigot distribution uses BuildTools, not an official prebuilt server JAR.
- **Folia automatic transition:** unavailable until region-threading and plugin compatibility are separately verified. It is visible, never falsely offered as a safe generic replacement.
- **Paper ↔ Purpur:** supported for the same exact version when existing world layout is verified and the official target artifact exists. Pre-26 Paper-family migration rejects nested Vanilla dimension directories; modern migration rejects legacy split dimensions.

These restrictions are explicit in the English/Arabic window. Vanilla → Paper/Purpur is specifically a destructive fresh-world change, not a preserving migration.

## Evidence and scope

Earlier focused evidence remains: 25 software tests and 2 backup-boundary tests passed, with the WPF window compiled through the Client integration build. Those tests were not repeated for the owner decision.

New fresh-world evidence: 18 cases passed, covering post-stop confirmation gating, stop-only preparation, readopted-process refusal, exact named-world deletion, fresh startup, settings/existing-backup preservation, failed staging, pre-start runtime-only recovery, no reverse rollback after startup failure, reserved/escaping paths, and registered/unregistered backup conflicts. The initial run passed 16; the 2 backup-refusal cases revealed an uncaught validation exception. After the minimal catch fix, only those 2 failed cases were rerun and passed. No world-file conversion tests or historical regression were run. All destructive checks used disposable fixtures/fake runtimes; Salem's real software, process, and world were not changed.

Official references checked during implementation:

- [Paper migration guide](https://docs.papermc.io/paper/migration/) — automatic Vanilla storage changes; modern reverse migration lists gamerule/data files that must move; historical dimension-layout differences.
- [Paper 26.1 world-storage changes](https://papermc.io/news/26-1/).
- [Official Paper downloads API](https://docs.papermc.io/misc/downloads-service/) — identified User-Agent and stable exact-version build/download/checksum metadata.
- [Official Purpur downloads API](https://api.purpurmc.org/).

API: `GET /api/v1/servers/{serverId}/minecraft/software`, `POST /api/v1/servers/{serverId}/minecraft/software/prepare` to stop, and `POST` at the software path with `MinecraftSoftwareMigrationRequest`. Fresh-world requests must set `ConfirmWorldDeletion` only after the destructive confirmation. Client integration: `ServerSoftwareWindow.Open(owner, serverId, serverName, preferredPlatform)` returns true only after a software change reports success and verified startup; the caller can preserve its existing plugin search/selection.
