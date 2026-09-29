# Connect Phase 2 — validation and acceptance record

Local checkpoint: 2026-09-27. Acceptance update: 2026-09-29. Branch: `claude/connect-phase-2`.

The local implementation slices A–G are complete. The September 27 validation below is a
historical checkpoint; the separately authorized Phase 2 live acceptance is **complete: 95/95
driver checks passed**, with cleanup and Windows comparisons passed. There is **no deployment**. The accepted Phase 1
baseline remains `4bfb9a8` (real-tsnet 58/58); that old smoke proof was not rerun.

## Current real-tailnet acceptance — 2026-09-29

- Fresh September 29 read-only prechecks passed OAuth token/scopes, auth-key/Devices API access,
  `devices:posture_attributes:read` and `policy_file:read`. Required permissions include `auth_keys`
  write and `devices:core` write, with only `tag:onesalem-host` on the replacement OAuth credential.
- The real policy passed the production analyzer: the approved tag ownership and client-to-host
  TCP 7780 restriction were verified. Tailnet Lock was independently observed OFF. Neither the
  policy nor Tailnet Lock was changed.
- Run `1p2-0929a007` completed on September 29 at 07:40:31 UTC under `%TEMP%\1p2-0929a007`. It used the actual Debug Agent's
  Connect lifecycle as the **current Windows user**, disposable Agent/friend state, a local
  Wrangler broker, and temporary real-tailnet nodes. It does not run the installed SYSTEM service.
- Final driver checks passed 95/95, including post-restart revocation and natural ticket expiry.
  All three temporary nodes and three one-time keys were removed; zero temporary nodes remain.
  Network, protected-process and service comparisons passed, as did local cleanup. Root evidence
  is `driver/result.json`, `driver/nodes.json` and `runner-result.json`.
- The separate Phase 2 staging credential is now present and used through the protected input
  path. The accepted Phase 1 credential is untouched. Cleanup removed only this run's native
  credential copy/state and created nodes/keys, not the permanent staging credential.

Full outcomes, live-discovered fix, focused tests and evidence: [real-tailnet acceptance report](CONNECT_PHASE2_REAL_TAILNET_ACCEPTANCE.md).

Procedure and safety boundaries: [Phase 2 acceptance harness](../connect/proof/PHASE2_ACCEPTANCE.md).
This acceptance does not validate the installed SYSTEM identity, graphical English/Arabic WPF
rendering/clipboard behavior, real Minecraft gameplay, a production broker deployment, packaging
or upgrades. No Cloudflare production change, Build 7 service replacement, push, packaging or
version/revision change is authorized by this run.

## Implemented slices — historical local checkpoint

| Slice | Local commit | Result |
|---|---|---|
| A | `52b9fde` | Candidate/confirmed/rejected node contract, ticket gating, fail-closed production limiter and broker readiness |
| B | `02999eb` | Safe friend transport `forget` operation |
| C | `24899d9` | Protected owner state, OAuth provisioning, policy/node validation and broker client |
| D | `da2024a` | Agent-hosted lifecycle, owner workflow, revocations and local-only API |
| E | `98b9da3` | Friend confirmation-pending state and stale-node recovery |
| F | `2ab2b84` | Owner setup, invitations and friend access management, English/Arabic |
| G | `4efcc8b` | Updated proof harnesses/docs, final regression and corrections found during verification |

G also fixes reconciliation startup/shutdown races, stale server responses, nickname drafts
lost during polling, hidden-page polling, protected invite copying through keyboard/context-menu
paths, raw DWORD clipboard opt-out markers, masked credential hints accidentally reused as input,
and cleanup controls hidden when account/eligibility checks fail.

## Local verification — 2026-09-27 historical checkpoint

All seven .NET test suites passed across the full Release run and the affected-suite reruns
after corrections: **1,203 tests total, none skipped**. This is an aggregate, not a claim that
every final test was executed in a single command.

| Check | Result |
|---|---|
| `dotnet build 1SalemServerManager.sln -c Release --no-restore` | Passed; 0 warnings, 0 errors |
| Connect core tests | 172/172 |
| Core tests | 155/155 |
| Setup tests | 34/34 |
| Agent integration tests | 38/38 |
| Friend app tests | 159/159 |
| Infrastructure tests, final rerun | 425/425 |
| Client tests, final rerun | 220/220 |
| Go `connect/transport/build.ps1 -Test` | Vet, tests and builds passed, default and `ts_omit_oauthkey` configurations |
| Broker TypeScript typecheck | Passed |
| Broker tests | 116/116 across 12 files |
| Broker production `deploy:check` | Passed dry-run only; D1, FLOOD and required-limiter bindings present |
| Fake end-to-end proof | 39/39; Windows network snapshot unchanged |
| Real-tsnet proof driver | Release compile passed; no live run |
| Source hygiene | `git diff --check` passed; changed files contain no long literal Tailscale keys or private-key PEM headers |

The fake proof now exercises candidate-ticket refusal and production owner-client confirmation,
and verifies the friend stays pending until confirmation. It uses throwaway local D1, keys and
loopback transports, not real Tailscale identity. The runner retains the last local evidence under
`%TEMP%\1cp-last` and cleans its disposable state.

Windows ACL/AppData test fixtures require execution outside the restricted workspace sandbox.
Sandbox permission failures were not counted as product failures or passes; the relevant Go and
.NET checks passed with the required local access. Broker tests passed despite sandbox log/static
export-analysis warnings; the separate production bundle dry-run also passed. A pre-existing
process re-adoption timing test failed once in the broad run, then passed its focused rerun and
the final full Infrastructure run. Two Connect lifecycle races found during testing were fixed;
the shutdown regression was reproduced deterministically before the correction.

## Preserved boundaries at the 2026-09-27 local checkpoint

- `main` remains `4bfb9a8`; no merge, push, deployment, remote migration or packaging.
- `VERSION` remains `1.5`; `BUILD_REVISION` remains `7`.
- No changes were made to the installed Agent, production Minecraft, Palworld or Playit.
- At this local-only checkpoint, no real nodes had been enrolled and the accepted OAuth
  credential had not been changed. Later authorized disposable live runs are recorded separately above.
- Approved tags remain `tag:onesalem-host` and `tag:onesalem-client`.
- The local pattern scan is a bounded credential check, not an exhaustive security audit.

## Historical credential gate and remaining acceptance boundaries

Historical acceptance checkpoint (2026-09-27): branch `claude/connect-phase-2`, local-validation HEAD
`4efcc8b`, clean worktree before the credential helper correction below. Only the accepted old
credential existed then; the separate Phase 2 staging file was absent. Live acceptance stopped at that
gate: no Tailscale calls, nodes or keys created, no production changes, and no regression or old
smoke rerun. The helper correction passed PowerShell parsing, Store/Status-only parameter-set
checks and read-only status checks for both paths. Credential contents were not opened.
This missing-credential gate is no longer current; the completed run used the separately stored replacement.

1. The **replacement** OAuth credential required by the approved secure input path is now stored:
   `auth_keys` write, `devices:core` write, `devices:posture_attributes:read` and **`policy_file:read`**, with only
   `tag:onesalem-host`. Do not paste credentials into chat or modify the accepted credential.
   Connect deliberately stays off if policy cannot be verified.

   The create-only secure prompt used for its isolated Phase 2 target is documented here; do not
   overwrite or recreate the credential for an existing acceptance run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File connect\proof\Set-TsnetSmokeCredential.ps1 -Phase2
   ```

   This writes only `%LOCALAPPDATA%\1Salem Connect Phase2 Acceptance\oauth-client.dpapi`
   (DPAPI CurrentUser, protected DACL), refuses to replace an existing file, and does not contact
   Tailscale or run the Phase 1 proof. `-Phase2 -Status` checks existence without opening it.
   The accepted `%LOCALAPPDATA%\1Salem Connect Smoke\oauth-client.dpapi` is left untouched.
   This is secure staging input for acceptance, not the Agent's differently formatted native
   DPAPI store. The isolated Agent saves/reads through that production path as the current user;
   the installed service's SYSTEM identity is not exercised.

2. The separately authorized real-tailnet acceptance of the isolated, current-user Agent-hosted
   path is complete, with policy and Tailnet Lock checks, invitations, confirmation, shared-node
   revocation and restart recovery passed. Installed SYSTEM identity/state permissions remain
   unvalidated; the prior Phase 1 smoke test does not replace either check.
3. Perform interactive WPF visual/keyboard acceptance in English and Arabic, including RTL,
   actual Windows clipboard behavior and error/recovery presentation. Headless tests and XAML
   compilation are verified here; visual rendering and clipboard history UI are not claimed.
4. Separately authorize Cloudflare provisioning/deployment and verify the real D1 migrations,
   secrets, key rotation, restore procedure, client-IP behavior and outer limiter. Placeholder
   deployment identifiers must be resolved before a real deploy.
5. Keep packaging/release authorization separate. Palworld/UDP and the existing deferred pipe
   denial-of-service / friend-card refresh limitations remain outside this delivery; see
   `CONNECT_ARCHITECTURE.md` §21 and `DEFERRED_ISSUES.md`.

Clipboard marker format reference: [Microsoft clipboard formats](https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats).
Dry-run semantics: [Cloudflare Wrangler commands](https://developers.cloudflare.com/workers/wrangler/commands/).
