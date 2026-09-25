# 1Salem Connect broker (Phase 1, local only)

The control plane of 1Salem Connect: a Cloudflare Worker with a D1 database that registers owner
and device keys, relays invites, approvals and enrollment ciphertext, signs short-lived session
tickets and publishes revocations. It never carries game traffic and holds no Tailscale
credential. The binding contract is [`docs/CONNECT_ARCHITECTURE.md`](../../docs/CONNECT_ARCHITECTURE.md)
(§5 signed requests, §6 invites, §9 tickets, §12 revocation, §13 API, §14 abuse protection).

**Nothing is deployed.** Phase 1 runs only under `wrangler dev --local` (workerd + Miniflare,
D1 in a local SQLite file). `wrangler.jsonc` sets `workers_dev: false` and `preview_urls: false`,
and its `database_id` is a placeholder of zeros, because `wrangler d1 create` is never run. Do not
run `wrangler deploy`, `wrangler secret put` or anything with `--remote`.

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
| `scripts/new-dev-secrets.mjs` | Local secrets, written only to `--out` |
| `scripts/local-proof.mjs` | Loopback-only lifecycle proof |

## Behaviour worth knowing

- Every "not found" is the identical body `{"error":"not_found"}`. That covers missing, foreign,
  malformed-id and invalid-invite cases. Other errors are bare codes: `bad_request`,
  `unauthorized`, `payload_too_large` (bodies over 16 KiB), `rate_limited` (with `Retry-After`),
  `invalid_state`, `already_member`, `already_bound`, `node_in_use`, `cursor_ahead`,
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
- `POST /v1/memberships/{id}/node` refuses a node id that a *different* device holds on a live
  (pending or approved) membership of the same owner with a bare 409 `node_in_use`. One device
  may bind the same node on all its memberships with that owner, since there is one node per owner
  tailnet. Other owners' bindings are never consulted. The node id is still self-reported by the
  friend's device; see the Agent requirement below.

## Requirements outside the broker

The broker cannot enforce these itself.

- **Before any deployment: an outer, non-D1 rate limit.** Every request that reaches the Worker
  costs at least one D1 read. A request under its budget also costs D1 writes (its counter and its
  nonce), and a validly signed request over its key's budget still costs its nonce write. The D1
  counters are exact, but they are D1 traffic themselves, so they cannot bound the
  cost of a flood. Put a Cloudflare WAF rate-limiting rule on the broker's route, or a Workers Rate
  Limiting binding checked first in `fetch`, in front of it. Key it per client IP, and set it well
  above the budgets in the table so it only ever catches floods. It is per location and eventually
  consistent, so it complements the D1 counters and does not replace them. It is deployment
  configuration and is deliberately not part of `wrangler.jsonc` while Phase 1 is local only.
- **After any database restore (for example D1 Time Travel), raise the revocation sequence before
  reopening the broker.** A restore rolls back `sqlite_sequence` too, so new revocations would
  reuse seq numbers issued before the restore. A host holding one of those as its cursor would pass
  the `cursor_ahead` check once a new event of its owner receives that same seq, and the events up
  to it would never be delivered to that host. Run, once, right after the restore:
  `UPDATE sqlite_sequence SET seq = seq + 1000000000 WHERE name = 'revocations';` Every seq issued
  afterwards is above every seq issued before, so every kept cursor gets `cursor_ahead` and its
  host starts again from `after=0` (events are idempotent). The host authorization component's
  local revocation set and immediate enforcement (architecture §12) do not depend on the feed. The
  Agent service hosts that component in Phase 2 (architecture §21 D-4); in Phase 1 it runs only in
  tests and the disposable proof, and nothing consumes the feed yet.
- **Leave Cloudflare's Pseudo IPv4 off, or at "Add header".** "Overwrite headers" replaces
  `CF-Connecting-IP` with an address derived from the full IPv6 address. That gives every
  address in a /64 its own bucket again.
- **The Agent must never `DELETE` a tailnet device id taken from the broker without checking its
  tags first.** A membership's `nodeId` is whatever the friend's device reported. Before
  revocation step 3 (`DELETE /api/v2/device/{id}`, §12), the Agent must read that device from the
  Tailscale API. It may delete the device only when it carries `tag:1salem-client` and is not one
  of the owner's own nodes. Otherwise a friend who bound the id of the owner's own machine would
  have it removed from the owner's tailnet when the friend is revoked.
