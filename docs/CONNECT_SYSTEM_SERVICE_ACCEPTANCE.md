# Connect SYSTEM-service acceptance — complete

Date: 2026-09-29. Branch: `claude/connect-phase-2`. Starting HEAD: `b0d9d40`.

**SYSTEM-SERVICE ACCEPTANCE COMPLETE.** Run `1ps-bfb21a19` (driver run `ddb25dc43c60`) passed
**24/24 driver checks**. The installed Build 7 service, its data, Minecraft, Palworld and Playit were
not touched; no Cloudflare account was used.

## What ran

`connect/proof/Run-SystemServiceAcceptance.ps1` (Windows PowerShell 5.1, elevated) started a local
Wrangler broker with throwaway secrets and local D1, then the existing acceptance driver in its new
`--system-service` mode. The driver registered the isolated Debug acceptance Agent as a disposable,
uniquely named **LocalSystem** service (`OneSalemConnectSystemAcceptance<run id>`, manual start,
with the repository .NET runtime in the service's own `Environment` value), transferred the staged
OAuth credential in memory through the authenticated local API, and enrolled **only the host** in
the disposable test tailnet. No friend node was created.

## Results

| Check | Result | Evidence |
|---|---|---|
| Service starts under SYSTEM | PASS | Service process owner SID `S-1-5-18` |
| Connect initializes under SYSTEM | PASS | Credential saved (SYSTEM DPAPI), owner registered, host enrolled with `tag:onesalem-host`, bridge TCP 7780 and revocation channel up |
| Host sidecar | PASS | Exactly one `1Salem.Connect.Host.Transport.exe`, child of the service process, owner SYSTEM |
| Named-pipe identities | PASS | Agent pipe served by the service PID (`GetNamedPipeServerProcessId`); authorization and transport-control pipes refuse even an elevated administrator |
| Protected state | PASS | Credential, owner identity and the Connect folder listing refused to an elevated administrator |
| Shutdown cleans children | PASS | After each service stop, no Agent or host sidecar process remained |
| Restart recovery | PASS | Same owner id and host node after a service restart; the sidecar came back under the new service process; no new key minted |
| Credential removal | PASS | `DELETE /api/v1/connect/credential` HTTP 200 before the final stop |
| Cleanup | PASS | Disposable service deleted; host device deleted (Devices API GET 404); one-time key deleted; zero temporary nodes |
| Production isolation | PASS | Network, protected-process and service snapshots identical before and after |

## Defects found and fixed

The run found no product defect. Two runner defects were fixed and only the affected step was
repeated:

- Windows PowerShell 5.1 lost the driver's exit code (it must hold the process handle while the
  process runs). Fixed by opening the handle at start; checked with a process that exits 7.
- Removing the SYSTEM-owned state after ownership was taken failed because the ACL grant used
  inheritance flags that cannot apply to files. Fixed by `icacls /reset /T` after `takeown`, which
  returns every entry to the run root's inherited user+SYSTEM ACL. The corrected cleanup was run
  elevated on this run's leftover state and removed it.

The Debug acceptance host now calls `UseWindowsService` too (a no-op outside the Service Control
Manager) and never writes to the Event Log, so no event source is registered. The Agent acceptance
tests pass (48/48).

## Not covered

The installed Release binary, installer/upgrade paths and the production broker are outside this
run; the Agent was the isolated Debug acceptance build running as LocalSystem.
