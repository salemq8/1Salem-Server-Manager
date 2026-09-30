# 1Salem Connect broker (live on Cloudflare workers.dev)

The control plane of 1Salem Connect: a Cloudflare Worker with a D1 database that registers owner
and device keys, relays invites, approvals and enrollment ciphertext, signs short-lived session
tickets and publishes revocations. It never carries game traffic and holds no Tailscale
credential. The binding contract is [`docs/CONNECT_ARCHITECTURE.md`](../../docs/CONNECT_ARCHITECTURE.md)
(§5 signed requests, §6 invites, §9 tickets, §12 revocation, §13 API, §14 abuse protection).

**Production is live** at `https://onesalem-connect-broker-production.onesalemconnect.workers.dev`
(see [Production](#production-live-on-workersdev)). The top-level configuration runs only under
`wrangler dev --local` (workerd + Miniflare, D1 in a local SQLite file). Do not run
`wrangler deploy`, `wrangler secret put` or anything with `--remote` during implementation or
tests; production changes need Salem's approval.

## Prerequisites

The repository carries its own toolchain in `.tools\` (git-ignored). In PowerShell, from this
directory:

```powershell
$env:PATH = "$PWD\..\..\.tools\node;$env:PATH"
$env:npm_config_cache = "$PWD\..\..\.tools\npm-cache"
$env:WRANGLER_SEND_METRICS = "false"
npm ci
```

## Tests and type checking

```powershell
npm run typecheck
npm test
```

Tests run offline inside workerd through `@cloudflare/vitest-plugin`. `vitest.config.ts` generates
a fresh ticket key and invite pepper for every run and passes them as bindings. Nothing is read
from or written to disk, and no `.dev.vars` is needed. A developer's own `.dev.vars` is overridden.

## Local development server

1. Generate local secrets. The script writes only to the path you give it and prints only the
   public key and its `kid`:

   ```powershell
   npm run dev-secrets -- --out .dev.vars
   ```

   `.dev.vars` is git-ignored. `.dev.vars.example` shows its shape with non-working placeholders.
   The script refuses to overwrite an existing file unless you pass `--force`.

   `CONNECT_DEV_LOOPBACK_BRIDGE=allow` lets owners register a loopback host bridge for the fake
   transport mode. Without it (the only valid state outside a local broker) `PUT /v1/servers`
   accepts tailnet addresses only, and `POST /v1/sessions` refuses to sign a ticket for a loopback
   bridge stored earlier. It cannot go in `.dev.vars`: because `wrangler.jsonc` declares
   `secrets.required`, wrangler loads only those names from that file and drops the rest. Pass it
   on the command line instead: `npm run dev -- --var CONNECT_DEV_LOOPBACK_BRIDGE:allow`.

2. Create the local database (`.wrangler/state`, git-ignored):

   ```powershell
   npm run migrate:local
   ```

3. Start the broker on `http://127.0.0.1:8787`. The inspector also listens on loopback only:

   ```powershell
   npm run dev
   ```

The cron purge (`src/maintenance.ts`) does not fire by itself locally. To run it once, open
`http://127.0.0.1:8787/cdn-cgi/local/scheduled`.

### Keeping everything outside the repository

To keep secrets and state in a throwaway directory instead:

```powershell
$t = Join-Path $env:TEMP "1sb"          # keep it short, see below
node scripts/new-dev-secrets.mjs --out "$t\dev-secrets.env"
npx wrangler d1 migrations apply onesalem-connect --local --persist-to "$t\state"
npx wrangler dev --local --persist-to "$t\state" --env-file "$t\dev-secrets.env"
```

On Windows, keep the `--persist-to` path short. Local D1 stores each database under
`v3\d1\miniflare-D1DatabaseObject\<64 hex>.sqlite`, and a long base path pushes that past
`MAX_PATH`. Wrangler then reports only `internal error; reference = …`.

## Local proof

With the dev server running:

```powershell
npm run local-proof
# or: node scripts/local-proof.mjs --base-url http://127.0.0.1:8787
```

The proof runs one full lifecycle: published keys, owner and device registration, server,
invite, redemption, approval, enrollment relay, node binding, session ticket, replay and tamper
checks, revocation and the revocation feed. It prints one JSON summary. The summary includes the
published keyset and the one ticket the proof was issued, which it revokes before finishing. Exit
codes:

| code | meaning |
|---|---|
| 0 | every check passed |
| 1 | a check failed (see `checks[].detail`) |
| 2 | usage error, a base URL that is not loopback, or the broker is unreachable |

The base URL must be a loopback IP literal (`127.0.0.0/8` or `[::1]`). `localhost` and any other
name are refused, and redirects are not followed. All data the proof registers is fake-mode data:
a loopback host bridge, a `fake-proof-…` node id, and opaque test bytes in place of enrollment
ciphertext. Registration is limited to 30 per hour per client network, and local requests share
one bucket, so the proof (2 registrations per run) can run about 15 times an hour against the
same state.

## Production (live on workers.dev)

Deployed on 2026-09-29 from `env.production`:

| Item | Value |
|---|---|
| Worker | `onesalem-connect-broker-production` |
| Origin | `https://onesalem-connect-broker-production.onesalemconnect.workers.dev` |
| API | `<origin>/v1/*` |
| Invite landing page | `<origin>/i`; links are `<origin>/i#<secret>` and the fragment never reaches the Worker |
| D1 | `onesalem-connect-production` (`b30aa061-4d2e-4bec-891f-3ec9a461d5c6`), migrations 0001–0003 |
| Secrets | `TICKET_SIGNING_KEY`, `INVITE_PEPPER` (stored; never recreate or print them) |
| Other bindings | `FLOOD` limiter (2000 per 60 s, `namespace_id` 1001), `CONNECT_REQUIRE_FLOOD_LIMIT=true`, cleanup cron every 15 minutes |
| Hostnames | workers.dev on, preview URLs off, no custom domain or zone routes |
| Invite page download | `CONNECT_DOWNLOAD_URL` = the current release's `1SalemConnect-Setup.exe` on GitHub (Build 10 onward) |

The Server Manager Agent (`ConnectHostOptions.ProductionBrokerOrigin`) and the friend app's
packaged `1Salem.Connect.settings.json` (`tools/build-release.ps1`) point at this origin, and the
Agent builds invite links from it. No `1salem.app` hostname is in use.

### Changing production (requires Salem's approval)

Every command below changes remote Cloudflare state. Wrangler must be logged in to the account that
owns the Worker (`npx wrangler login`, done by Salem).

- **Never** run `wrangler d1 create` for production again or change the database id: the database
  holds invites, memberships and revocations.
- **Never** replace the production secrets. `scripts/put-production-secrets.mjs` refuses to
  overwrite existing ones; replacing `TICKET_SIGNING_KEY` would invalidate every issued ticket.
- A new migration file is applied with `npm run migrate:remote` before the code that needs it.
- Keep `ratelimits[].namespace_id` (1001) unique among rate-limiter bindings in the account;
  bindings that share a namespace also share counters.
- Validate, then deploy:

  ```powershell
  npm run typecheck
  npm test
  npm run deploy:check
  npx wrangler deploy --env production
  ```

  Check that the output lists the workers.dev URL only, the `DB` and `FLOOD` bindings and no
  routes. `CONNECT_REQUIRE_FLOOD_LIMIT=true` makes a missing limiter fail closed with 503.
- Smoke-check read-only: `GET <origin>/v1/keys` returns the current `kid`, and `GET <origin>/i`
  returns the landing page.
- `CONNECT_DOWNLOAD_URL` in `env.production.vars` is the "Download 1Salem Connect" button on the
  invite page. Point it at a new release's `1SalemConnect-Setup.exe` only after that GitHub
  release asset exists, then deploy.

### Optional later migration to a custom domain

Not configured, and not needed for Build 8. If `connect.1salem.app` is wanted later: create its DNS
record in the `1salem.app` zone, add routes `connect.1salem.app/v1/*` and `connect.1salem.app/i`
(zone `1salem.app`) to `env.production`, then change `ConnectHostOptions.ProductionBrokerOrigin`
and the packaged `brokerUrl` in a new Build. Keep workers.dev enabled while installed Builds still
use the workers.dev origin. On that zone, leave **Pseudo IPv4** off or at **Add header** (never
**Overwrite headers**, which defeats the IPv6 /64 grouping); a zone WAF rate-limiting rule on
`/v1/*` can then add another coarse flood layer.

### D1 restore procedure

Keep the Worker closed to traffic immediately after a privileged D1 restore (for example, turn off
its workers.dev route in the Cloudflare dashboard). Before reopening it, advance the revocation
sequence exactly once:

```sql
UPDATE sqlite_sequence SET seq = seq + 1000000000 WHERE name = 'revocations';
```

With Wrangler: `npx wrangler d1 execute onesalem-connect-production --remote --env production
--command "UPDATE sqlite_sequence SET seq = seq + 1000000000 WHERE name = 'revocations';"`

Then verify that a pre-restore cursor receives `409 cursor_ahead` and that a host can replay the
owner's idempotent revocation feed from `after=0`. Skipping this step can let a host retain a cursor
that silently skips restored revocation events.

## Layout

| Path | Role |
|---|---|
| `src/index.ts` | Entry point: bounded body read, dispatch, generic errors, cron purge |
| `src/router.ts` | Route table (§13). Path ids are format-checked, so malformed ids get the generic 404 |
| `src/auth.ts` | Signed requests (§5): canonical string, ±300 s window, per-key nonces, registration proof of possession |
| `src/crypto.ts`, `src/encoding.ts` | Pinned ES256/P1363 and HMAC-SHA256; strict base64url/base32 |
| `src/secrets.ts` | `TICKET_SIGNING_KEY` (non-extractable) and `INVITE_PEPPER`; missing values give 503 `not_configured` |
| `src/tickets.ts` | Compact JWS tickets (§9) |
| `src/ratelimit.ts` | Exact fixed-window counters in D1 (§14); a spent budget is refused without a write |
| `src/clientip.ts` | The client network an IP bucket counts: IPv4 address, IPv6 /64, or one shared "unknown" |
| `src/routes/*.ts` | Identities, servers, invites, memberships, enrollment relay, sessions, revocation |
| `migrations/0001_init.sql` | D1 schema. No private key, auth key, OAuth secret or plaintext invite is stored |
| `migrations/0002_feed_cursor_and_node_index.sql` | Indexes for the seq-paged revocation feed and the node binding check |
| `migrations/0003_node_confirmation.sql` | Additive candidate / confirmed / rejected node state; existing bindings become candidates |
| `scripts/new-dev-secrets.mjs` | Local secrets, written only to `--out` |
| `scripts/local-proof.mjs` | Loopback-only lifecycle proof |

## Behaviour worth knowing

- Every "not found" is the identical body `{"error":"not_found"}`. That covers missing, foreign,
  malformed-id and invalid-invite cases. Other errors are bare codes: `bad_request`,
  `unauthorized`, `payload_too_large` (bodies over 16 KiB), `rate_limited` (with `Retry-After`),
  `invalid_state`, `already_member`, `already_bound`, `node_in_use`, `node_mismatch`,
  `node_rejected`, `cursor_ahead`,
  `not_configured`, `internal`.
- Rate limits, all fixed windows:

  | Budget | Limit | Keyed on |
  |---|---|---|
  | Registration (`POST /v1/owners`, `POST /v1/devices` together) | 30 per hour | client network |
  | Registration, every network together | 1000 per hour | global |
  | Redeem | 10 per hour / 30 per hour | device / client network |
  | Sessions | 120 per hour | device |
  | Owner writes (every owner-signed non-GET) | 600 per hour | owner |
  | Owner reads (friend list, revocation feed) | 120 per minute | owner |
  | Other device calls (membership list, enrollment pickup, node binding) | 120 per minute | device |

  A client network is the IPv4 address, or the /64 of an IPv6 address (an IPv4-mapped IPv6
  address counts as its IPv4 address). Anything unparseable, including a missing
  `CF-Connecting-IP`, shares one bucket. D1 holds only an HMAC pseudonym of the network.
  Every attempt that reaches a counter counts up to its limit, including ones that then fail (a
  wrong invite secret, a bad body), so failures cannot be retried for free. Attempts past a limit
  get 429 with `Retry-After` and add no hit: the counter never goes past its limit. Requests with
  missing or malformed signed headers, an out-of-window time or a bad registration body are
  refused before any counter; a correctly signed request with a bad body is charged like any other
  attempt. A request signed by a registered key is checked in this order:
  signature, then nonce, then the key's budget. A forger therefore writes nothing and spends
  nobody's budget. A replay, even many concurrent copies of one captured request, is refused by
  the nonce before the key's budget is charged. A request refused with 429 by its key's budget
  has already used its nonce, so its captured bytes can never be executed after the window
  resets; that nonce is the only write it causes, and only the key's holder can cause it. (A
  redeem refused by its per-network budget has also been charged to its device's budget, which
  comes first.) Registration differs: its per-network budget is charged before the signature is
  checked, so junk floods cost no cryptography; a registration refused by that budget is not
  used up and could be replayed later, which would only register the signer's own key. The
  global registration ceiling, in contrast, is charged only after the proof of possession
  verifies and the nonce is stored, so neither junk nor replays spend it. Once reached, it refuses
  all registrations, including repeats of an existing key, for the rest of the hour, while
  registered keys keep working. That is a deliberate trade: without it, rotating networks could
  create keys without bound.
- Tickets live 600 s. `kid` is the first 16 characters of base64url(SHA-256(SPKI DER)) of the
  ticket key.
- Enrollment blobs expire 15 minutes after they are stored and are deleted when the device reads
  them.
- `GET /v1/owners/me/revocations?after=<seq>` returns
  `{revocations: [{seq, kind, jti?, membershipId?, deviceId?, serverId?, av?, at}], cursor, more}`:
  at most 1000 of the owner's events with `seq > after`, in `seq` order. `after` defaults to 0
  (the whole history), and a value that is not a non-negative safe integer gets `bad_request`. A
  host stores `cursor` and passes it back as `after` next time. `cursor` is the last event's
  `seq`, or `after` itself when the page is empty. `more: true` means the next page is already
  waiting. `seq` is commit order, so an event that commits after a poll is never skipped, however
  its time compares with the poll's. Seqs are shared by all owners and have gaps; `at` is
  informational only. Events are idempotent. A non-zero `after` must be the `seq` of one of the
  caller's own events (every cursor the broker hands out is); anything else, such as a cursor
  from another broker, gets 409 `cursor_ahead` instead of a page, and the host starts again from
  `after=0`. Comparing with the table's highest `seq` would not do: other owners' events can pass
  a stale cursor. A database restore needs one extra step so that cursors kept across it are
  refused too; see "Requirements outside the broker".
- `POST /v1/memberships/{id}/node` records a **candidate** and refuses a node id that a different
  device holds as a candidate or confirmed binding on a live membership of the same owner, with a
  bare 409 `node_in_use`. One device may reuse its node across that owner's memberships. The
  owner-signed `/node/confirm` route makes a verified candidate usable; `/node/reject` keeps a
  failed candidate unusable and frees its node. Sessions are issued only for confirmed bindings.

## Requirements outside the broker

The broker cannot enforce these itself.

- **Before any deployment: verify the outer, non-D1 rate limit.** Every request that reaches the Worker
  costs at least one D1 read. A request under its budget also costs D1 writes (its counter and its
  nonce), and a validly signed request over its key's budget still costs its nonce write. The D1
  counters are exact, but they are D1 traffic themselves, so they cannot bound the
  cost of a flood. `env.production` declares a Workers Rate Limiting binding named `FLOOD`, keyed
  by the normalized client network and checked before route lookup, body reads or D1. It is set well
  above the exact D1 budgets so it catches floods only. It is per location and eventually
  consistent, so it complements the D1 counters and does not replace them. With a custom domain, a
  zone-level WAF rule may be added as another outer layer.
- **After any database restore (for example D1 Time Travel), raise the revocation sequence before
  reopening the broker.** A restore rolls back `sqlite_sequence` too, so new revocations would
  reuse seq numbers issued before the restore. A host holding one of those as its cursor would pass
  the `cursor_ahead` check once a new event of its owner receives that same seq, and the events up
  to it would never be delivered to that host. Run, once, right after the restore:
  `UPDATE sqlite_sequence SET seq = seq + 1000000000 WHERE name = 'revocations';` Every seq issued
  afterwards is above every seq issued before, so every kept cursor gets `cursor_ahead` and its
  host starts again from `after=0` (events are idempotent). The host authorization component's
  local revocation set and immediate enforcement (architecture §12) do not depend on the feed. The
  Agent service hosts that component, persists the cursor and consumes the feed (architecture
  §12 and §21 D-4).
- **With a custom domain, leave the zone's Pseudo IPv4 off, or at "Add header".** "Overwrite
  headers" replaces `CF-Connecting-IP` with an address derived from the full IPv6 address. That
  gives every address in a /64 its own bucket again. The workers.dev hostname is not in a zone, so
  there is no such setting today.
- **The Agent must verify before confirming or deleting.** A membership's candidate `nodeId` is
  whatever the friend's device reported. The Agent confirms it only after the checks in the Phase
  2 plan. Before revocation step 3 (`DELETE /api/v2/device/{id}`, §12), it must re-read the device
  and delete only an exact `tag:onesalem-client` device that is not the host node and is no longer
  shared by another live membership.
