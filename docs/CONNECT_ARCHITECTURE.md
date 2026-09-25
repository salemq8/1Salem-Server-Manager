# 1Salem Connect — Architecture (Build 8, Phase 1)

Status: **Phase 1 design and local foundation, security-reviewed.** Nothing here has enrolled a
real tailnet device, deployed a Cloudflare resource, or touched production. The contracts below
describe the Phase 1 code as built and after its security review; what is still missing is
listed in §19 and §21.

Everything marked **[verified]** is backed by an official source recorded in
`docs/CONNECT_PROVIDER_RESEARCH.md` (fact ids such as `T2-05` refer to that file; `P1-xx` ids
are facts found while building Phase 1, recorded in the same file with how they were checked).
Everything else is **our design**.

---

## 1. What we are building

A friend opens **1Salem Connect**, redeems an invitation, waits for the owner to approve, and
clicks **Connect**. A local address appears — `127.0.0.1:18211` — and Minecraft connects to it.
Only that connection crosses the private transport. The friend's PC is not put on a VPN.

```
 Friend PC                                             Owner PC
 ┌──────────────────────────────┐                      ┌───────────────────────────────────────┐
 │ Minecraft ──TCP──► 127.0.0.1:18211                   │                                       │
 │                     │                                │   1Salem.Connect.Host.Transport.exe  │
 │ 1Salem.Connect.Transport.exe (Go, tsnet)             │   (Go, tsnet, tag:1salem-host)        │
 │   local listener ── preamble+ticket ──► tailnet ───► │   bridge listener  tailnet:7780       │
 │ 1Salem.Connect.exe (WPF)  ◄─named pipe─►             │        │  named pipe  ▲               │
 └──────────────┬───────────────┘   WireGuard, direct   │        ▼             │               │
                │                    or DERP relay      │   1Salem Server Manager Agent         │
                │                                       │   (authoritative: ServerId → endpoint)│
                │                                       │        │                              │
                │                                       │        ▼  127.0.0.1:25565 only        │
                │                                       │   Minecraft server                    │
                │                                       └────────────────┬──────────────────────┘
                │            HTTPS, control plane only                   │
                └──────────► 1Salem Connect Broker (Cloudflare Worker + D1) ◄──┘
                             invites · approvals · signed tickets · revocation
                             never sees game traffic, never holds Tailscale credentials
```

Game bytes travel **Friend ↔ Tailscale data plane ↔ Host** only. The broker is never on that path.

---

## 2. The multi-owner question — DECISION REQUIRED

1Salem Connect must work for many unrelated Server Manager owners. The research settles what
Tailscale allows:

- A Tailscale OAuth client (trust credential) is owned by **one tailnet**, and every key and
  device endpoint is addressed by tailnet **[verified T3-05, T2-23]**. No official page states the
  single-tailnet limit outright for OAuth clients. It follows from the API shape, and there is no
  documented way to point a credential at an unrelated person's tailnet **[T3-06, as
  corrected]**. The one cross-tailnet exception is an `all`-scope client reaching API-only
  tailnets that its own organization created (see O3).
- Tailscale "OAuth apps" (alpha) work only inside the app's own tailnet; users from other
  tailnets are not supported **[verified T3-16, T3-17]**.
- Tagged devices cannot use node sharing across tailnets; only users can accept shares
  **[verified T4-18, T3-22, T7-37]**. Declarative node sharing is alpha, capped at 3 external
  tailnets, and not for untrusted tailnets **[verified T3-23]**.
- The Terms of Service grant use "solely for your own personal use or internal business
  purposes" (§2.1). They forbid commercially exploiting, reselling, renting or leasing use of the
  Services (§2.3). The Personal plan is non-commercial only **[verified T3-26, T7-23, T7-24,
  T7-30]**.
- API-created tailnets (Tailnets API) are alpha and capped at 10 per organization without a
  sales contract **[verified T3-14, T3-15, T7-35]**.

So a single Worker holding one OAuth credential **cannot** provision friends into many owners'
tailnets. The design has to pick where each owner's tailnet credential lives:

| Option | Where friends' nodes live | Where the Tailscale credential lives | Verdict |
|---|---|---|---|
| **O1 Owner tailnet, credential on owner's PC** | Owner's own tailnet, as tagged devices | Owner's PC, DPAPI-protected, used by the Agent | **Recommended** |
| O2 Owner tailnet, credential in the broker | Owner's own tailnet | Cloudflare Worker/D1, encrypted, one per owner | Rejected: custodial risk |
| O3 Vendor-operated tailnet(s) | 1Salem's tailnet(s) | Worker secret | Blocked without a Tailscale agreement |
| O4 Self-hosted coordination server | 1Salem's control plane | 1Salem infrastructure | Out of scope; future alternative |

**O1 (recommended).** Each owner connects their **own** Tailscale account once. They create an
OAuth client with scopes `auth_keys` and `devices:core`, tagged `tag:1salem-host` and
`tag:1salem-client`, and paste its id and secret into Server Manager. The Agent keeps the secret
DPAPI-protected on the owner's PC. It mints **one-off, pre-authorized, tagged** keys for
approved friends, and it deletes friend nodes on revocation **[verified T2-01, T2-04, T2-05,
T3-08, T3-09, T3-10]**. The broker never sees an OAuth secret or an auth key: the Agent encrypts
each friend's key to that friend's device public key, and the broker relays only ciphertext.

- Friends need no Tailscale account. Tagged nodes are owned by the tailnet, not a user
  **[verified T2-03, T4-11]**.
- It fits the Terms: the owner is the Customer, and 1Salem is an "Integration" **[verified
  T7-28]**. The owner stays responsible for consent from friends **[verified T7-27]**. This still
  deserves legal review before a commercial launch (§17).
- Costs fall on the owner's plan. The Personal plan includes 50 tagged resources, then $1 each
  per month **[verified T3-24, T7-30]**. That is one resource per friend device plus one per
  host node.
- Owner setup is real work: declare two tags, create the OAuth client, replace the default
  allow-all policy (§8). Server Manager must guide it and check the result.

**O2** keeps the owner's tailnet but moves every owner's non-expiring, tailnet-wide credential
into one cloud database **[verified T3-04]**. One breach would hand out key-minting and
device-deletion power over every owner's tailnet. It also resembles managing tailnets on owners'
behalf **[T7-34]**. Workload identity federation could avoid static secrets **[verified T3-18]**,
but whether a Worker can serve as the OIDC issuer is unverified, and it would still give the
vendor minting power. Rejected for now.

**O3** (one vendor tailnet, or one API tailnet per owner) conflicts with Terms §2.1/§2.3 unless
1Salem has a commercial or OEM agreement with Tailscale. It is capped at 10 API tailnets without
sales. In a single shared tailnet, owners would also share one policy, so isolation between
owners would rest on the application layer alone. Blocked pending a Tailscale agreement.

**O4** (for example, running Headscale) removes the Terms question but makes 1Salem a network
operator: control server, DERP, and on-call. Not for Phase 1.

> **Decision needed from Salem:** confirm O1. The Phase 1 code implements O1's shape: the broker
> holds no Tailscale credential, and a provisioning interface on the Agent has a fake
> implementation. It does not implement the "Worker holds the OAuth secret" model, because that
> model is either O2 or O3.

---

## 3. Components

| Component | Language | Where | Role |
|---|---|---|---|
| `1Salem.Connect.exe` | C# / WPF | `src/ServerManager.Connect.App` | Friend UI, device identity, broker client |
| `ServerManager.Connect.Core` | C# | `src/ServerManager.Connect.Core` | Shared: tickets, proofs, identities, pipe security, port selection, redaction, enrollment crypto |
| `1Salem.Connect.Transport.exe` | Go + tsnet | `connect/transport/cmd/connect-transport` | Friend data plane |
| `1Salem.Connect.Host.Transport.exe` | Go + tsnet | `connect/transport/cmd/connect-host-transport` | Host bridge data plane |
| Host authorization + supervision | C# | `src/ServerManager.Infrastructure/Connect` | Agent-side: verify tickets, map ServerId → endpoint, supervise host transport. Built as libraries; the Agent service hosts them in Phase 2 (§21 D-4) |
| Broker | TypeScript Worker + D1 | `connect/broker` | Control plane |
| Proof harness | PowerShell + C# | `connect/proof` | Disposable end-to-end proof, isolation snapshot |

The Go sidecars never decide what local services exist. The Agent is authoritative for
ServerId, GameType, local endpoint, running state and friend permissions.

---

## 4. Trust boundaries

1. **Friend PC ↔ broker (HTTPS).** Every friend request is signed with the device key.
2. **Owner PC ↔ broker (HTTPS).** Every owner request is signed with the owner key.
3. **Friend node ↔ host node (tailnet).** WireGuard gives confidentiality and node identity
   **[verified T4-25, T4-27]**. Tailscale policy limits friends to one port on hosts (§8).
4. **Host bridge ↔ Agent (named pipe, same machine).** The bridge asks; the Agent decides.
5. **Host bridge ↔ game server (loopback only).** The bridge dials only the endpoint the Agent
   returned, and only a loopback address.
6. **UI ↔ transport (named pipe, same user).** No operation accepts a destination. The UI sends
   nothing until it has verified which process serves the pipe (§11).

The broker is trusted to say who is approved. It is not trusted with Tailscale credentials or
game traffic. If the broker is compromised, an attacker can issue tickets. Those tickets still
fail against a host unless the attacker also controls a friend node enrolled in that owner's
tailnet, because tickets bind the Tailscale node id checked through WhoIs (§10).

---

## 5. Identity model

All application keys are **ECDSA P-256**. Signatures are ES256 in IEEE P1363 form (r‖s, 64
bytes), which Web Crypto, .NET and Go all handle **[verified T6-03, T6-13, T6-14, T6-23, T6-30]**.
Public keys travel as base64url **SubjectPublicKeyInfo DER**. .NET reads that with
`ImportSubjectPublicKeyInfo` and Go with `x509.ParsePKIXPublicKey` **[verified T6-16, T6-26]**.

| Identity | Created by | Private key storage | Id |
|---|---|---|---|
| Owner | Agent, once per installation | DPAPI (the Agent's account), `ProgramData\1SalemServerManager\connect\`, protected DACL granting only the Agent's account (SYSTEM) | `own_` + base32(SHA-256(SPKI))[0..26] |
| Device (friend) | 1Salem Connect, once per Windows user | DPAPI CurrentUser, `%LOCALAPPDATA%\1Salem Connect\identity\` | `dev_` + base32(SHA-256(SPKI))[0..26] |
| Session | 1Salem Connect, per session | Memory only; handed to the transport over the pipe, never written | — |
| Ticket signer | Broker operator | Worker secret `TICKET_SIGNING_KEY` (PKCS#8) | `kid` |

"base32" here is RFC 4648 base32 in **lowercase** (`a-z2-7`), unpadded. Every implementation must
produce and accept only that form: the broker rejects any other case, and the ids end up in
lowercase tailnet hostnames. The end-to-end proof caught an early .NET build emitting uppercase.

DPAPI CurrentUser data can be decrypted only by the same user, normally on the same machine
**[verified T6-20, T6-21]**. A lost key means re-pairing, not recovery.

**Identity folder trust** (`ConnectIdentityStore`, both identities). DPAPI alone does not stop a
planted key: a machine-scope DPAPI blob made by any local account also opens **[P1-04]**, and folders under
`ProgramData` let standard users create entries. So loading and creating both require:

- the identity folder to be owned by the current account (for a process running as SYSTEM, SYSTEM
  or `BUILTIN\Administrators` also count as owner) and to carry a protected, non-inheriting DACL
  whose allow entries go only to the current account and SYSTEM;
- `identity.v1.json` to be owned by such an owner, checked on the open handle.

Otherwise the store raises `ConnectIdentityUnavailableException`. It never deletes, replaces or
adopts the file, and it never takes ownership of an existing folder. New folders and files are
created with the current account as explicit owner. A folder left by an elevated process (owned
by Administrators) is refused for a non-SYSTEM process; this fails closed and the folder must be
removed by hand. No released build ever created one.

Ids derive from the public key, so registration is idempotent and an id cannot be claimed
without the key. There are no passwords and no reusable owner secrets. The broker stores public
keys only.

### Signed requests

Headers on every owner/device request:

```
X-1S-Key:   own_… | dev_…
X-1S-Time:  unix seconds
X-1S-Nonce: 16 random bytes, base64url
X-1S-Sig:   base64url ES256 over the canonical string
```

Canonical string, UTF-8, `\n` separated:

```
1SALEM-REQ-V1
{METHOD}
{PATH}            path only, no query string, e.g. /v1/sessions
{X-1S-Time}
{X-1S-Nonce}
{base64url(SHA-256(raw body bytes))}
```

The broker rejects a time more than 300 s away and a nonce seen before for that key (stored until
the time leaves the window). Registration (`POST /v1/owners`, `POST /v1/devices`) carries the SPKI
in the body, and the request is signed by that same key: proof of possession.

Check order for a route signed by a registered key, cheapest first: signed headers and clock; key
lookup; signature; **nonce** (one atomic `INSERT`, the only replay check); **then** the key's rate
budget (§14); then the handler. So a forger writes nothing and spends nobody's budget; a replay,
even many concurrent copies of one captured request, is refused before the key's budget is
charged; and a request refused with 429 by that budget has already used its nonce, so its
captured bytes can never run after the window resets.

Registration: body and `spki` field; signed headers and clock; **the per-network budget**; SPKI ↔
key id match and signature; nonce; the global registration ceiling; insert. The per-network
budget comes before any cryptography on purpose, so junk floods stay cheap. The consequence: a
registration refused by that budget is not used up and could be replayed later, which can only
register the signer's own key. The global ceiling is charged only after the proof of possession
and the nonce, so neither junk nor replays spend it.

---

## 6. Invitation model

- Secret: **32 bytes** from a CSPRNG, base64url (43 characters). Non-sequential, unguessable.
- Stored as `HMAC-SHA256(INVITE_PEPPER, secret)` only. `INVITE_PEPPER` is a Worker secret. A
  database leak does not reveal usable invites.
- Bound to one owner and one ServerId. Default lifetime 24 h, maximum 7 days. **Single use**:
  redemption is one conditional `UPDATE … WHERE state='active' AND expires_at > now`. Revocable
  by the owner.
- Presented as a link `https://connect.1salem.app/i#<secret>` (the fragment never reaches a
  server log), a code, or later a QR code.
- The invite carries nothing but the secret: no OAuth secret, auth key, Agent credential, path or
  IP.
- Redeeming an invite gives **no** access. It creates a *pending* membership that the owner must
  approve. Possession of an invite is never administrator authorization.
- Redemption failures are all the same generic "invite not valid" (§14).

The secret travels in the request **body**, not the path (`POST /v1/invites/redeem`), so it
cannot land in URL logs. This is a deliberate change from the conceptual
`POST /invites/{secret}/redeem`.

---

## 7. Provisioning model (O1)

```
Owner approves friend (Server Manager)
  → Agent: POST /v1/memberships/{id}/approve                        (owner-signed)
  → Agent mints auth key in the OWNER's tailnet (owner's OAuth client, token lifetime 1 h):
        POST /api/v2/tailnet/-/keys
        { capabilities: { devices: { create: {
              reusable: false, ephemeral: false, preauthorized: true,
              tags: ["tag:1salem-client"] } } },
          expirySeconds: 86400, description: "1salem connect <membership prefix>" }
  → Agent encrypts {authKey, keyId} to the friend's device public key (ECDH-ES P-256 +
    HKDF-SHA256 + AES-256-GCM, AAD = membershipId|deviceId|ownerId)
  → Agent: POST /v1/memberships/{id}/enrollment {ciphertext}       (owner-signed)
Friend app
  → GET /v1/memberships/{id}/enrollment                             (device-signed, deleted on read)
  → decrypts with the device key, hands authKey to the transport ONCE over the pipe
  → transport starts tsnet with a fresh Dir for this owner's tailnet, reports Self.ID
  → POST /v1/memberships/{id}/node {nodeId}                         (device-signed, once)
Agent                                                               (Phase 2, §21 D-1)
  → GET /api/v2/device/{nodeId}: confirm tag:1salem-client and created-after-key; then
    DELETE /api/v2/tailnet/-/keys/{keyId} if still unused (it is auto-revoked once used)
```

**Phase 1 status of this flow.** The broker, enrollment crypto, relay, pipe `enroll` and node
binding exist and are exercised end to end in fake mode. The Agent-side steps that call the
Tailscale API (mint, confirm, key cleanup) exist only as a mock-tested provisioner with no
callers, and the broker has no confirmation step yet: the binding is the device's report, with
the Phase 1 safeguards listed in §21, D-1.

Facts this relies on:

- Keys from an OAuth client must be tagged with the client's tags or tags they own **[verified
  T2-03, T3-10]**. One-off keys are revoked automatically after first use **[verified T2-05]**.
- **Expiry cannot be short.** The documented range is 1–90 days, and the API states no minimum
  in seconds **[T2-06, T3-13; open question]**. We request 1 day and delete the key as soon as
  the node is confirmed, or when the enrollment blob expires unused (15 minutes).
- Nothing in the API maps an auth key to the device it created **[verified T2-19]**. The friend
  reports `Status.Self.ID`, and (Phase 2) the Agent checks it through the API before the
  binding is confirmed.
- A tsnet node ignores `AuthKey` once state exists **[verified T1-10, T2-20]**. The friend
  transport therefore keeps **one state directory per owner tailnet** and never reuses a
  directory for a different tailnet.
- Friend nodes are **persistent, not ephemeral**, so a friend is not a new device every launch.
  Tagged devices do not expire on their own **[verified T2-13]**, so revocation always deletes
  the device (§12).

The host node is enrolled the same way, tagged `tag:1salem-host`, with the key handed to the
host transport over its pipe.

---

## 8. Tailscale policy (owner's tailnet)

Two layers must both allow a connection: the tailnet policy and 1Salem application
authorization. The policy lets friend nodes reach **one TCP port on host nodes and nothing
else**. Grants are deny-by-default and additive **[verified T4-05]**. Rules are enforced by the
destination **[verified T4-07]**. Reply traffic needs no reverse rule **[T4-08]**. Friends see
only peers they can reach **[verified T4-17]**.

```hujson
// Example only — not applied to any real tailnet in Phase 1.
{
  "tagOwners": {
    "tag:1salem-host":   ["autogroup:admin"],
    "tag:1salem-client": ["tag:1salem-host"],
  },
  "grants": [
    // Friends may open TCP connections to the 1Salem host bridge port, and to nothing else.
    { "src": ["tag:1salem-client"], "dst": ["tag:1salem-host"], "ip": ["tcp:7780"] },
    // The owner's own devices keep whatever access the owner already grants them. No rule
    // gives tag:1salem-client access to autogroup:member devices or to other clients.
  ],
  "tests": [
    { "src": "tag:1salem-client", "accept": ["tag:1salem-host:7780"],
      "deny":   ["tag:1salem-host:25565", "tag:1salem-host:8211", "tag:1salem-host:5251",
                 "tag:1salem-host:3389",  "tag:1salem-host:445"] },
  ],
}
```

- A new tailnet starts **allow-all**, and a policy file with no `acls` section also means
  allow-all **[verified T4-06]**. Server Manager must detect this and refuse to enable Connect
  until the owner replaces it. We cannot enforce least privilege while a `*`→`*` rule exists.
- `tests` make Tailscale reject a later edit that would widen friend access **[verified T4-15]**.
- Friends reach only the bridge port, never 25565, 8211, the Agent API (5251), RDP or SMB. tsnet
  drops flows to ports it is not listening on and never forwards them to the host's localhost
  **[verified T4-20]**. So even an over-broad owner rule exposes only the bridge.
- Tailnet Lock blocks OAuth-minted keys until a signing node signs them **[verified T4-30;
  T4-31]**. Connect will report Tailnet Lock as unsupported in the first release.

---

## 9. Session tickets

A compact JWS with ES256 (RFC 7515/7518). Verifiers pin the algorithm per key and check `typ`,
`aud` and `exp` **[verified T6-33]**.

Header: `{"alg":"ES256","typ":"1salem-ticket+jwt","kid":"<kid>"}`

Payload:

| Claim | Meaning |
|---|---|
| `iss` | `"1salem-connect-broker"` |
| `aud` | owner id (`own_…`). A host bridge rejects tickets for any other owner |
| `jti` | 128-bit random id, base64url |
| `sub` | device id (`dev_…`) |
| `mid` | membership id |
| `sid` | ServerId, lowercase GUID |
| `proto` | `"tcp"` (Phase 1). `"udp"` is reserved |
| `nid` | Tailscale StableNodeID of the friend node (fake id in fake mode) |
| `skp` | base64url SPKI of the session public key |
| `hb` | host bridge address `ip:port` on the tailnet |
| `av` | membership authorization version |
| `iat`, `nbf`, `exp` | seconds. `exp - iat` ≤ 900; issued with 600 |

Host verification (Agent, C#), in order: exact three-part shape; header `alg` = ES256; `typ`
exact; `kid` pinned; P1363 signature of exactly 64 bytes; `iss`; `aud` = this owner; time window
with 30 s skew; `exp - iat` ≤ 900; `proto` supported; `sid` is a Connect-enabled server of that
protocol; `nid` equals the WhoIs StableNodeID of the connecting peer; `jti`, `sub` and `mid` not
revoked; `av` not below the revocation floor for `mid`; connection proof valid (§10).

Friend-side check (Go transport): signature against pinned broker keys, `typ`, `exp`, and that
`hb` is a Tailscale address (100.64.0.0/10 or fd7a:115c:a1e0::/48), or a loopback address in fake
mode. This matters because tsnet `Dial` falls back to the host network for non-tailnet
destinations **[verified T1-17]**. The transport never dials anything else.

That check alone is not enough. tsnet decides netstack-or-host-network again **at dial time**
(`tsdial.dialOneUser`, from `UseNetstackForIP` **[P1-01]**): if the peer leaves the
netmap between the WhoIs check and the dial (host device deleted or re-keyed, policy change), the
dial silently goes out on the host network to whatever answers that 100.x address. So after every
tsnet dial the transport requires the connection's **local** IP to be one of the node's own
Tailscale addresses (`Server.TailscaleIPs()`); a netstack connection is bound to them and a
host-network one is not. Otherwise the connection is closed before a single byte (preamble,
ticket, proof or game data) is written, and the dial fails with the destination error. Being
inside 100.64.0.0/10 is not accepted as proof, because a system Tailscale adapter also has such an
address. What remains is the TCP handshake of that one refused dial.

---

## 10. Connection preamble and replay protection

When a local TCP connection arrives, the friend transport dials `hb` and sends:

```
"1SC" 0x01 | uint16 big-endian length N (≤ 4096) | N bytes UTF-8 JSON
{"t":"<ticket>","n":"<16-byte nonce b64url>","ts":<unix seconds>,"p":"<b64url ES256 by the session key>"}
```

Proof signing input: `1SALEM-CONN-V1\n{jti}\n{n}\n{ts}\n{sid}`.

The host bridge reads the preamble under a 10 s deadline and asks the Agent. The Agent replies
with one status byte to the friend: `0x00` accepted (raw stream follows) or `0x01` refused
(generic), then close.

Replay handling does not rely on expiry alone:

1. **Node binding.** `nid` must equal the WhoIs StableNodeID of the peer **[verified T1-18,
   T1-19]**. A stolen ticket is useless from any other node.
2. **Proof of possession.** Each connection is signed by the session private key, which exists
   only in the friend transport's memory. A captured ticket alone cannot open a connection.
3. **Nonce cache.** `(jti, n)` is remembered until the ticket expires (+30 s). An exact replay of
   a captured preamble is rejected. The cache is bounded twice. **Per ticket:** one `jti` may open
   at most **64** accepted connections over its lifetime (`ReplayCache.DefaultPerTicketLimit`;
   a Minecraft session needs a handful, including reconnects), and beyond that only that ticket
   is refused (`TicketConnectionLimitReached`). So one approved friend minting fresh nonces
   cannot fill the cache and lock every other friend out. **Globally:** 100,000 pairs, a last
   resort that refuses rather than evicts, since eviction would re-enable replay. Revoking a
   device, membership or ticket also drops that principal's entries at once (safe, because
   revoked tickets are refused before the replay check), so a revoked abuser releases what they
   held immediately instead of when their tickets expire.
4. **Clock window.** `ts` within ±60 s.
5. **Revocation.** `jti`, `sub` and `mid` revocations and the `av` floor apply immediately on the
   host. Revoking a friend also closes their live bridged connections.

Tickets authorize **starting** connections. On the host, an established Minecraft stream
outlives the ticket, and revocation ends it explicitly. While a session is open, the UI refreshes
its ticket before expiry; if it cannot, the friend app itself ends the session (§16).

**Destination (Agent catalog).** The Agent never takes a destination from the friend. A ticket's
`sid` maps, through the catalog, to `127.0.0.1:<that server's registered game port>` and nothing
else. A Minecraft server is bridgeable only when all of these hold; otherwise it is absent from
the catalog and its tickets are denied as `UnknownServer`:

- Connect is enabled for it;
- its port is 1024–65535 (`ConnectServerCatalog.LowestBridgeablePort`);
- its port is not a sensitive local service: 3389 (RDP), 5985/5986 (WinRM), 5357 (WSD), 8212
  (Palworld REST default), 25575 (RCON default);
- its port is not an Agent API port (5251 loopback, 5252 LAN, and any ports the Agent is
  configured with, passed through the three-argument constructor);
- no other registered server of any game uses that port.

Palworld servers are never bridgeable in Phase 1. A REST or RCON port moved away from its default
is not known to the catalog (its definition carries only the game port); the owner's own tailnet
policy still exposes only the bridge port (§8).

---

## 11. Transport and IPC contracts

### Named pipes

Framing: one UTF-8 JSON object per line, at most 64 KiB. Requests carry `{"id":n,"op":"…"}`.
Responses carry `{"id":n,"ok":true,…}` or `{"id":n,"ok":false,"error":"<code>"}`.

Pipe security: a protected DACL granting only the owning account (and SYSTEM), with an explicit
**deny for NETWORK (NU)**, first-instance creation to stop squatting, and remote clients rejected
**[verified T8-17 … T8-27]**. go-winio's `ListenPipe` creates the first instance with
`FILE_CREATE` and rejects remote clients **[T8-24]**. In .NET we set `FirstPipeInstance` and an
explicit `PipeSecurity`, because `NamedPipeServerStream` never sets `PIPE_REJECT_REMOTE_CLIENTS`
**[verified T8-27]**.

First-instance creation only lets the real server *detect* that someone else already owns the
name; it fails to start rather than being impersonated **[T8-18, as corrected]**. Clients
therefore also check who they are talking to: they verify that the pipe's owner is the expected
account (the same user for the friend pipe; the Agent's account for the host pipe) before sending
anything sensitive, and an auth key or session key is never sent to an unverified pipe. The owner
check cannot tell a sandboxed process of the same user from the friend transport, so the friend
app also checks which process serves its pipe (below, "Friend app and its transport").

**Friend UI ↔ friend transport**: `\\.\pipe\1Salem.Connect.Transport.<user SID>`

| op | request | response |
|---|---|---|
| `hello` | `{v:1}` | `{v:1, mode:"fake"\|"tsnet", version}` |
| `status` | — | `{nodes:[…], sessions:[{sessionId, sid, local, state}]}` |
| `enroll` | `{node, authKey, hostname}` | `{nodeId}` — key used once, then wiped |
| `open` | `{node, ticket, sessionKey, preferredPort}` | `{sessionId, local:"127.0.0.1:18211"}` |
| `refresh` | `{sessionId, ticket, sessionKey}` | `{}` |
| `close` | `{sessionId}` | `{}` |
| `diag` | — | technical detail for Diagnostics, with only the newest redacted log lines that fit in 32 KiB (so the reply always fits one pipe line) |

**No operation takes a destination.** The only address the transport ever dials is `hb` from a
ticket whose signature it verified. The transport logs one line per local connection outcome
(`could not reach the host bridge`, `connection not accepted`, `connection accepted` right after
a successful handshake, `connection ended`, and `ticket expired; connection refused until
refresh`); the app reads session state from the newest of the first four and ignores the last
(its own expiry check ends the session, §16).

**Host transport ↔ Agent**: `\\.\pipe\1Salem.Connect.HostAuthz.v1` (the Agent is the server)

| op | request | response |
|---|---|---|
| `hello` | `{v:1}` | `{v:1}` |
| `authorize` | `{preamble:{t,n,ts,p}, peer:{nodeId, addr}}` | `{decision:"allow", endpoint:"127.0.0.1:25565", connId}` or `{decision:"deny"}` |
| `closed` | `{connId, bytesIn, bytesOut}` | `{}` |
| `subscribe` | `{}` | `{}` then, on the same pipe connection, a line per event: `{"event":"close","connIds":["…"]}` |

The host transport re-checks that an allowed endpoint is a loopback address before dialing it.
It keeps one `subscribe` connection open. The Agent uses it to end live connections by id when a
friend is revoked. The transport closes the listed connections immediately.

Agent side of this pipe (`ConnectHostAuthorizationPipeServer`):

- `hello` must come first on every connection; anything else before it gets `hello_required`.
- **No subscriber, no allow.** While no `subscribe` connection is open, every `authorize` is a
  bare deny, because the Agent could not end that connection on revocation. (The host transport
  applies the same rule on its side and closes all live connections when its subscription drops.)
- **Decision budget.** A decision not ready within **6 s** of the Agent reading the request
  (`DecisionBudget`, below the host transport's 8 s authorize timeout) is answered `deny`, and
  nothing is tracked or used up (no nonce, no ticket slot).
- **Live-connection table.** Every allow is tracked with its device, membership, ticket, ServerId
  and local port (capacity 10,000). Closes are pushed as `{"event":"close","connIds":[…]}` and
  repeated up to **5** times, every 2 s, until the transport reports `closed{}`; an id still
  unconfirmed after its last repeat is forgotten. When a subscription ends, every tracked
  connection is marked closing (the transport ends them all when its subscription drops) and the
  next subscriber receives those ids straight after its ack. So a crashed or restarted host
  transport cannot leave stale entries that would eventually fill the table and deny everyone.
- **Revocation** (`RevokeDeviceAsync`, `RevokeMembershipAsync`, `RevokeTicketAsync`) refuses the
  principal from then on, drops its replay-cache entries (§10) and closes its live connections.
- **Per-server closes.** `DisableConnectAsync(serverId)` switches Connect off for a server and
  closes that server's live connections under one lock; `CloseServerConnectionsAsync(serverId)`
  closes them without changing the setting. Every authorize refresh, and a periodic check every
  10 s while connections are live (`ServerCheckInterval`), also closes live connections whose
  server was deleted, switched off, dropped from the catalog, or re-registered on a different
  port. A store failure during the periodic check keeps connections open rather than guessing.
  (Turning Connect off through `ConnectEnabledServers.Disable` directly still takes effect at the
  next check; the Agent wiring should use `DisableConnectAsync`.)

Host transport side of this pipe and its bridge:

- **Late allow.** An `authorize` answer that arrives after the host transport's 8 s authorize
  timeout is not dropped: if it is an `allow` with a valid `connId`, the transport sends
  `closed{connId, bytesIn:0, bytesOut:0}` for it (asynchronously, 5 s timeout, logged), so the
  Agent never keeps an entry for a connection the bridge already refused. Late denies and anything
  else are ignored. This is the general rule "an allow the transport could not use is reported
  closed", applied to late replies.
- **Per-source limits** on the bridge (source = the peer's remote IP: its tailnet IP in tsnet mode,
  loopback in fake mode, where all fake friends share one budget). Checked **before** a slot of the
  global limit (256) is taken: at most **16** connections per source waiting for the Agent's
  decision and at most **32** live per source. A connection from a source at either cap is closed
  at once, with no status byte and no call to the Agent; the live cap is checked again when the
  Agent allows (over it, the friend gets `0x01` and the Agent a `closed{…,0,0}`). So one tailnet
  peer (another friend, or a revoked friend whose device deletion is pending) cannot hold every
  slot with idle connections. The pending cap of 16 leaves room for a Minecraft server-list
  refresh, which pings all of an owner's servers at once through the friend's one node for that
  owner. Refusal log lines are limited to 10 per minute; the rest are counted and summarised in
  the next logged line.

### Friend local endpoint

- Binds **127.0.0.1 only** (`SO_EXCLUSIVEADDRUSE`). Preferred port **18211**. If that is taken,
  try the next ports in 18211–18299 (wrapping) and report the chosen one. A port with any listener
  on any IPv4 or IPv6 address (read from the TCP table, never probed) is skipped. Never kill or
  take over the process holding a port. Never bind 0.0.0.0.
- At most 64 connections per session. From 30 s after its ticket's expiry (the verifiers' clock
  skew), the listener refuses new local connections until the UI refreshes the ticket. The
  transport does not cut established streams at expiry by itself; when the app cannot renew the
  ticket, it closes the whole session, listener and live streams included (§16).

### Friend app and its transport (trust and lifetime)

`1Salem.Connect.exe` sends auth keys and session keys down the friend pipe, so it must know which
process serves it. It must also not leave that process, or a session in it, running after the
friend has left.

**Which process may serve the pipe** (`TransportServerVerifier`). Checked on every new pipe
connection, after the owner check and before one byte is written; a failure is `untrusted`, and
nothing is sent.

- **A transport this app started.** Its process id is pinned the moment it starts, before its first
  `hello`, and from then on only that process may serve the pipe. The app holds the process handle
  for as long as the pin lasts, so the id cannot pass to another process, and it clears the pin
  before it releases the handle.
- **A running transport it reuses** (started by another copy of the app, or left by an earlier
  run). Accepted only while nothing is pinned, only if its image is the configured
  `1Salem.Connect.Transport.exe` (full path), and only if its integrity level is not below the
  app's own. A sandboxed process of the same user owns its pipes as this user too, and it can
  start the real executable, but only at its own lower level. The process is opened once
  (`PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`), both facts are read through that handle,
  and the handle is **kept** until the process is known to have exited. So its id cannot pass to
  another process meanwhile, and the app knows exactly when that transport, and every listener in
  it, is gone. Anything that cannot be read is refused.
- A transport that answers `hello` in the other mode (fake where tsnet is configured, or the
  reverse) is refused (`mode_mismatch`), not reused.

**Starting and restarting** (`TransportProcess.EnsureRunningAsync`, one caller at a time):

1. A transport this app started that has since exited is let go first. Its pin is cleared while
   the handle still reserves the id, so a genuine transport that another copy starts afterwards is
   verified as a reused one instead of being refused for the rest of the run.
2. `hello`. An answer means a verified transport serves the pipe. Only `unavailable` leads to a
   start: no pipe answered the connect within 2 s, or a connection closed without an answer. (A
   full pipe refuses a client in exactly that second way, which is the F7 limitation in §21 D-3.)
   `no_answer` (there, but busy) and `untrusted` fail the call instead: starting another transport
   would kill a busy one with its sessions, or could not take a pipe that something else holds.
3. Start. The broker's ticket keyset (public keys only) is fetched and written to
   `%LOCALAPPDATA%\1Salem Connect\transport\ticket-keys.json`; a folder that cannot be written is
   `data_unwritable`. The command line carries only the mode, the pipe name, the keyset path and
   the state directory (or the fake node id), and every `TS_*`/`TSNET_*` variable is removed from
   the environment. The new process id is pinned, the process is put in the kill-on-close job, and
   `hello` is retried every 250 ms for up to 20 s. A transport that exits, never serves or answers
   wrongly in that time is killed, not left half-started.

A session whose transport restarted is no longer listed by `status`; one whose transport died gets
no answer from `status` at all. Either way the monitor ends it as "Disconnected" ("The connection
stopped. Connect again to continue."): its listener went with the old process.

**Kill-on-close job** (`KillOnCloseJob`). Right after a transport this app starts is created, it is
put in a job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. Only the app holds the job handle,
and the handle is not inheritable (the transport is started with inherited handles for its
redirected output, and an inherited copy would keep the job alive). Windows closes the handle
when the app ends for any reason, a crash or a kill included, and that ends the transport with its
sessions and node. If Windows refuses the assignment, the refusal is recorded in Diagnostics and
the transport still runs; it is then ended only by a clean exit. A reused transport is never put
in this job, because it belongs to another copy of the app.

**Pipe calls and their deadlines** (`PipeTransportClient`). The transport answers requests on one
connection in order, so calls share one connection and queue for it.

- Waiting in that queue does not count against a call's time; only the caller's own cancellation
  applies there. Each call ahead is bounded by its own deadline, and a call that timed out before
  it was even sent would report a healthy transport as gone.
- Once a call holds the connection, one deadline covers connecting (at most 2 s), writing and
  reading: 30 s, or 2 minutes for `enroll`, which in tsnet mode brings a node up before answering.
- `enroll` gets a fresh, freshly verified connection for each call, so `hello`, `status` and
  `close` never wait behind it, and the auth key only goes down a connection checked moments
  before.
- A deadline that expires on a verified connection is `no_answer` (the transport is there but did
  not answer), not `unavailable`. The connection is dropped, because its framing is now unknown,
  and the next call reconnects. The session monitor treats `no_answer` as "look again on the next
  step", and a refresh that got it is retried with a new ticket.
- Each request line is wiped once written, since it may carry an auth key or a session key.

**Broker calls** (`BrokerClient`). One 20 s deadline covers each whole exchange: signing, sending,
the headers **and reading the body**. The body is read as a stream, so `HttpClient.Timeout` would
stop at the headers, and a broker that stalls mid-body could otherwise hold the caller and any gate
it holds. The body is read up to 64 KiB. A longer body (whatever the status), a success body that
cannot be parsed, or an `expiresAt` outside what a date can hold is `InvalidResponse`; an error
status is classified by its code. Redirects are never followed, and cookies are off.

**Ending a session honestly** (`SessionService.CloseAsync`, `ConnectionViewModel`). A session
counts as closed only when the transport confirms it: `close` succeeded, `close` answered
`no_session`, or a following `status` no longer lists the session. When the transport cannot be
asked (`unavailable` or `untrusted`), the session counts as closed only if every transport process
that served this app has exited, because a listener cannot outlive its process. That means the
one it started (including one it let go after it exited) and every reused one it holds. Otherwise
the page keeps the local address on screen and Disconnect available, and it says "The connection
couldn't be closed yet, so the address may still work…"; the monitor tries again every 5 s. A
session ending because of revocation or expiry keeps that outcome while it waits, even if the
friend presses Disconnect meanwhile. The page never reports a session it opened as over while its
address may still reach the owner's server. (An `open` whose answer never arrived can leave a
listener the app never learned about; §21 D-3.)

**Connection state from the transport's log.** `status` reports a session only as listening or
expired. The outcome of each game connection appears only in the transport's redacted log lines
(§11, friend pipe table). Every 5 s the monitor reads the newest such line for its session:

| Newest line | Shown |
|---|---|
| `could not reach the host bridge` | "Server offline" |
| `connection not accepted` | membership looked up: "Access revoked" if revoked, else "Server offline" |
| `connection accepted` (logged right after the handshake, while the game runs) or `connection ended` | membership looked up: "Access revoked" if revoked (revoking a friend ends their live connections from the owner's side), else "Connected" |

So a successful connection after a failed attempt clears a stale "Server offline" at the next
step, instead of leaving it on screen for the whole game. Wording the app does not recognise
changes nothing. (Verified in fake mode. In tsnet mode the `diag` window of the newest 32 KiB is
shared with tsnet's own log, so a burst of tsnet output can push the line out before the next
step; the state is then left unchanged, not guessed.)

**Exit** (`MainViewModel.Shutdown`, from `App.OnExit`). In order:

1. Polling and every session monitor stop.
2. Every session this run opened and has not seen closed is closed through the transport, whether
   the app started that transport or reused it. This is best effort: it runs on the thread pool,
   bounded to 3 s by a deadline and by a capped wait. `no_session` counts as closed, and a
   session that could not be closed in time is only recorded in Diagnostics.
3. The transport this app started is killed.

A reused transport keeps running, without the sessions this run managed to close.

Limits:

- A Connect still in progress at the moment of exit is not in the list yet.
- If the app crashes or is killed while it is using a reused transport, its sessions stay open in
  that transport (the process is not this app's to end) until that transport stops. Their
  listeners refuse new connections once the ticket expires, but a live game stream continues until
  it ends or the owner revokes the friend (§21, D-3).

**Enrollment across cancellation and restarts** (`EnrollmentCoordinator`, `ConsumedEnrollments`).
Enrollments run one at a time. If the owner's node already exists (a second server of the same
owner, or a bind that failed after enrolling), it is bound directly and no auth key is used.

Otherwise the one-time blob is taken. The broker deletes it on read, so the membership is marked
used **before** anything else can end the attempt. The mark is saved in
`%LOCALAPPDATA%\1Salem Connect\consumed-enrollments.json`. That file holds membership ids only,
never anything from the blob; it is written to a temporary file that is then moved into place; and
a membership's mark is removed once its node is bound. A key id is handed to the transport at most
once per run.

- **A definite refusal** (`bad_auth_key`, `enroll_failed`, a blob that cannot be opened) puts
  "Setting up this server failed. Ask the server owner to approve this PC again." under the
  card's "Enrollment pending" status.
- **An ambiguous end** is settled on the next attempt. That covers a transport that went away, did
  not answer or is untrusted, a friend who left the page, and the app exiting. A node that came up
  anyway is bound. Otherwise the blob is marked used, so the card shows that same failure instead
  of a bare "Enrollment pending" forever, and it stays so after a restart.

Limits:

- A blob the broker deleted whose answer never reached the app is not marked, so its card shows a
  bare "Enrollment pending".
- A file that cannot be read on first use (for example briefly locked) is recorded, and the run
  continues from an empty list. Its next save can drop earlier marks, so those memberships show a
  bare "Enrollment pending" instead of the failure.

### Transport abstraction

`Transport { Dial(ctx, addr) ; Listen(addr) ; PeerNodeID(conn) ; Close() }` with two
implementations:

- **tsnet**: real node, userspace netstack. Never sets `Server.Tun`, so there is no adapter, no
  route change and no DNS change **[verified T1-04, T4-21]**. `PeerNodeID` uses WhoIs.
- **fake**: loopback TCP standing in for the tailnet. The peer node id is self-asserted in a
  `FAKENODE <id>\n` line and is **not a security boundary**. It exists only for tests and the
  disposable proof, and is labelled as fake everywhere.

Sidecars start with a scrubbed environment: `TS_AUTHKEY`, `TS_AUTH_KEY`, `TS_CLIENT_SECRET`,
`TS_CONTROL_URL`, `TSNET_FORCE_LOGIN`, `TS_CLIENT_ID`, `TS_ID_TOKEN` and `TS_AUDIENCE` are removed
**[verified T1-11, T1-12]** (the friend app removes every `TS_*`/`TSNET_*` name). Auth keys come
over the pipe, never on the command line. `RunWebClient` stays off and `Loopback()` is never
used **[T1-23]**. Logging goes through a redacting logger.

### tsnet state directories (both sidecars)

A node's identity is its tsnet state directory, `<state root>\nodes\<node>\` (one per owner
tailnet on the friend; the root is `%LOCALAPPDATA%\1Salem Connect\transport\state` for the friend
and an Agent-chosen directory for the host). Anyone who can read it can clone the node; anyone who can
plant a file in it before tsnet starts can have the node's keys written where they can read them
(`ReplaceFileW` keeps the replaced file's DACL **[P1-03]**), and tsnet writes state as soon as it
starts, before enrollment succeeds **[P1-02]**. So:

- A missing root is created (its parent must exist) with the protected DACL
  `O:<process SID> D:P(A;OICI;FA;;;<process SID>)(A;OICI;FA;;;SY)`. An existing root, `nodes` and
  node directory must each be a plain directory (not a junction, symbolic link or file), checked
  through a handle opened without following reparse points, and **owned by the process account**
  (for a process running as SYSTEM, SYSTEM or `BUILTIN\Administrators` also count). An existing
  root keeps its own DACL; `nodes` and the node directory get the protected DACL re-applied through
  the checked handle. Ownership is never taken. Violations fail `Enroll`, `Get` and host start
  (reported on the pipe as `internal`; the path and owner SID go to the sidecar's local log).
- `Enroll`, atomically under the node lock: busy check (this process); directory checks; marker
  (`1salem-node.json`: present means `already_enrolled`); **empty the node directory** (every entry
  removed; a junction or link inside is removed itself, never followed); reserve the node; start
  tsnet. A marker-less directory with tsnet files is what a failed or killed first enrollment
  leaves, so emptying it lets the next attempt recover, and tsnet never sees a planted file. If
  anything cannot be removed (for example a file still held open), enrollment fails closed. A
  failed enrollment inside the process removes the node directory at once.
- `status`/`diag` list enrolled nodes from disk only when the root, `nodes` and that node's
  directory pass the same checks; failing directories are skipped and logged, never reported.

**No OAuth minting inside a sidecar.** tsnet compiles in an OAuth hook by default. It treats any
`AuthKey` beginning with `tskey-client-` as an OAuth client secret and uses it to mint new keys on
every start **[verified T1-11, as corrected]**. Both sidecars are therefore built with the
`ts_omit_oauthkey` build tag, and the `enroll` operation rejects any value that is not a
`tskey-auth-` key. A friend machine must never hold anything that can mint keys.

---

## 12. Revocation

**Phase 1 status.** Step 1 below (broker) is built. The host side of step 2 is built as a library,
`ConnectHostAuthorizationPipeServer` (`RevokeDeviceAsync`, `RevokeMembershipAsync`,
`RevokeTicketAsync`: refuse the principal, drop its replay-cache entries, close its live
connections), and is exercised by tests and the disposable proof, but the Agent service does not
host it yet. Nothing consumes the revocation feed, no code outside tests deletes a device (step 3;
§21 D-1), and the owner's Connect card is a disabled preview. The rest of this section is the
contract that the Agent wiring must meet (§21 D-4).

**Revoke friend** (owner, in Server Manager):

1. Broker: membership → `revoked`, `av` + 1, all its session tickets revoked (owner-signed).
2. Agent: adds the device and membership to its local revocation set **immediately** and closes
   the friend's live bridged connections.
3. Agent: `DELETE /api/v2/device/{nodeId}` in the owner's tailnet (`devices:core`) **[verified
   T2-17, T3-09]**. Deleting or revoking the auth key alone would leave the node connected
   **[verified T2-07]**.
4. The UI reports "access revoked" only when all three steps have succeeded. Otherwise it says
   exactly which step is pending. We never claim full revocation because a database row changed.

Before step 3 the Agent must read that device from the Tailscale API and delete it **only if it
carries `tag:1salem-client` and is not one of the owner's own nodes**. A membership's `nodeId`
is reported by the friend's device (§21, D-1); without this check a friend who bound the id of
the owner's machine would have that machine removed from the owner's tailnet on revocation.

Tickets are short-lived (10 min). The Agent also pulls the revocation feed
(`GET /v1/owners/me/revocations?after=<seq>`, §13) so that revocations made elsewhere take effect.
The feed pages on a commit-ordered sequence, never on time: an event that commits after a poll
always has a higher `seq` than anything that poll could return, so it cannot be skipped however
its timestamp compares. (The first design used a wall-clock `since=` cursor, which silently lost a
revocation whose request began in the second before a poll but committed after it.) The host
keeps `cursor` and passes it back as `after`; a cursor that is not one of the owner's own events
(for example from another broker, or kept across a database restore once the operator's restore
step has run, §13) gets 409 `cursor_ahead` and the host restarts from `after=0`. Events are
idempotent.

---

## 13. Broker (control plane only)

Cloudflare Worker + D1. In Phase 1 it runs **locally only** (`wrangler dev --local`, Miniflare
on workerd, D1 in `.wrangler/state`) **[verified T5-19, T5-20]**.

Responsibilities: owner/device registration, invites, redemption, approval, enrollment relay
(ciphertext only, one-time pickup), session tickets, revocation. It never receives Minecraft or
Palworld packets, never relays TCP or UDP, and holds no Tailscale credential (O1).

Secrets (`wrangler secret put`; locally a git-ignored `.dev.vars`) **[verified T5-10, T5-11,
T5-12]**:

- `TICKET_SIGNING_KEY` — PKCS#8 ECDSA P-256, imported non-extractable
- `INVITE_PEPPER` — 32 random bytes

Nothing sensitive goes in `vars` or `wrangler.jsonc`.

### API (v1)

| Method & path | Auth | Purpose |
|---|---|---|
| `GET /v1/keys` | none | Published ticket keys `{keys:[{kid, alg, spki}]}` |
| `POST /v1/owners` | self-signed | Register owner key → `{ownerId}` |
| `POST /v1/devices` | self-signed | Register device key → `{deviceId}` |
| `PUT /v1/servers/{serverId}` | owner | Register/update a server `{label, protocol, hostBridge}` |
| `POST /v1/invites` | owner | `{serverId, ttlSeconds}` → `{inviteId, secret, expiresAt}` |
| `POST /v1/invites/{inviteId}/revoke` | owner | Revoke an unused invite |
| `POST /v1/invites/redeem` | device | `{secret}` → `{membershipId, state:"pending", serverLabel}` |
| `GET /v1/owners/me/memberships` | owner | Pending/approved friends |
| `POST /v1/memberships/{id}/approve` | owner | Approve |
| `POST /v1/memberships/{id}/reject` | owner | Reject |
| `POST /v1/memberships/{id}/enrollment` | owner | Store ciphertext for the friend |
| `GET /v1/memberships/{id}/enrollment` | device | Fetch and delete ciphertext |
| `POST /v1/memberships/{id}/node` | device | Bind the friend's node id (once; 409 `node_in_use` if a *different* device holds it on a live membership of the same owner) |
| `GET /v1/devices/me/memberships` | device | The friend's servers and their state |
| `POST /v1/sessions` | device | `{membershipId, sessionSpki}` → `{ticket, expiresAt, hostBridge}` |
| `POST /v1/sessions/{jti}/revoke` | owner | Revoke one ticket |
| `POST /v1/memberships/{id}/revoke` | owner | Revoke a friend's access to one server |
| `POST /v1/devices/{deviceId}/revoke` | owner | Revoke a device on all of this owner's servers |
| `GET /v1/owners/me/revocations?after=<seq>` | owner | Revocations for host enforcement (§12) |

Approval is per **(device, server)**, so `…/approve` lives on the membership. That is a
deliberate change from the conceptual `POST /devices/{id}/approve`.

Revocation feed: `?after=` is an exclusive `seq` cursor, default 0, `^[0-9]{1,16}$` and at most
2^53−1 (anything else is 400). Response
`{revocations: [{seq, kind, jti?, membershipId?, deviceId?, serverId?, av?, at}], cursor, more}`:
at most 1000 events with `seq > after`, ascending; `cursor` is the last `seq` returned, or `after`
itself for an empty page; `more: true` means the next page is already waiting. `seq` is the
table's `AUTOINCREMENT` key, shared by all owners (so it has gaps); a D1 database runs one query
at a time and, without the Sessions API, every query runs on the primary **[verified T5-07,
T5-09]**, so `seq` order is commit order. `at` is informational. A non-zero `after` must be the
`seq` of one of the caller's own events (every cursor the broker hands out is; events are never
deleted), otherwise 409 `cursor_ahead`. The check is folded into the page query (`seq >= after`,
the cursor's own row dropped). Comparing with the table's highest `seq` would not do: other
owners' events can pass a stale cursor.

**Database restore.** A restore (for example D1 Time Travel) rolls the `seq` counter back with
the rows, so new events would reuse `seq` values issued before it, and a host whose kept cursor
equals one of them would skip events silently. The operator must therefore, right after any
restore and before reopening the broker, run
`UPDATE sqlite_sequence SET seq = seq + 1000000000 WHERE name = 'revocations'`; every kept
cursor then gets `cursor_ahead` and its host replays from `after=0`. This is in the broker
README's pre-deployment requirements and pinned by a test that simulates a restore with and
without the step (local D1 simulation).

Node binding: first write wins per membership (a different node later is 409 `already_bound`;
the same node again is 200). The same device may bind one node on all its memberships with an
owner, since there is one node per owner tailnet. A node id that a **different** device holds on
a live (pending or approved) membership of the same owner is refused with 409 `node_in_use`;
other owners' bindings are never consulted. The check runs inside the single conditional
`UPDATE`, so it is race-safe. The node id is still self-reported; see §21, D-1.

### Data (D1)

`owners`, `devices`, `servers`, `invites` (secret HMAC only), `memberships` (state, `av`,
`node_id`), `enrollments` (ciphertext, 15 min), `sessions` (jti, exp, revoked), `revocations`
(`seq` feed), `request_nonces`, `rate_counters` (HMAC pseudonyms, never addresses), `audit`.
There is no private key, OAuth secret, auth key or plaintext invite anywhere in D1. Migrations:
`0001_init.sql`; `0002_feed_cursor_and_node_index.sql` (indexes only: `revocations(owner_id, seq)`
for the feed and a partial `memberships(owner_id, node_id)` index for the binding check).

D1 has no interactive transactions. `batch()` is atomic, and single-use steps are conditional
`UPDATE … WHERE state=…` statements **[verified T5-08]**.

---

## 14. Abuse protection

- Invite secrets are 256-bit random; invite, membership and ticket ids are 128-bit random.
- Every failure to redeem, approve, enroll, or open a session for something the caller does not
  own returns the same generic 404. A foreign membership id is indistinguishable from a
  nonexistent one. Nothing reveals whether a ServerId or DeviceId exists.
- Signed-request nonces stop replay of any control-plane call (§5).
- Bodies over 16 KiB are rejected.
- **Rate limits** are exact fixed-window counters in D1. Every signed route declares a budget in
  the route table (the type makes an unmetered signed route impossible):

  | Budget | Limit | Keyed on |
  |---|---|---|
  | Registration (`POST /v1/owners` and `/v1/devices` together) | 30 / hour | client network |
  | Registration, every network together | 1000 / hour | global |
  | Redeem | 10 / hour and 30 / hour | device, and client network |
  | Sessions | 120 / hour | device |
  | Owner writes (every owner-signed non-GET) | 600 / hour | owner |
  | Owner reads (friend list, revocation feed) | 120 / 60 s | owner |
  | Other device calls (membership list, enrollment pickup, node binding) | 120 / 60 s | device |

- **Client network.** An IPv4 address counts exactly. An IPv6 address counts as its **/64**, so
  rotating through one /64 gives no extra buckets. An IPv4-mapped IPv6 address counts as its
  IPv4 address. Anything unparseable, including a missing `CF-Connecting-IP`, shares one bucket.
  D1 stores only `HMAC-SHA256(INVITE_PEPPER, "1SALEM-RATE-IP\n" + network)` (16 bytes,
  base64url). A holder of a /48 still gets many /64 buckets; the global registration ceiling
  bounds that, at the cost that a flood can block new registrations (never existing keys) for
  the rest of an hour.
- **No write amplification.** Each counter is one conditional `UPSERT … SET hits = hits + 1
  WHERE hits < max RETURNING hits`: past the limit it changes nothing, returns no row and the
  request gets 429 with `Retry-After`. Requests with missing or malformed signed headers, an
  out-of-window time, or a registration body without a string `spki` are refused before any
  counter. On routes signed by a registered key, a forged signature is refused before any write
  and a replay by the nonce before the key's budget (§5). Registration is the exception: its
  per-network budget is charged before the SPKI, signature and nonce checks (§5), so a forged,
  unusable or replayed registration costs one counter write until that network's budget is
  spent; its global ceiling is charged only after them. A correctly signed request with a bad
  body is charged like any other attempt. What remains per
  request that reaches the Worker is at least one D1 read, and for a request under budget its
  counter and nonce writes; for a signed request over budget, only its nonce (which only the key
  holder can cause). Bounding that is the outer limiter's job (§21, D-2).

---

## 15. Traffic isolation

Running 1Salem Connect must not change the Windows route table, default gateway, DNS servers,
WinINet proxy, WinHTTP proxy, or any other application's traffic.

- tsnet uses a userspace network stack with a fake TUN, a no-op router and a no-op DNS
  configurator **[verified T1-04, T4-21]**. No adapter is created.
- The only host sockets are the loopback listener, the tsnet WireGuard UDP socket **[T1-27]** and
  HTTPS to Tailscale's control plane.
- The proof harness takes a read-only snapshot before and after (`Get-NetRoute`,
  `Get-DnsClientServerAddress`, `netsh winhttp show advproxy`, HKCU Internet Settings, proxy
  environment variables) and requires them to be identical **[verified T8-28 … T8-32]**.

---

## 16. Failure model

| Failure | Behaviour |
|---|---|
| Broker unreachable or over quota | New invites, approvals and sessions fail closed with a clear message **[T5-01, T5-06]**. An open session is **not** kept alive by an outage: it lasts at most about a minute past its current ticket's expiry. From 2 minutes before the ticket expires, the running friend app tries to renew it on every monitor step (each step starts 5 s after the previous one ended, and each broker call is bounded by its 20 s deadline). If no new ticket could be issued by the time the current one expires (10 minutes after it was issued), the app ends the session as "Access expired" at the first monitor step at or after expiry whose renewal also fails, and closes its local listener and its live game streams. Against a broker that fails fast that is within about 5 s of expiry; against one that hangs until the deadlines, up to about a minute. Without a fresh ticket the app cannot show that access is still granted, so the session fails closed: any broker outage that lasts until the current ticket expires interrupts play. Because renewal starts 2 minutes before expiry, that can be an outage of only about 2 minutes (at most about 10). Sessions no running app controls any more are covered in §21, D-3 |
| Friend app exits | The sessions this run opened are closed first (best effort, at most 3 s; any not closed are recorded in Diagnostics), then the transport it started is stopped. A reused transport keeps running without the sessions that were closed (§11) |
| Friend app crashes or is killed | The transport it started ends with it (kill-on-close job, §11), unless Windows refused the job (recorded in Diagnostics). Sessions it had opened in a reused transport stay open until that transport stops (§21, D-3) |
| Friend transport dies or restarts | Its sessions end as "Disconnected". The next Connect starts a new transport, or reuses a verified running one (§11) |
| Host offline | Friend sees "Server offline". The ticket may still issue, but connections are refused |
| Owner revokes | Contract; the Agent wiring is Phase 2 (§12 status, §21 D-4). Live connections close. New tickets are refused. The node is deleted from the tailnet after the tag check (§12, §21 D-1) |
| Ticket expired | The UI refreshes it 2 minutes before expiry. If refresh keeps failing, "Access expired" and the session is closed |
| Local port busy | The next free loopback port is chosen and shown. Nothing is killed |
| Direct path impossible | Tailscale falls back to DERP relays: slower, still end-to-end encrypted **[verified T4-25, T4-27]** |
| Tailnet Lock enabled | Phase 2; no check exists yet. Connect reports it unsupported instead of enrolling nodes that end up locked out **[T4-30]** |
| Owner policy still allow-all | Phase 2; no check exists yet. Connect refuses to enable and says why |
| DPAPI key unreadable (new PC or profile) | Re-pair; never treat as corruption **[T6-21]** |

---

## 17. Legal and naming (from research, not legal advice)

- Code licensing is not a blocker. tsnet is BSD-3-Clause, and its dependencies are permissive
  (Apache-2.0, MIT, ISC, BSD) **[verified T7-01, T7-10]**. Tailscale's own inventory, which covers
  its Linux/macOS commands rather than tsnet on Windows, also lists `golang/freetype`, which is
  dual-licensed FreeType or GPLv2+ **[T7-11, as corrected]**. The license set of **our** Windows
  binaries must therefore be generated with `go-licenses` from the real build rather than assumed.
  Shipping binaries requires a THIRD-PARTY-NOTICES file (Tailscale, Go, gVisor, wireguard-go and
  the rest), regenerated whenever tsnet is updated **[T7-02, T7-12 … T7-15]**.
- Do not bundle Wintun or wireguard-windows. The userspace sidecar does not need them **[T7-16]**.
- Keep "Tailscale" and "WireGuard" out of product names, logos and domains **[T7-03, T7-18 …
  T7-21]**. Refer to them factually, at most, in About/Licenses.
- Hosted-service use is governed by Tailscale's Terms, separately from the code license. Under O1
  the owner is the Customer. A legal read on Terms §2.3 ("commercially exploit") for a paid
  1Salem product is recommended before launch **[T7-24, T7-25]**.
- Tailscale receives friend device metadata under the owner's account **[T7-38]**. Both apps
  should say so plainly.
- By default tsnet uploads logs to Tailscale's log service and may request UPnP/NAT-PMP port
  mappings **[T1-27, T1-28]**. Both need a consent decision before real use (§19).

---

## 17a. Minecraft specifics (Phase 1 target)

- Java Edition uses one TCP connection per login, and a separate short TCP connection for each
  server-list ping **[verified T8-04, T8-05]**. The friend listener must accept many sequential
  and concurrent connections per session.
- Only the game port is bridged. Query (UDP) and RCON (TCP 25575, unencrypted) never are
  **[verified T8-02, T8-03]**.
- The server sees every friend as coming from `127.0.0.1`, because the bridge dials loopback.
  `ban-ip` and per-IP limits therefore apply to all friends together. Per-friend identity and
  revocation live in Connect, not in the game.
- `prevent-proxy-connections=true` makes the server pass its view of the client's address to
  Mojang and kick on mismatch. Behind the bridge, that would kick every friend. The default is
  `false` **[T8-08, as corrected]**. Server Manager should check the setting and warn before
  enabling Connect for a server.
- Handshake and login-start packets travel unencrypted by Minecraft itself **[T8-07, as
  corrected]**. Across the tailnet they are still inside WireGuard. On the friend and host PCs
  they only cross loopback.
- Transfer packets or proxy networks (Velocity/Bungee) can send a client to another host and
  port, which the bridge will not follow. That is unsupported by design **[T8-09]**.

## 18. Palworld (researched, not implemented)

Palworld's game traffic is **UDP 8211** by default **[verified T8-10]**. Its REST API (Basic
Auth) and RCON (deprecated) are not built for exposure and must never be bridged **[verified
T8-12, T8-13]**. tsnet supports UDP through `ListenPacket`, which requires an explicit Tailscale
IP rather than a wildcard **[verified T1-16, T8-16]**.

Replies to a friend's UDP traffic depend on flow tracking that userspace (tsnet) nodes lacked
until tailscale.com v1.102 (PR #20204, merged June 2026). With older versions, a one-way grant
dropped the replies **[T4-08, as corrected]**. Palworld therefore requires v1.102 or later on
both sides (we pin v1.102.4), and must be re-tested whenever tsnet is updated.

A UDP bridge will need a per-source session table, idle expiry, and WhoIs lookups per source.
Whether the game client accepts `127.0.0.1` and behaves through a relay has to be measured in a
lab. The `proto` claim and the transport interface already leave room for it. **Not implemented
in Phase 1.**

---

## 19. Unresolved questions

1. **Multi-owner model (§2).** O1 recommended, Salem to confirm.
2. Minimum `expirySeconds` for auth keys. The docs say 1 day; we delete keys early instead.
3. Is `Status.Self.ID` always the API's `nodeId`? The formats match; the docs do not say
   **[T2-21]**.
4. Does a `devices:core` credential reach every device in the tailnet, or only its tagged ones?
   **[T2 open]**
5. Disabling tsnet logtail uploads, and whether to build with `ts_omit_portmapper` **[T1 open]**.
   Until consent is decided, both sidecars call `logtail.Disable()` and set
   `TS_DISABLE_PORTMAPPER` before the first tsnet server starts (`tsnet.go`, `applyPrivacyDefaults`).
   Neither is tested yet; the first real tsnet run must confirm that nothing is uploaded and no
   UPnP/NAT-PMP/PCP request is made.
6. Windows Firewall prompts for the tsnet UDP socket.
7. tsnet throughput and latency on Windows for game traffic **[T1-30]**.
8. Legal review of Terms §2.3 for a paid product; Tailscale naming.
9. Hardening the **existing** Agent pipe (BUILTIN\Users with create-instance rights, no first
   instance). That is a Build 7 finding, outside this phase **[T8 implications]**.

---

## 20. Rejected alternatives

- **Worker holds each owner's OAuth secret (O2)**: see §2.
- **One vendor tailnet (O3)**: see §2.
- **Node sharing**: tagged devices cannot accept or use shares **[verified T4-18]**.
- **System VPN / Wintun / subnet router / exit node**: would change routes and DNS for the whole
  PC. Explicitly forbidden.
- **Generic SOCKS or HTTP proxy** (for example tsnet `Loopback()`): turns the transport into a
  general proxy.
- **Direct tailnet access to 25565 or 8211**: puts the game port and whatever else the owner
  exposes on the tailnet, removing the application-authorization layer.
- **Ephemeral friend nodes with in-memory state**: a new device and IP every launch, removal
  30–60 minutes after disconnect, and billing once present over 4 hours **[verified T2-11,
  T1-13, T1-14]**.
- **Ticket secret in the URL**: ends up in logs. The body is used instead.
- **Custom encrypted tsnet state**: tsnet's supported mechanism is its state directory
  **[verified T1-08, T1-09]**. It is protected with a current-user ACL.

---

## 21. Deferred to Phase 2

What the Phase 1 security review could not finish without Phase 2 parts (the Agent's live
Tailscale API wiring, the Agent hosting the Connect host components, and a production Cloudflare
deployment), and the smaller limitations it accepted. Each is stated with what Phase 1 already
does, so none is mistaken for complete.

### D-1. Owner-confirmed Tailscale node binding

- **Threat.** `POST /v1/memberships/{id}/node` records whatever `nodeId` the friend's device
  reports. A malicious approved friend could report a node id that is not their own: another
  friend's node (to have tickets carry it), or the owner's own machine (so that revoking the
  friend later deletes the owner's device from the tailnet in §12 step 3).
- **Phase 1 protection.**
  - The broker refuses a node id that a **different** device already holds on a live membership
    of the same owner (409 `node_in_use`, race-safe inside one `UPDATE`), so two friend devices
    can never share one node binding.
  - A wrong `nid` gives the friend no access: the host verifies `nid` against the **WhoIs**
    identity of the connecting peer (§9, §10), so binding someone else's node only breaks the
    friend's own connections.
  - The broker binds once and never lets the friend change it (`already_bound`).
  - The Agent's revocation must read the device from the Tailscale API and delete it only if it
    carries `tag:1salem-client` and is not an owner node (§12). No Agent code deletes devices yet.
- **Remaining risk.** Until confirmation exists, a friend who learns another friend's node id
  before that friend binds it can take the binding first (a denial of service against that
  friend, not an access gain). The owner cannot yet see or clear a wrong binding.
- **Exact Phase 2 action.** Split binding into a device-reported *candidate* and an
  owner-signed *confirmation*: after the friend reports its node, the Agent calls
  `GET /api/v2/device/{nodeId}` in the owner's tailnet and checks that the device carries
  `tag:1salem-client`, was created after the enrollment key was minted, and is not bound
  elsewhere; then it calls a new owner-signed `POST /v1/memberships/{id}/node/confirm`.
  `POST /v1/sessions` issues tickets only for a confirmed binding, and the owner can reject or
  clear a candidate. The tag check before any `DELETE` stays mandatory. The existing primitive
  `TailscaleApiProvisioner.DeleteDeviceAsync` deletes by id with no tag check of its own, so its
  Phase 2 caller must run `GetDeviceAsync` and the tag / owner-node check first, or that check
  must move into it.
- **Why Phase 1 is still safe.** Phase 1 enrolls no real node at all (fake mode only). Tickets
  are useless from any node but the WhoIs-verified one, and no code path deletes a tailnet device
  from a broker-supplied id (`DeleteDeviceAsync` has no caller outside tests).

### D-2. Outer deployment-level rate limiter

- **Threat.** A flood aimed at the broker. Every request that reaches the Worker costs at least
  one D1 read, and D1 serves one database's queries one at a time, so a large enough flood slows
  or blocks legitimate calls (approvals, revocations, ticket refresh) and is billed per row.
- **Phase 1 protection.** Every signed route has an exact per-key D1 budget; registration and
  redeem have per-network budgets (IPv6 grouped by /64) and registration a global ceiling.
  Over-limit traffic adds no counter write. On routes signed by a registered key, requests with
  missing or malformed signed headers or a forged signature write nothing, and replays are refused
  before the key's budget is charged. Registration charges its per-network budget before it checks
  the SPKI, the signature and the nonce, so a forged, unusable or replayed registration costs one
  counter write until that network's budget is spent. A correctly signed request with a bad body
  is charged like any other attempt (§5, §14).
- **Remaining risk.** Exact D1 counters are themselves D1 traffic, so they cannot bound the cost
  of a flood; a rotating attacker still costs one D1 read per request, and a key holder over
  budget still costs one nonce write per request.
- **Exact Phase 2 action.** Before any deployment, put a limiter in front of D1: a Cloudflare
  WAF rate-limiting rule on the broker route, or a Workers Rate Limiting binding checked first in
  `fetch`, keyed per client IP and sized well above the D1 budgets so it only ever catches
  floods **[verified T5-14, T5-15, T5-17]**. Keep Cloudflare Pseudo IPv4 off (or "Add header"),
  because "Overwrite headers" would defeat the /64 grouping. It complements the D1 counters,
  which stay the exact enforcement.
- **Why Phase 1 is still safe.** The broker runs only locally (`wrangler dev --local`,
  `workers_dev: false`, placeholder database id); it is not reachable from the internet. The host
  component's revocation (`ConnectHostAuthorizationPipeServer`: local revocation set, replay-cache
  drop, closing live connections) does not depend on broker availability; in Phase 1 it runs only
  in tests and the disposable proof, and the Agent hosts it in Phase 2 (D-4). A broker outage does
  not extend a running friend app's access either: the app ends an open session shortly after its
  ticket expires without renewal (§16). The exceptions are sessions no running app controls any
  more (D-3).

### D-2a. Database restore runbook (deployment requirement)

- **Threat.** After a D1 restore the revocation `seq` counter is rolled back, so a host's kept
  feed cursor can match a new event's `seq` and silently skip events up to it.
- **Phase 1 protection.** The feed accepts only cursors that are the caller's own events (§13), and
  a test demonstrates both the failure without the restore step and the refusal with it.
- **Remaining risk.** Only if an operator restores the database and skips the step.
- **Phase 2 action.** Put the step (`UPDATE sqlite_sequence SET seq = seq + 1000000000 WHERE name =
  'revocations'`, run once right after a restore) in the production runbook, and verify it
  against the real D1 service once, since the test runs on the local D1 simulation.
- **Why Phase 1 is safe.** The broker is local only and never restored, and nothing consumes the
  feed yet. Once the Agent hosts the host component (D-4), its own revocation set and immediate
  enforcement (§12) do not depend on the feed.

### D-3. Smaller accepted limitations (Phase 1 review)

Low-severity items the review found and deliberately left, each with why it is acceptable now:

- **Local pipe-slot denial of service by a same-user, lower-integrity process (review F7;
  deferred).** The friend transport's pipe serves at most 8 clients and refuses the rest by
  accepting and closing. A sandboxed process of the same user can hold those slots, because read
  access is not blocked by the default label. When the app then needs a new pipe connection (its
  first call, every `enroll`, or a reconnect after a dropped connection), that connection is
  closed without an answer and reads as `unavailable` (§11). What follows depends on the call. At
  the start of a Connect or an enrollment (`hello`), the app restarts a transport it started,
  which ends live sessions, or fails the call when it reused one. An `enroll` refused this way has
  already used up the one-time blob, so that server's card shows "Setting up this server failed…"
  until the owner approves again. A monitor step or Disconnect refused this way leaves the page
  saying the connection could not be closed yet, and the session stays open until a slot frees.
  This is a local denial of service only: nothing is sent to the squatter (the app verifies the
  serving process before writing, §11). Phase 2: restart only when the started process has
  exited, treat a verified connection that closes without answering as `no_answer`, and label the
  pipe no-read-up.
- **A server card can show a stale connection state for up to about 60 s, or longer during a
  broker outage (review F8; deferred).** Each card on the Servers page is a snapshot taken at the
  last successful refresh, and the page refreshes every 60 s when nothing is pending; it is not
  bound to the live connection state. So while the broker answers, a card can still say
  "Connected" or "Server offline" for up to about a minute after the session ended. While the
  broker cannot be reached or rate-limits the refresh, the page shows its error line but keeps the
  last cards, so a card can stay stale for the whole outage. The Connection page follows the
  session itself (every 5 s). Phase 2: bind cards to the connection's state changes.
- **Refusal log lines on the host bridge are limited globally**, not per source: a flooding peer
  also hides other friends' refusal reasons for the rest of that minute (the refusal counter and
  a summary line remain). Logs only; no effect on who is allowed.
- **Non-default REST/RCON ports are not known to the catalog** (a server's definition carries only
  its game port); only the defaults 8212 and 25575 are refused. The owner's tailnet policy still
  exposes only the bridge port (§8).
- **Two Minecraft servers registered on the same port are both refused**, and the owner is not yet
  told why Connect fails for them. Safe, but needs UI in Phase 2.
- **Sessions no running app controls any more.** A killed friend app leaves no stale listener on
  a transport it started, because that transport is in a kill-on-close job (§11). Three cases
  remain:
  - An `open` whose answer never arrives or cannot be read (`no_answer`, a connection that broke
    mid-call, an unreadable reply) may leave a loopback listener until the transport stops,
    because the app never learned its session id.
  - Sessions that a crashed or killed app had opened in a *reused* transport stay open until that
    transport stops. A clean exit tries to close them (for up to 3 s; any it cannot close are
    recorded in Diagnostics, §11 "Exit").
  - If Windows refused the kill-on-close job (recorded in Diagnostics), a crashed or killed app
    leaves the transport it started running, like a reused one.

  In each case the listener refuses new connections from 30 s after the ticket expires, but a live
  stream lasts until it ends or the friend is revoked (once the Agent hosts revocation, D-4).
- **A node-binding race answers with the wrong code.** If an approval commits between the binding
  `UPDATE` failing and the follow-up read that explains why, the friend gets 409 `node_in_use`
  instead of `invalid_state`; a retry binds. Cosmetic.
- **The broker keeps history.** Only request nonces, rate counters, enrollment blobs and (a day
  after expiry) session rows are ever deleted (`maintenance.ts`). Revocation events are never
  purged, so the feed replays the whole history from `after=0`. Invites, memberships, servers,
  audit rows and registered owner and device rows, used or not, are never expired. The rate
  limits (§14) bound how fast this grows, not how large it gets.

### D-4. Agent hosting of the Connect host components

- **What exists.** The host side is built as libraries in `src/ServerManager.Infrastructure/Connect`
  (`ConnectHostAuthorizationPipeServer`, `ConnectServerCatalog`, `ConnectLiveConnections`,
  `ConnectHostTransportSupervisor`, `TailscaleApiProvisioner`) and exercised by tests; the
  disposable proof also drives the catalog and the authorization pipe server in-process.
- **What is missing.** The Agent service (`src/ServerManager.Agent`) references none of them: it
  does not supervise the host transport, serve the host authorization pipe, hold an owner
  identity or pinned broker keyset, pull the revocation feed (§12), store an OAuth credential, or
  back the owner's Connect card, which is a disabled preview. So in Phase 1 no friend can reach
  an owner's real server, and the revocation contract of §12 is enforced only where the proof
  drives it.
- **Phase 2 action.** Host these components in the Agent: start the host transport under the
  supervisor, serve the pipe with the catalog's three-argument constructor (the Agent's real API
  ports), route "turn Connect off" through `DisableConnectAsync`, pull the revocation feed with a
  kept `cursor` (restarting from `after=0` on `cursor_ahead`), apply it through the `Revoke*`
  methods, and add the owner UI. §12's four-step revocation, including the tag check before any
  device deletion (D-1), is the contract.
