# Phase 2 real-tailnet acceptance — complete

Date: 2026-09-29. Branch: `claude/connect-phase-2`. Starting HEAD: `f6dffa8`.

**PHASE 2 REAL-TAILNET ACCEPTANCE COMPLETE.** Final run `1p2-0929a007`, internal run ID
`38bf9bbd1990`, passed **95/95 driver checks**, with no failed checks. Driver and runner exited 0.
The driver ran from 07:28:36 to 07:40:30 UTC; runner safety/cleanup verification finished at
07:40:31 UTC. This report and the fixes are included in the local acceptance commit.

## Results

| Acceptance category | Result | Evidence |
|---|---|---|
| OAuth scopes | PASS | Token HTTP 200; granted `auth_keys`, `devices:core`, `devices:core:read`, `devices:posture_attributes:read`, `policy_file:read`; real key creation and device deletion also succeeded |
| Policy verification | PASS | Policy GET 200; production analyzer Safe; host owned by `autogroup:admin`, client owned by `tag:onesalem-host`; client to host limited to TCP 7780 |
| Agent-hosted host enrollment | PASS | Actual Agent lifecycle/provisioner, native protected CurrentUser DPAPI store, real host tag and bridge |
| Invite flow | PASS | Agent invitation; actual friend InviteViewModel; one-time redemption; owner sees pending and approves |
| Friend enrollment | PASS | Actual EnrollmentCoordinator, one-time sealed key pickup, real tagged device |
| Candidate ticket gating | PASS | Candidate cannot obtain a session ticket before owner confirmation |
| Owner-confirmed node binding | PASS | Actual Agent Devices API read, expected tag, non-host identity, creation-time gate and broker confirmation before ticket issuance |
| Minecraft loopback path | PASS | Actual friend ConnectionViewModel/SessionService, loopback listener, real tsnet path, disposable Minecraft identity destination, banner and bidirectional echo |
| Real WhoIs / wrong peer | PASS | Real wrong-node and forged-ticket probes; Agent `WrongPeerNode` and `InvalidSignature` decisions; replay refused with `ReplayedNonce` |
| Fallback guard | PASS | Real tsnet host-network witness connection rejected before application bytes; non-tailnet and unowned-peer destinations refused |
| Sensitive ports blocked | PASS | Host-tailnet TCP 25565, 8211, 8212, 5251, 3389, 445, isolated Agent port and disposable game port blocked |
| Revocation | PASS | Broker and local revoke, live stream closure, no new ticket, actual friend view model Access revoked, shared-node retention, then safe last-membership device deletion |
| Restart recovery | PASS | Same owner/host identity; same friend's valid ticket accepted and revoked ticket explicitly refused after restart; persisted revocation verified |
| Windows network unchanged | PASS | Before/after adapter, route, DNS and proxy snapshots equal; protected processes/services also unchanged |
| Cleanup | PASS | Three temporary nodes and three one-time keys removed; zero temporary nodes; owned processes and sensitive local state removed |

Natural expiry was tested using a genuine broker-issued ticket after its ten-minute lifetime and
clock-skew allowance, without changing clocks or re-signing the ticket. A fresh valid ticket from
the same real friend succeeded, while the expired ticket returned the explicit refusal status and
the Agent logged `Expired`. The last membership then deleted the friend through the real Agent's
ownership/tag-checked path; Devices API GET returned 404.

## Precheck and isolation

Read-only precheck evidence was refreshed at **07:24:04 UTC**, before this run. Token issuance,
auth-key listing, device listing and policy retrieval all returned 200. The approved-tag baseline
was zero devices. Tailnet Lock was independently observed OFF in the admin device-management UI
(`Enable Tailnet Lock` was available); absence of device lock errors was not used as the sole proof.
Neither policy nor Tailnet Lock was modified. Neither permanent OAuth credential was recreated,
replaced or deleted.

The Debug-only acceptance launcher uses production `ConnectHostService`, owner endpoints,
provisioner, authorization server and sidecars. State, three Agent pipes, friend pipe, authenticated
loopback API, broker and two disposable test-server identities are isolated. Unrelated production
background services/endpoints are not started. The Cloudflare/Wrangler procedure uses a local-only
broker and local D1; production deployment is not part of the run.

This is the **current-user Debug Agent**, not the installed LocalSystem service. The Minecraft
target is an isolated registered identity/echo endpoint, not gameplay. Production friend view models
and services run headlessly; graphical WPF, RTL and clipboard UI behavior are not claimed. Installed
SYSTEM identity, production broker deployment, packaging, upgrade and gameplay acceptance remain
separate work requiring authorization.

## Defect and focused verification

The live test exposed one product defect: a successful authorization logged an `IPEndPoint` object
through the production JSON logger. Serializing its IPv4 address's `ScopeId` getter threw, closing
the authorization pipe before the allow response. The smallest fix logs `result.Endpoint.ToString()`.
The real JSON-logger/named-pipe regression reproduced failure before the fix and passes afterward.
The final live run confirms successful bidirectional traffic with that logger enabled.

The test launcher also needed a bounded cold-start peer-readiness gate. Agent Ready means local
lifecycle readiness; it does not guarantee that another node's network map already contains the
host. Before sending any frame, the test now waits at most 20 seconds for Up and WhoIs to identify
the exact friend and host. The successful post-restart gate required five WhoIs observations over
1,003 ms. Each actual dial and authorization/status check remains single-attempt; timeouts cannot
count as authentication refusals. No production authorization or fallback rule was weakened.

Only focused affected checks were run during this acceptance work:

- Debug Agent acceptance/options tests: 40/40; Release options/refusal checks: 4/4.
- Connect host workflow tests: 10/10; cleanup identity-gate tests: 9/9.
- Host authorization-pipe tests: 42/42 Debug; the new JSON-logger regression additionally passed 1/1 Release.
- Probe error-classifier cases: 18/18; tagged real-probe compilation passed with `ts_omit_oauthkey`.
- Focused Debug Agent and acceptance-driver builds: zero warnings/errors; runner parsing and owned-process helper checks passed.
- Source whitespace checks passed; bounded credential-pattern scan found no literal long Tailscale keys or private-key PEM headers in changed source.

The previous 1,203-test regression and old Phase 1 smoke proof were **not rerun**. Earlier disposable
attempts `1p2-0928a001` through `1p2-0928a006` are diagnostic history, not passing final acceptance.
Their cleanup completed; cleaned state was not reused for retries.

## Retained evidence and preserved boundaries

Metadata root: `C:\Users\MS1\AppData\Local\Temp\1p2-0929a007`.

- `driver/result.json`: 95 checks, all passed; zero temporary nodes; Agent refusal reasons.
- `driver/nodes.json`: three positively identified nodes and three one-time keys, all removed.
- `driver/wrong-peer.json`, `before-restart.json`, `after-restart.json`, `expired.json`: real probe evidence.
- `runner-result.json`: driver exit 0; network/protected processes/services unchanged; local cleanup true; no runner errors.
- Before/after snapshots and bounded progress logs remain for inspection.

Cleanup removed the run's native DPAPI copy, Agent/friend/probe state, local broker secrets and local
D1 data, and stopped owned processes. Only redacted metadata was retained; the disposable identities
and one-time keys are no longer usable. Both permanent DPAPI credential files remain untouched.

Cloudflare production touched: **NO**. Installed Build 7, production Minecraft, Palworld and Playit
touched: **NO**. No push, merge, deployment, packaging, adapter/route/DNS/proxy change or release bump.
`main` remains `4bfb9a8`, `VERSION` remains `1.5`, and `BUILD_REVISION` remains `7`.
