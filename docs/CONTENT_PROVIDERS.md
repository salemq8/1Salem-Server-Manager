# Content providers

1Salem Server Manager does not host plugins. It browses and installs content from trusted
external providers on the person's behalf. This document records what each provider's API
actually offers, verified against the official specification and live read-only calls on
2026-09-22. Everything here was checked; nothing is written from memory.

No provider website HTML is scraped. Every provider call in the product uses a documented
JSON API.

---

## Modrinth

| | |
|---|---|
| Base API | `https://api.modrinth.com/v2` |
| API version | v2 (spec reported v2.7.0) |
| Staging | `https://staging-api.modrinth.com` (not used in the product) |
| Documentation | <https://docs.modrinth.com/api/> |

### Authentication

No token for public reads. Tokens are only needed to create or change data, or to read
private data. The product only performs public reads and file downloads, so it sends no
credentials and stores none.

### Rate limits

300 requests per minute per IP, the same with or without a token. Every response carries:

- `X-Ratelimit-Limit` — requests allowed per window
- `X-Ratelimit-Remaining` — requests left
- `X-Ratelimit-Reset` — seconds until the window resets

Verified live: `X-Ratelimit-Limit: 300`, `X-Ratelimit-Remaining: 299`.

### User-Agent

A uniquely identifying `User-Agent` is **mandatory**. Generic library agents risk being
blocked. Modrinth recommends `project_name/version (contact)`. The product sends its own
application identity on every request.

### Endpoints used

| Purpose | Call |
|---|---|
| Search | `GET /v2/search?query=&facets=&index=&offset=&limit=` |
| Project | `GET /v2/project/{id\|slug}` |
| Versions | `GET /v2/project/{id\|slug}/version?loaders=[…]&game_versions=[…]` |
| Identify a local file | `GET /v2/version_file/{hash}?algorithm=sha1` |
| Update check | `POST /v2/version_files/update` with `hashes`, `algorithm`, `loaders`, `game_versions` |
| Loader list | `GET /v2/tag/loader` |

`index` accepts `relevance`, `downloads`, `follows`, `newest`, `updated`. `limit` maxes at
100.

### Compatibility semantics

Facets are an array of arrays: entries **within** one inner array are OR, and separate inner
arrays are AND. Verified: `[["categories:paper"],["versions:1.21.8"]]` returned 11,824 hits,
and adding `["categories:purpur"]` narrowed it further, confirming the AND behaviour.

Server plugin loaders, from `GET /v2/tag/loader` filtered to those whose
`supported_project_types` contains `plugin`: **bukkit, bungeecord, folia, geyser, paper,
purpur, spigot, sponge, velocity, waterfall**.

A caution that matters: a server plugin's `project_type` is reported as `mod`, not `plugin`.
`project_type:plugin` works as a *search facet* (17,848 hits), but the returned object still
says `mod`. Plugin identity therefore comes from the loaders, never from `project_type`.

Minecraft versions are exact strings in `game_versions` (`1.21.8`). There is no range syntax,
so compatibility is an exact membership test.

### Download semantics

Each version exposes `files[]` with `url`, `filename`, `size`, `primary` and `file_type`.
Verified URLs are on `https://cdn.modrinth.com/...`. The product downloads only a URL that
came from a version response, never a path built by hand, and never a URL typed by a person.

### Hashes

Every file carries both `sha1` and `sha512` (verified live on LuckPerms). SHA-512 is the
strongest available and is what the product verifies. SHA-1 is kept only because it is the
key for provider hash lookups.

### Dependencies

`dependencies[]` per version, each with `project_id`, `version_id`, `file_name` and
`dependency_type` (`required`, `optional`, `incompatible`, `embedded`).

### Known limitations

- `project_type` cannot distinguish plugins from mods; loaders must be used.
- A project's `loaders` list is the union across all versions, so per-version loaders must be
  checked before installing.
- Deprecated API versions eventually return **410 Gone**, which the product must handle as a
  clear "this provider needs an app update" state rather than a crash.

---

## Hangar (PaperMC)

| | |
|---|---|
| Base API | `https://hangar.papermc.io/api/v1` |
| Specification | `https://hangar.papermc.io/v3/api-docs` (OpenAPI 3.1, "Hangar API" 1.0) |
| Documentation | <https://hangar.papermc.io/api-docs> |

### Authentication

Anonymous access covers all public information, which is everything the product needs.
An API key exchanged at `/api/v1/authenticate?apiKey=…` for a JWT is only required for
private or write operations. The product stays anonymous and stores no key.

### Rate limits

Documented as 20 requests per 5 seconds, with an initial overdraft, and stricter limits on
some write endpoints. No rate-limit headers were present in the live responses, so the
product must pace itself rather than rely on headers, and handle 429 with `Retry-After` when
sent.

### Endpoints used

| Purpose | Call |
|---|---|
| Search | `GET /api/v1/projects?q=&sort=&category=&platform=&version=&limit=&offset=` |
| Project | `GET /api/v1/projects/{slugOrId}` |
| Versions | `GET /api/v1/projects/{slugOrId}/versions?platform=&platformVersion=&channel=&limit=&offset=` |
| Single version | `GET /api/v1/projects/{slugOrId}/versions/{nameOrId}` |
| Download | `GET /api/v1/projects/{slugOrId}/versions/{nameOrId}/{platform}/download` |
| Identify a local file | `GET /api/v1/versions/hash/{sha256}` |

**Important:** the `/api/v1/projects/{author}/{slugOrId}/…` forms are marked deprecated in the
current specification. The product uses only the `{slugOrId}` forms. This was the specific
point the research brief said to verify before coding, and it is verified: the deprecated
two-segment paths are still present but flagged, and the single-segment paths are current.

Search returns `{ pagination: { count, limit, offset }, result: [Project] }`.

### Compatibility semantics

The `Platform` enum has exactly three values: **PAPER, WATERFALL, VELOCITY**. There is no
Spigot, Bukkit or Purpur platform. Consequences the product must respect:

- Paper and Purpur servers map to `PAPER` (Purpur is a Paper fork with a superset API).
- Spigot and Bukkit servers have **no** Hangar platform. A Hangar `PAPER` artifact may use
  Paper-only API, and nothing in the response proves otherwise, so the product does not offer
  Hangar installs to Spigot or Bukkit servers. Those servers use Modrinth, which models
  `spigot` and `bukkit` explicitly.

`supportedPlatforms` on a project, and `platformDependencies` on a version, map each platform
to an exact list of Minecraft versions. The versions endpoint also filters by `platform` and
`platformVersion` server-side, so compatibility can be resolved by the provider rather than
by guessing locally.

Categories: `admin_tools, chat, dev_tools, economy, gameplay, games, protection, role_playing,
world_management, misc, undefined`.

### Download semantics

`downloads` maps a platform to `{ fileInfo, downloadUrl, externalUrl }`. Verified on
Maintenance 5.1.0: `downloadUrl` on `https://hangarcdn.papermc.io/...` with `externalUrl`
null.

`externalUrl` is the case that needs care: some versions are hosted off-site. When a version
has an `externalUrl` instead of a provider-hosted `downloadUrl`, the product does **not**
download it silently. It shows the project link so the person can decide, because the file
would come from a third party the provider does not host or hash.

### Hashes

`fileInfo` carries `name`, `sizeBytes` and **`sha256Hash`** (verified live). Hangar's
`/api/v1/versions/hash/{hash}` lookup is SHA-256, which is convenient: the same digest serves
both provider verification and the product's own record.

### Dependencies

`pluginDependencies` maps a platform to entries of `{ name, projectId, required, externalUrl,
platform }`. A dependency may be external, identified only by name and URL, in which case it
cannot be resolved automatically and must be reported honestly instead.

### Known limitations

- No Spigot/Bukkit/Purpur platforms (see above).
- No rate-limit headers observed; pacing is the client's responsibility.
- Some versions are externally hosted, with no provider hash.
- Dependencies may be external and unresolvable.

---

## Attribution and terms

- **Modrinth:** the Terms of Use permit third-party services to download, display and query
  user-generated content via the API. They do not state an explicit attribution or
  link-back requirement for API consumers, and they prohibit using the content to train
  machine-learning models. The product nonetheless names Modrinth as the source on every
  listing and links to the project page, so nothing looks as though 1Salem published it.
- **Hangar:** the API documentation covers anonymous access and rate limits. A dedicated
  third-party attribution policy was not located, so the product applies the same rule:
  every listing names Hangar and links to the project page.
- Each plugin carries its own licence, which is shown when the provider reports one. 1Salem
  Server Manager never republishes or rehosts provider files; downloads go directly from the
  provider's own CDN to the person's machine.

---

## Content types: who actually serves what

Verified 2026-09-22 against `GET /v2/tag/project_type`, `GET /v2/tag/loader` and live searches.

| Content type | Modrinth | Hangar |
|---|---|---|
| Plugin | yes (`project_type:plugin` facet; loaders paper, spigot, bukkit, purpur, folia) | yes (the only type it has) |
| Modpack | yes (`project_type:modpack`; loaders fabric, forge, neoforge, quilt) | no |
| Data pack | yes (`project_type:datapack` facet; loader `datapack`) | no |
| Resource pack | yes (`project_type:resourcepack`; loader `minecraft`) | no |

Hangar is a plugin repository: its `Platform` enum is PAPER, WATERFALL and VELOCITY, and it
has no pack types at all. Modpacks, data packs and resource packs therefore come from
Modrinth only, and the UI says so rather than showing an empty Hangar section.

Modrinth's full project type list is `mod, modpack, resourcepack, shader, plugin, datapack,
minecraft_java_server`.

---

## Modpacks (Modrinth)

### Search and versions

`project_type:modpack`, with the loader as a category facet. A version's file is a
`.mrpack` archive; its `loaders` are mod loaders (**fabric, forge, neoforge, quilt**), never
Paper or Spigot. A modpack is not a plugin and cannot be installed into a plugin server.

### The .mrpack format

A ZIP containing `modrinth.index.json` (UTF-8, at the root):

- `formatVersion` (currently 1), `game` (`minecraft` only), `versionId`, `name`, `summary`
- `files[]`: `path` (destination relative to the instance root), `hashes` (**sha1 and sha512
  required**), `env` (`client`/`server`, each `required`, `optional` or `unsupported`),
  `downloads[]` (HTTPS), `fileSize`
- `dependencies`: `minecraft` plus one of `fabric-loader`, `forge`, `neoforge`,
  `quilt-loader`, each with a version
- Optional folders applied in order: `overrides`, then `server-overrides`.
  `client-overrides` is for clients and is ignored on a server.

### Trust model for pack file downloads

Modrinth restricts pack downloads to four domains: **cdn.modrinth.com, github.com,
raw.githubusercontent.com, gitlab.com**. The manager accepts only those four, over HTTPS
only, and verifies every file against the SHA-512 in the index (falling back to SHA-1 when
SHA-512 is absent). A file whose host is not on the list, or whose hash does not match, stops
the whole install.

### Loader support, and its honest limit

A modpack server needs the loader itself, which is not part of the pack.

- **Fabric** can be installed deterministically: `meta.fabricmc.net` publishes a server
  launcher jar at `/v2/versions/loader/{game}/{loader}/{installer}/server/jar`
  (verified: 1.21.8 returns HTTP 200).
- **Forge, NeoForge and Quilt** require running their own installer programs. The manager
  does not run third-party installers, so packs for these loaders are listed and described
  but not installed, and the reason is shown.

### Limitations

- Installs into a **new** server only. A modpack changes the Minecraft version, the loader
  and the whole mods set, so it is never applied over an existing configured server.
- `env.client: required` files that are `server: unsupported` are skipped: they would never
  load on a dedicated server.

---

## Data packs (Modrinth)

### Search and versions

The `project_type:datapack` facet works, but a hit's own `project_type` still reads `mod`
with `datapack` among its categories, exactly like plugins. Identity comes from the
`datapack` loader, never from `project_type`.

### Compatibility

Provider `game_versions` must contain the server's exact Minecraft version. The archive's own
`pack.mcmeta` is also read: it carries `description` plus `pack_format`, or `min_format` and
`max_format` on newer packs. The wiki does not state that a mismatched format blocks loading,
so a mismatch is reported as uncertain rather than claimed as incompatible.

### Destination

`<server root>/<level-name>/datapacks`, where `level-name` comes from that server's
`server.properties` and names both the world and its directory. The path is never assumed to
be `world/datapacks`: if `server.properties` has no readable `level-name`, the manager says it
cannot identify the world instead of guessing.

### Activation

A data pack added to a running world needs `/reload` or a restart. The manager never sends
console commands; it reports "Reload or restart required" honestly.

---

## Resource packs (Modrinth)

### Search and versions

`project_type:resourcepack`, loader tag `minecraft`, filtered by game version.

### What "support" means on a dedicated server

A dedicated server does not load resource packs itself. It *points clients at one* through
`server.properties` (verified against the current Java Edition documentation):

- `resource-pack` — a URL clients download from; the pack may not exceed 250 MiB
- `resource-pack-sha1` — lowercase hexadecimal SHA-1; a wrong value logs
  "Invalid sha1 for resource-pack-sha1" at startup
- `resource-pack-id` — optional UUID identifying the pack to clients
- `resource-pack-prompt` — chat component shown when the pack is required
- `require-resource-pack` — whether declining disconnects the player

### How that is handled here

Modrinth already serves the file over HTTPS from `cdn.modrinth.com` and publishes its SHA-1,
so a provider-hosted pack can be distributed by pointing `resource-pack` at that URL with the
provider's SHA-1 — no hosting by this app. Writing those settings happens only when the person
asks for it.

A pack that exists only as a local file cannot be distributed: clients need a reachable URL.
The manager does not start an HTTP server, open ports or touch the tunnel, so that case is
reported as "needs a reachable URL" rather than shown as installed.

---

## Product-wide rules that follow from this research

1. Public reads only. No accounts, no tokens, no uploads.
2. A unique application `User-Agent` on every provider request.
3. Search input is debounced; provider calls are paced within the documented limits.
4. A download URL must come from a provider version response. Provider-supplied filenames
   and slugs are never used as filesystem paths.
5. Provider hashes are verified when present: SHA-512 on Modrinth, SHA-256 on Hangar. The
   product also records its own SHA-256 of every installed file. Where a provider gives no
   hash, that is recorded honestly rather than shown as "verified".
6. Compatibility is resolved against the server's real Minecraft version and platform, and
   the newest *compatible* release is installed, not the newest version overall.
