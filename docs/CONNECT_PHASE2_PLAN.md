# 1Salem Connect — Phase 2 implementation plan (Build 8)

Status: **implemented locally; live acceptance and deployment remain separate gates.**
*Superseded 2026-09-29: live acceptance is complete and the broker is deployed on workers.dev; see
the broker README.* See
`CONNECT_PHASE2_VALIDATION.md` for the final verification record and remaining limitations.
Phase 1 (secure transport foundation, commit `cc4d7b3`) and the real-tsnet
smoke test (commit `4bfb9a8`, 58/58) are complete and are not redone. This plan turns the Phase 1
libraries into a working product for the **owner** (Server Manager + Agent) and the **friend**
(`1Salem.Connect.exe`), Minecraft first. `docs/CONNECT_ARCHITECTURE.md` stays the contract; each
section below says which part of it changes.

Scope (from Salem): host integration into the real Agent; real Tailscale provisioning; owner-confirmed
node binding (§21 D-1); production-ready Cloudflare Worker + D1 integration (§21 D-2, D-2a); invite /
approval / revoke workflow; Connect UI in Server Manager; Minecraft first.

Out of scope: redoing Phase 1 or the real-tsnet proof; Palworld / UDP; Playit; packaging Build 8
(`VERSION` stays `1.5`, `BUILD_REVISION` stays `7`); deploying anything to Cloudflare; touching the
installed Build 7, production Minecraft, Palworld or Playit.

---

## 0. Decisions

Taken in this plan (sensible defaults, reversible):

| # | Decision | Why |
|---|---|---|
| P2-1 | **O1** (§2): the owner's own OAuth client lives on the owner's PC, DPAPI-protected by the Agent (SYSTEM). | The accepted smoke test used exactly this; the broker never holds a Tailscale credential. |
| P2-2 | Connect host components are orchestrated by a `ConnectHost` class in `ServerManager.Infrastructure/Connect`; the Agent only hosts it as a `BackgroundService`. | Keeps it testable and drivable from a disposable harness without replacing the installed Agent. |
| P2-3 | All Connect state lives in `%ProgramData%\1SalemServerManager\connect\` under the protected SYSTEM-only DACL (`CurrentUserOnlyAccess`, as the owner identity already requires). JSON files written atomically (temp file + move). | Same trust model as the Phase 1 identity store; nothing Connect-related in the general SQLite settings. |
| P2-4 | Local revocations are **persisted** (`revocations.json`) and seeded into the host authorization pipe server **before** it starts serving; the feed then runs incrementally from the kept cursor. | An Agent restart must not forget a revocation, and must not depend on the broker being reachable (§16). |
| P2-5 | The Agent verifies every friend node binding itself and confirms it with the new owner-signed broker route. Tickets are issued only for confirmed bindings. A binding that fails verification is rejected, never deleted. | D-1. |
| P2-6 | A friend has **one node per owner tailnet**, shared by all of that friend's servers with this owner. Revoking one server deletes the tailnet device only when no other live membership of that owner is bound to it; otherwise the UI says so honestly. | Deleting it would cut the friend off servers they still legitimately have. |
| P2-7 | Owner UI: an owner-level card on the **Network** page (+ a setup window), and a per-server card in **Server Detail › Settings › Network** (Minecraft only). No new sidebar destination or tab. | Structural tests pin five destinations and five tabs. |
| P2-8 | Only per-server **Revoke access** is offered in the UI. Device-wide revocation (`/v1/devices/{id}/revoke`) is not exposed in Phase 2. | The host's device revocation is permanent while the broker allows re-invite; exposing it would create a confusing, unrecoverable state. |
| P2-9 | Broker production config targets the route pattern `connect.1salem.app/v1/*`, leaving `connect.1salem.app/i#…` for the invite landing page. *Superseded 2026-09-29: production is served on its workers.dev origin (`/v1/*` and `/i`), no zone routes.* | A custom domain on the whole host would 404 every invite link. |

**Needed from Salem (none blocks implementation):**

1. **Policy check scope.** Detecting an allow-all or over-broad tailnet policy (§8) needs `GET
   /api/v2/tailnet/-/acl`, which requires the read-only OAuth scope **`policy_file:read`**. The
   current OAuth client has only `auth_keys` + `devices:core`. The plan keeps §8 as written:
   **Connect stays off until the policy is verified.** At acceptance time Salem creates a
   replacement OAuth client with `auth_keys`, `devices:core` and `policy_file:read`, tagged
   `tag:onesalem-host`. (No change is made to the existing client.)
2. **Broker hostname** (P2-9) — confirm `connect.1salem.app/v1/*` or choose a separate API host.
   Only configuration and docs depend on it. *Resolved 2026-09-29:* production uses the Worker's
   workers.dev origin `https://onesalem-connect-broker-production.onesalemconnect.workers.dev`
   (API `/v1/*`, invite page `/i`) with no zone routes; `connect.1salem.app` is an optional later
   migration.
3. **Deployment** — nothing is deployed in Phase 2 implementation. A separate, explicitly approved
   step creates the D1 database, sets the secrets, adds the outer limiter and deploys. *Done
   2026-09-29 by Salem; see the broker README.*
4. **Real acceptance run** — proving the Agent-hosted path on the real tailnet needs a new
   disposable harness run (like the smoke test) with Salem's authorization; it never replaces the
   installed Build 7 Agent.

---

## 1. Target flow (owner and friend)

```
Owner (Server Manager, Network page)                 Agent (SYSTEM)                     Tailscale API / broker
 1 Set up private friend access: paste OAuth id+secret ─► store DPAPI ─► token, policy read ─► verdict
 2                                                       owner identity ─► POST /v1/owners, GET /v1/keys (pin)
 3                                                       start authz pipe (seeded revocations), start host transport
 4                                                       mint tag:onesalem-host key ─► enroll over control pipe
                                                         GET device: host tag, no Tailnet Lock error ─► hostBridge 100.x:7780
Owner (Server Detail › Settings › Network, Minecraft)
 5 Enable for this server ────────────────────────────► PUT /v1/servers/{id} {label, tcp, hostBridge}
 6 Invite friend ─────────────────────────────────────► POST /v1/invites ─► link <broker origin>/i#<secret> (shown once)
Friend (1Salem Connect)
 7 redeem ─► pending
Owner
 8 Friends: Approve ──────────────────────────────────► POST approve; mint tag:onesalem-client key; seal to device key;
                                                         POST enrollment (15 min)
Friend
 9 take blob ─► enroll node ─► POST node {nodeId} (candidate)
Agent (poll)
10 candidate: GET device(nodeId): tag:onesalem-client, not host, created after this key, not bound elsewhere
   ─► POST /v1/memberships/{id}/node/confirm {nodeId} ─► delete key if unused
Friend
11 Connect ─► POST /v1/sessions (only for confirmed) ─► ticket ─► tailnet:7780 ─► Agent authorizes ─► 127.0.0.1:<port>
Owner
12 Revoke access ─► broker revoke ─► local revoke + close live ─► tag-checked device delete (if unshared) ─► report each step
```

---

## 2. Broker (connect/broker) — D-1, D-2, D-2a

**Schema** `migrations/0003_node_confirmation.sql` (additive; never rebuild `memberships` or
`revocations`):

- `memberships.node_state TEXT NULL CHECK (node_state IS NULL OR node_state IN ('candidate','confirmed','rejected'))`
- `memberships.node_confirmed_at INTEGER NULL`
- backfill `UPDATE memberships SET node_state='candidate' WHERE node_id IS NOT NULL` (existing bindings fail closed).

**Routes:**

| Route | Auth / budget | Behaviour |
|---|---|---|
| `POST /v1/memberships/{id}/node` (changed) | device / device-call | Binds as `candidate`. `node_in_use` counts only holders whose `node_state` is `candidate` or `confirmed`. A membership whose candidate was rejected answers 409 `node_rejected`. |
| `POST /v1/memberships/{id}/node/confirm` (new) | owner / owner-write | Body `{nodeId}`. One conditional `UPDATE … WHERE owner_id=? AND state='approved' AND node_id=? AND node_state='candidate'`. Same node already confirmed → 200 (idempotent). Different node → 409 `node_mismatch`. No candidate / wrong state → 409 `invalid_state`. Foreign/missing → generic 404. Audit `membership.confirm_node`. |
| `POST /v1/memberships/{id}/node/reject` (new) | owner / owner-write | Body `{nodeId}`. Candidate → `rejected` (node id kept for the record). Confirmed → 409 `invalid_state` (use revoke). Idempotent. Emits **no** revocation event. Audit `membership.reject_node`. |
| `POST /v1/sessions` (changed) | device / session | Requires `node_state='confirmed'` in the SELECT and in the `INSERT … SELECT`. |
| `GET /v1/owners/me/memberships` (changed) | owner / owner-read | Adds `nodeState`, `nodeBoundAt`, `nodeConfirmedAt`. |
| `GET /v1/devices/me/memberships` (changed) | device / device-call | Adds `nodeState`. |

New error codes: `node_mismatch`, `node_rejected` (bare, `^[a-z_]+$`).

**Production readiness (not deployed):** *Superseded 2026-09-29: production is live on workers.dev (`workers_dev:true`, no routes); see the broker README.*

- `wrangler.jsonc` gets `env.production`: `workers_dev:false`, `preview_urls:false`, route
  `connect.1salem.app/v1/*` (zone `1salem.app`), its own `d1_databases` entry (placeholder id,
  replaced at deployment), `secrets.required`, cron `*/15 * * * *`, `observability` with invocation
  logs off, a `ratelimits` binding `FLOOD` (per client network, well above the D1 budgets), and var
  `CONNECT_REQUIRE_FLOOD_LIMIT="true"`. The top-level config stays local-shaped for tests.
- `src/index.ts`: the outer limiter runs first in `fetch`, before any body read or D1 access, keyed
  on `clientNetwork(CF-Connecting-IP)`; over the limit → 429 `rate_limited` + `Retry-After`. With
  `CONNECT_REQUIRE_FLOOD_LIMIT="true"` and no binding → 503 `not_configured` (fail closed).
- `scheduled()` catches and logs (redacted) a purge failure.
- `package.json`: `deploy:check` (`wrangler deploy --dry-run --env production`) and
  `migrate:remote` (documented, not run).
- README: a deployment runbook (D1 create, remote migrations, secrets, deploy, Pseudo IPv4 off or
  "Add header", optional WAF rule) and the D-2a restore step.
- Tests: `confirmNode` helper; `readyFriend` confirms; new suites for confirm/reject (ownership,
  generic 404s, idempotency, mismatch, reject frees `node_in_use`, sessions refused until
  confirmed); the outer limiter (binding present / absent / required); `scripts/local-proof.mjs`
  confirms before the session.

---

## 3. Owner side: libraries (ServerManager.Connect.Core / ServerManager.Infrastructure)

| Component | Where | What |
|---|---|---|
| `ConnectProtectedDirectory` | Core/Identity | Public wrapper over `CurrentUserOnlyAccess` to create/verify a protected folder (SYSTEM-only under the Agent). |
| `ConnectOwnerPaths` | Infrastructure/Connect | `<DataRoot>\connect\` layout: `identity\`, `host-transport\` (tsnet state root), `state.json`, `revocations.json`, `ticket-keys.json`, `oauth-client.dpapi`. |
| `ConnectOAuthCredentialStore` | Infrastructure/Connect | Save / load / delete the owner's OAuth client, DPAPI (CurrentUser = the Agent's account) with its own entropy. Never logged; `ToString` redacted. |
| `TailscaleApiProvisioner` (extended) | Infrastructure/Connect | `CreateHostAuthKeyAsync` (exactly `tag:onesalem-host`, one-off, pre-authorized, non-ephemeral, 1 day); `ConnectTailnetDevice` gains `Hostname`, `Addresses`, `IsEphemeral`, `TailnetLockError`; `GetPolicyAsync` (`GET tailnet/-/acl`, `Accept: application/json`, scope `policy_file:read`; 403 → "not permitted", distinct from other failures); **`DeleteFriendDeviceAsync`** that re-reads the device and refuses unless it carries `tag:onesalem-client`, does not carry `tag:onesalem-host`, and is not the host node. The raw `DeleteDeviceAsync` becomes internal. |
| `ConnectPolicyAnalyzer` | Infrastructure/Connect | Pure function over the policy JSON (grants and legacy `acls`, `src`/`users`, `dst`/`ports`). Verdict `Safe`, `Unsafe(reasons)` or `Unverifiable(reason)`. Unsafe when any rule whose source could include a friend node (`*`, `tag:onesalem-client`, `autogroup:tagged`, any IP/CIDR/host alias that may cover 100.64.0.0/10 or fd7a:115c:a1e0::/48) grants anything other than exactly `tag:onesalem-host` TCP 7780; when either tag is missing from `tagOwners`; or when `tag:onesalem-client` is not owned by `tag:onesalem-host`. |
| `ConnectOwnerBrokerClient` | Infrastructure/Connect | Every owner route of §13 plus confirm/reject and the feed. Hardening copied from the friend `BrokerClient`: HTTPS origin only (plain HTTP only to loopback with an explicit development flag), no redirects, no cookies, 20 s deadline per exchange including the body, 64 KiB bounded reads, typed failures with the broker's error code, response buffers zeroed (invite secrets). |
| `ConnectHostTransportControlClient` | Infrastructure/Connect | The host transport's control pipe (`hello`, `status`, `enroll`, `diag`): owner-verified (`VerifyServerOwner` = the Agent's account) and serving-process-verified (the PID the supervisor started) before any byte; 2 min deadline for `enroll`; request lines wiped. |
| `ConnectHostTransportSupervisor` (extended) | Infrastructure/Connect | Passes an explicit `--pipe`; exposes the running PID; puts the sidecar in a kill-on-close job so an Agent crash cannot leave it orphaned. |
| `ConnectHostAuthorizationPipeServer` (extended) | Infrastructure/Connect | Accepts initial revocations in its options (applied before the first client is served); read-only status (subscriber present, live connections). |
| `ConnectStateStore` | Infrastructure/Connect | `state.json`: owner id, host node id and addresses, host bridge, feed cursor, Connect-enabled servers, registered server labels/bridges, invites (ids, server, expiry, state — **never** the secret), per-membership records (device id, server, key id + mint time, enrollment posted at, confirmed node id, local nickname). `revocations.json`: revoked devices, memberships, tickets (with expiry). |
| `ConnectHost` | Infrastructure/Connect | The orchestrator (§4). |

---

## 4. Agent hosting (D-4) — `ConnectHost`

**Lifecycle** (one instance, driven by `ConnectHostService : BackgroundService` in the Agent):

1. **Off** while no OAuth client is stored. Nothing Connect-related runs; no pipe is created.
2. **Start** (credential present): ensure `connect\` folders → owner identity (`LoadOrCreate`) →
   `POST /v1/owners` (idempotent) → keyset: pinned `ticket-keys.json`, or fetch `GET /v1/keys` over
   HTTPS the first time and pin it → catalog (three-argument constructor with the Agent's real API
   ports) and persisted Connect-enabled servers → **authorization pipe server with seeded
   revocations** (a name already in use → Connect off, reported, never retried blindly) → host
   transport supervisor (tsnet, `:7780`, state under `connect\host-transport`).
3. **Host node**: control-pipe `hello`/`status`. `waiting-for-enroll` → mint a `tag:onesalem-host`
   key → `enroll{authKey, hostname}` → read the device through the API: exactly
   `tag:onesalem-host`, not ephemeral, empty `tailnetLockError` (otherwise report "Tailnet Lock is
   not supported", §16) → keep node id and addresses → delete the key if still present → host
   bridge = `<first tailnet IPv4>:7780`.
4. **Policy**: `GET acl` → analyzer. Anything but `Safe` keeps Connect **unavailable for enabling**
   (existing enabled servers are not served either) and the UI shows the reason.
5. **Reconcile loop** (every 15 s while running, jittered; owner-read budget 120/60 s):
   - revocation feed from the kept cursor (loop while `more`; `cursor_ahead` → `after=0`), applied
     to the pipe server and persisted;
   - owner membership list →
     - approved, no confirmed node for that device, no live enrollment → mint friend key, seal to the
       device key (`EnrollmentCrypto.Encrypt`, which also checks the device id), `POST enrollment`,
       record key id + mint time; re-post after the 15-minute TTL while unused;
     - candidate → verify (§5) → confirm or reject;
     - confirmed → delete the friend key if unused;
   - enabled servers: `PUT /v1/servers/{id}` whenever label or host bridge changed.
6. **Stop** (Agent stop, credential removed, "turn off"): supervisor stop, pipe server dispose.
   Turning a single server off goes through `DisableConnectAsync` so its live connections close.

**Owner actions** (`ConnectOwnerWorkflow`, used by the Agent endpoints): enable/disable a server,
create/revoke invite, list friends, approve, reject, revoke access (§12 four steps with a per-step
outcome), rename a friend locally, re-check setup.

**Agent HTTP API** (new `ConnectEndpoints.cs`; **local client only** — a paired LAN client is
refused; DTOs in `ServerManager.Contracts/ConnectContracts.cs`):

| Method & path | Purpose |
|---|---|
| `GET /api/v1/connect/status` | Account, policy verdict, Tailnet Lock, host node, bridge, broker reachability, last error. |
| `PUT /api/v1/connect/credential` | Store the OAuth client (id + secret), validate it (token, policy read), start Connect. |
| `DELETE /api/v1/connect/credential` | Turn Connect off and forget the OAuth client (tailnet devices are listed for manual removal, never deleted silently). |
| `POST /api/v1/connect/check` | Re-run the policy / Tailnet Lock / host checks. |
| `GET /api/v1/servers/{id}/connect` | Eligibility (Minecraft, port rules, duplicates, `prevent-proxy-connections`), enabled, invites, friends. |
| `POST /api/v1/servers/{id}/connect/enable` / `disable` | Per-server switch. |
| `POST /api/v1/servers/{id}/connect/invites` | `{ttlSeconds}` → `{inviteId, link, code, expiresAt}` (secret returned once, never stored). |
| `POST /api/v1/connect/invites/{inviteId}/revoke` | Revoke an unused invite. |
| `POST /api/v1/connect/memberships/{id}/approve` / `reject` / `revoke` | Friend decisions; revoke returns the per-step outcome. |
| `PUT /api/v1/connect/memberships/{id}/nickname` | Owner's local label for a friend. |

---

## 5. Owner-confirmed binding (D-1) — the Agent's verification

A candidate `nodeId` for membership M (device D) is confirmed only if the API device exists and:

- it carries `tag:onesalem-client` and not `tag:onesalem-host`, and is not the host node;
- it is not ephemeral;
- either (a) the same node is already **confirmed** for another live membership of the same device
  D with this owner (the friend's one node per owner tailnet), or (b) it was **created at or after**
  the mint time of the key the Agent sealed for M (2 minutes of clock skew allowed);
- no live membership of a **different** device is bound to it.

A definite failure (wrong tag, host node, created before the key, bound elsewhere, not found after a
grace period of 10 minutes) → `node/reject`. Transient API failures → retry next loop. The friend
sees "Setting up this PC failed — ask the owner to invite you again".

---

## 6. Revocation (§12) as built in Phase 2

1. Broker `POST /v1/memberships/{id}/revoke`.
2. Local: `RevokeMembershipAsync` (refuse, drop replay entries, close live connections) and persist.
3. Tailnet: if the membership had a confirmed node and **no other live membership of this owner**
   is bound to it → `DeleteFriendDeviceAsync` (tag-checked) → confirm 404. If it is still used
   → step reported as "kept: the friend still has access to other servers".
4. The UI says "Access revoked" only when every applicable step succeeded; otherwise it names the
   pending step, and the reconcile loop retries steps 2–3.

A revocation arriving from the feed (made elsewhere) runs steps 2–3.

---

## 7. Owner UI (ServerManager.Client)

- **Network page** card "Private friend access": state line (not set up / checking / policy needs
  changes / Tailnet Lock unsupported / host starting / ready / error) and one button that opens
  `ConnectSetupWindow` (explain → OAuth client id + secret (PasswordBox, cleared after one POST) →
  check results with "Check again" → turn off). Tailscale is named only factually; the window says
  that Tailscale receives friend device metadata under the owner's account (§17).
- **Server Detail › Settings › Network** card (replaces the disabled preview): Minecraft only
  (Palworld: "not available for this game yet"); ineligible reasons (port < 1024, sensitive or Agent
  port, shared port, `prevent-proxy-connections=true`); Off → Enable; On → Disable, Invite friend,
  Friends (pending count).
- `ConnectInviteWindow`: validity (24 h default, up to 7 days) → link and code shown once (LTR),
  Copy link / Copy code (clipboard-history excluded), expiry, Revoke this invite.
- `ConnectFriendsWindow`: pending (Approve / Reject), approved (setup state: waiting for the friend /
  checking this PC / ready / setup failed), Revoke access (confirmation dialog, per-step outcome),
  local nickname.
- Pure `ConnectPresentation` presenter + public view models (unit-tested headlessly); every string in
  English **and** Arabic; RTL-safe technical values; polling only while visible.
- Diagnostics export chains `SecretRedactor` (tskey-*, clientSecret, invite fragments).
- `ConnectPreviewCardTests` is deliberately replaced by `ConnectOwnerUiTests`.

---

## 8. Friend app and Go transports

- Broker model: `Membership.NodeState` (`None`, `Candidate`, `Confirmed`, `Rejected`, `Unknown`)
  parsed from `nodeState`; `CanConnect` requires `Confirmed`; new card states "Waiting for the owner
  to finish setting up this PC" and "Setting up this PC failed — ask the server owner to invite you
  again"; polling keeps its backoff while a confirmation is pending; `BrokerException` carries the
  broker's error code.
- Stale node after revocation: the friend transport gains a `forget {node}` pipe op (stops that
  node, closes its sessions, empties `<state>\nodes\<node>`). The enrollment coordinator forgets the
  owner's node and enrolls fresh when a blob is waiting and no approved membership with that owner is
  still bound (candidate or confirmed) to the local node id.
- Host transport: unchanged contract; the Agent passes `--pipe` explicitly.

---

## 9. Proof harnesses and docs

- `connect/proof/ConnectProof` (fake mode): add the owner confirm step (through the production
  `ConnectOwnerBrokerClient`) and rerun the fake end-to-end proof in the final regression.
- `connect/proof/TsnetSmoke`: add the confirm step so it stays correct and compiling; **not rerun**.
- Docs: `CONNECT_ARCHITECTURE.md` (§7, §8, §11, §12, §13, §16, §19, §21 statuses), broker README,
  `DEFERRED_ISSUES.md`.

---

## 10. Slices, order and verification

| Slice | Content | Depends on |
|---|---|---|
| A | Broker D-1 + production readiness + tests | — |
| B | Go `forget` op (friend transport) + tests | — |
| C | Core/Infrastructure libraries (§3) + tests | — |
| D | `ConnectHost`, workflow, Agent service + endpoints + contracts + tests | A (contract), C |
| E | Friend app (§8) + tests | A (contract), B |
| F | Owner UI (§7) + tests | D (contracts) |
| G | Proof harness updates, docs, final regression | all |

Each slice: implement → focused tests → independent review → fix → commit (local only, no push).
Final regression: .NET solution (Release build + all tests), Go (both tag configurations), broker
(typecheck + tests + `deploy:check` dry run), fake-mode end-to-end proof; secret scan; Build 7 /
production unchanged check. Real-tailnet acceptance of the Agent-hosted path is a separate step that
needs Salem's authorization (decision 1 and 4).
