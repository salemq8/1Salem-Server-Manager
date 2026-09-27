# Real tsnet smoke test

Phase 1 acceptance remains `4bfb9a8` (58/58). Phase 2 updates below are compile-checked, not
rerun on the real tailnet. The full Agent-hosted Phase 2 acceptance additionally requires a
replacement OAuth credential with `policy_file:read`; do not change the existing accepted
credential or run live acceptance without Salem's separate authorization.

Development-only. Nothing here ships, and nothing here touches the installed Server Manager,
its Agent, Minecraft, Palworld, Playit, `C:\ProgramData\1SalemServerManager`, `artifacts\release`
or a real `%LOCALAPPDATA%\1Salem Connect`.

## What it proves

The fake-mode proof (`Run-ConnectProof.ps1`) uses loopback in place of the tailnet, and a node's
identity is self-asserted. Two properties cannot be shown that way.
This harness runs the same production components on the owner's real tailnet, with temporary
tagged nodes, to show them:

1. **Wrong-peer rejection by real Tailscale identity.** A third node (the probe) presents a
   genuine ticket that the broker bound to the friend's node. The host must refuse it, and the
   Agent-side log must name the probe's node id as WhoIs reported it (`WrongPeerNode`).
2. **The host-network fallback guard on a real tsnet connection.** tsnet dials an address that no
   peer owns over the host network. The probe has tsnet make such a connection (to a loopback
   witness) and shows that `requireNetstackSource`, which the production `Dial` runs before it
   hands out a connection, classifies it as a host-network connection. This does not reproduce the
   race in which a peer leaves between WhoIs and the dial, nor a host-network dial to a 100.x
   address; the probe itself never writes to the connection.

Along the way it repeats, over the tailnet, what the fake-mode proof shows: enrollment through the
friend app's own code, owner confirmation only after the real device and tag check,
a loopback-only local listener, traffic to the test
service, refusal of a forged ServerId, of a ServerId the host does not serve and of a request
naming a destination,
refusal of an address no peer owns and of a departed peer, revocation of a live connection, and no
change to Windows network settings. It also checks that the friend's node id is the same in the
enroll result, the friend transport's status, the broker's binding, the tailnet API and the host's
WhoIs.

**The port checks, precisely.** The probe dials the host on 25565, 8211, 8212, 5251, 3389, 445 and
the test service's port (only these ports). The host's tsnet has no listener there and never
forwards to localhost, so it refuses a connection whatever the policy says. A port therefore
passes "the tailnet policy drops TCP n" only when the dial times out (the policy dropped the SYN);
a refused dial means the policy let it through. The ports are tried only after the probe reached
the host bridge through the tailnet.

Components used (none are copies): the local broker (`wrangler dev --local`), the Agent's
`ConnectHostAuthorizationPipeServer` and `ConnectServerCatalog`, both Go sidecars in tsnet mode, the
friend app's `TransportProcess`, `PipeTransportClient`, `TransportServerVerifier`,
`EnrollmentCoordinator`, `SessionService`, `ConnectionViewModel` and `MainViewModel`, and the
production `TailscaleApiProvisioner` (friend and probe keys, and the device read the Agent uses).
The calls the provisioner deliberately cannot make (a host-tagged key, a device's full fields,
deletions that report their status, the tag-filtered device list) are in the test-only
`TsnetSmoke/SmokeTailnetApi.cs`. The probe is `connect/transport/internal/transport/smoke_test.go`,
compiled only with `-tags tsnetsmoke`.

## Tailnet policy

The approved tags are `tag:onesalem-host` and `tag:onesalem-client`. Salem's policy must contain,
before any live run:

```hujson
"tagOwners": {
  "tag:onesalem-host":   ["autogroup:admin"],
  "tag:onesalem-client": ["tag:onesalem-host"],
},
// in "grants": the client tag reaches the host bridge, and nothing else
{ "src": ["tag:onesalem-client"], "dst": ["tag:onesalem-host"], "ip": ["tcp:7780"] },
```

and no broader rule may match the client tag: the default allow-all's `"src": ["*"]` is narrowed
to `["autogroup:member"]`, so Salem's own devices keep their access and tagged nodes do not inherit
it. The probe's per-port checks show whether that holds (a policy drop is a timeout).

## Before a live run (Salem, in the admin console)

- **Tag names.** The defaults are `tag:onesalem-host` and `tag:onesalem-client`. The client tag
  must equal the production `TailscaleApiProvisioner.FriendTag` (the driver refuses anything
  else). Tailscale's own tag check (`tailcfg.CheckTag`) wants a letter right after `tag:`;
  preflight warns about any tag that breaks that rule.
- **Tailnet Lock must be off.** Open
  https://console.tailscale.com/admin/settings/device-management (read only; click nothing). If it
  offers "Disable Tailnet Lock", Tailnet Lock is on: do not run. A locked-out node still comes up,
  so every result would be meaningless; the driver also stops the run if the API reports a
  `tailnetLockError` for any node.
- **The OAuth client** (a trust credential), created by the tailnet Owner or an Admin at
  https://console.tailscale.com/admin/settings/trust-credentials: "+ Credential", OAuth, scopes
  exactly "Keys > Auth Keys" Write and "Devices > Core" Write (`auth_keys`, `devices:core`, which
  includes reading devices), and **only the host tag**. The host key then matches the client's tag
  set exactly, and friend and probe keys are allowed because the host tag owns the client tag.
  Copy the client ID and secret at once; the secret is not shown again.

## Commands

Run from the repository root in your own PowerShell.

```powershell
# 1. Offline preflight (the default). Contacts nothing, needs no credential.
powershell -NoProfile -ExecutionPolicy Bypass -File connect\proof\Run-TsnetSmoke.ps1

# 2. Store the OAuth client once. Prompts for the client ID and the secret (hidden, never shown).
powershell -NoProfile -ExecutionPolicy Bypass -File connect\proof\Set-TsnetSmokeCredential.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File connect\proof\Set-TsnetSmokeCredential.ps1 -Status

# 3. The live run on the real tailnet.
powershell -NoProfile -ExecutionPolicy Bypass -File connect\proof\Run-TsnetSmoke.ps1 -Live
```

Then do the teardown below.

Prerequisites: the repository toolchain in `.tools` (dotnet, go, node, and the NuGet package cache:
the driver is restored from it only, never from nuget.org), `connect\broker\node_modules`, and
both sidecars built with `connect\transport\build.ps1` (preflight checks that they record
`-tags=ts_omit_oauthkey`). No `TS_*` or `TSNET_*` variable may be set in the environment.

Options of `Run-TsnetSmoke.ps1`: `-WorkRoot` (a folder under `%TEMP%` that must not exist yet,
default `%TEMP%\1ts-<8 hex>`, kept short for MAX_PATH), `-BrokerPort` (default 8798),
`-KeepEvidence` (keep the logs in the work root too), `-HostTag` and `-ClientTag` (default: the
driver's `tag:onesalem-host` and `TailscaleApiProvisioner.FriendTag`).

The credential is stored DPAPI-encrypted for the current Windows user in
`%LOCALAPPDATA%\1Salem Connect Smoke\oauth-client.dpapi`, in a folder that only the current user
and SYSTEM can open. The runner never reads it; only the driver does, in memory, in live mode.

## What a live run creates

The cleanup deletes all of it; "Cleanup and deletion safety" below says what it cannot delete.

| What | Details |
|------|---------|
| Local broker | `wrangler dev --local` on `127.0.0.1:<BrokerPort>`, throwaway secrets and local D1 in the work root. Never deployed. |
| 3 auth keys | One-off, pre-authorized, non-ephemeral, 1 day expiry: one with exactly the host tag (`1salem smoke host <run id>`), two with the client tag minted by the production provisioner (the friend's and the probe's). |
| Host node | `1salem-smoke-host-<run id>`, host tag, bridge on TCP 7780 of its tailnet address. |
| Friend node | The friend app's own node, hostname `1salem-<device id>` as the app names it, client tag. |
| Probe node | `1salem-smoke-probe-<run id>`, client tag. |
| Local state | Owner and device identities, the friend app's data folder, the host's and probe's tsnet state, all in the work root. A test service (`SMOKE-GAME`) on a free loopback port. Private random pipe names only. |

## Cleanup and deletion safety

The driver's cleanup runs when the run ends, fails, or is interrupted with Ctrl+C (the run stops at
its next step, and further presses are refused until the cleanup is done). If the runner's time
budget runs out, it asks the driver to stop the same way (a `stop.request` file) and waits for the
cleanup before stopping it. The host transport and the probe run in a kill-on-close job, and the
friend transport in the app's own; the run stops if the app reports that it could not place its
transport in that job. A node cut off in the middle of its enrollment may never report its id: the
cleanup then lists it by hostname and cannot delete it. Closing the console window or killing the
process cannot be caught: the nodes then
stay on the tailnet, and `nodes.json` (in the work root, copied to `%TEMP%\1ts-last`) lists every
node and key the run created, as soon as it existed, and whether it was removed.

The cleanup deletes a tailnet device only if this run recorded its node id when it enrolled it
(or the node's own marker file recorded it with this run's hostname), and only after the API
shows exactly the expected tag, no `isEphemeral: true` (the API omits the field for a
non-ephemeral device) and a creation time after the run started
(5 minutes of clock skew allowed). It never lists devices in order to delete them. Every GET and
DELETE is recorded with its HTTP status, and a deletion is confirmed by a second GET returning 404.
A device that does not match, or that the API refuses to delete, is reported with how to remove it
by hand. Order: stop the friend app (which stops its transport) and the host transport; delete the
host node; delete the friend node; run the departed-peer probe (the probe node restarts from its
state and must be refused); delete the probe node; delete every minted key (404 means already used
up); list the devices that carry either tag (exact-match filters, no other device is read) to show
that none of the run's remain, including one with the run's hostname whose id was never recorded
(reported, never deleted); record the node evidence below; delete the state folders.

The runner waits for the driver (up to 15 minutes after Ctrl+C; a driver that overruns is stopped
and reported), then stops the broker and the sampler, compares the network settings, copies the
evidence out and deletes the work root with `rmdir /s` (which never follows a junction). With
`-KeepEvidence` the work root keeps its logs; secrets, D1 and node state are deleted anyway. A
failed preflight keeps its work root (build logs only) so the failure can be read.

## Teardown after the run

1. Open https://console.tailscale.com/admin/machines?q=managedby:tag:onesalem-host,tag:onesalem-client
   (with your tag names). It must list none of the run's devices. Remove any that are left:
   the device's ... menu, Remove, then Remove machine.
2. Keys the cleanup could not delete expire by themselves one day after they were minted.
3. Revoke the OAuth client: https://console.tailscale.com/admin/settings/trust-credentials, find it,
   Revoke, and Revoke again to confirm. Trust credentials do not expire, and revoking also revokes
   the access tokens it issued.
4. Remove the local copy:
   `powershell -NoProfile -ExecutionPolicy Bypass -File connect\proof\Set-TsnetSmokeCredential.ps1 -Remove`

## Log upload and port mapping evidence

What the harness records, and what it does not claim:

- **Log upload.** No log line proves that uploads are off, and none is claimed to. With
  `logtail.Disable()` tsnet writes nothing to its upload buffers, so the driver checks that
  `tailscaled.log1.txt` and `tailscaled.log2.txt` of the host and friend nodes are 0 bytes before
  the state is deleted. The probe is a `go test` binary, where tsnet starts no logger at all, so
  its buffers are expected to be absent. The runner checks the Windows DNS cache for `log.tailscale.com` and `log.tailscale.io`
  before, every 3 s during, and after the run; the cache is shared by the whole PC, so with an
  installed Tailscale client running (it uploads its own logs) or an entry cached before the run,
  that check is recorded as INCONCLUSIVE, which is not a pass. Only after the last look at the
  cache does the runner resolve both names and compare them with the sampled TCP connections of
  the sidecars and the probe: a match fails the run, no match is not proof.
- **Port mapping.** The transports' tsnet backend lines reach only their in-memory log, so the
  driver reads it every second throughout the run. For each node it requires
  `envknob: TS_DISABLE_PORTMAPPER="true"` (logged once when the node starts) and
  `netcheck: probePortMapServices: port mapping is disabled`, and it fails on any `portmapper: `
  line, on `Ignoring authkey`, `tkaSyncIfNeeded` and `this node is locked out`.
  `logpolicy: using LocalAppData dir ...` is expected and harmless (nothing is written there).
  Control traffic on TCP 80 and 443, DERP on TCP 443, STUN on UDP 3478, UDP to peers and WPAD
  lookups are expected.

Exit code: 0 only if every check passed, the cleanup was complete, the network settings did not
change, no sampled connection went to a Tailscale log host and the DNS-cache check did not fail
(an INCONCLUSIVE one is named in the result line). `2` from the driver means it refused its
arguments.

## Evidence (`%TEMP%\1ts-last`)

| File | Content |
|------|---------|
| `driver.log`, `driver.err.log`, `result.json` | Every check as `PASS\|FAIL  name  --  detail`; `result.json` has `mode: "tsnet (real tailnet, temporary tagged nodes)"`, the run id, the tags and the nodes. |
| `nodes.json` | Every node and key the run created, written the moment each existed, and whether it was removed. |
| `summary.json`, `runner.log` | The runner's view: preflight, driver exit, network result, DNS-cache result, failures. |
| `driver-build.log`, `probe-build.log`, `driver-arguments.log` | The preflight builds and the driver's dry run (tags and warnings). |
| `network-before.txt`, `network-after.txt`, `network-diff.txt` | `Get-NetworkIsolationSnapshot.ps1` before and after; the diff must be empty. |
| `connections.csv` | Every 3 s, the TCP connections owned by the two sidecars and the probe (local and remote address and port, state). |
| `dns-cache-before.txt`, `dns-cache.csv`, `dns-cache-after.txt` | Log-host entries in the Windows DNS cache before, during and after the run. |
| `log-hosts.txt`, `log-evidence.txt` | The log hosts resolved after the run, and the runner's comparison and notes. |
| `host-transport.log`, `host-transport-diag.log`, `friend-transport-diag.log`, `probe.log`, `probe-departed.log`, `probe-result.json`, `probe-departed.json` | The sidecars' and probe's own redacted logs and results. The friend transport's log is the source for the "tailnet path established" detail (DERP or direct), quoted only as the log states it. |

## Optional: UDP port-mapping capture (elevated, run it yourself)

The harness cannot see UDP port-mapping probes (NAT-PMP and PCP to UDP 5351, SSDP to UDP 1900)
without an elevated packet capture, and it does not do one. To check, in an elevated PowerShell
around a live run:

```powershell
pktmon filter remove
pktmon filter add NatPmpPcp -t UDP -p 5351
pktmon filter add Ssdp -t UDP -p 1900
pktmon start --capture --pkt-size 0 --file-name "$env:TEMP\1ts-portmap.etl"
# ... run Run-TsnetSmoke.ps1 -Live in another window, then:
pktmon stop
pktmon etl2txt "$env:TEMP\1ts-portmap.etl" --out "$env:TEMP\1ts-portmap.txt"
pktmon filter remove
```

pktmon does not name the sending process. Packets to your router on port 5351, or SSDP searches,
sent while the smoke run was active are the ones to look at; other programs on the PC (browsers,
media players) also use SSDP, so compare with a capture taken without the run. `pktmon filter
remove` at the end leaves no filters behind.
