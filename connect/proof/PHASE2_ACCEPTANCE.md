# Phase 2: isolated Agent-hosted acceptance

This development harness exercises the **actual Debug Agent** and production friend-app services against a disposable local Wrangler broker and real temporary Tailscale nodes. It is separate from the Phase 1 smoke runner. This document describes the procedure, not a completed or passing live acceptance result.

The completed September 29 run and its scope are recorded separately in the [real-tailnet acceptance report](../../docs/CONNECT_PHASE2_REAL_TAILNET_ACCEPTANCE.md).

The test target is a disposable loopback identity/echo service registered as Minecraft in the isolated SQLite database. Banner and bidirectional-byte checks establish the transport path; they do not test Minecraft gameplay. The Agent runs as the current Windows user, not the installed LocalSystem service or the installed Build 7 binary. Wrangler runs locally; no Worker or D1 deployment occurs.

## Prerequisites

- Windows, PowerShell 7, repository `.tools` .NET/Go/Node toolchains, restored .NET dependencies, cached Go modules, and `connect/broker/node_modules`.
- Explicit authorization for the real-tailnet test. The current driver requires no pre-existing devices with `tag:onesalem-host` or `tag:onesalem-client` in its disposable test tailnet.
- A separately staged CurrentUser-DPAPI OAuth credential at `%LOCALAPPDATA%\1Salem Connect Phase2 Acceptance\oauth-client.dpapi`. The driver accepts only this path. Its parent must be current-user-owned with protected permissions allowing only that user and SYSTEM; the leaf may be owned by the user or Administrators but may not grant another identity access. No credential is passed on a command line or written as plaintext. The Phase 1 credential path is not used.
- Fresh read-only evidence that the host-tagged OAuth client has `auth_keys`, `devices:core`, `devices:posture_attributes:read` and `policy_file:read`, the production policy analyzer returns Safe, and Tailnet Lock is independently verified OFF in the Tailscale admin interface. Empty `tailnetLockError` alone does not establish that Tailnet Lock is off.
- Free distinct loopback ports; defaults are broker `8799` and Agent `5253`. The runner refuses production/sensitive ports and inherited `TS_*`/`TSNET_*` settings. Run paths must be reparse-free.

Save the actual precheck result as JSON, with its real UTC observation time and source. Both the runner and driver require evidence less than 30 minutes old; do not merely refresh the timestamp without repeating the checks.

```json
{
  "tailnetLockOff": true,
  "checkedAtUtc": "<actual UTC timestamp in ISO 8601 format>",
  "source": "Tailscale admin device-management UI: Enable Tailnet Lock link; read-only",
  "policySafe": true,
  "requiredScopesPresent": true
}
```

## Focused preparation and invocation

From the repository root, with the existing restored dependencies:

```powershell
& .\.tools\dotnet\dotnet.exe build src\ServerManager.Agent\ServerManager.Agent.csproj -c Debug --no-restore
& .\.tools\dotnet\dotnet.exe build connect\proof\Phase2Acceptance\Phase2Acceptance.csproj -c Debug --no-restore
& .\.tools\dotnet\dotnet.exe test tests\ServerManager.Agent.IntegrationTests\ServerManager.Agent.IntegrationTests.csproj -c Debug --no-restore --filter 'FullyQualifiedName~ConnectAcceptance'
pwsh -NoProfile -File connect\transport\build.ps1
```

Compile the tagged probe in a dedicated PowerShell session using the repository's cached Go environment. This compiles the tests; it does not run them or contact the tailnet:

```powershell
$phase2Tools = (Get-Item .\.tools).FullName
$env:GOPATH = Join-Path $phase2Tools 'gopath'
$env:GOMODCACHE = Join-Path $phase2Tools 'go-mod'
$env:GOCACHE = Join-Path $phase2Tools 'go-build-cache'
$env:GOPROXY = 'off'
$env:GOTOOLCHAIN = 'local'
$env:GOTELEMETRY = 'off'
$env:CGO_ENABLED = '0'
Push-Location connect\transport
& (Join-Path $phase2Tools 'go\bin\go.exe') test -c -tags 'tsnetsmoke,ts_omit_oauthkey' -o (Join-Path $phase2Tools 'phase2-probe.test.exe') ./internal/transport
Pop-Location
```

Stop if any build/check exits nonzero. The runner checks that both transports and the probe record `ts_omit_oauthkey`. It expects the Debug Agent at `src/ServerManager.Agent/bin/Debug/net8.0-windows/1Salem.ServerManager.Agent.exe` and the driver at `connect/proof/Phase2Acceptance/bin/Debug/net8.0-windows/Phase2Acceptance.exe`.

```powershell
# Validates outputs, paths, ports and evidence; no broker/Agent launch, API calls or credential decryption.
pwsh -NoProfile -File connect\proof\Run-Phase2Acceptance.ps1 -PrecheckEvidence 'C:\absolute\precheck.json'

# Run only under the separately authorized live acceptance workflow.
pwsh -NoProfile -File connect\proof\Run-Phase2Acceptance.ps1 -Live -PrecheckEvidence 'C:\absolute\precheck.json'
```

Each invocation chooses a new `%TEMP%\1p2-<8 hex>` root. Optional `-WorkRoot` must use that exact direct-TEMP-child naming scheme and must not exist. `-BrokerPort` and `-AgentPort` override the defaults. Do not reuse a prior run's directory.

## What the driver exercises

The driver starts the real Agent with `--connect-acceptance`, a private data root, separate authenticated loopback API and run-specific named pipes. It transfers the staging credential in memory through the authenticated local credential endpoint; the Agent saves its own native DPAPI copy. It uses the real `ConnectHostService`, owner endpoints, runtime factory, Devices API checks, policy analyzer and authorization bridge.

Production friend `InviteViewModel`, `BrokerClient`, `EnrollmentCoordinator`, `TransportProcess`, `SessionService` and `ConnectionViewModel` handle invite redemption, enrollment and the loopback listener. Stopping the isolated Agent after key delivery makes the candidate-before-confirmation gate deterministic. Restarting it exercises actual persisted owner identity and host reconciliation. Further checks cover shared-node membership, live revocation closure, UI revocation state, real WhoIs wrong-peer refusal, forged/destination refusals, sensitive ports, fallback protection, replay, persisted revocation and natural ticket expiry. A tagged test probe temporarily reopens only the disposable friend's cached node for direct authenticated-frame checks; it does not replace Agent authorization.

## Evidence, cleanup and failures

Inspect `driver/result.json` for individual checks and Agent refusal reasons, `driver/nodes.json` for resource IDs/removal state, and `runner-result.json` for process/service/network comparisons and local cleanup. Probe JSON and `driver/failure-diagnostics.json`, when present, provide bounded diagnostic evidence. Failure summaries identify exception types/locations; do not dump HTTP bodies, tickets, keys, enrollment ciphertext, native DPAPI data or complete transport state into reports. A failed or absent check is not a pass.

The Debug Agent journals key creation/deletion and device-read/deletion metadata immediately in its isolated `connect-acceptance-resources.jsonl`. Cleanup merges that journal with exact IDs from the run's own node markers. Device deletion requires matching ID, hostname, expected tag, non-ephemeral status and creation time; baseline or merely discovered devices are not adopted as deletion targets. An initial 404 needs prior positive API visibility to prove removal.

The driver owns its Agent/probe process job and friend transport, temporary nodes/keys, native credential copy and isolated state. The runner owns the local broker job, disposable broker secrets and local D1 data. Successful cleanup removes the run's sensitive state and retains redacted evidence; it does not delete the separately approved staging credential. If cleanup cannot prove ownership/removal, it retains recovery material: resolve the recorded resources before starting another live run. To request a running driver's orderly cancellation, create its `driver/stop.request` file and allow cleanup to finish; killing the console cannot guarantee cloud cleanup.

Acceptance requires passing driver checks, confirmed zero temporary nodes, successful key cleanup, and passing runner network/process/service comparisons. It does not validate the installed SYSTEM service identity, production broker deployment, packaging, upgrades, real gameplay or graphical WPF rendering. No release/version change, service replacement, production credential edit, network-adapter/route/DNS change, or deployment is part of this procedure.

## SYSTEM-service mode

`Run-SystemServiceAcceptance.ps1` (Windows PowerShell 5.1, run elevated) is the short check of the
installed service identity. It needs the same focused builds and the staged credential, but no
precheck file, friend transport or probe. The driver's `--system-service` mode registers the
isolated Debug Agent as a disposable LocalSystem service, enrolls only the host, checks the service
and sidecar identities, pipe access, SYSTEM-only state, stop/restart behaviour and credential
removal, then deletes the service, the host device and its key. The runner then takes ownership of
the SYSTEM-owned state and removes it. Results: [SYSTEM-service acceptance](../../docs/CONNECT_SYSTEM_SERVICE_ACCEPTANCE.md).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File connect\proof\Run-SystemServiceAcceptance.ps1
```
