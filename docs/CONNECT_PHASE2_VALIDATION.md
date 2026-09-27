# Connect Phase 2 — local validation and acceptance handoff

Date: 2026-09-27. Branch: `claude/connect-phase-2`.

The local implementation slices A–G are complete. This is **not a deployment or a live
acceptance result**. The accepted Phase 1 baseline remains `4bfb9a8` (real-tsnet 58/58).
That live test was not rerun.

## Implemented slices

| Slice | Local commit | Result |
|---|---|---|
| A | `52b9fde` | Candidate/confirmed/rejected node contract, ticket gating, fail-closed production limiter and broker readiness |
| B | `02999eb` | Safe friend transport `forget` operation |
| C | `24899d9` | Protected owner state, OAuth provisioning, policy/node validation and broker client |
| D | `da2024a` | Agent-hosted lifecycle, owner workflow, revocations and local-only API |
| E | `98b9da3` | Friend confirmation-pending state and stale-node recovery |
| F | `2ab2b84` | Owner setup, invitations and friend access management, English/Arabic |
| G | This validation commit | Updated proof harnesses/docs, final regression and corrections found during verification |

G also fixes reconciliation startup/shutdown races, stale server responses, nickname drafts
lost during polling, hidden-page polling, protected invite copying through keyboard/context-menu
paths, raw DWORD clipboard opt-out markers, masked credential hints accidentally reused as input,
and cleanup controls hidden when account/eligibility checks fail.

## Verification

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

## Preserved boundaries

- `main` remains `4bfb9a8`; no merge, push, deployment, remote migration or packaging.
- `VERSION` remains `1.5`; `BUILD_REVISION` remains `7`.
- No changes were made to the installed Agent, production Minecraft, Palworld or Playit.
- No real nodes were enrolled and the accepted OAuth credential was not changed.
- Approved tags remain `tag:onesalem-host` and `tag:onesalem-client`.
- The local pattern scan is a bounded credential check, not an exhaustive security audit.

## Acceptance still required

1. Salem creates a **replacement** OAuth credential through the approved secure input path:
   `auth_keys` write, `devices:core` write and **`policy_file:read`**, with only
   `tag:onesalem-host`. Do not paste credentials into chat or modify the accepted credential.
   Connect deliberately stays off if policy cannot be verified.
2. Separately authorize a real-tailnet acceptance of the Agent-hosted path, including SYSTEM
   identity/state permissions, policy and Tailnet Lock handling, invitations, confirmation,
   shared-node revocation and restart recovery. The prior Phase 1 smoke test does not replace this.
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
