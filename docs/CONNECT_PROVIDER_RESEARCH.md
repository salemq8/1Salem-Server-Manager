# 1Salem Connect — Provider Research (Build 8, Phase 1)

Researched **23 September 2026** from official primary sources: Tailscale documentation, API
reference (OpenAPI), Go package docs and source; Cloudflare developer docs; Microsoft Learn; IETF
RFCs; W3C Web Crypto; official game documentation. Blogs and forums appear only where marked.

## How to read this document

- **Verified official facts** are what the sources say, paraphrased with the exact URL. Each
  topic was researched by one agent and its load-bearing claims were then **re-fetched and
  checked by a separate, sceptical agent**. The verdict is shown on every fact: *confirmed*,
  *corrected* (the correction is quoted and is what we rely on), or *not independently
  re-checked*.
- **Our reading** sections are proposals for 1Salem Connect. They are **not** facts.
- The design itself is in `docs/CONNECT_ARCHITECTURE.md`, which cites fact ids from here.

## Verification summary

| | Count |
|---|---|
| Facts recorded | 258 |
| Confirmed by independent check | 95 |
| Corrected by independent check | 17 |
| Unsupported | 0 |
| Contradicted | 0 |
| Not independently re-checked | 146 |

## Findings that shaped the design

1. **Multi-owner.** Tailscale credentials act on one tailnet. Tagged devices cannot use
   cross-tailnet sharing. The Terms limit use to personal or internal business purposes and
   forbid resale. So one vendor-held OAuth credential cannot provision friends into many owners'
   tailnets without a commercial agreement (T3-05/06/14/15/17, T4-18, T7-23/24). Recommended:
   each owner's own tailnet, with the credential kept on the owner's PC (architecture §2).
2. **tsnet is userspace.** No adapter, no route or DNS changes (T1-04, T4-21). But `Dial`
   falls back to the host network for non-tailnet addresses (T1-17), so destinations must be
   validated before dialing.
3. **tsnet mints keys from OAuth secrets by default.** Any `tskey-client-` value is treated as
   an OAuth secret on every start (T1-11 as corrected). Build with `ts_omit_oauthkey`.
4. **Auth keys** are one-off and auto-revoked after use, but cannot be shorter than 1 day
   (T2-05, T2-06, T3-13). Revoking a key does not remove a node; deleting the device does
   (T2-07, T2-17).
5. **Policy** starts allow-all. Grants are additive. `tests` lock in least privilege (T4-05,
   T4-06, T4-15).
6. **Signatures**: Web Crypto ECDSA is P1363 r‖s, the same as JWS ES256. .NET and Go must be
   told explicitly (T6-03/13/14/23/30).
7. **Local development**: wrangler 4.137 on Node ≥ 22, workerd/Miniflare, D1 under
   `.wrangler/state`. The Vitest integration is now `@cloudflare/vitest-plugin` (T5-18..25).
8. **Named pipes**: the default ACL gives Everyone read. .NET never rejects remote clients.
   Deny NETWORK explicitly (T8-17..27).
9. **UDP through tsnet** replies only work from v1.102 (T4-08 as corrected). Relevant for
   Palworld, which is researched but not implemented.

## Findings from the Phase 1 build and security review (24–25 September 2026)

These were **not** researched from documentation. They were found while building and
security-reviewing Phase 1, and each was checked against the pinned source code in the local module
cache (`tailscale.com v1.102.4`, `wrangler 4.137`) or reproduced with a throwaway local test. They
are recorded here, with how they were checked, because the design in
`docs/CONNECT_ARCHITECTURE.md` now depends on them.

- **P1-01 tsnet decides netstack or host network again at dial time.** `tsdial.dialOneUser`
  re-evaluates `UseNetstackForIP` (`lb.PeerForIP` in tsnet) when `Dial` runs. If the peer has left
  the netmap since an earlier `WhoIs` check, the dial goes out on the host network (`SystemDial`,
  the peer dialer or a plain `net.Dialer`). A netstack connection's local address is one of the
  node's own Tailscale addresses (`UpdateNetstackIPs`); a host-network one is not. *Checked:*
  `net/tsdial/tsdial.go` and `wgengine/netstack/netstack.go` at v1.102.4. This also answers the T1
  open question below: the `Dial` fallback of T1-17 is present at v1.102.4. *Design:* architecture
  §9, local-address check after every tsnet dial.
- **P1-02 tsnet writes state as soon as it starts.** `tailscaled.state`, `tailscaled.log.conf` and
  `tailscaled.log*.txt` are written when `Start` runs, before `Up` succeeds (which can take up to
  90 s). A killed first enrollment therefore leaves state without success. *Checked:*
  `tsnet/tsnet.go` at v1.102.4. *Design:* §11, marker-less node directories are emptied, not
  trusted or refused.
- **P1-03 Replacing a file on Windows keeps the replaced file's DACL.** tsnet's file store writes
  through `atomicfile.WriteFile`, which on Windows uses `ReplaceFileW`; a pre-planted
  `tailscaled.state` keeps its own DACL after the node keys are written into it. *Reproduced* with
  a throwaway test (planted file readable by Everyone stayed readable). *Design:* §11, state
  directory owner and reparse checks before tsnet starts.
- **P1-04 DPAPI does not authenticate who wrote a blob.** `CryptUnprotectData` without extra flags
  opens a `CRYPTPROTECT_LOCAL_MACHINE` blob made by any local account, including from a
  current-user-scope caller. *Reproduced* in memory with `ProtectedData`. *Design:* §5, identity
  folder and file ownership checks.
- **P1-05 `wrangler dev --env-file` drops non-secret names when secrets are declared.** With
  `secrets.required` in `wrangler.jsonc`, wrangler 4.137 loads only the required secret names from
  env files (`getVarsForDev`); any other name is ignored. Local development settings must be passed
  with `--var NAME:value`. *Checked* in wrangler's source and by a failing local proof run.
  *Design:* the broker's `CONNECT_DEV_LOOPBACK_BRIDGE` setting.
- **P1-06 A named-pipe server's identity can be checked from the client.**
  `GetNamedPipeServerProcessId` returns the serving process; the client can open it with
  `PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE` and read its image path and token integrity
  through that one handle. The id-to-handle step is the only unavoidable gap. *Checked* with live
  tests against a real child process. This answers, for the client side, the T8 open question on
  checking the other end of a pipe. *Design:* §11, friend app pipe verification.

## T1. tsnet (embedded Tailscale node)

_Scope as researched:_ T1: tailscale.com/tsnet as an embedded node (version, Server fields/methods, Windows/userspace behaviour, TCP/UDP, state and identity, ephemeral nodes, peer identity via WhoIs, Go/CGO requirements)

### Verified official facts

- **T1-01** The latest stable tailscale.com module (which contains tsnet) is v1.102.4, published Sep 10, 2026. v1.103.0-pre is a pre-release. v1.102.0 came out Jul 23, 2026.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet?tab=versions> — The versions tab lists v1.102.4 (Sep 10, 2026), v1.102.3 (Aug 19), v1.102.2, v1.102.1, v1.102.0 (Jul 23) and v1.103.0-pre.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T1-02** Minimum Go version: go.mod at tag v1.102.4 declares 'go 1.26.6'. main (Sep 2026) declares 'go 1.27.1' with no toolchain line. The README says Tailscale always requires the latest Go release, currently 1.27.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/v1.102.4/go.mod> — Tag go.mod has 'module tailscale.com' and 'go 1.26.6'. main go.mod (raw.githubusercontent.com/.../main/go.mod) has 'go 1.27.1'. The README states the latest Go is always required.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: The go.mod lines are right: v1.102.4 declares 'go 1.26.6' and main declares 'go 1.27.1'. Neither file has a toolchain line. The README quote is only true on main, which says the latest Go release is 'currently Go 1.27'. The README at tag v1.102.4 says 'currently Go 1.26'. So pinning v1.102.4 needs Go >= 1.26.6, not 1.27. Only main or v1.103+ needs 1.27.1.
- **T1-03** tsnet embeds a full Tailscale node inside a Go process. It joins a tailnet and accepts or dials connections without a tailscaled daemon or any system-level configuration. It uses gVisor's userspace TCP/IP stack and needs no root/admin rights.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet> — Package doc: joins a tailnet without a separate tailscaled daemon or system-level configuration. It uses a gVisor userspace stack. The README adds that no root and no system daemons are needed.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T1-04** By default tsnet creates no TUN/Wintun adapter and changes no routes or OS DNS. start() passes wgengine.Config{Tun: s.Tun, ...} with no Router or DNS set. NewUserspaceEngine then uses a fake TUN (tstun.NewFake), a fake no-op Router and a no-op DNS configurator. A real device is used only if the caller sets the Server.Tun field.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/wgengine/userspace.go> — Config docs: a nil Tun means a fake Device that does nothing. A nil Router means a fake Router. A nil DNS means a fake OSConfigurator. tsnet.go sets only Tun: s.Tun and never sets Router or DNS.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T1-05** netstack is created with ProcessLocalIPs=true and ProcessSubnets=true. When Tun is nil, TCP listeners are served by tsnet's internal listener registry instead of an OS socket. Only a custom Tun makes Listen build a gVisor TCP listener directly.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — start() calls netstack.Create(...) and sets ns.ProcessLocalIPs = true and ns.ProcessSubnets = true. listen() calls s.listenTCP only when s.Tun != nil && isTCP.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T1-06** Server struct fields: Dir, Store (ipn.StateStore), Hostname, UserLogf, Logf, Ephemeral, AuthKey, ClientSecret, ClientID/IDToken/Audience (workload identity), ControlURL, RunWebClient, Port (uint16), AdvertiseTags, Tun. Exported fields may be changed only until the first method call.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet> — pkg.go.dev v1.102.4 lists these fields. The source doc on type Server says its exported fields may be changed until the first method call.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T1-07** Method signatures on *Server: - Start() error; Up(ctx) (*ipnstate.Status, error) - Listen(network, addr) (net.Listener, error); ListenPacket(network, addr) (net.PacketConn, error); ListenTLS(network, addr) (net.Listener, error) - ListenFunnel(network, addr, opts...); ListenService(name, mode); ListenSSH(addr) - Dial(ctx, network, address) (net.Conn, error); HTTPClient() *http.Client; LocalClient() (*local.Client, error) - TailscaleIPs() (ip4, ip6 netip.Addr); CertDomains() []string; Close() error - Loopback() (addr, proxyCred, localAPICred string, err error) - Also: CapturePcap, GetRootPath, RegisterFallbackTCPHandler, Sys
  - Source: <https://pkg.go.dev/tailscale.com/tsnet> — The index for v1.102.4 lists these methods and signatures. Listen, Dial and LocalClient implicitly call Start.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: All listed signatures are right, but LogtailWriter() io.Writer is missing. It writes to Tailscale's logging service. Full return types: ListenFunnel(network, addr string, opts ...FunnelOption) (net.Listener, error); ListenService(name string, mode ServiceMode) (*ServiceListener, error); ListenSSH(addr string) (net.Listener, error); CapturePcap(ctx, pcapFile string) error; GetRootPath() string; RegisterFallbackTCPHandler(cb FallbackTCPHandler) func(); Sys() *tsd.System. Dial and Listen start the server automatically if it is not started yet.
- **T1-08** State location: Dir defaults to os.UserConfigDir()/tsnet-<binary name>, which is %AppData%\tsnet-<prog> on Windows. start() creates it with os.MkdirAll(0700). If Store is nil, a FileStore is created at <Dir>/tailscaled.state. The same directory also holds tailscaled.log.conf and a filch log buffer with the 'tailscaled' prefix. GetRootPath() returns the directory.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — Code: rootPath = filepath.Join(confDir, "tsnet-"+prog) and os.MkdirAll(rootPath, 0700). stateFile is <root>/tailscaled.state. It also writes tailscaled.log.conf and filch.New(<root>/tailscaled). os.UserConfigDir returns %AppData% on Windows.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T1-09** The state store persists the node's machine key and login profiles, under keys such as _machinekey, _profiles and _current-profile. Restarting with the same Dir/Store therefore reloads the same node identity instead of registering a new node.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/ipn/store.go> — MachineKeyStateKey stores the machine key. KnownProfilesStateKey stores the JSON list of LoginProfiles. CurrentProfileStateKey stores the current profile. The StateStore interface is ReadState/WriteState.
  - Confidence: medium · load-bearing for our design · Verification: **confirmed** by independent check
- **T1-10** If AuthKey is set but stored state exists and the backend is not in NeedsLogin, the key is ignored. tsnet only logs 'Authkey is set; but state is X. Ignoring authkey'. The key is used only if TSNET_FORCE_LOGIN=1.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — start(): if state is NeedsLogin or TSNET_FORCE_LOGIN, it calls StartLoginInteractive. Else, if authKey != "", it logs 'Ignoring authkey. Re-run with TSNET_FORCE_LOGIN=1'. The README says the same.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T1-11** Auth precedence is the AuthKey field, then TS_AUTHKEY, then TS_AUTH_KEY. With the OAuth hook compiled in, an empty AuthKey falls back to ClientSecret, then TS_CLIENT_SECRET, and resolveAuthKey mints a key using AdvertiseTags. ClientSecret needs the auth_keys write scope and at least one tag. It is not used once the node already exists.
  - Source: <https://tailscale.com/docs/reference/tsnet-server-api> — The docs say ClientSecret defaults to TS_CLIENT_SECRET, needs auth_keys write scope and at least one AdvertiseTags tag. Source getAuthKey checks AuthKey, TS_AUTHKEY, TS_AUTH_KEY in that order.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: getAuthKey order is right: the AuthKey field, then TS_AUTHKEY, then TS_AUTH_KEY. The cited KB page only mentions TS_AUTHKEY. Four corrections. (1) The OAuth hook is compiled in by default and can be omitted only with the ts_omit_oauthkey build tag. (2) Any AuthKey or ClientSecret value starting with 'tskey-client-' is treated as an OAuth secret. (3) resolveAuthKey runs on EVERY Start, before the state check. With a client secret, each start mints a new key through the API even when the node already exists, and the key is then ignored. (4) Minted keys default to ephemeral=true, preauthorized=false and reusable=false. The KB confirms the OAuth client needs the auth_keys write scope and at least one AdvertiseTags value.
- **T1-12** ControlURL defaults to the TS_CONTROL_URL environment variable, then to Tailscale's default control server. Several tsnet settings can therefore be injected through the process environment (TS_AUTHKEY, TS_AUTH_KEY, TS_CLIENT_SECRET, TS_CONTROL_URL, TSNET_FORCE_LOGIN).
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — getControlURL returns s.ControlURL if set, else os.Getenv("TS_CONTROL_URL"). The field doc says an empty value falls back to the env var, then the Tailscale default.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T1-13** Ephemeral=true makes tsnet log in with controlclient.LoginEphemeral. An in-memory store (store/mem) is allowed only for ephemeral nodes; a non-ephemeral node with mem.Store returns an error.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — Code: if s.Ephemeral, loginFlags = LoginEphemeral. If isMemStore && !s.Ephemeral, it returns the error 'in-memory store is only supported for Ephemeral nodes'.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T1-14** Ephemeral devices are normally removed 30 to 60 minutes after their last activity. A new ephemeral node gets a different IP. 'tailscale logout' removes one immediately. tsnet's Close() does not log the node out; it only shuts down listeners, netstack and the backend.
  - Source: <https://tailscale.com/kb/1111/ephemeral-nodes> — The KB (updated Dec 4, 2025) gives 30–60 minutes auto-removal and says logout removes immediately. tsnet.go close() has no Logout call.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T1-15** Listen accepts the network values "", tcp, tcp4, tcp6 (udp variants also pass the switch). A listener with no IP (e.g. ":25565") matches only traffic to this node's own Tailscale IPv4/IPv6. Subnet-routed traffic needs an explicit address or RegisterFallbackTCPHandler.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — The Listen doc says listeners without an IP match only the node's own IPv4/IPv6 destination. The switch accepts "", tcp, tcp4, tcp6, udp, udp4 and udp6.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T1-16** UDP is supported through ListenPacket. network must be udp, udp4 or udp6, and addr must be 'ip:port' with an explicit, valid IP; an empty host is rejected. Port 0 is allowed (gVisor assigns one). The node's IP must first be learned from TailscaleIPs() or Up().
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — The doc says the IP must be specified. The code errors 'address must be a valid IP' when the host is empty, and uses gVisor's port when port 0 is requested.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T1-17** Dial starts the server, waits until it is Running, then calls tsdial.UserDial. Both TCP and UDP ('udp*' uses NetstackDialUDP) are supported. If the destination is not a Tailscale route, UserDial falls back to SystemDial, i.e. the host's normal network.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/net/tsdial/tsdial.go> — dialOneUser uses NetstackDialUDP/TCP when UseNetstackForIP matches. Otherwise, if the route table lookup is not a Tailscale route, it returns d.SystemDial(...). tsnet.Dial calls awaitRunning, then UserDial.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T1-18** To identify an incoming peer, call LocalClient().WhoIs(ctx, remoteAddr), where remoteAddr is an IP or IP:port. It returns *apitype.WhoIsResponse{Node *tailcfg.Node, UserProfile *tailcfg.UserProfile, CapMap}; Node and UserProfile are never nil on success. Failures return ErrPeerNotFound. WhoIsProto(ctx, proto, addr) and WhoIsNodeKey also exist.
  - Source: <https://pkg.go.dev/tailscale.com/client/local> — v1.102.4 signatures: WhoIs(ctx, remoteAddr string). ErrPeerNotFound is returned when lookup fails. The apitype docs say Node and UserProfile are never nil on success. The tshello example calls WhoIs(r.Context(), r.RemoteAddr).
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T1-19** For tagged peers, check the WhoIs result's Node.Tags, Node.StableID and Node.ID, not UserProfile. Node.User is the creating user and does not reflect the ACL identity of a tagged node. IsTagged() reports whether the node has any tags.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tailcfg/tailcfg.go> — Node.User doc: with ACL tags in use, it doesn't reflect the ACL identity the node runs as. Tags use the form tag:<value>. IsTagged returns len(n.Tags) > 0.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T1-20** AdvertiseTags asks for tags, but control may refuse them. With OAuth or workload-identity auth, AdvertiseTags is required.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — Field doc: advertising a tag on the client doesn't guarantee that the control server will allow the node to adopt it.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T1-21** Up(ctx) watches the IPN bus until the state is Running. It errors on any backend ErrMessage or if Running comes with no IP. On first success it also clears the node's serve config and Service advertisements, then returns *ipnstate.Status.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — Up loops on watcher.Next(). It returns an error for n.ErrMessage, returns status when state == Running, and uses resetServeStateOnce to clear ServeConfig and AdvertiseServices.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T1-22** Close must not be called before or concurrently with Start. A second call returns an error wrapping net.ErrClosed. It closes listeners, netstack, the backend, netmon and the dialer, and flushes logtail with a 5-second timeout. The docs say this is like turning Tailscale off; the node stops answering pings.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — Close doc: 'must not be called before or concurrently with Start'. closeOnce returns 'tsnet: net.ErrClosed' on a repeat call. close() uses a 5s context.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T1-23** Loopback() opens a host listener on 127.0.0.1:0. It serves a SOCKS5 proxy into the tailnet (username 'tsnet', random proxyCred) and a write-enabled LocalAPI, which needs the Sec-Tailscale: localapi header plus basic auth with localAPICred. RunWebClient=true serves a management UI on port 5252 over the Tailscale interface.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — The Loopback code uses net.Listen("tcp","127.0.0.1:0"), PermitWrite=true, RequiredPassword=localAPICred and socks5 Username "tsnet". The RunWebClient doc gives port 5252.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T1-24** ListenTLS supports only tcp. It calls Up and requires MagicDNS and HTTPS to be enabled in the tailnet admin panel. CertDomains returns nil when the node is not running.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — ListenTLS returns 'only tcp is supported' for other networks. It errors unless MagicDNSEnabled is true and CertDomains is non-empty. CertDomains returns nil if the netmap is nil.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T1-25** Windows and CGO: CI cross-builds ./cmd/... and compiles every package's tests (./...) for windows/amd64 and windows/arm64 with CGO_ENABLED=0, so tsnet compiles for Windows without cgo. A separate Windows job runs sharded tests on a Windows 2022 runner. No official tsnet doc page states a Windows support tier.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/.github/workflows/test.yml> — The 'cross' matrix includes goos windows amd64/arm64 with env CGO_ENABLED "0" and runs 'go test -exec=true ./...'. The 'windows' job runs on ci-windows-github-1 (2022 runner).
  - Confidence: medium · load-bearing for our design · Verification: not independently re-checked
- **T1-26** On Windows the tsnet dependency graph still links golang.zx2c4.com/wintun and wireguard/windows/tunnel/winipcfg, both marked Windows-only and unsafe/cgo. Per T1-04 they are not used unless a custom Tun is supplied.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/depaware.txt> — depaware lines: 'W 💣 golang.zx2c4.com/wintun from wireguard-go/tun' and 'W 💣 .../winipcfg from tailscale.com/net/dns+'. The depaware README says the bomb marks unsafe/cgo.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T1-27** Host-level side effects that remain: tsnet binds a real host UDP port for WireGuard and peer traffic (Port field; auto-selected if zero). It also imports the portmapper and useproxy features by default; these can be removed with the build tags ts_omit_portmapper and ts_omit_useproxy.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/feature/condregister/portmapper/maybe_portmapper.go> — The file has //go:build !ts_omit_portmapper and imports feature/portmapper. useproxy.go uses //go:build !ts_omit_useproxy. The tsnet.go import block includes both condregister packages. Port doc: UDP port for WireGuard.
  - Confidence: medium · load-bearing for our design · Verification: not independently re-checked
- **T1-28** Logging: UserLogf defaults to log.Printf. The source says an unset Logf discards backend logs, but the reference docs say Logf defaults to log.Printf. Unless running in tests, tsnet starts logtail, which uploads compressed logs to Tailscale's log service (logtail.DefaultHost) using an ID kept in Dir/tailscaled.log.conf.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — Field doc: 'If unset, logs are discarded.' startLogger returns early only for testenv.InTest(). It builds logtail.Config with HTTPC to logtail.DefaultHost.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T1-29** Multiple tsnet.Server instances can run in one process. Each one is an independent node and needs its own Dir and Hostname.
  - Source: <https://tailscale.com/docs/reference/tsnet-server-api> — Docs (validated Jul 7, 2026): multiple instances need distinct state directories. Source: each Server is an independent node; give each a unique Dir and Hostname.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T1-30** Performance: GitHub issue #9707 (Oct 2023) reported tsnet throughput far below Windows tailscaled (about 4 vs 35 Mbps) on the same host. It is now closed with linked PRs #21303 and #21025. Current Windows tsnet throughput is not documented.
  - Source: <https://github.com/tailscale/tailscale/issues/9707> — The issue 'Slow throughput over tsnet compared to Windows tailscaled' (Windows 11, tsnet 1.48) is closed and references PRs #21303 and #21025.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T1-31** KB 1244 (tsnet overview, updated Jul 24, 2026) covers only basics: go get, Hostname, Start/Listen/Close, and auth keys to pre-approve devices. Field and method detail lives at /docs/reference/tsnet-server-api. Neither page documents binary size or CGO.
  - Source: <https://tailscale.com/kb/1244/tsnet> — Sections: Overview, Include tsnet, Make calls with tsnet.Server, Device creation and authentication, Additional information. It links to /docs/reference/tsnet-server-api.
  - Confidence: high · Verification: not re-checked (not load-bearing)

### Additional facts found by the verifier

- The built-in OAuth key minting always creates the key in the OAuth client's OWN tailnet. By default the key is single-use (reusable=false), ephemeral=true and preauthorized=false. You can change ephemeral, preauthorized and baseURL only by appending '?ephemeral=..&preauthorized=..&baseURL=..' to the tskey-client- secret. A secret shipped to a friend's machine can mint keys on every start, so client secrets must stay in the Worker and never reach any sidecar. The friend sidecar can be built with -tags ts_omit_oauthkey.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/feature/oauthkey/oauthkey.go> — resolveAuthKey uses tailscale.NewClient("-") and calls CreateKey with Reusable:false. Defaults are ephemeral true, preauthorized false. condregister includes it unless ts_omit_oauthkey is set.
- By default tsnet uploads backend logs to Tailscale's log service (logtail.DefaultHost) under a log ID kept in <Dir>/tailscaled.log.conf. Per the KB, client logs include open/close events for every connection. Setting TS_NO_LOGS_NO_SUPPORT=true in the sidecar's environment turns the upload transport into a no-op. This affects privacy disclosure for friends.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/logpolicy/logpolicy.go> — TransportOptions.New returns noopPretendSuccessTransport when envknob.NoLogsNoSupport(). tsnet startLogger builds a logtail.NewLogger that only skips uploading inside tests (testenv.InTest).
- For Palworld UDP, ListenPacket needs an explicit IP: the address must be 'ip:port' with network udp/udp4/udp6. The owner-side bridge must first get the address from TailscaleIPs() after Up(), or use Listen("udp", ":port") for per-flow conns.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet> — The ListenPacket doc says the addr must be ip:port where ip is a valid IPv4/IPv6 address, and states 'IP must be specified'.
- tsnet does no per-listener authorization. Reachability comes from the tailnet policy. The host bridge can identify the dialing node or user with LocalClient().WhoIs(ctx, remoteAddr) and check that against the session ticket. For revocation, LocalClient().Logout(ctx) removes an ephemeral node right away, which Close() does not do.
  - Source: <https://pkg.go.dev/tailscale.com/client/local> — v1.102.4: WhoIs(ctx, remoteAddr) returns the owner of an IP or IP:port, and Logout(ctx) logs out the current node.
- Ephemeral nodes can only be created with ephemeral auth keys (or state=mem). Ephemeral usage is free only up to a per-plan monthly limit counted in minutes. A node present for 4 hours or more counts as a standard tagged device against the owner's plan, which matters for long game sessions.
  - Source: <https://tailscale.com/kb/1111/ephemeral-nodes> — The KB (validated Dec 4, 2025) says usage is free up to a monthly minute limit, and nodes present four or more hours count as a standard tagged device.
- tsnet imports the portmapper by default (unless built with ts_omit_portmapper). The portmapper maps UDP ports over NAT-PMP, UPnP and PCP, so the sidecar may ask the home router for port mappings. It also binds a real host UDP socket (Port, or a random port), which can matter for Windows Firewall prompts. Whether tsnet actually triggers mappings at runtime is inferred; the docs do not say.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/feature/condregister/portmapper/maybe_portmapper.go> — The file has //go:build !ts_omit_portmapper and a blank import of feature/portmapper. The portmapper package doc says it maps over NAT-PMP, UPnP and PCP.

### Our reading (proposed, not an official fact)

- Pin tailscale.com to v1.102.4 and build the Go sidecar with Go 1.26.6 or newer (1.27.x if we track main). Build with CGO_ENABLED=0 for windows/amd64, which matches Tailscale's own Windows cross-build CI, so no MSYS/MinGW toolchain is needed.
- The default tsnet configuration fits the 'do not route the friend's whole PC' rule: no Wintun adapter, no route changes, no OS DNS changes (fake TUN, router and DNS). Never set Server.Tun.
- The sidecar still binds a host UDP port for WireGuard/magicsock. Consider a fixed Port and plan for a possible Windows Firewall prompt. Consider building with ts_omit_portmapper so it does not ask the user's router for UPnP/NAT-PMP mappings (this costs some direct-path success, so measure it).
- Identity is the state directory: <Dir>/tailscaled.state holds the machine key and profiles. Use a separate Dir per (owner tailnet, role), locked down with Windows ACLs. Copying the directory clones the node, so never log or export it.
- An AuthKey is used only when there is no state yet. With existing state it is silently ignored, apart from a log line. The launcher must detect the case where an invite key targets a different tailnet than the stored state (compare Up() status with the expected tailnet/node) and then use a fresh Dir. Do not rely on TSNET_FORCE_LOGIN.
- Friend side: Ephemeral=true plus mem.Store leaves no disk state. Each session then needs a fresh auth key, and the node gets a new IP each time. Call LocalClient().Logout before Close, because Close alone leaves the node listed for 30–60 minutes. Session tickets must not bind to a fixed friend IP.
- Owner bridge: Listen("tcp", ":<port>") for Minecraft only matches this node's own Tailscale IPs. For Palworld, ListenPacket needs an explicit IP, so call Up() or TailscaleIPs() first and listen on "<100.x.y.z>:<port>".
- On every accepted TCP connection and every new UDP source address, call LocalClient().WhoIs or WhoIsProto("udp", addr). Authorize on Node.StableID and Node.Tags checked against the D1-approved session, not UserProfile. Cache results per UDP source and drop on ErrPeerNotFound.
- Friend side: validate that the dial target is exactly the ticket's owner Tailscale IP inside 100.64.0.0/10 or fd7a:115c:a1e0::/48 before calling tsnet Dial. UserDial falls back to the host network (SystemDial) for non-tailnet destinations.
- The C# host should start the sidecar with an explicit, scrubbed environment: remove TS_AUTHKEY, TS_AUTH_KEY, TS_CLIENT_SECRET, TS_CONTROL_URL and TSNET_FORCE_LOGIN, and pass the auth key through a pipe or stdin rather than the command line. Set AuthKey and ControlURL explicitly.
- Never give ClientSecret or other OAuth material to a sidecar. tsnet can mint keys from an OAuth secret, but for us that belongs only in the Cloudflare Worker, which should hand out single-use, pre-authorized, tagged auth keys.
- Keep RunWebClient=false. Avoid Loopback(); if it is ever used, treat its localAPICred as a secret, since it grants write access to the LocalAPI.
- Set Logf and UserLogf to our own redacting logger. Decide on and disclose Tailscale logtail uploads; see the open question on disabling them.
- Budget for userspace netstack overhead. Benchmark UDP (Palworld) and TCP (Minecraft) throughput and latency through tsnet on Windows before committing (see T1-30).

### Risks noted

- Minimum Go version moves up often: main already requires Go 1.27.1, and the README says the latest Go is always required. Sidecar builds must follow Tailscale's Go version or stay pinned to an older tsnet release.
- A stale state Dir silently ignores a new AuthKey, so a friend could stay attached to the wrong tailnet or node. Without explicit checks this breaks the many-owners model.
- tsnet Dial can reach the open internet through the host network for non-Tailscale destinations. A bug in destination validation would turn the friend sidecar into a general forwarder.
- By default the node uploads logs to Tailscale's log service and may try UPnP/NAT-PMP port mapping on the user's router. Both are privacy or consent concerns for a consumer app.
- Ephemeral nodes stay listed 30–60 minutes after disconnect unless logged out explicitly, and tsnet Close() does not log out. This can pile up devices and addresses in an owner's tailnet.
- gVisor userspace networking has historically shown lower throughput on Windows than kernel/Wintun Tailscale (issue #9707, now closed). Game-traffic performance is unverified.
- The docs contradict the source in places. The docs say Dir must already exist, but the code runs MkdirAll. The docs say Logf defaults to log.Printf, but the source says logs are discarded. The source was read from main, which may differ slightly from v1.102.4.
- Wintun and winipcfg code is linked into the Windows binary even though it is unused. That affects binary size and could draw antivirus heuristics; size is not documented and must be measured.
- AdvertiseTags does not guarantee the tags are applied; control may reject them. Authorization must be based on the tags WhoIs actually reports, not the requested ones.

### Open questions

- Does TS_NO_LOGS_NO_SUPPORT, or anything else, disable tsnet's own logtail upload? logpolicy.New checks envknob.NoLogsNoSupport, but tsnet's startLogger builds logtail directly and only skips it in tests. logtail.Disable() exists process-wide, and a ts_omit_logtail-style build feature is implied by buildfeatures.HasLogTail. Both need checking against v1.102.4.
- Is the tsnet.go/tsdial.go behaviour read from main (Sep 2026) identical at tag v1.102.4? In particular: ListenPacket port-0 handling, Up() clearing serve config, and the Dial fallback.
- Does Windows Defender Firewall prompt when the sidecar binds its magicsock UDP port? Does a fixed Port avoid repeated prompts across updates or binary paths? Not documented.
- What exactly does the default-on portmapper do on a home router (UPnP/NAT-PMP/PCP), and how much does ts_omit_portmapper reduce direct connections versus DERP relay?
- What does feature/useproxy do in tsnet on Windows (e.g. reading WinHTTP/IE proxy settings for control and DERP connections)? Presumably it only reads settings, but that is not verified.
- What is the maximum UDP datagram size or MTU through tsnet netstack, and how do latency and jitter look for Palworld-style UDP? Not documented.
- For UDP, is WhoIs(ctx, "ip:port") enough, or is WhoIsProto("udp", ...) needed for correct peer lookup? The docs do not spell this out.
- How does the OAuth resolveAuthKey hook treat a normal tskey-auth- key passed through AuthKey? It is presumably passed through unchanged, but that is not confirmed from the feature/oauthkey source.
- Can one tsnet state Dir hold profiles for several tailnets and switch between them, or is one Dir per tailnet required? Profiles exist in the StateStore, but tsnet has no documented profile-switching API.
- How large is a minimal Windows tsnet sidecar binary (with or without -ldflags -s -w and ts_omit_* tags)? Not documented for tsnet; must be measured.
- Is there an official statement of tsnet's Windows support level? None was found in the KB or reference docs; the evidence is CI builds and tests only.
- Once a friend's Ephemeral node has disconnected, how quickly does WhoIs stop resolving it, and does a revoked or deleted node's existing tsnet connection drop promptly? This matters for revocation semantics.

## T2. Auth keys and device management API

_Scope as researched:_ T2: Tailscale auth keys and device management via the API (create/revoke auth keys, one-off/ephemeral/pre-authorized/tagged semantics, key vs node-key expiry, device list/delete/authorize/tag, mapping key -> device). Researched 2026-09-23 against the live OpenAPI spec behind tailscale.com/api (version v2) plus KB pages 1085 (updated Jun 30 2026), 1111 (Dec 4 2025), 1028 (Jan 5 2026), 1068 (Dec 4 2025), 1099 (Jan 5 2026), 1215 (Jun 30 2026), 1623 (Jan 30 2026), 1213 (Jan 5 2026), and tsnet v1.102.4 (Sep 10 2026).

### Verified official facts

- **T2-01** Create an auth key with POST https://api.tailscale.com/api/v2/tailnet/{tailnet}/keys. Body: keyType (defaults to "auth"), description, capabilities.devices.create.{reusable, ephemeral, preauthorized, tags}, expirySeconds. The smallest valid auth-key body is capabilities.devices = {}, which produces a single-use key with no tags.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — createKey operation: auth keys need a capabilities object with a devices object, which may be empty. With nothing else set, you get a single-use key with no tags.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-02** The create response is a Key object with these fields: id (e.g. k123456CNTRL), key (secret tskey-auth-..., returned only at creation), keyType, expirySeconds, created, updated, expires, revoked (date-time), capabilities, description, invalid, and userId (empty for keys made by a trust credential). The full key cannot be fetched again later.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — Key schema lists those fields. 'key' holds secret material that is only populated at creation. createKey says the full key cannot be retrieved after the first response.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-03** Auth keys created with an OAuth-derived token are owned by the tailnet, not a user. They MUST carry tags, and those tags must exactly match the OAuth client's tags or be tags owned by them. Tags are optional on keys made with a user token.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — KeyCapabilities.tags: OAuth-owned keys must have tags matching the OAuth client's tags or tags those tags own. For user-owned keys, tags are optional.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-04** Creating auth keys needs an OAuth client with the auth_keys scope, and that client must have one or more tags. Clients get access tokens from https://api.tailscale.com/api/v2/oauth/token. Each access token lasts one hour, and this cannot be changed.
  - Source: <https://tailscale.com/kb/1215/oauth-clients> — KB 1215 (updated Jun 30 2026): an auth_keys-scoped client requires one or more tags. Access tokens expire after one hour and this cannot be modified.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-05** A one-off (reusable=false) key can register exactly one device. Tailscale automatically revokes it after first use, so a used one-off key cannot register another device.
  - Source: <https://tailscale.com/kb/1085/auth-keys> — KB 1085 (updated Jun 30 2026): one-off keys connect a device only once and are automatically revoked after use.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-06** The KB says auth key expiry can be set from 1 to 90 days inclusive, with 90 days the default and maximum. The API only says expirySeconds (int64) defaults to 90 days; it documents no minimum or maximum in seconds.
  - Source: <https://tailscale.com/kb/1085/auth-keys> — KB: choose between 1 and 90 days inclusive; no value means 90 days. OpenAPI expirySeconds: 'Defaults to 90 days if not supplied', with no stated bounds.
  - Confidence: medium · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-07** Revoking or expiring an auth key does NOT deauthorize devices already registered with it. Those devices stay authorized until their node key expires. To cut a device off, you must delete it.
  - Source: <https://tailscale.com/kb/1085/auth-keys> — KB 1085: revoking a key does not deauthorize nodes using it; delete the node from Machines. After an auth key expires, its devices stay authorized until node-key expiry.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: KB 1085 supports the core claim. Devices authorized by an expired key stay authorized until their node key expires, and revoking a key does not deauthorize nodes using it. The KB says to delete a node from the Machines page to deauthorize it. But "must delete" is too strong. The API also has POST /device/{id}/authorized {authorized:false}, described as working for tailnets where device authorization is required, and POST /device/{id}/expire. DELETE /device/{deviceId} (devices:core) is the definitive cut-off. For tagged devices, key expiry is disabled by default, so a device authorized by an expired key effectively stays authorized forever.
- **T2-08** Revoke or delete an auth key with DELETE /api/v2/tailnet/{tailnet}/keys/{keyId} (OAuth scope auth_keys). GET on the same path (scope auth_keys:read) returns invalid=true for a revoked (deleted) or expired key.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — deleteKey deletes an api access token or auth key and returns 403 if access is insufficient. getKey: a revoked (deleted) or expired key has invalid set to true.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-09** A preauthorized ('pre-approved') key only matters when device approval is enabled on the tailnet; devices registered with it skip admin approval. A device still awaiting approval cannot send or receive tailnet traffic. Whether approval is on shows in the tailnet setting devicesApprovalOn.
  - Source: <https://tailscale.com/kb/1099/device-approval> — KB 1099 (Jan 5 2026): the Pre-approved option exists only when device approval is enabled. A device awaiting approval cannot send or receive traffic.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-10** Approve or deauthorize a device with POST /api/v2/device/{deviceId}/authorized and body {"authorized": true\|false} (scope devices:core). It returns 402 when the plan or billing state does not allow authorizing more devices.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — authorizeDevice: true authorizes or re-authorizes, false deauthorizes. A 402 means the plan or billing state does not allow authorizing more devices.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-11** Ephemeral nodes are removed automatically, normally 30 to 60 minutes after their last activity, and come back with a new IP next time. 'tailscale logout' removes them immediately. They can only be created with ephemeral auth keys or state=mem:. A node present for 4 or more hours is billed as a standard tagged device, not ephemeral minutes.
  - Source: <https://tailscale.com/kb/1111/ephemeral-nodes> — KB 1111 (Dec 4 2025): auto-removed 30–60 minutes after last activity. A new ephemeral node gets a new IP. A node present 4+ hours counts as a standard tagged device.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-12** Node-key expiry is separate from auth-key expiry. Node keys default to 180 days, and a tailnet can set 1 to 180 days (tailnet setting devicesKeyDurationDays, min 1, max 180). When a node key expires, the device's connections stop working.
  - Source: <https://tailscale.com/kb/1028/key-expiry> — KB 1028 (Jan 5 2026): default 180 days, custom 1–180 days, and connections stop at expiry. The OpenAPI TailnetSettings.devicesKeyDurationDays has minimum 1 and maximum 180.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T2-13** Tagged devices have node-key expiry disabled by default when they are first tagged and authenticated, so a tagged friend node never expires on its own. Changing tags later does not change expiry unless the device re-authenticates.
  - Source: <https://tailscale.com/kb/1068/tags> — KB 1068 (Dec 4 2025): after a device is first tagged and authenticated, its key expiry is disabled by default. KB 1085 and KB 1028 say the same.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-14** Two API endpoints control node-key expiry. POST /api/v2/device/{deviceId}/key with {"keyExpiryDisabled": bool} turns expiry off, or back on at the original expiry time. POST /api/v2/device/{deviceId}/expire expires the key now, forcing re-authentication. Both need scope devices:core.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — updateDeviceKey: disabling keeps the original expiry, and re-enabling expires the key at that time. expireDeviceKey marks the node key expired, so the device must re-authenticate.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-15** List devices with GET /api/v2/tailnet/{tailnet}/devices (scope devices:core:read). fields=default\|all controls which fields come back. You can filter server-side with exact-match <field>=<value> on top-level fields such as tags, hostname, and isEphemeral; multiple filters are ANDed. The API has no pagination.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — listTailnetDevices supports filters like isEphemeral=true&tags=tag:prod, ANDed. The spec overview says the API does not currently support pagination.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T2-16** Device fields include nodeId (preferred ID, e.g. n292kg92CNTRL), id (legacy numeric ID, still accepted), hostname, name (MagicDNS), addresses, tags, isEphemeral, authorized, created, lastSeen, connectedToControl, expires, and keyExpiryDisabled. lastSeen is left out if the device was never online or is currently connected to control.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — Device schema: nodeId is preferred and id is legacy. lastSeen is omitted if the device was never online or connectedToControl is true. The default fields list includes tags and isEphemeral.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T2-17** Delete a device with DELETE /api/v2/device/{deviceId} (scope devices:core). The device must belong to the caller's tailnet. Devices shared into the tailnet cannot be deleted this way (501 Device not owned by tailnet).
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — deleteDevice: the device must belong to the requesting user's tailnet, and deleting shared devices is not supported. It returns 501 when the device is not owned by the tailnet.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T2-18** Set device tags with POST /api/v2/device/{deviceId}/tags and body {"tags": [...]}, which replaces the whole list (scope devices:core). A device cannot be both tagged and user-owned. A tagged device must keep at least one tag.
  - Source: <https://tailscale.com/kb/1068/tags> — KB 1068: tagging removes user identity, and user login removes tags. You cannot remove all tags. The OpenAPI setDeviceTags body is the new tags list.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T2-19** The docs give NO way to map an auth key ID to the device it created. The Device schema has no auth-key field. nodeCreated webhook payloads carry nodeID, deviceName, managedBy, actor, and url. Audit-log entries have actor and target but no key ID.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — Neither the Device nor the ConfigurationAuditLog schema has an auth-key reference. KB 1213 (webhooks) lists nodeCreated fields and none of them identify the key used.
  - Confidence: medium · load-bearing for our design · Verification: not independently re-checked
- **T2-20** tsnet.Server (v1.102.4, Sep 10 2026) has AuthKey, ClientSecret, ClientID/IDToken, Ephemeral, AdvertiseTags, Hostname, Dir, and Store fields. AuthKey is ignored once the node's state already exists in Store, unless TSNET_FORCE_LOGIN=1 is set. Up(ctx) returns *ipnstate.Status, and its Self.ID is a tailcfg.StableNodeID.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet> — tsnet.go: if the node already exists in Store, this field is not used. The ipnstate PeerStatus.ID type is tailcfg.StableNodeID.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T2-21** tailcfg.StableNodeID is the string form of NodeID. NodeIDs are unique per control plane and, as of 2025-01-06, are never reused even after a node is deleted. The docs never state that StableNodeID equals the API's nodeId; that is inferred from the shared n...CNTRL format.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tailcfg/tailcfg.go> — Source comments: StableNodeID is a string form of NodeID, and NodeID is never reused even after deletion (as of 2025-01-06).
  - Confidence: medium · load-bearing for our design · Verification: not independently re-checked
- **T2-22** An OAuth client secret (tskey-client-...) can be passed directly as an auth key with URL parameters ?ephemeral=&preauthorized=&baseURL=. ephemeral defaults to true and preauthorized to false. The client's tags must be advertised, and the resulting devices are tag-owned.
  - Source: <https://tailscale.com/kb/1215/oauth-clients> — KB 1215: pass --advertise-tags. ephemeral defaults true, preauthorized defaults false. Devices registered with OAuth client credentials are tag-owned.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T2-23** Every key and device endpoint is scoped to one tailnet: {tailnet} is a tailnet ID, or '-' for the token's default tailnet. User API access tokens expire in 1–90 days. Trust credentials (OAuth clients and federated identities) never expire and belong to the tailnet rather than a user.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — Spec overview: user tokens can be set to expire in 1–90 days. Trust credentials don't expire and aren't tied to a user. Using '-' means the token's default tailnet.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T2-24** Trust-credential scopes: auth_keys:read reads auth keys, and auth_keys reads or modifies them. devices:core:read reads devices. devices:core reads devices, authorizes or removes machines, and edits tags, and it requires selecting one or more tags.
  - Source: <https://tailscale.com/kb/1623/trust-credentials> — KB 1623 (Jan 30 2026): credentials with the devices:core scope must select one or more tags. The same page lists the auth_keys and devices scopes.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T2-25** Webhooks can subscribe to nodeCreated, nodeNeedsApproval, nodeApproved, nodeKeyExpiringInOneDay, nodeKeyExpired, and nodeDeleted. The payload carries nodeID, which could serve as an owner-side signal that a friend node joined.
  - Source: <https://tailscale.com/kb/1213/webhooks> — The OpenAPI webhook subscriptions enum lists these events. KB 1213 (Jan 5 2026) says node payloads include nodeID, deviceName, managedBy, actor, and url.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T2-26** GET /api/v2/tailnet/{tailnet}/keys lists only active keys, and which keys appear depends on the caller: a user token sees that user's keys, and ?all=true returns all. The description of what an OAuth-derived token sees is confusingly worded.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — listTailnetKeys returns active keys. User tokens see only their own keys. The all parameter returns every auth key, token, and OAuth client.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T2-27** API-only tailnets can be created through the organization API (Alpha): POST /api/v2/organizations/{organization}/tailnets, scope tailnets. Every plan is capped at 10 tailnets including the original. The response includes an OAuth client for the new tailnet.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — createOrganizationTailnet: all plans can create at most 10 tailnets including the original. The response includes an OAuth client for the new tailnet.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T2-28** The OpenAPI spec says its endpoints are stable unless noted, but the spec document itself is unstable and may change without notice. The API base URL is https://api.tailscale.com/api/v2/, and it accepts Bearer or Basic authentication.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — Spec overview: endpoints are stable unless noted, but the OpenAPI spec may change or break without notice. Base URL is https://api.tailscale.com/api/v2/.
  - Confidence: high · Verification: not re-checked (not load-bearing)

### Additional facts found by the verifier

- Credentials and the keys they mint belong to one tailnet. With a trust-credential token, a created auth key is owned by that token's tailnet, and tailnet "-" means the token's default tailnet. So one server-side OAuth client in the Worker can only mint keys for one tailnet. Supporting many independent owners means either every owner registers their own OAuth client (the Worker stores N client secrets, each with auth_keys scope and tags) or all owners share one Salem-operated tailnet. The docs say this implicitly, not explicitly.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — createKey: keys made with an OAuth- or federated-derived token are owned by the tailnet. The tailnet parameter says '-' refers to the default tailnet of the access token.
- The tailnet creation API (POST /organizations/{organization}/tailnets, Alpha, scope 'tailnets') creates API-only tailnets with no human users. It caps every plan at 10 tailnets per organization, including the original one. This rules out a Salem-run 'one tailnet per owner' model at scale without contacting sales. The new tailnet's OAuth client secret is returned only once.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — createOrganizationTailnet: all plans can create a maximum of 10 tailnets including the original. More needs sales. The response's client secret cannot be retrieved later.
- Node sharing (device invites) cannot be automated with an OAuth-derived token, because shared devices are scoped to a user. A Worker holding only an OAuth client therefore cannot share an owner's host node into a friend's tailnet as an alternative to minting auth keys.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — POST /device/{deviceId}/device-invites: device invites cannot be created using an API access token generated from an OAuth client, because the shared device is scoped to a user.
- On the free Personal plan, every tagged friend node counts against a quota of 50 tagged resources. Ephemeral nodes get 1,000 minutes per month, and an ephemeral node present for 4 or more hours counts as a tagged device (KB 1111). This caps friends per owner. It also explains the 402 from device authorization when plan limits are hit.
  - Source: <https://tailscale.com/pricing> — The Personal plan lists up to 6 users, unlimited user devices, 50 tagged resources included, and 1,000 minutes per month for ephemeral resources.
- If an owner's tailnet has Tailnet Lock enabled, a node that joins with a Worker-minted auth key, even a preauthorized one, cannot talk to peers until a trusted node signs its node key. The alternative is a pre-signed auth key made with `tailscale lock sign`. The coordination server cannot bypass this, so automatic key minting breaks for locked tailnets.
  - Source: <https://tailscale.com/kb/1226/tailnet-lock> — All new node keys must be signed by an existing Tailnet Lock key, and a trusted node must sign new additions. Signed auth keys let devices join a locked tailnet. Last validated Dec 2, 2025.
- tsnet v1.102.4 (published Sep 10, 2026) has Server.ClientSecret, which lets tsnet generate auth keys itself from an OAuth client secret (env TS_CLIENT_SECRET). It also has AdvertiseTags and Ephemeral. If state already exists in Server.Store, the auth key is ignored unless TSNET_FORCE_LOGIN=1. KB 1215 adds that an OAuth secret used directly as an auth key defaults to ephemeral=true and preauthorized=false. Design impact: never ship the client secret to the friend; mint one-off keys on the server instead. A friend who loses the tsnet state directory cannot rejoin with the already-used one-off key. The Device API field multipleConnections can flag node state copied between machines.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet> — The ClientSecret field doc says it is used to generate authkeys via OAuth. The docs say the auth key is ignored if the node is already enrolled, unless TSNET_FORCE_LOGIN=1.

### Our reading (proposed, not an official fact)

- Each owner's tailnet is a separate API scope (T2-23). The Worker can only mint keys or delete devices in a tailnet where it holds that owner's trust credential: an OAuth client with auth_keys and devices:core, tagged e.g. tag:1salem-guest. Those credentials never expire, so the Worker would hold one long-lived secret per owner in D1. Encrypt them at rest and plan for owners revoking them.
- Proposed per-invite key: POST /tailnet/{tid}/keys with reusable=false, preauthorized=true, tags=[tag:1salem-guest] (must match the OAuth client's tags), a short expirySeconds, and a description built from the invite ID. The description allows at most 50 characters: alphanumerics, hyphens and spaces only. Store the returned id and hand the key secret straight to the friend's sidecar.
- Revoking access takes two calls. An unused key needs DELETE /keys/{keyId}. A device that has already joined needs DELETE /device/{nodeId}. Deleting the key alone leaves the friend's node connected (T2-07). Revocation in D1 must track both IDs.
- The friend's node is tagged, so its node-key expiry is off by default (T2-13) and access would never expire on its own. Session TTL has to be enforced by the control plane: delete the device, call POST /device/{id}/expire, or re-enable expiry with POST /device/{id}/key. If ephemeral, removal still needs the node to go offline.
- No API links a key ID to the device it created (T2-19). The friend's tsnet sidecar should report Status.Self.ID (StableNodeID) after Up(). The Worker then checks it with GET /device/{nodeId}, confirming the expected tag, a hostname set to a unique invite-derived value, and a created time after key issue. An alternative is filtering GET /devices?hostname=<unique>&tags=tag:1salem-guest.
- tsnet ignores AuthKey once state exists in its Store (T2-20). A friend sidecar with persisted state reconnects without a new key. A memory-only or ephemeral sidecar needs a fresh one-off key on every launch and gets a new node and new IP each time. Pick one model: persistent tagged node with explicit deletion, or ephemeral node with a key per session.
- Ephemeral friend nodes clean themselves up 30–60 minutes after going offline. After 4 hours of presence they are billed as standard tagged devices. authorize can return 402 at plan device limits. Every friend node counts against the owner's tailnet and appears in the owner's admin console.
- preauthorized=true only matters when the owner has device approval on (devicesApprovalOn). Set it anyway so the flow works whatever that setting is.
- The owner must grant tag:1salem-guest access to exactly the host bridge's tag and port in the tailnet policy. Keys only apply tags; network reach comes from the policy, which T2 does not cover.

### Risks noted

- Holding many owners' non-expiring OAuth client secrets with devices:core in one Cloudflare D1 is a high-value custodial target. The docs do not say whether devices:core is limited to devices carrying the credential's tags, so a leaked credential might be able to delete or deauthorize any device in an owner's tailnet.
- A one-off key intercepted before the friend uses it lets an attacker register a node instead. Because nothing maps key ID to device, the Worker must verify the joined nodeId, and ideally the node's public key or hostname, before issuing a session ticket.
- The API documents no minimum expirySeconds. The KB says 1–90 days, so sub-day keys (e.g. 10 minutes) may be rejected. If so, one-off keys stay valid for at least a day, which makes prompt deletion of unused keys more important.
- Deleting a key does not deauthorize devices, and tagged devices do not expire by default. If revocation deletes only the key, the friend keeps access indefinitely.
- The OpenAPI spec is explicitly 'unstable'. Field names or shapes could change without notice, and the organization/tailnet-creation API is Alpha.
- Friend nodes count toward the owner's plan limits (402 on authorize) and ephemeral-minute billing. Pricing and limits as of Sept 2026 were not checked in this topic.
- The API has no pagination: device listing returns every device in the tailnet. Fine for home tailnets, but server-side filters should always be used.

### Open questions

- What is the smallest expirySeconds POST /tailnet/{tailnet}/keys accepts (e.g. 300 or 3600), and what error does a sub-day or over-90-day value return? The API reference gives no bounds; only the KB says 1–90 days.
- After a one-off key is consumed, does GET /keys/{keyId} return 200 with invalid=true and a revoked timestamp, or 404? That decides how the Worker detects that a key was used.
- Is ipnstate.Status.Self.ID (tailcfg.StableNodeID) always identical to the API's Device.nodeId? The formats match, but no doc states it.
- Does an OAuth client with devices:core tagged tag:X only manage devices tagged tag:X, or every device in the tailnet? KB 1623 requires tags but does not describe the effect.
- Is a tsnet node with Ephemeral=true and persisted on-disk state logged out, and removed immediately, when Server.Close() is called? Not documented in tsnet or KB 1111.
- Can the Worker avoid storing a long-lived OAuth secret by using federated identity (ClientID + IDToken, workload identity) per owner, with a Cloudflare-issued OIDC token? That needs a separate check against KB 1581.
- Does any audit-log or webhook field (e.g. actor.type/actor.id on node CREATE) reveal which auth key or OAuth client registered a node? The spec does not say.
- Does the 50-character key description allow characters such as ':' or '_'? The spec says alphanumerics plus hyphens and spaces only, so the invite-ID encoding must fit that.
- What do the free and personal plans in Sept 2026 allow for tagged-device count, ephemeral minutes and device approval? Not covered by these pages.

## T3. OAuth clients, OAuth apps, trust credentials and multiple tailnets

_Scope as researched:_ T3: Tailscale OAuth clients, OAuth apps, trust credentials / workload identity federation, API access tokens, and multi-tailnet implications (researched 2026-09-23)

### Verified official facts

- **T3-01** OAuth clients use the OAuth 2.0 client credentials grant. The token endpoint is https://api.tailscale.com/api/v2/oauth/token.
  - Source: <https://tailscale.com/docs/features/oauth-clients> — The page names the token endpoint URL and says it follows the RFC 6749 client credentials flow. The kb/1215 URL serves this page, last validated Jun 30, 2026.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-02** An API access token minted from an OAuth client lasts exactly 1 hour and the lifetime cannot be changed. The token response includes access_token, token_type Bearer, expires_in 3600 and scope.
  - Source: <https://tailscale.com/docs/features/oauth-clients> — The Limitations section says an OAuth access token expires after 1 hour and this cannot be modified. The example response shows expires_in 3600.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-03** A token request can narrow its grant with optional space-delimited 'scope' and 'tags' parameters. The client must already hold the scopes and tags it asks for.
  - Source: <https://tailscale.com/docs/features/oauth-clients> — Token requests can include scope and tags parameters. The OAuth client must have permission to grant what is requested.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T3-04** Trust credentials (OAuth clients and federated identities) never expire. Neither they nor their access tokens are tied to a Tailscale user. OAuth client secrets start with tskey-client-. User API access tokens start with tskey-api-, keep the owning user's permissions and expire in 1 to 90 days.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — The Authentication section of the OpenAPI info says trust credentials don't expire and aren't tied to a user. It gives both key prefixes and the 1–90 day expiry for user tokens.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-05** An OAuth client belongs to the tailnet, not to a person. Only an Owner, Admin, Network admin or IT admin of that tailnet can create, revoke or delete one.
  - Source: <https://tailscale.com/docs/features/oauth-clients> — Limitations: OAuth clients must be owned by the tailnet, not an individual user. Prerequisites list the four roles that can create or delete clients.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-06** Each credential works on one tailnet only. The API tailnet path takes a tailnet ID, or '-' for the token's default tailnet. The only documented cross-tailnet access is the Tailnets API: an 'all'-scope client can get tokens for API-only tailnets in its own organization (T3-14). No documented way exists to point an OAuth client or its tokens at an unrelated tailnet.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — Per the tailnet parameter description, '-' means the default tailnet of the access token. Delete-device requires the device to belong to the requester's tailnet.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: (1) The {tailnet} path takes the tailnet ID, '-' for the token's default tailnet, or a legacy ID for tailnets created before Oct 2025. (2) No official page says outright that an OAuth client or federated identity works on only one tailnet. That is inferred from silence, and the trust-credentials, OAuth-clients and multiple-tailnets pages don't address it. Only the OAuth apps page states a single-tailnet limit. (3) The only documented way for a credential to reach another tailnet: an 'all'-scope OAuth client from the creating tailnet sends tailnet=<ID> in the /oauth/token body to get a token for an API-only tailnet (tailnets-api KB). (4) Cross-tailnet device sharing exists, but device invites cannot be created with OAuth-derived tokens (see missed facts). No documented way exists to point a credential at an unrelated person's tailnet.
- **T3-07** The current granular scopes are: all, dns, policy_file, users, devices:core, devices:posture_attributes, devices:routes, devices_invites, api_access_tokens, auth_keys, oauth_keys, federated_keys, webhooks, log_streaming, logs:network, account_settings and feature_settings. Most also have a ':read' form, and there are read-only scopes such as all:read, logs:configuration:read and logs:network:read.
  - Source: <https://tailscale.com/docs/reference/trust-credentials> — The Scopes section lists read-only and full-access scopes with descriptions. The page (kb/1623) was last validated Jan 30, 2026.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T3-08** Creating auth keys needs the 'auth_keys' scope and uses POST /api/v2/tailnet/{tailnet}/keys. The body takes capabilities.devices.create {reusable, ephemeral, preauthorized, tags}, expirySeconds (default 90 days) and description. keyType can be auth, client or federated.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — The createKey operation says the auth_keys scope grants access to create machine auth keys. The KeyCapabilities schema lists reusable, ephemeral, preauthorized and tags.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-09** Deleting a device needs the 'devices:core' scope and uses DELETE /api/v2/device/{deviceId}. The device must belong to the caller's tailnet. Devices shared into the tailnet cannot be deleted this way (501, device not owned by tailnet).
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — deleteDevice: 'OAuth Scope: devices:core'. It deletes a device from its tailnet and does not support deleting devices shared with the tailnet.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-10** A trust credential must carry at least one tag if its scopes include devices:core or auth_keys. Auth keys it creates must use exactly the credential's tags, or tags that the credential's tags own under tagOwners.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — The createKey 'tags' field is mandatory when scopes include devices:core or auth_keys. Created keys must have these exact tags or tags owned by them.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-11** Any auth key created through OAuth is owned by the tailnet and must have tags. Keys created with a user's API token belong to that user and tags are optional.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — The KeyCapabilities.tags description says tailnet-owned keys (via OAuth) must have tags matching or owned by the client's tags, and user-owned keys have optional tags.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-12** Revoking a trust credential also revokes the access tokens it has issued. Each token mint shows in the configuration audit log with the credential's Client ID as the actor.
  - Source: <https://tailscale.com/docs/reference/trust-credentials> — Revoking a trust credential also revokes the active API access tokens it created. The audit log entry names the Client ID as actor.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T3-13** Auth key expiry is 1 to 90 days, with 90 as the default. One-off keys are revoked automatically once used. Revoking a key does not remove devices already registered with it; you have to delete the device. Tagged devices have node-key expiry off by default.
  - Source: <https://tailscale.com/docs/features/access-control/auth-keys> — Expiry must be between 1 and 90 days. One-off keys are auto-revoked after use. Revoking a key doesn't deauthorize nodes. Page validated Jun 30, 2026.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-14** The Tailnets API (alpha) creates API-only tailnets via POST /api/v2/organizations/{organization}/tailnets with the 'tailnets' scope. These tailnets have no human users, hold only tagged devices and do not appear in the admin console. The response returns an 'all'-scope OAuth client, and its secret is shown only once. An 'all'-scope client in the creating tailnet can also get tokens for them by passing a 'tailnet' parameter to /token.
  - Source: <https://tailscale.com/docs/features/tailnets-api> — You can only authenticate to these APIs with an OAuth client. Use the 'tailnet' parameter in the /token body to target a created tailnet. Page validated Aug 25, 2026.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-15** Every plan can create at most 10 tailnets in an organization, counting the original one. More than 10 needs a sales contract. The Tailnets API is alpha, may change, and is not in the Go client, Terraform or Pulumi.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — createOrganizationTailnet: all plans can create a maximum of 10 tailnets including the original; for more, contact sales. The endpoint carries an Alpha badge.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-16** OAuth apps (alpha) use the authorization-code grant. The authorize endpoint is https://login.tailscale.com/a/oauth_authorize and the token endpoint is the same as for OAuth clients. Device provisioning uses the scope auth_keys:create:once: each consent yields one auth key and no refresh token. Devices created this way belong to the consenting user and count against that user's quota.
  - Source: <https://tailscale.com/docs/features/oauth-apps/device-provisioning> — The device-provisioning guide (alpha, validated Jun 24, 2026) describes the code flow, the single-use scope, and user-owned devices subject to user ACLs and quota.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-17** OAuth apps only work inside one tailnet. The app and every user who authorizes it must be in the same tailnet, so a third party cannot use them to get delegated access to other people's tailnets. Only an Owner or Admin can create one (POST /api/v2/tailnet/{tailnet}/oauth-apps, scope oauth_apps), and the consent screen cannot be customized.
  - Source: <https://tailscale.com/docs/features/oauth-apps> — The OAuth app and every authorizing user must belong to the same tailnet, and users from other tailnets are not supported. Page validated Jun 30, 2026.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T3-18** Workload identity federation swaps an external OIDC JWT for a short-lived API token at https://api.tailscale.com/api/v2/oauth/token-exchange, sending client_id and jwt. Custom OIDC issuers are allowed if publicly reachable over https. Matching uses a subject pattern with wildcards, the audience and optional custom-claim rules. Each tailnet configures its own federated identities.
  - Source: <https://tailscale.com/docs/features/workload-identity-federation> — The WIF page (validated Jan 30, 2026) documents the token-exchange endpoint and custom issuers. The OpenAPI createKey 'issuer' field must be a publicly reachable https:// URL.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T3-19** Workload identity federation became generally available on all plans on Feb 19, 2026, with support for GitHub, GCP, AWS and custom OIDC, plus tsnet support.
  - Source: <https://tailscale.com/blog/workload-identity-ga> — Official Tailscale blog (secondary source), dated Feb 19, 2026: GA and available on all plans, with tsnet support.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T3-20** tsnet v1.102.4 (published Sep 10, 2026) has these Server fields: AuthKey (preferred over TS_AUTHKEY), ClientSecret (an OAuth client secret used to generate authkeys), ClientID/IDToken/Audience (workload identity federation), AdvertiseTags and Ephemeral.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet> — Per the field doc comments, ClientSecret generates authkeys via OAuth and ClientID generates authkeys via workload identity federation. WIF also needs the feature/identityfederation import.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T3-21** If you pass an OAuth client secret as --auth-key, it accepts URL-style parameters: ephemeral (default true), preauthorized (default false) and baseURL. The node must advertise one or more of the client's tags.
  - Source: <https://tailscale.com/docs/features/oauth-clients> — The 'Register new nodes using OAuth credentials' section documents these parameters and defaults, and requires --advertise-tags.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T3-22** Link-based machine sharing cannot give a tagged device access across tailnets. Tagged devices cannot be shared or accept shares, and only an Owner, Admin or IT admin can accept a share. Shared machines are quarantined. The feature is in beta.
  - Source: <https://tailscale.com/kb/1084/sharing> — A machine cannot be shared with a tag or accessed by tagged machines on another tailnet, because only users can accept shares. Page validated Jan 5, 2026.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T3-23** Declarative node sharing (alpha, waitlist) lets tagged devices in an external tailnet reach shared resources, but admins on both sides must opt in through their policy files. Limits are 3 external tailnets per policy and under 100 nodes in the sharing tailnet. Tailscale says not to use it with unknown or untrusted tailnets.
  - Source: <https://tailscale.com/docs/features/declarative-node-sharing> — The page (validated Aug 26, 2026) describes double opt-in via externalTailnets and allowIncomingConnections, the max 3 external tailnets, and the under-100-node limit.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T3-24** Pricing: Personal is $0 with up to 6 users; Standard is $8 per user per month and Premium $18. Every plan includes 50 tagged resources, with more at $1/month each. Personal and Standard include 1,000 ephemeral minutes a month, Premium 10,000. An ephemeral device present more than 4 hours counts as a tagged resource instead.
  - Source: <https://tailscale.com/pricing> — The pricing table and FAQ define tagged resources, the ephemeral minute pool and the 4-hour rule. Ephemeral resources are defined as devices tagged as short-running.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T3-25** The Personal plan is only for non-commercial use. The 'Multiple tailnets' add-on, which the page calls 'Great option for OEM', is available only through sales.
  - Source: <https://tailscale.com/pricing> — Personal is for individuals at home and is only suitable for non-commercial use. The Multiple tailnets add-on is priced as 'Contact sales'.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T3-26** The Terms of Service (last updated Aug 25, 2026) grant access only for the customer's own personal use or internal business purposes. Customers may not commercially exploit, sell, resell, rent or lease use of the Services. A Permitted User is a human the Customer authorizes, and the Customer is liable for Permitted Users' breaches.
  - Source: <https://tailscale.com/terms> — Section 2.1 limits use to personal or internal business purposes. Section 2.3 prohibits commercial exploitation and reselling. Sections 1.13 and 2.5 define Permitted Users and customer liability.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T3-27** The Acceptable Use Policy (last updated Jun 30, 2025) does not mention reselling, game servers or relaying for others. It prohibits unauthorized access, malware, phishing and placing an undue burden on the Tailscale Solution.
  - Source: <https://tailscale.com/tailscale-aup> — The AUP lists unlawful and malicious uses and interfering with or unduly burdening the service. It has no clause on third-party provisioning.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T3-28** Tailscale's partnership page lists reseller (VAR), MSP and technology-partner programs. It documents no OEM program and no way for a partner to get delegated access to customer tailnets.
  - Source: <https://tailscale.com/partnerships> — The page lists Value Added Reseller, MSPs and Technology Partners, and otherwise directs readers to contact the team.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T3-29** Tailscale positions API-generated tailnets for OEM and embedding, including 'a tailnet per customer', and returns an all-scope OAuth client for each one.
  - Source: <https://tailscale.com/blog/multiple-tailnets-alpha> — Official Tailscale blog (secondary source), dated Oct 29, 2025: early testers embed Tailscale by creating one tailnet per customer.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T3-30** The legacy 'devices' scope was split into auth_keys, devices:posture_attributes and devices:core. Other legacy renames: routes became devices:routes, acl became policy_file, and logs became logs:configuration.
  - Source: <https://tailscale.com/docs/reference/trust-credentials> — The 'Legacy scope equivalents' section maps the old devices scope to the three new scopes.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T3-31** The documentation disagrees on scope names. The API spec uses scopes such as 'tailnets', 'tailnets:read', 'oauth_apps' and 'auth_keys:create', and the OAuth-app guide uses 'auth_keys:create:once'. None of these appear in the trust-credentials scope list, which was last validated Jan 30, 2026.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — The OpenAPI has 'OAuth Scope: tailnets' and 'oauth_apps' and an app scope example of auth_keys:create. The trust-credentials page does not list them.
  - Confidence: medium · Verification: not re-checked (not load-bearing)

### Additional facts found by the verifier

- Cross-tailnet device sharing (device invites) cannot be automated with OAuth-derived tokens. Creating, resending and accepting device invites all fail with a token from an OAuth client, because a shared device is scoped to a user. A server-side OAuth credential therefore cannot share an owner's host bridge into a friend's tailnet.
  - Source: <https://api.tailscale.com/api/v2?outputOpenapiSchema=true> — createDeviceInvites, resend and accept descriptions: invites cannot be created/resent/accepted using an OAuth-client access token as the shared device is scoped to a user.
- Workload identity federation lets a tailnet trust a Custom issuer, meaning any OIDC provider. A workload swaps a signed JWT plus client_id at https://api.tailscale.com/api/v2/oauth/token-exchange for a short-lived API token. It can also register a node directly with tailscale up --client-id --id-token --advertise-tags (client v1.90.1+), and needs the auth_keys scope. The issuer must be publicly reachable. This could let each owner trust a 1Salem-hosted issuer, so D1 would not need to store each owner's long-lived tskey-client secret.
  - Source: <https://tailscale.com/kb/1581/workload-identity-federation> — Page lists Custom issuer, token-exchange endpoint with client_id and jwt, direct tailscale up registration, auth_keys scope needed, issuer must be publicly accessible; last validated Jan 30, 2026.
- tsnet.Server (tailscale.com v1.102.4, published Sep 10, 2026) has fields beyond AuthKey. ClientSecret takes an OAuth client secret used to generate auth keys. ClientID plus IDToken (and Audience) use workload identity federation. AdvertiseTags and Ephemeral are also there. A sidecar can therefore join with a federated ID token instead of a pre-minted auth key. The same fields mean a sidecar handed an OAuth secret gets the full power of that secret.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet> — Doc comments: ClientSecret is the OAuth secret used to generate authkeys; ClientID is for workload identity federation; IDToken is exchanged with the control server.
- An OAuth client secret can be used directly as an auth key to register nodes. This needs the auth_keys scope and one or more of the client's tags in --advertise-tags. In this mode ephemeral defaults to true and preauthorized defaults to false, which differs from API-created keys. The secret never expires, so it must never be given to a friend's machine.
  - Source: <https://tailscale.com/docs/features/oauth-clients> — Registration section: client must have auth_keys scope, pass its tags to --advertise-tags; ephemeral defaults to true, preauthorized defaults to false.
- In OAuth-app device provisioning, the access_token from the token response is itself the auth key; no separate key-creation call is made. It expires after 1 hour, which cannot be configured, and the backend rejects it after its first successful use. The friend's side must finish tailscale up or tsnet login within that hour.
  - Source: <https://tailscale.com/docs/features/oauth-apps/device-provisioning> — Page: access token is already a valid auth key; expires after 1 hour (3600 s), not configurable; single-use, rejected after first successful use.
- Which scopes an owner can grant depends on the owner's role. Only Owners and Admins can create a client with any scope and any tag. Other roles are limited to scopes and tags they hold themselves; for example, a Network admin cannot grant devices:core but an IT admin can. An owner onboarding flow that asks users to create a devices:core + auth_keys client will fail for some roles.
  - Source: <https://tailscale.com/docs/features/oauth-clients> — Owners/Admins can create a client with any scope and tag; other users only with scopes/tags they have; a Network admin cannot grant devices:core, an IT admin can.

### Our reading (proposed, not an official fact)

- One vendor-held OAuth client cannot mint auth keys or delete devices in many independent owners' tailnets (T3-05, T3-06). Each owner's tailnet needs its own trust credential, created by an Owner, Admin, Network admin or IT admin of that tailnet.
- The smallest credential that covers the planned flow is an OAuth client or federated identity with 'auth_keys' (to create one-off, preauthorized, tagged keys) plus 'devices:core' (to delete the friend's node on revocation). Both scopes require tags on the credential. Every friend key must use exactly those tags, or tags they own in tagOwners (T3-08 to T3-11). Each token request can narrow scope and tags further (T3-03).
- To revoke a friend, the device itself must be deleted via DELETE /api/v2/device/{id} with devices:core. Revoking the auth key does not disconnect a node that already joined, and one-off keys become useless after first use anyway (T3-09, T3-13). The control plane has to store the friend node's device ID.
- An OAuth client secret does not expire and has tailnet-wide power within its scopes (T3-04). If the Cloudflare Worker keeps one per owner, a Worker or D1 breach exposes every owner's tailnet. Two documented alternatives: keep the secret on the owner's PC, or use workload identity federation with a custom HTTPS OIDC issuer so no static secret is stored (T3-18, T3-19). Whether a Cloudflare Worker can act as that issuer is unverified.
- The friend-side tsnet sidecar should only ever receive a single-use AuthKey. tsnet also accepts ClientSecret and workload-identity fields, which would hand the friend minting power over the owner's tailnet (T3-20, T3-21).
- No Tailscale mechanism gives a third-party app delegated access to other people's tailnets. OAuth apps work in one tailnet only (T3-17). Trust credentials belong to one tailnet (T3-06). The Tailnets API covers only tailnets in the vendor's own organization (T3-14). Link sharing excludes tagged nodes (T3-22). Declarative sharing needs admin opt-in on both sides and allows at most 3 external tailnets (T3-23).
- One vendor tailnet (or one vendor-created API-only tailnet per owner) hosting unrelated owners faces three obstacles. Terms §2.1 and §2.3 limit use to personal or internal business purposes and forbid commercial exploitation or resale (T3-26). The Personal plan is non-commercial only (T3-25). The Tailnets API caps at 10 tailnets without a sales contract and is alpha (T3-15). A vendor-operated model would realistically need a commercial or OEM agreement with Tailscale.
- In an owner-operated model, each owner's tailnet pays for friend nodes. Tagged friend nodes count against the 50 included tagged resources, after which each costs $1/month. Ephemeral friend nodes draw from the 1,000-minute monthly pool on Personal/Standard, and any session over 4 hours counts as a tagged resource instead (T3-24).
- Owners face setup work in their own admin console: define the friend tag in tagOwners, create the credential with the right roles, and write a policy/ACL grant limiting the tag to the game port. Tailscale documents all of these steps; there is no zero-config path.

### Risks noted

- Terms of Service risk: the Terms (§2.1 personal or internal business use; §2.3 no commercial exploitation or resale) could be read to forbid a vendor running tailnets on behalf of unrelated end users. This needs Tailscale's confirmation or a sales/OEM contract before any vendor-hosted design.
- The Tailnets API (per-owner API-only tailnets) is alpha, may change, is capped at 10 tailnets per organization without sales, and is not supported in the Go client, Terraform or Pulumi.
- OAuth apps are alpha and single-tailnet, and they produce user-owned rather than tagged devices, so they cannot deliver cross-tailnet onboarding.
- Keeping many owners' non-expiring OAuth client secrets in one Worker/D1 concentrates risk. A leak gives the attacker tailnet-wide key minting and device deletion within the granted scopes.
- The Personal plan is non-commercial. Owners' plans pay for tagged or ephemeral friend nodes. How overages are enforced on Personal (blocking or billing) is not documented.
- The scope documentation is inconsistent across pages (T3-31), so exact scope strings must be checked against the live admin console or API before implementation.
- Declarative node sharing is limited to 3 external tailnets and a waitlist, and link sharing excludes tagged nodes, so neither scales to many owners and friends.

### Open questions

- Workload identity federation with a custom issuer: does Tailscale need /.well-known/openid-configuration and a JWKS endpoint, and which JWT algorithms does it accept? Could a Cloudflare Worker serve as the issuer? How long do token-exchange access tokens last? The docs only say the issuer must be publicly reachable over https and the token is 'short-lived'.
- Can a token with devices:core delete any device in the tailnet, or only devices carrying the credential's tags? The docs say tags matter for devices:core but do not state a restriction on deletion.
- Does expirySeconds on POST /keys accept values under 1 day? The admin-console docs say 1–90 days; the API only documents the 90-day default.
- Which scope strings are authoritative today: auth_keys versus auth_keys:create, plus tailnets and oauth_apps? The trust-credentials page (Jan 2026) and the OpenAPI spec disagree.
- For API-only tailnets: how is 'organization' defined, do their tagged devices count against the creating organization's plan or pricing, and is there per-tailnet billing? The Tailnets API docs are silent.
- Would Tailscale treat a free desktop app that coordinates owner-owned tailnets (owners bring their own account and credential) as permitted personal or internal use? And what agreement is needed if the vendor instead operates the tailnets? The Terms do not address either case.
- What is the current price of the 'Multiple tailnets' OEM add-on or a Tailnets API sales contract above 10 tailnets? The pricing page only says contact sales.
- What are the rate limits on the key-creation and token endpoints? None were found in the OpenAPI spec for these endpoints.

## T4. Access policy (grants, tags) and connectivity (DERP, direct, relays)

_Scope as researched:_ T4: Tailscale access policy (grants vs ACLs, tags, autogroups, tests, least privilege) and connectivity (DERP, direct/NAT traversal, peer relays, tsnet behaviour, MagicDNS, Tailnet Lock)

### Verified official facts

- **T4-01** Grants are the recommended policy syntax. ACLs still work and will keep working indefinitely. The two can coexist in one policy file, and grants can do everything ACLs can.
  - Source: <https://tailscale.com/kb/1324/grants> — The docs recommend favoring grants for most use cases and say grants and legacy ACLs can coexist. Page last validated Jul 24, 2026 (same content at /docs/features/access-control/grants).
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T4-02** The migration guide says ACLs will keep working indefinitely but grants are the recommended practice. It shows a tag-destination ACL on port 443 rewritten as a grant with dst [tag] and ip ["tcp:443"].
  - Source: <https://tailscale.com/docs/reference/migrate-acls-grants> — Converted example: src group:eng, dst tag:web-server, ip tcp:443 (the ACL form puts the port in dst plus proto). Last validated Mar 13, 2026.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T4-03** A grant needs src and dst, plus either ip or app (or both). via and srcPosture are optional. src and dst accept tag:<name> selectors.
  - Source: <https://tailscale.com/docs/reference/syntax/grants> — The grants syntax reference says every grant needs dst and must have ip, app, or both. Tags are valid selectors in src and dst. Last validated Jan 5, 2026.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T4-04** ip selectors can be "*" (all ports, meaning TCP, UDP and ICMP), a bare port or range ("443", "80-443"), proto:port ("tcp:443", "tcp:80-443") or proto:* ("icmp:*"). Protocols can be given by name (tcp, udp, icmp, sctp and others) or by IANA number.
  - Source: <https://tailscale.com/docs/reference/syntax/grants> — The reference lists these formats and says "*" implies TCP, UDP and ICMP access. Example: "ip": ["tcp:443","udp:53"].
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T4-05** Grants are deny-by-default and have no deny rules. When several grants match a connection, the engine applies the union of their capabilities. So adding a rule can only widen access.
  - Source: <https://tailscale.com/docs/reference/syntax/grants> — The reference says the union of granted capabilities applies when grants overlap, and access is permitted only if explicitly granted.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T4-06** A new tailnet starts with an allow-all policy. If the policy has no acls section, Tailscale applies allow-all. An empty acls section denies everything. The allow-all grant form is src ["*"], dst ["*"], ip ["*"].
  - Source: <https://tailscale.com/kb/1018/acls> — The ACL page says an absent acls section means the default allow-all policy and an empty acls section denies traffic. Last validated Jan 5, 2026. The ACL examples page (Feb 2, 2026) says new tailnets start allow-all.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T4-07** Access rules are directional and enforced locally. Each destination device filters incoming connections against the rules distributed to it. A rule letting A connect to B does not let B connect to A.
  - Source: <https://tailscale.com/kb/1018/acls> — The page says allowing a source to reach a destination does not let the destination reach the source, and each device enforces rules on its own incoming connections.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T4-08** The client packet filter lets reply traffic back in without a matching rule. TCP non-SYN packets are accepted as part of an existing session. Replies on UDP/SCTP flows the node sent out are admitted from an outbound-flow cache. A one-way grant is therefore enough for TCP (Minecraft) and UDP (Palworld) request/response.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/wgengine/filter/filter.go> — runIn4/runIn6 allow non-SYN packets as session continuations. The UpdateOutboundFlowState comment says inbound replies on the same flow are admitted without an explicit allow rule.
  - Confidence: medium · load-bearing for our design · Verification: **corrected** by independent check: Current main filter.go does accept TCP non-SYN packets ('tcp non-syn'), admits UDP/SCTP replies from an outbound-flow LRU (lruMax = 512 entries), and accepts ICMP echo replies and errors. But tsnet runs in userspace (netstack) mode, and there the netstack's outbound packets take the injectedRead path, which skipped RunOut, so UDP flow state was never recorded and replies were dropped as 'no matching rule' (issue #14229, reported Nov 2024). PR #20204 fixed this with 'track UDP flow state for injected packets', merged 2026-06-22. The fix is in release-branch/1.102 but not release-branch/1.100. So a one-way grant is enough for TCP (Minecraft). For UDP (Palworld) it is enough only if the friend-side tsnet is built from tailscale.com v1.102 or later; older versions need a reverse UDP grant.
- **T4-09** Tags must be created in the tailnet policy file's tagOwners before any device can be given them. Tag names start with "tag:" and are treated as lowercase. Tag owners can be users, groups, autogroups or other tags. The shorthand [] means only autogroup:admin and autogroup:network-admin can assign the tag.
  - Source: <https://tailscale.com/kb/1068/tags> — The tags page says you must create a tag in the policy file before assigning it, and tag case is ignored (validated Dec 4, 2025). The policy-syntax page (Apr 8, 2026) covers the tag: prefix and the [] shorthand.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T4-10** When an OAuth client or auth key assigns tags, the requested tags must either match the key's or client's full tag set exactly, or each requested tag must be owned by one of the authenticating entity's tags.
  - Source: <https://tailscale.com/kb/1068/tags> — The tags page states this matching and ownership rule for tags assigned by OAuth clients and auth keys.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T4-11** A tagged device's identity is the combination of all its tags, and it no longer has a user identity. Key expiry is off by default for tagged devices. Tags give no access by themselves; access comes only from grants or ACL rules that name them.
  - Source: <https://tailscale.com/kb/1068/tags> — The page says identity is the combination (not intersection) of tags, key expiry is disabled by default after first tagging, and applying a tag removes the user identity.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: Confirmed: a tagged device's identity is the combination of all its tags, and applying a tag removes user-based authentication. Key expiry is disabled by default when a device is first tagged and authenticated, and a tagged device must keep at least one tag. Correction: access does not come only from rules that name the tag. Any rule whose selector matches the device grants access, including "*", autogroup:tagged, and IP, CIDR, host or ipset selectors. kb/1068 also never states outright that tags grant no access; that follows from deny-by-default.
- **T4-12** An auth key can carry tags. A device that logs in with it takes on the key's tag identity. Keys can also be pre-approved (auto-authorized when device approval is on) or ephemeral (the device is removed after it goes offline).
  - Source: <https://tailscale.com/docs/features/access-control/auth-keys> — The auth-keys page says the device assumes the identity of the auth key's tags. It describes pre-approved and ephemeral keys. Last validated Jun 30, 2026.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T4-13** Autogroups include autogroup:member (direct tailnet members, usable in src and dst), autogroup:tagged (any tagged device, src and dst), autogroup:self (dst only), autogroup:shared (src only, users who accepted a share), autogroup:internet (dst) and autogroup:danger-all (src, includes sources outside the tailnet).
  - Source: <https://tailscale.com/kb/1337/policy-syntax> — The autogroup table in the policy syntax reference gives each group's allowed position. Last validated Apr 8, 2026.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T4-14** The tailnet policy file is HuJSON, so comments and trailing commas are allowed. Top-level sections include grants, acls, ssh, tagOwners, groups, hosts, ipsets, postures, nodeAttrs, autoApprovers, tests, sshTests, plus network options such as derpMap, randomizeClientPort and OneCGNATRoute.
  - Source: <https://tailscale.com/kb/1337/policy-syntax> — The reference lists these sections and describes the file as human JSON (HuJSON) supporting comments and trailing commas.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T4-15** The tests section checks access for a given src (user, group, tag or host). It takes an optional proto (defaults to TCP or UDP), accept and deny lists of host:port with one numeric port, and optional srcPostureAttrs. Tests cover both ACLs and network-level grants. If an assertion fails, the policy update is rejected.
  - Source: <https://tailscale.com/docs/reference/syntax/policy-file> — The page says tests apply to ACLs and network-level grants, and a failed assertion makes Tailscale reject the updated policy file with an error.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T4-16** Least-privilege pattern built from the documented syntax: tagOwners declares tag:X and tag:Y. One grant {src:["tag:X"], dst:["tag:Y"], ip:["tcp:P"]} is the only rule mentioning those tags. Tests assert tag:X accepts tag:Y:P and denies other ports. This only holds if no broader rule ("*", autogroup:tagged) exists.
  - Source: <https://tailscale.com/docs/reference/examples/grants> — Official examples show tag-to-tag grants limited to listed ports (e.g. src tag:monitoring, dst tag:logging, ip 80/443/9100). Validated Jan 5, 2026. Combining them this way is my inference.
  - Confidence: medium · load-bearing for our design · Verification: **corrected** by independent check: The cited grants-examples page contains neither this tag-to-tag pattern nor any tests section. The pattern is an inference built from the grants syntax and policy-file pages. It holds, with these limits. (1) Tests only take single numeric ports and specific entities, so 'denies other ports' can only be spot-checked, one port per entry; set proto explicitly to tcp for Minecraft and udp for Palworld. (2) Every friend on tag:X can reach every device on tag:Y, so one tag pair cannot give per-friend or per-server isolation. (3) An owner's default allow-all (src *) or any autogroup:tagged, CIDR or host rule overrides it by union. The examples page itself warns that a tag-to-same-tag grant 'might be too permissive'.
- **T4-17** Netmap trimming: a device only learns about peers it can connect to, peers that can connect to it, exit nodes available to it, and devices of the same user. With a single tag:X to tag:Y grant, a friend node sees only the host bridge.
  - Source: <https://tailscale.com/docs/concepts/device-visibility> — The page says if no rule lets device-a reach device-b, neither can know about the other. Last validated Jan 5, 2026.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: The trimming rules are confirmed (page validated Jan 5 2026). A device sees peers it can connect to, including devices shared in from other tailnets, plus peers that can connect to it, exit nodes it can use, and same-user devices. The conclusion is too strong: a friend sees every tag:Y device, not just one host bridge. The host bridge in turn sees every friend, because they can all connect to it. The page does not say how the same-user rule applies to tagged devices, which have no user identity. It also does not call visibility a security boundary; enforcement stays with the packet filter.
- **T4-18** Tagged devices cannot use node sharing across tailnets. A machine cannot be shared with a tag or reached by tagged machines on another tailnet, because only users can accept shares. Shared machines are one-way: they can answer connections but cannot start them.
  - Source: <https://tailscale.com/kb/1084/sharing> — The page says only users can accept machine shares, so tagged machines on another tailnet cannot access them, and shared machines cannot start connections. Validated Jan 5, 2026.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T4-19** The ShieldsUp pref blocks all incoming connections regardless of the packet filter from the control server, while outbound traffic still works. tsnet does not set ShieldsUp itself.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/ipn/prefs.go> — The Prefs.ShieldsUp comment says it blocks incoming connections regardless of the control-provided packet filter. "ShieldsUp" does not appear in tsnet/tsnet.go.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T4-20** When a tsnet node receives a TCP or UDP flow for a port with no tsnet listener (and, for TCP, no registered fallback handler), it drops the flow. It does not forward it to the host's localhost, so the host OS ports are not exposed.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — getTCPHandlerForFlow and getUDPHandlerForFlow return nil,true with the comment "don't handle, don't forward to localhost" when no listener matches.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T4-21** tsnet does not change the host OS's routes or DNS. It builds the userspace engine without a Router or DNS configurator, and the engine then substitutes a no-op router and a no-op DNS OS configurator. MagicDNS resolution happens inside tsnet only.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/wgengine/userspace.go> — NewUserspaceEngine uses router.NewFake and dns.NewNoopManager when those fields are nil. The Config that tsnet.go passes sets neither Router nor DNS.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T4-22** tsnet's Dial can use short MagicDNS names when MagicDNS is enabled in the tailnet. Name resolution happens inside the program's own dialer.
  - Source: <https://tailscale.com/kb/1522/tsnet-server> — The tsnet.Server doc says MagicDNS names can replace full DNS names such as yourmachine.tail-scale.ts.net. Validated Jul 7, 2026.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T4-23** tsnet (v1.102.4, published Sep 10, 2026) embeds a full node on a userspace gVisor stack with no tailscaled and no root. Its Port field is the UDP port for WireGuard and peer-to-peer traffic (zero means auto-select), so tsnet nodes do NAT traversal and direct connections.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet> — The package doc says the Port field is the UDP port for WireGuard and peer-to-peer traffic, and the source advises leaving it at zero unless you know what you are doing.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T4-24** tsnet.Server has AdvertiseTags, and the control server may refuse to apply them. It also has ClientSecret (an OAuth secret it uses to generate auth keys itself) and ClientID/IDToken/Audience for workload identity federation. AdvertiseTags is required for those two methods.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/tsnet.go> — Field comments: advertising a tag does not guarantee control will allow it, and ClientSecret generates authkeys via OAuth. kb/1522 says AdvertiseTags is required for OAuth and WIF.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T4-25** There are three connection types, all end-to-end WireGuard-encrypted: direct UDP, peer relay, and DERP relay. Connections start on DERP, try NAT traversal for a direct path, use a peer relay if one is available, stay on DERP otherwise, and are re-checked periodically.
  - Source: <https://tailscale.com/kb/1257/connection-types> — The page describes the order DERP, then NAT traversal, then peer relay, with DERP as fallback and periodic re-checks. `tailscale status` shows direct, relay or peer-relay. Validated Jun 1, 2026.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T4-26** Direct connections work for No NAT or Easy NAT on at least one side. Hard NAT on both sides, or Hard plus Easy, falls back to relaying. IPv6 counts as Easy NAT.
  - Source: <https://tailscale.com/docs/reference/device-connectivity> — The page's NAT matrix lists which pairs connect directly and which relay, and says relays use DERP unless a peer relay exists. Validated Jan 7, 2026.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T4-27** DERP servers forward already-encrypted WireGuard packets and cannot decrypt them. They run in 20+ regions, and each client picks a home region by latency. A tailnet can disable regions via derpMap in the policy file. Tailscale recommends peer relays over running custom DERP servers.
  - Source: <https://tailscale.com/kb/1232/derp-servers> — The page says a DERP server cannot decrypt traffic and forwards encrypted packets blindly. It describes derpMap RegionID null to disable a region. Validated Jan 21, 2026.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T4-28** Clients need outbound TCP 443 (control plane and DERP over HTTPS), UDP 41641 (default WireGuard source port) and UDP 3478 (STUN). Inbound ports are usually not needed, and traffic still flows via DERP when UDP is blocked, only slower.
  - Source: <https://tailscale.com/kb/1082/firewall-ports> — The page says most of the time no firewall ports need opening, and DERP relays keep traffic flowing when UDP is blocked. Validated Feb 2, 2026.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T4-29** Peer relays went GA on Feb 18, 2026 and are on all plans, including the free Personal plan. A relay needs Tailscale 1.86+ on the relay and its clients. It is enabled with `tailscale set --relay-server-port` and allowed by a grant with app {"tailscale.com/cap/relay": []} (src = devices using the relay, dst = relay). It only relays within the same tailnet and is tried before DERP.
  - Source: <https://tailscale.com/docs/features/peer-relay> — The feature doc (validated Feb 4, 2026) covers the version requirement, same-tailnet limit and grant. The GA date and plan availability come from the official blog tailscale.com/blog/peer-relays-ga (blog source).
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T4-30** With Tailnet Lock, a new node can talk to the tailnet only after a Tailnet Lock key signs its node key. Unsigned nodes show as "Locked out". A signing node can pre-sign an auth key with `tailscale lock sign $AUTH_KEY`. Tailnet Lock and device approval are mutually exclusive. The limit is 20 signing nodes, and losing the disablement secrets makes the tailnet unrecoverable.
  - Source: <https://tailscale.com/kb/1226/tailnet-lock> — The page covers pre-signed auth keys, the locked-out badge, the `tailscale lock sign nodekey:...` command and these limitations. Last validated Dec 2, 2025.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T4-31** OAuth clients cannot pre-sign auth keys for Tailnet Lock. The feature request (issue #10008, opened Oct 28, 2023) was still open when fetched, because signing needs a private key held on a node. A key minted by the Worker through OAuth would need a signing node to sign it.
  - Source: <https://github.com/tailscale/tailscale/issues/10008> — The issue 'FR: Tailnet lock signing by OAuth clients' asks for OAuth-created keys to be pre-signed without a signing node, and was open with no documented workaround.
  - Confidence: medium · load-bearing for our design · Verification: not independently re-checked

### Additional facts found by the verifier

- On tsnet/userspace versions before 1.102, UDP replies to netstack-originated flows are dropped under a one-way grant. PR #20204 (merged 2026-06-22) is in release-branch/1.102 but not 1.100. Pin tailscale.com v1.102 or later in go.mod, or add a reverse UDP grant.
  - Source: <https://github.com/tailscale/tailscale/pull/20204> — PR says netstack outbound packets take injectedRead, which bypasses Filter.RunOut, so reply flow state was missing; it fixes #14229 and #20064. release-branch/1.100 wrap.go lacks the UpdateOutboundFlowState call; release-branch/1.102 has it.
- Machine sharing between tailnets cannot target tagged nodes. A friend's tagged tsnet node therefore cannot reach an owner's host bridge through sharing; it must join the owner's tailnet, or the friend must be a user in their own tailnet. Separate tailnets cannot see each other by default.
  - Source: <https://tailscale.com/kb/1084/sharing> — A machine cannot be shared with a tag or reached by tagged machines on another tailnet; only users can accept shares. Shared machines are quarantined and cannot start connections.
- Owners with Tailnet Lock enabled will block friend nodes joined with keys minted by the Worker. Every new node key must be signed by a trusted lock key. Pre-approved auth keys must be pre-signed with 'tailscale lock sign' on a signing node, which a Cloudflare Worker cannot do.
  - Source: <https://tailscale.com/kb/1226/tailnet-lock> — With Tailnet Lock, all new node keys must be signed by an existing lock key; pre-signed auth keys are made by running tailscale lock sign on a signing node (validated Dec 2 2025).
- Revoking an auth key does not remove nodes already joined with it. Session revocation must delete the device, or rely on ephemeral cleanup, which happens only 30 to 60 minutes after last activity unless the node logs out.
  - Source: <https://tailscale.com/docs/features/access-control/auth-keys> — Revoking a key does not deauthorize nodes using it; delete the node from the Machines page to deauthorize it. The ephemeral KB gives 30 to 60 minute auto-removal, or immediate removal via tailscale logout.
- Tagged devices are capped per tailnet. All plans include 50 tagged devices, and an ephemeral node present for four hours or more counts as a standard tagged device. Each owner's tailnet can therefore hold only a limited number of friend nodes at once.
  - Source: <https://tailscale.com/kb/1068/tags> — kb/1068: all plans include 50 tagged devices; contact Sales for more. kb/1111: an ephemeral node present four or more hours counts as a standard tagged device.
- Policy tests cannot use wildcards or CIDR ranges and take only a single numeric port, so they cannot prove that nothing else is reachable. Least-privilege checks must list specific ports, and specific owner devices, as deny assertions.
  - Source: <https://tailscale.com/docs/reference/syntax/policy-file> — Sources and destinations in tests must refer to specific entities and do not support * wildcards; CIDR notation is not allowed; the port is a single numeric port.

### Our reading (proposed, not an official fact)

- Tagged friend devices cannot reach a machine shared from another tailnet (T4-18). So for each owner, the friend's tsnet node has to join that owner's own tailnet with a tagged auth key from that owner's tailnet. One shared 1Salem tailnet or node sharing between tailnets will not work for tagged nodes. The control plane therefore needs per-owner credentials: an OAuth client the owner creates, stored and used server-side, or minted locally on the owner's machine.
- Least-privilege policy per owner tailnet, in HuJSON: tagOwners {"tag:1salem-host": ["autogroup:admin"], "tag:1salem-friend": ["tag:1salem-host" or the OAuth client's tag]}. grants [{src:["tag:1salem-friend"], dst:["tag:1salem-host"], ip:["tcp:25565"]}], or ["udp:8211"] for Palworld. tests [{src:"tag:1salem-friend", accept:["tag:1salem-host:25565"], deny:["tag:1salem-host:22", ...]}]. The tests stop later policy edits from silently widening access (T4-15).
- Most owner tailnets still have the default allow-all rule (T4-06), and grants add up (T4-05). If "*"→"*" or broad autogroup:tagged rules stay, a friend node can reach every device in the owner's tailnet, and every device can reach it. Onboarding must detect this, warn, and make the owner replace it. 1Salem cannot enforce least privilege while such a rule exists.
- Grants are tag-level, not per-server. Any tag:1salem-friend node can reach any tag:1salem-host node on the allowed ports. To isolate servers or friends, use per-server tags, or have the host bridge check signed session tickets at the application layer.
- Defense in depth on the friend side: the friend tsnet sidecar never calls Listen (so there are no inbound handlers, T4-20) and sets ShieldsUp (T4-19). Even a later over-broad owner rule then cannot open inbound connections to the friend node.
- Host side: the owner's tsnet bridge listens only on the one bridged port. tsnet drops flows to other ports and never forwards them to host localhost (T4-20), so the owner's other PC services are not exposed through the tailnet.
- Neither sidecar changes Windows DNS or routes, because tsnet uses a no-op router and DNS configurator (T4-21). The friend's PC is not routed through a VPN; only the local 127.0.0.1:<port> forward uses the tailnet.
- Direct peer-to-peer works for tsnet nodes over UDP, with Port left at 0 (T4-23). DERP is the always-available fallback over TCP 443 but is slower (T4-25, T4-28). Players on hard NAT should expect relayed latency. UDP Palworld over DERP is a performance risk worth testing later.
- Owners with Tailnet Lock enabled will see Worker/OAuth-minted friend keys come up as 'Locked out' until a signing node signs them (T4-30, T4-31). 1Salem should detect Tailnet Lock and either tell the owner to sign manually or document it as unsupported at first. Tailnet Lock is also mutually exclusive with device approval, which affects the choice of pre-approved keys.
- Mint friend keys as tagged, ephemeral, pre-approved, single-use auth keys (T4-12). Tagged devices have key expiry off by default (T4-11), so revocation must be explicit: delete the device or key through the API, or rely on the node being ephemeral.
- Do not give friends tsnet's ClientSecret or OAuth fields (T4-24). An OAuth secret on a friend machine could mint more keys. Keep OAuth on the Worker or the owner side and pass friends only one-off auth keys.

### Risks noted

- Blocker for any design that relies on cross-tailnet node sharing: tagged nodes on another tailnet cannot access shared machines (T4-18).
- Owner tailnets on the default allow-all policy defeat least privilege. 1Salem cannot enforce restrictions without editing or requiring edits to the owner's policy file, and editing it through the API needs a policy-file-scoped credential (outside this topic).
- Tailnet Lock owners: OAuth-created keys cannot be pre-signed (issue #10008 open as of fetch), so automated friend onboarding breaks without manual signing on a signing node.
- Grants are additive and have no deny rules, so any broader rule an owner adds later expands friend access. Policy tests catch this only if the owner keeps them in the file.
- Relayed (DERP) paths add latency and have unspecified throughput limits, which could degrade real-time game traffic. Peer relays help only if the owner runs a relay-capable node (1.86+) in the same tailnet.
- The return-traffic behaviour (T4-08) is inferred from the source on the main branch, not from product docs, so it could change between releases.

### Open questions

- Whether a tsnet (embedded, userspace) node can act as a peer relay server or use one. The peer-relay docs are silent; tsnet v1.102.4 is newer than 1.86, but I could not confirm this.
- Whether a tsnet node can be a Tailnet Lock signing node, or sign pre-authorised keys through LocalClient. The docs are silent.
- The docs do not say which protocols a bare-port ip selector (e.g. "25565" without a proto) matches. Use explicit "tcp:25565" / "udp:8211" to be safe.
- Whether the default allow-all for new tailnets is now written as grants or ACLs, and how to detect it reliably through the API. The docs describe the ACL form and the equivalent grant form separately.
- Whether rules that name a tag as a tagOwner are enough for an OAuth client (owning tag:1salem-host) to mint keys for tag:1salem-friend, i.e. the exact ownership chain for OAuth-created keys. The tags doc states the rule but gives no worked OAuth example.
- The tags page describes some constraints on changing or removing tags on auth-key devices, but I could not find exact behaviour for ephemeral tagged tsnet nodes that re-authenticate.
- The docs do not state DERP throughput limits or QoS, so relayed game-traffic performance is unknown.
- Whether Windows Defender Firewall prompts for the tsnet UDP socket; this is outside Tailscale docs and needs checking against Microsoft sources.
- Whether any policy mechanism, beyond simply having no grant, marks a tagged node receive-nothing at the control level. ShieldsUp is a client pref and nodeAttrs were not checked.

## T5. Cloudflare Workers, D1, secrets, rate limiting, local development

_Scope as researched:_ T5: Cloudflare Workers, D1, secrets, rate limiting and local development (checked 2026-09-23)

### Verified official facts

- **T5-01** On the Workers Free plan, an account gets 100,000 requests per day, and the count resets at 00:00 UTC. Past that, requests get Error 1027. A route can be set to fail open (skip the Worker) or fail closed (show an error page).
  - Source: <https://developers.cloudflare.com/workers/platform/limits/> — Limits table: Free 100,000/day, Paid no limit. The daily limit section mentions a midnight UTC reset, Error 1027 and the fail-open/fail-closed choice. Page last updated 2026-09-05.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-02** On the Free plan, each HTTP invocation gets 10 ms of CPU time. Time spent waiting on the network (fetch, database queries) does not count. The Paid plan defaults to 30 s and can be raised to 5 min with limits.cpu_ms.
  - Source: <https://developers.cloudflare.com/workers/platform/limits/index.md> — CPU table: Free 10 ms, Paid 5 min. The page says CPU time leaves out network waits, and cpu_ms defaults to 30000 with 300000 as the maximum.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-03** On the Free plan, a Worker can make 50 subrequests per invocation using fetch. Calls to internal Cloudflare services (KV, R2, D1) have their own limit, reported as 1,000 on Free. Paid defaults to 10,000 and can be raised to 10M through the limits config.
  - Source: <https://developers.cloudflare.com/workers/platform/limits/index.md> — A subrequest is any fetch or call to R2/KV/D1. There is a row for 'Subrequests to internal services' showing 1,000. The limit can be changed with the limits configuration.
  - Confidence: medium · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-04** On both plans, a Worker can be 64 MiB uncompressed, and there is no separate compressed-size limit. Startup must finish within 1 s, memory is 128 MB per isolate, the Free plan allows 100 Workers, and each Worker can have 64 environment variables of up to 5 KB each on Free.
  - Source: <https://developers.cloudflare.com/workers/platform/limits/index.md> — The limits table shows the same Worker size (64 MiB) for Free and Paid and says only the uncompressed bundle size counts. Page updated 2026-09-05. This differs from older 3 MB/10 MB figures.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T5-05** D1 Free limits: 10 databases per account, 500 MB per database, 5 GB total storage, 50 queries per Worker invocation, 100 bound parameters per query, 2 MB per row, 100 KB per SQL statement, 30 s per query, and 7 days of Time Travel. Paid allows 50,000 databases of up to 10 GB each and 1,000 queries per invocation.
  - Source: <https://developers.cloudflare.com/d1/platform/limits/> — The D1 limits table has these Free and Paid columns, and notes that the 10 GB per-database limit cannot be raised.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-06** The D1 free tier includes 5 million rows read per day, 100,000 rows written per day and 5 GB of storage, with a reset at 00:00 UTC. Once a daily limit is exceeded, D1 queries return errors until the reset.
  - Source: <https://developers.cloudflare.com/d1/platform/pricing/> — The pricing table lists 5 million reads and 100,000 writes per day. The page says you cannot run queries after exceeding the limits and the API returns errors.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-07** Each D1 database is single-threaded and runs one query at a time. Throughput therefore depends on how long queries take. Extra concurrent requests wait in a queue, and a full queue returns an 'overloaded' error.
  - Source: <https://developers.cloudflare.com/d1/platform/limits/> — The page gives examples: 1 ms queries allow about 1,000 per second, 100 ms queries about 10 per second. A full queue returns 'overloaded'.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T5-08** D1 batch() runs its statements one after another as a single SQL transaction. If any statement fails, the whole batch is aborted and rolled back. Outside a batch, D1 runs in auto-commit mode. The Worker API docs do not describe interactive BEGIN/COMMIT transactions.
  - Source: <https://developers.cloudflare.com/d1/worker-api/d1-database/> — The docs say batched statements are SQL transactions and a failure rolls back the entire sequence. D1 is described as auto-commit. Page updated 2026-06-22.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-09** D1 read replication is off unless you turn it on per database. If you do not use the Sessions API, every query still runs on the primary. The Sessions API (withSession and bookmarks) gives sequential consistency, including read-your-own-writes and monotonic reads, at no extra cost.
  - Source: <https://developers.cloudflare.com/d1/best-practices/read-replication/> — Replication is enabled in dashboard Settings. Without the Sessions API, queries run only on the primary. Replicas cost nothing extra. Page updated 2026-08-10.
  - Confidence: medium · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-10** Secrets are set with `npx wrangler secret put <KEY>`, or `wrangler versions secret put` for gradual deployments. They can also be set with `wrangler deploy --secrets-file` or in the dashboard. In code, secrets and vars both appear on `env`. The difference is visibility: a secret is hidden after it is set, while vars are plaintext.
  - Source: <https://developers.cloudflare.com/workers/configuration/secrets/> — The secrets page covers the put commands, the dashboard path and bulk upload. It says code sees secrets and vars the same way and only visibility differs. Page updated 2026-07-03.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-11** Sensitive values must not go in the Wrangler config (wrangler.toml or wrangler.jsonc). Anything under `vars` is plaintext and visible there.
  - Source: <https://developers.cloudflare.com/workers/configuration/secrets/index.md> — The page warns against using vars for sensitive information in the Wrangler config and says to use secrets instead. The configuration page also describes vars as plaintext.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-12** For local dev, secrets come from either .dev.vars or .env, never both. If .dev.vars exists, .env is ignored. A .dev.vars.<env> file replaces the generic .dev.vars entirely. .env files merge in this order of precedence: .env.<env>.local > .env.local > .env.<env> > .env. Setting CLOUDFLARE_LOAD_DEV_VARS_FROM_DOT_ENV=false turns off .env loading, and CLOUDFLARE_INCLUDE_PROCESS_ENV=true adds process environment variables. The docs say to gitignore .*vars* and .env*.
  - Source: <https://developers.cloudflare.com/workers/configuration/secrets/index.md> — The page says to choose one file type, and that a .dev.vars file stops .env values from being loaded. It also covers the env-specific behavior and both control variables.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: All correct except the gitignore patterns. The docs say to add '.dev.vars*' and '.env*' to .gitignore, not '.*vars*'. The rest checks out: use .dev.vars or .env but not both; if .dev.vars exists, values from .env are not included. If .dev.vars.<env> exists, only that file loads. .env files merge with precedence .env.<env>.local > .env.local > .env.<env> > .env. CLOUDFLARE_LOAD_DEV_VARS_FROM_DOT_ENV=false and CLOUDFLARE_INCLUDE_PROCESS_ENV=true behave as described.
- **T5-13** The `secrets.required` config property lists the secret names a Worker needs. `wrangler types` uses it to generate types, and `wrangler deploy` fails unless every required secret is set on the Worker. The docs recommend wrangler.jsonc for new projects, and some newer features work only with the JSON config.
  - Source: <https://developers.cloudflare.com/workers/wrangler/configuration/> — The configuration page shows a secrets.required example and says deploy validates required secrets. It also recommends jsonc. Page updated 2026-09-22.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T5-14** The Rate Limiting binding is configured in the `ratelimits` array with name, namespace_id (a positive integer as a string, unique within the account) and simple {limit, period}. period must be 10 or 60 seconds. Calling `await env.RL.limit({ key })` returns {success}. The binding needs Wrangler 4.36.0 or later.
  - Source: <https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/> — The page says the period must be 10 or 60, and limit() returns an object with a success boolean. A search snippet from the same page gives the Wrangler 4.36.0 minimum. Page updated 2026-04-23.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-15** Rate limits are counted separately in each Cloudflare location. Counters are cached on the machine and updated asynchronously. The API is deliberately permissive and eventually consistent, not an accurate accounting system. Bindings that share a namespace_id share counters, even across Workers. Cloudflare recommends keys such as API keys, user IDs or tenant IDs rather than IP addresses.
  - Source: <https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/index.md> — The page says limits are local to the Cloudflare location, the API is 'permissive, eventually consistent', and a shared namespace_id shares counters. It advises against using IP addresses as keys.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-16** In `wrangler dev`, Rate Limiting always runs as a local simulation. It cannot be switched to remote with remote:true, and Cloudflare says to test rate-limiting logic against the local simulation. Secrets, vars, Durable Objects and Workflows also always run locally.
  - Source: <https://developers.cloudflare.com/workers/development-testing/> — The page lists Rate Limiting as not supported for remote connections, because local sessions should not affect the deployed Worker's rate limits. Page updated 2026-08-20.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T5-17** There are other rate-limit options, both limited on Free. WAF rate limiting rules on Free allow 1 rule, counting by IP only, with a 10 s period, a 10 s mitigation timeout and only the Path and Verified Bot fields, and they are not precise. Durable Objects are on Free with the SQLite backend only: 100k requests, 5M rows read and 100k rows written per day. They could hold exact counters.
  - Source: <https://developers.cloudflare.com/waf/rate-limiting-rules/> — The WAF page (updated 2026-08-25) lists the Free availability. The Durable Objects pricing page (https://developers.cloudflare.com/durable-objects/platform/pricing/, updated 2026-08-25) says Free supports only the SQLite backend and gives the daily limits.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T5-18** The current Wrangler major version is 4. The npm 'latest' tag is 4.137.0 and the 'legacy' tag is 3.114.17. The package requires Node >=22.0.0. The docs say Wrangler supports the Current, Active and Maintenance Node releases, and recommend installing it per project as a devDependency.
  - Source: <https://registry.npmjs.org/wrangler/latest> — The npm registry lists version 4.137.0 with engines node '>=22.0.0'. The dist-tags endpoint shows legacy 3.114.17. The install doc (https://developers.cloudflare.com/workers/wrangler/install-and-update/, updated 2026-07-21) recommends a local install.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T5-19** `wrangler dev` runs locally by default, using Miniflare on the same workerd runtime as production. All bindings are simulated locally unless a binding sets remote:true, and AI bindings always run remotely. The Cloudflare Vite plugin is an alternative and also uses Miniflare.
  - Source: <https://developers.cloudflare.com/workers/development-testing/> — The page says local development is the default, Miniflare uses workerd, and bound resources are simulated locally. remote:true connects a binding to the deployed resource.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: wrangler dev runs locally by default through Miniflare on workerd, and bindings default to local simulations except AI. The Vite plugin also uses Miniflare. The overstatement is 'all bindings are simulated locally'. Browser Run, Workers AI, Vectorize and mTLS have no local simulation, Images is only partly supported, and remote:true is recommended for all of these. Some bindings cannot be remote at all: Durable Objects, Workflows, vars, secrets, static assets, Version Metadata, Analytics Engine, Hyperdrive and Rate Limiting. Local rate limits therefore never reflect production counters. Last updated Aug 20, 2026.
- **T5-20** By default, local D1, KV and R2 data is stored in `.wrangler/state` in the project folder, shared by Wrangler and the Vite plugin, and it persists between runs. `--persist-to <dir>` changes the location but must be passed on every command, including `wrangler d1 execute --local`.
  - Source: <https://developers.cloudflare.com/workers/development-testing/local-data/> — The page says both tools store local binding data in `.wrangler/state` and that --persist-to must be given every time. Page updated 2026-06-25.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T5-21** D1 migrations use three commands. `wrangler d1 migrations create` writes a .sql file into `migrations/`, `list` shows migrations not yet applied, and `apply` runs them. `apply` targets the local or remote database with --local or --remote. Applied migrations are recorded in the `d1_migrations` table, and migrations_dir, migrations_table and migrations_pattern can be configured. A migration that fails is rolled back, and earlier ones stay applied.
  - Source: <https://developers.cloudflare.com/d1/reference/migrations/> — The migrations page (updated 2026-06-08) covers the commands, config keys and d1_migrations table. The D1 commands page (https://developers.cloudflare.com/workers/wrangler/commands/d1/) covers rollback on error and a backup after applying.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T5-22** The docs do not say what `wrangler d1 execute` or `migrations apply` do when neither --local nor --remote is given. In the source on main, execute runs against the local database unless remote or preview is set. The default for migrations apply is still unclear.
  - Source: <https://raw.githubusercontent.com/cloudflare/workers-sdk/main/packages/wrangler/src/d1/execute.ts> — execute.ts calls executeRemotely only if remote or preview is set, and otherwise executeLocally. The commands docs do not state a default.
  - Confidence: low · Verification: not re-checked (not load-bearing)
- **T5-23** As of Aug 2026, Cloudflare replaced `@cloudflare/vitest-pool-workers` (npm 0.22.0) with `@cloudflare/vitest-plugin` (npm 1.2.4), which needs Vitest ^4.1.0. The docs say the API and configuration are otherwise unchanged. A codemod handles the migration: `npx @cloudflare/codemods vitest:pool-workers-to-vitest-plugin`. Mocking of outbound requests moves to `@msw/cloudflare`.
  - Source: <https://developers.cloudflare.com/workers/testing/vitest-integration/migration-guides/migrate-to-vitest-plugin/> — The migration guide (updated 2026-08-20) says the plugin replaces pool-workers. npm lists vitest-plugin 1.2.4 with peer vitest ^4.1.0, wrangler 4.137.0 and miniflare 5.20260921.0-alpha.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T5-24** Setup: `npm i -D vitest@^4.1.0 @cloudflare/vitest-plugin`, then add `cloudflareTest({ wrangler: { configPath: './wrangler.jsonc' } })` to the Vitest config's plugins. Tests import env from 'cloudflare:workers' and helpers from 'cloudflare:test'.
  - Source: <https://developers.cloudflare.com/workers/testing/vitest-integration/write-your-first-test/> — The page shows the install command and a defineConfig example using the cloudflareTest plugin from @cloudflare/vitest-plugin. Page updated 2026-08-20.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T5-25** Tests run inside the Workers runtime, 'fully-locally using Miniflare'. Each test file gets its own isolated storage. Files run concurrently, and `--max-workers=1 --no-isolate` makes them share storage. D1 migrations are applied in tests by calling readD1Migrations() in Node (from @cloudflare/vitest-plugin/config), passing the result in as a miniflare binding such as TEST_MIGRATIONS, and calling applyD1Migrations(db, migrations) from cloudflare:test in a setup file.
  - Source: <https://developers.cloudflare.com/workers/testing/vitest-integration/test-apis/> — applyD1Migrations applies pending migrations and records them in d1_migrations. Also see /vitest-integration/ (fully local), /isolation-and-concurrency/ (per-file storage) and /configuration/ (TEST_MIGRATIONS example).
  - Confidence: medium · load-bearing for our design · Verification: not independently re-checked
- **T5-26** Known limits of the Vitest integration: V8 coverage is not supported (use Istanbul), fake timers do not affect the KV, R2 or cache simulators, dynamic import() inside handlers fails, and custom Vitest environments or runners are not supported.
  - Source: <https://developers.cloudflare.com/workers/testing/vitest-integration/known-issues/> — The known-issues page (updated 2026-08-20) lists these. The configuration page says custom environments and runners are unsupported.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T5-27** Prebuilt workerd binaries, which Miniflare and local wrangler dev depend on, support Windows only on x86-64. They also need a CPU with SSE4.2 and CLMUL. Linux and macOS are supported on x86-64 and arm64.
  - Source: <https://github.com/cloudflare/workerd> — The README lists supported platforms, including Windows x86-64, and the CPU requirement of SSE4.2 plus CLMUL.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T5-28** `wrangler dev` uses a local D1 database by default. `wrangler d1 migrations apply <DB> --local` applies migrations to it. The docs say local D1 runs the same version of D1 that Cloudflare runs globally. With remote:true a real database is used, and changes to it cannot be undone.
  - Source: <https://developers.cloudflare.com/d1/best-practices/local-development/> — The page states the local-mode default, the --local migrations command, parity with the global D1 version, and the warning about remote changes. Page updated 2026-06-25.
  - Confidence: medium · Verification: not re-checked (not load-bearing)

### Additional facts found by the verifier

- Each D1 database is single-threaded and runs one query at a time. Throughput is roughly 1,000 queries/s at 1 ms per query and about 10/s at 100 ms. When a database is overloaded, requests queue, and a full queue returns errors.
  - Source: <https://developers.cloudflare.com/d1/platform/limits/> — The D1 limits page (last updated Apr 21, 2026) calls each database 'inherently single-threaded'. It gives throughput estimates by query time and warns that full queues cause errors. For design: keep control-plane queries indexed and short, avoid frequent sidecar heartbeats, and consider one database per shard if needed.
- D1 retries only read-only queries on its own (SELECT/EXPLAIN/WITH, up to 2 retries). Write queries are never retried, so the app has to retry idempotent writes itself with exponential backoff and jitter.
  - Source: <https://developers.cloudflare.com/d1/best-practices/retry-queries/> — The retry-queries page (last updated Aug 10, 2026) says D1 does not retry writes and recommends retries in app code. For design: invite redemption, approval, ticket issuance and revocation writes need idempotency keys and unique constraints.
- D1 bills and counts rows scanned, not rows returned, so full table scans count every row. Each indexed column adds one extra row write, and DDL counts toward both reads and writes.
  - Source: <https://developers.cloudflare.com/d1/platform/pricing/> — The pricing page's section on how rows are counted says this. For design: at 100k writes/day on Free, every index multiplies write cost, so any write-heavy table (heartbeats, audit logs) could hit the daily cap and make every D1 query fail until 00:00 UTC.
- SQLite-backed Durable Objects are available on the Workers Free plan. The daily limits are 100k requests, 13,000 GB-s, 5M rows read, 100k rows written and 5 GB, resetting at 00:00 UTC, and operations fail once a limit is exceeded.
  - Source: <https://developers.cloudflare.com/durable-objects/platform/pricing/> — The Durable Objects pricing page (last updated Aug 25, 2026) says only SQLite-backed DOs are on Free, with those limits. For design: a DO gives strongly consistent, single-writer state per owner or session (exact rate or quota counters, revocation), which the eventually consistent Rate Limiting binding cannot.
- Workers Web Crypto supports Ed25519 (and NODE-ED25519), ECDSA and HMAC sign/verify, plus a non-standard crypto.subtle.timingSafeEqual.
  - Source: <https://developers.cloudflare.com/workers/runtime-apis/web-crypto/> — The Web Crypto page (last updated Apr 23, 2026) lists these algorithms as supported and describes timingSafeEqual as timing-attack resistant. For design: the Worker can sign session tickets with Ed25519, and the Go sidecars can verify them offline with a public key, so no shared HMAC secret has to be on users' PCs.
- Wrangler config can declare required secret names (secrets.required). When declared, wrangler deploy and wrangler versions upload fail if any is missing, and the names are used for type generation. Secrets Store (beta) adds account-level secrets separate from per-Worker secrets.
  - Source: <https://developers.cloudflare.com/workers/configuration/secrets/> — The secrets page (last updated Jul 3, 2026) and the Wrangler configuration reference describe secrets.required with validation at deploy, and Secrets Store as beta. For design: a server-side Tailscale OAuth client secret or ticket-signing key can be required at deploy, and a missing one blocks the deploy instead of failing at runtime.

### Our reading (proposed, not an official fact)

- The Free-plan quotas (100k Worker requests/day, 100k D1 rows written/day, 10 ms CPU) apply to the whole Cloudflare account, so every owner shares them. Sidecars should not poll or send frequent heartbeats to the Worker; do the work on the tailnet data plane. Size the D1 writes from invites, approvals, audit logs and revocations against the 100k/day limit, or budget for Workers Paid ($5/month minimum) once there are many owners.
- Hitting a quota causes hard errors until 00:00 UTC (Error 1027 for the Worker, errors from D1). The owner app and friend sidecar should treat the control plane as unavailable at that point: new invites or approvals fail closed with a clear message. As built in Phase 1, an open session lasts at most until shortly after its current ticket (10 minutes) expires: if the ticket cannot be renewed, the running friend app ends the session, listener and live streams included, at the first monitor step after expiry (within about a minute even against a broker that hangs). This is fail closed; see `CONNECT_ARCHITECTURE.md` §16, and §21 D-3 for sessions no running app controls.
- Store the Tailscale OAuth client secret and the ticket-signing key only as Worker secrets (`wrangler secret put`), never in vars or wrangler.jsonc. List them in `secrets.required` so a deploy without them fails. For local dev use a gitignored `.dev.vars` holding fake values, and do not also create `.env`, because `.dev.vars` makes Wrangler ignore `.env`.
- The Rate Limiting binding suits coarse abuse throttling, such as invite redemptions per invite ID, approval attempts per owner ID, or auth-key mint requests per owner. Use owner, invite or ticket IDs as keys, not IP addresses. The binding is per-location and eventually consistent, so hard caps like 'max N auth keys per owner per day' must be enforced in D1 (or a SQLite-backed Durable Object).
- Use D1 batch() for atomic multi-statement steps, such as marking an invite redeemed, inserting a session row and writing an audit row. There is no documented interactive transaction, so any check-then-act logic must be written in SQL: conditional UPDATE ... WHERE state='pending', unique constraints, INSERT ... ON CONFLICT, and so on.
- Leave D1 read replication off at first, so revocation checks always read the primary. If replication is enabled later, run revocation and ticket checks through withSession('first-primary') or bookmarks to keep read-your-writes.
- Everything in this phase can run offline on the Windows x64 dev PC: `wrangler dev` with local Miniflare/workerd, D1 stored under `.wrangler/state`, `wrangler d1 migrations apply <DB> --local`, and Vitest with @cloudflare/vitest-plugin. That fits 'nothing touches a real tailnet'. Tailscale API calls from the Worker should be mocked (for example with @msw/cloudflare) behind an interface.
- Toolchain to pin: Node 22+ LTS, wrangler ^4 (4.137.0) as a devDependency, vitest ^4.1, and @cloudflare/vitest-plugin ^1.x (not the older vitest-pool-workers), with wrangler.jsonc as the config file. Add `.wrangler/`, `.dev.vars*` and `.env*` to .gitignore.
- Always pass --local or --remote explicitly to d1 execute and migrations apply in scripts and CI, because the docs do not state the default.
- Minting a Tailscale auth key from the Worker takes about 2 fetch subrequests (OAuth token, then key creation), well under the 50-per-invocation Free limit. Network wait time does not count toward the 10 ms CPU limit, but signing with WebCrypto should be measured to confirm it fits.

### Risks noted

- The Free quotas are shared across all owners on one Cloudflare account, and the D1 limit of 100k rows written per day is probably the first one a multi-owner rollout will hit. Going over means hard errors until 00:00 UTC.
- The docs do not say whether the Workers Rate Limiting binding is available or billed on the Free plan.
- Rate limiting is approximate and counted per Cloudflare location. Local tests only exercise a Miniflare simulation, so a limit that passes locally may behave differently in production.
- The testing toolchain changed recently: vitest-pool-workers was replaced by vitest-plugin in Aug 2026, Vitest 4.1 is required, and the current releases depend on a miniflare 5.x alpha. Expect churn and pin exact versions.
- The Workers limits page (updated 2026-09-05) shows large changes from older figures (64 MiB size, internal-service subrequest limit). Check again before relying on exact numbers.
- It is unclear whether `wrangler d1 migrations apply` targets the local or the remote database when no flag is given, so a migration could reach the real database by accident once the machine is logged in.
- If both `.dev.vars` and `.env` exist, `.env` values are silently ignored, which can cause confusing local-secret bugs.
- workerd prebuilt binaries support Windows only on x86-64 with an SSE4.2/CLMUL CPU. A contributor on Windows ARM64 could not run local dev or tests.
- D1 has no documented interactive transactions, and each database runs one query at a time, so long queries or heavy contention return 'overloaded' errors.

### Open questions

- Is the Workers Rate Limiting binding available on Workers Free, and is it billed? The rate-limit and pricing pages do not say.
- In Wrangler 4.137, does `wrangler d1 migrations apply` default to local or remote when neither --local nor --remote is given? The source for `d1 execute` defaults to local; the docs are silent for apply.
- Does `wrangler dev` in local mode need `wrangler login` or network access when a D1 binding has a real database_id? The docs suggest not, but never say so.
- Does @cloudflare/vitest-plugin load `.dev.vars` or `.env` secrets into test bindings automatically, or must they be supplied through the miniflare options? The configuration page does not say.
- Is the internal-service subrequest limit (1,000 on Free) the same thing as the D1 limit of 50 queries per invocation on Free, or separate? And does each statement in a batch() count as a query toward the 50?
- What is the exact subfolder layout inside .wrangler/state (for example v3/d1)? The pages fetched do not document it.
- Does the Miniflare Rate Limiting simulation enforce the configured limit and period exactly, and does it reset between Vitest test files?
- Is Cloudflare Secrets Store available on Free, and would it be a better home for the Tailscale OAuth credential than per-Worker secrets?
- Does the 10 ms Free CPU limit leave enough room for Ed25519 or HMAC ticket signing plus JSON parsing on each request? This needs measuring.

## T6. Cryptography interop: Web Crypto, .NET, Go

_Scope as researched:_ T6: Cryptography interop, with a Cloudflare Worker (Web Crypto) signing and .NET 8 and Go verifying. Covers ECDSA P-256 signature encoding, key import/export formats, HMAC and constant-time compares, randomness, DPAPI/ProtectedData on .NET 8, and JWS ES256.

### Verified official facts

- **T6-01** Cloudflare Workers Web Crypto supports ECDSA and HMAC for sign/verify, generateKey, importKey and exportKey. It also supports Ed25519, X25519, ECDH, RSA (PKCS1 v1.5, PSS, OAEP), AES (CTR, CBC, GCM, KW), HKDF, PBKDF2 and SHA-1/256/384/512 digests.
  - Source: <https://developers.cloudflare.com/workers/runtime-apis/web-crypto/> — The supported-algorithms table lists ECDSA and HMAC with sign/verify, generateKey, exportKey and importKey. Page last updated 23 Apr 2026.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-02** The Workers Web Crypto doc page does not list the ECDSA named curves or the key formats (jwk, pkcs8, spki, raw); it points readers to MDN. The workerd runtime source fills the gap: P-256, P-384 and P-521 are supported. Import accepts raw (public keys only), spki, pkcs8 and jwk. Export likewise, with raw export limited to public keys.
  - Source: <https://raw.githubusercontent.com/cloudflare/workerd/main/src/workerd/api/crypto/ec.c++> — The curve table maps P-256 to NID_X9_62_prime256v1 with a 32-byte size. An error message says raw export of elliptic curve keys is allowed only for public keys. spki, pkcs8 and jwk go through importAsymmetricForWebCrypto.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-03** workerd produces ECDSA signatures in the IEEE P1363 / WebCrypto layout: fixed-width r followed by s (64 bytes for P-256). It converts the DER output from BoringSSL into this layout. On verify, a signature whose length is not 2*rsSize is treated as invalid.
  - Source: <https://raw.githubusercontent.com/cloudflare/workerd/main/src/workerd/api/crypto/ec.c++> — The code manually decodes ASN.1, trims leading zeros and right-aligns r and s to rsSize. If signature.size() != rsSize*2, it returns an empty signature, which is then judged invalid.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-04** The W3C Web Crypto spec defines the ECDSA signature as r and s, each converted to an n-octet string and concatenated (n = 32 for P-256, so 64 bytes). Verify returns false when the signature length is not 2n. The first n octets are r and the next n are s. This is P1363 format, not DER.
  - Source: <https://w3c.github.io/webcrypto/> — The Sign steps convert r to an n-length octet string and append s of the same length. The Verify steps return false if the signature length is not n*2. Editor's Draft dated 11 Aug 2026.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-05** Per the W3C spec, ECDSA raw export is the SEC 1 section 2.3.3 uncompressed point (0x04\|\|X\|\|Y, 65 bytes for P-256). JWK export sets kty 'EC', crv, and base64url x and y (plus d for private keys). HMAC keys import as raw or jwk. The hash, e.g. {name:'ECDSA', hash:'SHA-256'}, is passed at sign/verify time through EcdsaParams.
  - Source: <https://w3c.github.io/webcrypto/> — The ECDSA Export Key section says raw is the uncompressed point per SEC 1 2.3.3. JWK export sets kty EC, crv, x, y, and d for private keys. EcdsaParams carries the hash.
  - Confidence: medium · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-06** Workers provide crypto.getRandomValues, which fills integer typed arrays (Int8 through BigUint64) with cryptographically sound random values, and crypto.randomUUID, which returns an RFC 4122 version 4 UUID.
  - Source: <https://developers.cloudflare.com/workers/runtime-apis/web-crypto/> — getRandomValues fills the passed ArrayBufferView with cryptographically sound random values. randomUUID generates a random version 4 UUID per RFC 4122.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-07** The W3C spec caps crypto.getRandomValues at 65,536 bytes per call; larger requests throw QuotaExceededError. The Workers doc page states no limit of its own.
  - Source: <https://www.w3.org/TR/WebCryptoAPI/> — getRandomValues throws QuotaExceededError when the requested byte length is over 65536. Web Cryptography Level 2, First Public Working Draft of 22 Apr 2025.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T6-08** crypto.subtle.timingSafeEqual(a, b) is a non-standard Workers extension. It takes ArrayBuffer or TypedArray arguments and returns a bool from a timing-attack-resistant comparison. Strings must first be encoded to bytes, e.g. with TextEncoder.
  - Source: <https://developers.cloudflare.com/workers/runtime-apis/web-crypto/> — The doc lists timingSafeEqual(a, b): bool as a non-standard extension that compares two buffers in a way resistant to timing attacks. Parameters are ArrayBuffer or TypedArray.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-09** timingSafeEqual throws an exception when the two buffers differ in length, and it is not constant-time with respect to length. Cloudflare's example avoids returning early on a length mismatch: it compares the user input against itself and negates the result.
  - Source: <https://developers.cloudflare.com/workers/examples/protect-against-timing-attacks/> — Buffers must be of equal length or an exception is thrown. The function is not constant time with respect to parameter length. Page last updated 23 Apr 2026.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-10** Workers Web Crypto has several non-standard additions: NODE-ED25519 (legacy EdDSA, where raw private-key import is unsupported), crypto.DigestStream (a WritableStream for streaming hashes), and MD5 as a digest-only legacy algorithm. Ed25519 and X25519 follow the Secure Curves spec.
  - Source: <https://developers.cloudflare.com/workers/runtime-apis/web-crypto/> — Footnotes cover NODE-ED25519 as legacy non-standard and the MD5 weakness warning. DigestStream is called a non-standard extension for streaming data.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T6-11** Cloudflare notes that Web Crypto differs significantly from the Node.js crypto API. node:crypto requires the nodejs_compat compatibility flag.
  - Source: <https://developers.cloudflare.com/workers/runtime-apis/web-crypto/> — The page says Web Crypto differs significantly from Node.js Crypto and recommends enabling nodejs_compat for Node compatibility.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T6-12** .NET DSASignatureFormat has two values: IeeeP1363FixedFieldConcatenation (0), which is fixed-size, and Rfc3279DerSequence (1), which is variable-size DER. It exists in .NET 5 through .NET 11, including net-8.0.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dsasignatureformat?view=net-8.0> — The fields table lists IeeeP1363FixedFieldConcatenation=0 (fixed size for a given key) and Rfc3279DerSequence=1 (variably sized). Monikers include net-8.0.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-13** .NET ECDsa.SignData overloads without a format parameter encode the signature as IeeeP1363FixedFieldConcatenation. This is the same format Web Crypto produces.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecdsa.signdata?view=net-8.0> — The Remarks on SignData(ReadOnlySpan<byte>, HashAlgorithmName) say it uses IeeeP1363FixedFieldConcatenation, and point to the DSASignatureFormat overload for other formats.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: The docs state 'will use IeeeP1363FixedFieldConcatenation' only on the span overloads added in .NET 7: SignData(ReadOnlySpan, HashAlgorithmName) and SignData(ReadOnlySpan, Span, HashAlgorithmName). For SignData(byte[]/Stream/byte[]+offset, HashAlgorithmName) the docs say nothing about format. In the .NET 8 source these call the abstract SignHash(hash), so the format is whatever the implementation produces. The built-in implementations produce P1363, which matches Web Crypto. To be safe, call the explicit DSASignatureFormat overloads.
- **T6-14** In the .NET 8 source, ECDsa.VerifyData overloads without a format parameter verify in IEEE P1363 format (VerifyHash to VerifyHashCore with IeeeP1363FixedFieldConcatenation). When Rfc3279DerSequence is passed, VerifyDataCore first converts DER to P1363 and returns false if conversion fails.
  - Source: <https://raw.githubusercontent.com/dotnet/runtime/release/8.0/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/ECDsa.cs> — VerifyHash(span, span) calls VerifyHashCore(hash, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation). ConvertSignatureToIeeeP1363 returning null leads to false.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: The conclusion is right but the call path differs. VerifyData(byte[]...) without a format calls the abstract VerifyHash(byte[], byte[]) directly. Only the span overload goes through VerifyHash(span), which calls VerifyHashCore(..., IeeeP1363). With Rfc3279DerSequence, VerifyDataCore only hashes. The DER-to-P1363 conversion (ConvertSignatureToIeeeP1363) happens inside VerifyHashCore, which returns false if the conversion fails.
- **T6-15** From .NET 5 on, including .NET 8, ECDsa.VerifyData has overloads that take an explicit DSASignatureFormat for byte[], Stream, ReadOnlySpan<byte> and byte[]+offset/count inputs. They return bool, throw ArgumentOutOfRangeException for an unknown format and CryptographicException on hashing or verification errors.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecdsa.verifydata?view=net-8.0> — The overload list includes VerifyData(ReadOnlySpan<byte>, ReadOnlySpan<byte>, HashAlgorithmName, DSASignatureFormat) with monikers net-5.0 through net-11.0. signatureFormat is documented as the encoding format for signature.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-16** On .NET 7+ (including 8), ECAlgorithm.ImportSubjectPublicKeyInfo(ReadOnlySpan<byte>, out int) accepts only binary DER SubjectPublicKeyInfo, so Base64 must be decoded first. PEM text should go through ImportFromPem, which accepts the labels PUBLIC KEY, PRIVATE KEY and EC PRIVATE KEY.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecalgorithm.importsubjectpublickeyinfo?view=net-8.0> — Remarks say only the binary DER encoding is supported: Base64 must be decoded and PEM should use ImportFromPem. Monikers are net-7.0 through net-11.0.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-17** ECParameters (fields Curve, D, Q) plus ECDsa.Create(ECParameters) can build a verifier from raw coordinates. Validate() requires Q.X and Q.Y to be present and the same length, and ECCurve.NamedCurves.nistP256 names the curve. The fetched ECDsa/ECAlgorithm docs showed no JWK import API, so a Web Crypto JWK (x, y base64url) must be mapped to Q.X/Q.Y by hand.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecparameters.validate?view=net-8.0> — Remarks say Q.X and Q.Y are required and must be the same length. For a named curve, D must match their length. Curve.Validate must succeed.
  - Confidence: medium · load-bearing for our design · Verification: **confirmed** by independent check
- **T6-18** System.Security.Cryptography.ProtectedData wraps Windows DPAPI and works on Windows only; other platforms throw PlatformNotSupportedException. It ships in System.Security.Cryptography.ProtectedData.dll. The docs list it under windowsdesktop-8.0 but not plain net-8.0, so a plain net8.0 app needs the NuGet package.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata?view=net-8.0> — The Important note says it is supported only on Windows because it depends on DPAPI, and throws PlatformNotSupportedException elsewhere. Monikers show windowsdesktop-8.0.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-19** The NuGet package System.Security.Cryptography.ProtectedData supports net8.0 with no dependencies and is Windows-only. The latest stable version is 10.0.12 (8 Sep 2026); the 9.0.x line is at 9.0.20.
  - Source: <https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/> — The target frameworks list .NET 8.0 and higher, .NET Standard 2.0 and .NET Framework 4.6.2. The readme says the package is supported only on Windows.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-20** DataProtectionScope.CurrentUser (0) lets only threads running as the current user unprotect the data. LocalMachine (1) lets any process on the computer unprotect it. Microsoft advises CurrentUser for most cases.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope?view=net-8.0> — Caution: LocalMachine allows multiple accounts to unprotect data; use it only if every account is trusted. For most situations use CurrentUser.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-21** Data protected with DPAPI can normally be decrypted only by a user with the same logon credentials on the same computer (roaming profiles are the exception). DPAPI adds a MAC to detect tampering. Under impersonation without a loaded profile, calls fail with 'Key not valid for use in specified state'.
  - Source: <https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata> — Remarks say only matching logon credentials can decrypt, and usually only on the encrypting computer, except with a roaming profile. A MAC is added to guard against tampering.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-22** The CRYPTPROTECT_PROMPTSTRUCT prompt-based flow in CryptProtectData is deprecated and will be removed in February 2027. Passing NULL uses the non-interactive path, but data protected with the prompt flow will fail to decrypt.
  - Source: <https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata> — Important: the prompt-based flow is deprecated and will be removed in February 2027. A NULL prompt struct uses the non-interactive path. Doc updated May 2026.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T6-23** Go crypto/ecdsa (go1.27.1) has two verify functions: Verify(pub, hash, r, s *big.Int) for raw r,s and VerifyASN1(pub, hash, sig) for DER. Neither is deprecated, and the package has no parser for a fixed-width r\|\|s signature. A Web Crypto signature must therefore be length-checked (64 bytes), split into two 32-byte big-endian halves, and passed to Verify. VerifyASN1 will not accept it.
  - Source: <https://pkg.go.dev/crypto/ecdsa> — Verify checks the signature in r, s. VerifyASN1 checks an ASN.1-encoded signature. Most applications should prefer VerifyASN1 over raw r, s.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-24** Go ecdsa Sign and Verify operate on a hash, not the message: the caller must hash first (SHA-256 for ES256). Hashes longer than the curve order are truncated. P-256 operations are constant-time.
  - Source: <https://pkg.go.dev/crypto/ecdsa> — The hash should be the result of hashing a larger message and is truncated if longer than the curve order. P224/P256/P384/P521 use constant-time algorithms.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-25** Go 1.25+ adds ecdsa.ParseUncompressedPublicKey(curve, data), which parses the SEC 1 2.3.3 uncompressed point (the same format as Web Crypto raw export), and PublicKey.Bytes(). The PublicKey X and Y big.Int fields are deprecated.
  - Source: <https://pkg.go.dev/crypto/ecdsa> — ParseUncompressedPublicKey parses the SEC 1 v2.0 2.3.3 (X9.62) uncompressed format. The X,Y comment is marked Deprecated and points to Bytes/ParseUncompressedPublicKey or x509.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-26** Go crypto/x509.ParsePKIXPublicKey(derBytes) parses DER SubjectPublicKeyInfo (Web Crypto 'spki' export, PEM label 'PUBLIC KEY') and returns *ecdsa.PublicKey for EC keys. SPKI DER is therefore readable by both .NET (ImportSubjectPublicKeyInfo) and Go.
  - Source: <https://pkg.go.dev/crypto/x509#ParsePKIXPublicKey> — Parses a PKIX ASN.1 DER SubjectPublicKeyInfo (RFC 5280 4.1). Return types include *ecdsa.PublicKey. go1.27.1.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-27** Go crypto/hmac: hmac.New(sha256.New, key) computes HMAC-SHA256, and hmac.Equal(mac1, mac2) compares MACs without leaking timing. The docs tell receivers to use Equal.
  - Source: <https://pkg.go.dev/crypto/hmac> — Receivers should use Equal to compare MACs to avoid timing side-channels. Equal has existed since go1.1.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T6-28** .NET CryptographicOperations.FixedTimeEquals(ReadOnlySpan<byte>, ReadOnlySpan<byte>) (net-8.0 included) returns false early only when the lengths differ; otherwise it runs in fixed time. It does not throw on a length mismatch, unlike Workers timingSafeEqual.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.fixedtimeequals?view=net-8.0> — It short-circuits and returns false only for different lengths. Fixed-time behavior is guaranteed in all other cases.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T6-29** .NET 6+ (including 8) has static one-shot HMACSHA256.HashData(key, source) overloads for byte[], ReadOnlySpan and Stream. The output is always 32 bytes.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.hmacsha256.hashdata?view=net-8.0> — HashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source) lists monikers net-6.0 to net-11.0. SHA-256 always produces a 256-bit HMAC.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T6-30** In JWS ES256 (RFC 7518 section 3.4), the signature is R and S each as 32-octet big-endian values, concatenated into a 64-octet R\|\|S. Validation MUST fail when the value is not 64 octets. No ASN.1 DER is used, so this matches Web Crypto output directly.
  - Source: <https://datatracker.ietf.org/doc/html/rfc7518#section-3.4> — R and S are 256-bit values turned into 32-octet big-endian sequences and concatenated. The JWS Signature must be 64 octets or validation fails. RFC 7518, May 2015.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-31** RFC 7518 requires HS256 keys of at least 256 bits. An EC JWK uses crv (P-256/P-384/P-521) and base64url x and y, each padded to the full coordinate length for the curve.
  - Source: <https://datatracker.ietf.org/doc/html/rfc7518#section-3.4> — A key the same size as the hash output or larger MUST be used for HS256. The x and y coordinate lengths must match the full coordinate size for the curve.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T6-32** The JWS signing input is ASCII(BASE64URL(UTF8(header)) \|\| '.' \|\| BASE64URL(payload)). The compact form adds '.' \|\| BASE64URL(signature). Base64url uses the RFC 4648 section 5 alphabet with trailing '=' removed and no line breaks.
  - Source: <https://datatracker.ietf.org/doc/html/rfc7515#section-5.2> — The signing input concatenates the base64url header and payload with a period. Base64url omits all trailing '=' characters. RFC 7515, May 2015.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-33** RFC 8725 (JWT BCP) requires verifiers to pin acceptable algorithms, with each key bound to exactly one algorithm. It also says to validate aud when an issuer serves multiple recipients, and recommends explicit typ to stop one token type substituting for another.
  - Source: <https://datatracker.ietf.org/doc/html/rfc8725#section-3.1> — Section 3.1: each key is used with exactly one algorithm, checked at operation time. Section 3.9: reject a missing or mismatched aud. Sections 3.11/3.12 recommend explicit typing. Feb 2020.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-34** Workers limits: each variable is capped at 5 KB. A Worker can have 64 variables on Free and 128 on Paid, with secrets and text counted together. CPU time per HTTP request is 10 ms on Free and up to 5 min on Paid (default 30 s).
  - Source: <https://developers.cloudflare.com/workers/platform/limits/> — The table shows 'Variables per Worker (secrets + text)' 64/128 and variable size 5 KB. CPU time is 10 ms Free and 5 min Paid. Last updated 5 Sep 2026.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-35** Worker secrets are set with 'wrangler secret put' or in the dashboard and read through env. Their values cannot be viewed after being set. Local development uses .dev.vars or .env. Secrets Store (beta) offers account-level secrets that can be shared across Workers.
  - Source: <https://developers.cloudflare.com/workers/configuration/secrets/> — Secret values are not visible in Wrangler or the dashboard after being defined. Secrets Store (beta) is bound to individual Workers. Last updated 3 Jul 2026.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T6-36** golang.org/x/sys/windows exports CryptProtectData and CryptUnprotectData with a DataBlob type (module v0.48.0, 31 Aug 2026). A Go sidecar could therefore call DPAPI directly. The Go standard library has no DPAPI wrapper.
  - Source: <https://pkg.go.dev/golang.org/x/sys/windows#CryptProtectData> — The package exports CryptProtectData/CryptUnprotectData(dataIn, name, optionalEntropy, reserved, promptStruct, flags, dataOut) and the DataBlob type. Version v0.48.0.
  - Confidence: medium · Verification: not re-checked (not load-bearing)

### Additional facts found by the verifier

- Ed25519 cannot be the shared signature algorithm for a .NET 8 host without a third-party library. Workers support Ed25519, but .NET's System.Security.Cryptography has no Ed25519 signing class, even in the net-11.0 listing. That listing adds only X25519DiffieHellman plus ML-DSA and SLH-DSA. Choose ES256 (ECDSA P-256 + SHA-256) or HMAC for tickets the .NET app must verify.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography?view=net-11.0> — The namespace class list shows ECDsa, MLDsa, SlhDsa and X25519DiffieHellman but no Ed25519/EdDsa class. The cross-platform crypto page (2026-08-03) lists only RSA, ECDSA, ECDH, DSA and PQC.
- Go's crypto/ecdsa (go1.27.1) has no P1363 API. SignASN1/VerifyASN1 use ASN.1 DER, and the legacy Verify takes r and s as *big.Int. A Go tsnet sidecar must split the 64-byte Web Crypto/.NET signature into r and s, or re-encode it as DER. ParseUncompressedPublicKey (Go 1.25+) reads SEC1 2.3.3 keys, which matches Web Crypto raw export.
  - Source: <https://pkg.go.dev/crypto/ecdsa> — SignASN1 returns an ASN.1-encoded signature. Most applications should use SignASN1 rather than r and s. ParseUncompressedPublicKey and PublicKey.Bytes were added in Go 1.25.
- ECDSA keys are not bound to a hash. The W3C spec takes the hash from EcdsaParams at call time, and workerd's chooseHash accepts any hash name, so P-256 with SHA-384 or SHA-512 is accepted. Every verifier (Worker, .NET, Go) must pin ES256 = P-256 + SHA-256 and must not trust an alg field inside the token.
  - Source: <https://raw.githubusercontent.com/cloudflare/workerd/main/src/workerd/api/crypto/ec.c++> — chooseHash requires only a non-null call-time hash ('ECDSA requires that the hash algorithm be specified at call time') and applies no curve/hash restriction. The alg-vs-crv check exists only for JWK import.
- Constant-time comparison handles unequal lengths differently on each side. Workers crypto.subtle.timingSafeEqual throws when lengths differ. .NET CryptographicOperations.FixedTimeEquals (.NET Core 2.1+, including 8) returns false early on unequal lengths and is fixed-time otherwise. Shared MAC/token comparison code must first normalize to fixed-length values, such as HMAC outputs or hashes.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.fixedtimeequals?view=net-8.0> — The method short-circuits and returns false only when left and right have different lengths. Fixed-time behavior is guaranteed in all other cases.
- W3C getRandomValues caps each call at 65536 bytes and throws QuotaExceededError above that. The Cloudflare page does not mention the cap. Code that generates large random buffers (key material, invite codes in bulk) must chunk its calls.
  - Source: <https://w3c.github.io/webcrypto/> — Section 10.1.1 (ED 11 Aug 2026): if byteLength exceeds 65536, throw QuotaExceededError. Only Int8 through BigUint64 typed arrays are accepted, otherwise TypeMismatchError.
- The Worker's ECDSA private signing key cannot be stored or imported as 'raw'. Web Crypto raw import yields only a public key with 'verify' usage, and raw export throws for private keys. Store the Worker secret as pkcs8 (DER, base64) or as a JWK with d. Distribute the public key as raw SEC1, spki or JWK x/y for .NET (ImportSubjectPublicKeyInfo or ECParameters) and Go.
  - Source: <https://w3c.github.io/webcrypto/> — ECDSA import, format raw: usages other than 'verify' throw SyntaxError and [[type]] is set to 'public'. Raw export throws InvalidAccessError if the key is not public.

### Our reading (proposed, not an official fact)

- The Worker's ECDSA P-256/SHA-256 signature is 64 bytes, laid out as r then s (P1363). ES256 JWS uses exactly the same bytes, so the Worker can emit a standard compact JWS with no DER conversion.
- In .NET 8, call VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation) and pass the format explicitly, even though it is already the default. Do not use Rfc3279DerSequence.
- In the Go sidecar: require len(sig)==64, split it into r=sig[:32] and s=sig[32:] with big.Int.SetBytes, compute sha256.Sum256(signingInput), then call ecdsa.Verify. Never use ecdsa.VerifyASN1 on Worker-issued signatures; it expects DER and will reject them.
- Distribute the Worker public key as base64 SPKI DER, from exportKey('spki'). .NET reads it with ImportSubjectPublicKeyInfo and Go with x509.ParsePKIXPublicKey, so no manual JWK-to-ECParameters mapping is needed. A 65-byte raw uncompressed point is the alternative (Go ParseUncompressedPublicKey; .NET via ECParameters with nistP256).
- Store the Worker's ECDSA private key as a pkcs8 or JWK string in a Worker secret. It fits well under the 5 KB variable limit. Import it with extractable=false.
- Include a 'kid' in the ticket header and ship multiple trusted public keys in the clients so the signing key can be rotated.
- Many independent owners share one signer, so ticket claims should include aud (the specific owner/bridge/server id), a jti from crypto.randomUUID(), and exp/nbf, plus an explicit typ (e.g. '1salem-ticket+jwt'). Pin alg=ES256 per key as RFC 8725 recommends. This stops a ticket for owner A being replayed against owner B's bridge.
- Generate invite codes, nonces and jti values in the Worker with crypto.getRandomValues or crypto.randomUUID. Keep single getRandomValues calls at or below 65,536 bytes.
- Any symmetric MAC or secret comparison in the Worker must use crypto.subtle.timingSafeEqual. Check the lengths first and handle a mismatch without throwing, following Cloudflare's pattern; .NET uses CryptographicOperations.FixedTimeEquals and Go uses hmac.Equal. HMAC keys must be at least 32 random bytes.
- The .NET 8 app (net8.0 or net8.0-windows) should reference the System.Security.Cryptography.ProtectedData NuGet package and use DataProtectionScope.CurrentUser to protect local secrets such as owner device credentials and cached tickets. The feature is Windows-only.
- DPAPI CurrentUser blobs are bound to the Windows user and machine. The app needs a re-enrolment or re-pair path for when a blob cannot be decrypted (new PC, reinstall, different user) rather than treating it as fatal.
- If the Go sidecar must read secrets that .NET protected, it can call DPAPI through golang.org/x/sys/windows under the same user. The simpler option is for the .NET host to unprotect the secret and hand it to the sidecar over a local channel, keeping DPAPI calls in one place.
- Build cross-language test vectors early: the Worker signs a fixed payload, and .NET and Go unit tests verify it and reject tampered, wrong-length and DER-encoded versions. No real tailnet is needed for this.

### Risks noted

- Signature-format mismatch fails silently. Web Crypto emits P1363 (r\|\|s), while Go VerifyASN1 and a .NET verifier set to Rfc3279DerSequence expect DER, so verification just returns false with no error. Cross-language test vectors are the only reliable guard.
- Workers timingSafeEqual throws when the lengths differ. An unguarded call can turn a bad token into an uncaught exception or HTTP 500, or leak through the error path. Callers must check lengths or catch.
- ECDSA signatures are malleable: (r, n-s) is also valid, and none of the fetched docs mention low-S normalization. This is general crypto knowledge, not from the fetched sources. Never use raw signature bytes as a unique ticket id or dedupe key; use a signed jti.
- The Workers docs do not list curves or key formats. The P-256, spki/pkcs8/jwk/raw and P1363 details come from workerd source on the main branch and the W3C spec. That source is authoritative but not a versioned doc guarantee.
- DPAPI data is effectively unrecoverable if the Windows user profile or credentials change, or on another machine without a roaming profile. Losing a protected owner credential must lead to re-pairing, not data corruption.
- ProtectedData throws PlatformNotSupportedException off Windows, and it is not in the base net8.0 shared framework, so the NuGet package reference is required.
- CryptProtectData's prompt-struct flow is removed in February 2027. .NET ProtectedData does not expose prompts, so the app is unaffected unless someone P/Invokes DPAPI with prompts.
- The Workers Free plan allows 10 ms CPU per request. The cost of an ECDSA P-256 sign in Workers is undocumented, so heavy per-request crypto on Free could hit the limit; measure it.
- Rotating the key means updating the Worker secret and redeploying. Clients without a multi-key or kid mechanism would reject all tickets during the rotation window.

### Open questions

- Does .NET 8 ECDsa (ECDsaCng on Windows) VerifyData with IeeeP1363FixedFieldConcatenation return false, or throw CryptographicException, when the signature is not exactly 64 bytes? The docs list CryptographicException generically; test it and length-check before calling.
- Is the Workers variable size limit of 5 KB also applied to secrets? The limits page's footnotes don't say.
- Does workerd enforce the W3C 65,536-byte quota on crypto.getRandomValues? The Workers docs are silent.
- Does workerd's raw ECDSA import accept compressed points (0x02/0x03), or only uncompressed 0x04 points? Neither the docs nor the code summary confirmed this; plan on uncompressed or SPKI.
- How much CPU does a single P-256 sign cost inside a Worker, measured against the 10 ms Free-plan limit? The Cloudflare docs don't say.
- Is Cloudflare Secrets Store still in beta in September 2026, and is it appropriate for holding the signing private key instead of a per-Worker secret?
- Should the .NET app pin the 8.0.x or the 10.0.x line of System.Security.Cryptography.ProtectedData? Both target net8.0; the NuGet page excerpt did not show 8.0.x release dates.
- Would the ticket format be a standard compact JWS (ES256), usable with off-the-shelf libraries, or a custom signed blob? This choice decides whether the RFC 7515/7518 and RFC 8725 rules apply directly.
- Can the tsnet sidecar's on-disk state (node key and so on) be protected with DPAPI, or does it need filesystem ACLs only? This is outside T6 and needs a tsnet-specific check.

## T7. Licensing, naming and Terms of Service

_Scope as researched:_ T7: Licensing, distribution and naming implications of embedding Tailscale (tsnet) in a commercial Windows app (researched 2026-09-23)

### Verified official facts

- **T7-01** The tailscale/tailscale repository, which contains tsnet, is licensed BSD-3-Clause. The copyright line is 'Copyright (c) 2020 Tailscale Inc & contributors'.
  - Source: <https://github.com/tailscale/tailscale/blob/main/LICENSE> — The LICENSE file is BSD 3-Clause, and the copyright is held by Tailscale Inc & contributors (2020).
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-02** When you ship binaries, BSD-3 requires the copyright notice, the list of conditions and the disclaimer to appear in the documentation or other materials that ship with the product.
  - Source: <https://github.com/tailscale/tailscale/blob/main/LICENSE> — Clause 2: binary redistributions must reproduce the notice, conditions and disclaimer in the documentation and/or other materials.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-03** The BSD-3 no-endorsement clause bars using the names of Tailscale or its contributors to endorse or promote derived products without prior written permission.
  - Source: <https://github.com/tailscale/tailscale/blob/main/LICENSE> — Clause 3: the holder's or contributors' names may not endorse or promote derived products without specific prior written permission.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-04** pkg.go.dev shows tailscale.com/tsnet at v1.102.4, published Sep 10, 2026, under BSD-3-Clause. The license was detected only from the module's root LICENSE file.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet?tab=licenses> — Header: Version v1.102.4, Published Sep 10, 2026, License BSD-3-Clause. The Licenses tab lists only tailscale.com@v1.102.4/LICENSE.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-05** tsnet runs a Tailscale node inside a Go program without the tailscaled daemon or system configuration, using a userspace gVisor TCP/IP stack. That matches the sidecar design and means no TUN/Wintun driver is needed.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet> — The overview says tsnet embeds a node without a separate tailscaled or system-level configuration, using a userspace TCP/IP stack (gVisor), with no root needed.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-06** The repo also has a PATENTS file. It gives a perpetual, royalty-free patent license limited to claims necessarily infringed by 'this implementation of Tailscale'. The license ends if you start patent litigation over that implementation.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/PATENTS> — The grant is perpetual, worldwide, no-charge and irrevocable. It does not cover further modifications and ends on instituting patent litigation.
  - Confidence: medium · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-07** Tailscale lists 8 US patents that may cover its products. The patents page does not mention any grant for open-source users.
  - Source: <https://tailscale.com/patents> — Lists US 11,575,661 through 12,341,772 and says products 'may be covered'. Last updated August 14, 2025.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T7-08** Current tailscale go.mod (main branch) declares go 1.27.1. It depends on gvisor.dev/gvisor (2026-09-15 pseudo-version), github.com/tailscale/wireguard-go (2026-09-11), golang.zx2c4.com/wireguard and wireguard/windows v1.0.1. It has no replace or toolchain lines.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/go.mod> — First lines: 'module tailscale.com', 'go 1.27.1'. gvisor v0.0.0-20260915211658-a6f909f08a72 and tailscale/wireguard-go v0.0.0-20260911194433-e3222a3340cd are listed.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T7-09** The tsnet dependency graph (depaware.txt) pulls in third-party modules including gvisor.dev/gvisor, tailscale/wireguard-go, golang.org/x/*, klauspost/compress, coder/websocket, fxamacker/cbor, go4.org, google/btree, gaissmai/bart, web-client-prebuilt and filippo.io/edwards25519. Some, such as dblohm7/wingoes and tailscale/go-winio, are Windows-only.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/depaware.txt> — The header reads 'tailscale.com/tsnet dependencies: (generated by github.com/tailscale/depaware)' and lists these module prefixes. Platform markers were read through a summarizer.
  - Confidence: medium · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-10** Tailscale's own license inventory lists these licenses: gvisor.dev/gvisor/pkg Apache-2.0, tailscale/wireguard-go MIT, coder/websocket ISC, klauspost/compress Apache-2.0, go4.org/mem Apache-2.0, google/btree Apache-2.0, golang.org/x/crypto BSD-3-Clause, tailscale.com BSD-3-Clause.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/licenses/tailscale.md> — The list items were copied verbatim, e.g. '[gvisor.dev/gvisor/pkg] ([Apache-2.0]...)' and '[github.com/tailscale/wireguard-go] ([MIT]...)'.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-11** The dependency inventory for the tailscale/tailscaled builds contains only permissive licenses (BSD-2, BSD-3, Apache-2.0, MIT, ISC). No GPL, LGPL or MPL entries were found. So there is no copyleft obligation to release 1Salem source.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/licenses/tailscale.md> — A scan for MPL, GPL and LGPL returned no entries. The license types present were BSD-3, BSD-2, Apache-2.0, MIT and ISC.
  - Confidence: medium · load-bearing for our design · Verification: **corrected** by independent check: The inventory is not purely BSD, Apache, MIT and ISC. It has 2 'Unknown' entries, github.com/golang/freetype/raster and /truetype, whose LICENSE offers a choice of the FreeType License or GPL v2 or later. There are no explicit GPL, LGPL or MPL entries. The file also covers the tailscale and tailscaled commands for Linux, BSD and the macOS option, not tsnet on Windows. It omits huin/goupnp (BSD-2-Clause), which tsnet does pull in. freetype does not appear in tsnet's depaware, so the no-copyleft conclusion probably holds for tsnet. It must still be checked by running go-licenses on the actual GOOS=windows sidecar build. It cannot be taken from this file.
- **T7-12** gVisor is Apache-2.0. Redistributors must give recipients a copy of the license, mark modified files, keep existing notices, and carry forward any NOTICE file. The gVisor repo root has no NOTICE file. Its LICENSE appendix also includes MIT and BSD texts for specific files.
  - Source: <https://raw.githubusercontent.com/google/gvisor/master/LICENSE> — Apache License 2.0, section 4(a)-(d), plus an appendix with MIT and Google BSD licenses for certain files. The repo listing at https://github.com/google/gvisor shows no NOTICE file.
  - Confidence: medium · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-13** Tailscale's wireguard-go fork is MIT-licensed. The copyright and permission notice must be included in all copies or substantial portions, which covers compiled binaries.
  - Source: <https://github.com/tailscale/wireguard-go/blob/tailscale/LICENSE> — MIT: 'The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.'
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-14** The Go standard library and runtime compiled into the sidecar are BSD-3 ('Copyright 2009 The Go Authors'). Their notice must also be reproduced when binaries are distributed, and the no-endorsement clause names Google.
  - Source: <https://go.dev/LICENSE> — BSD 3-Clause: binary redistributions must include the notice in accompanying materials. Google's and contributors' names may not endorse derived products.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-15** Tailscale builds its per-platform dependency license lists with google/go-licenses plus custom templates, refreshes them about weekly, and keeps them per release tag.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/licenses/README.md> — Lists are 'generated using the go-licenses tool' over the binaries' Go packages, 'updated roughly every week'. Release tags show a given release's dependencies.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T7-16** The official Windows client also ships Wintun under a separate 'Prebuilt Binaries License' and wireguard-windows (MIT). These apply only if a TUN driver or DLL is shipped, which a userspace tsnet sidecar should not need.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/licenses/windows.md> — Additional dependencies: 'Wintun ... ([Prebuilt Binaries License]...)' and 'wireguard-windows ... ([MIT]...)'. The Go bindings golang.zx2c4.com/wintun are MIT.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T7-17** Only the client and daemon code is open source. The Windows, macOS and iOS GUI wrappers and the coordination server are closed. The hosted coordination service is governed by Tailscale's terms, not by BSD-3.
  - Source: <https://github.com/tailscale/tailscale> — README: GUI wrappers on non-open-source platforms 'are themselves not open source'. tailscale.com/opensource calls the coordination server closed source.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: The GitHub README does not support most of this claim. It says the repo holds 'the majority' of Tailscale's open-source code, and that GUI wrappers on non-open-source platforms (macOS, iOS, Windows) are not open source. The closed coordination server is stated on tailscale.com/opensource, which also lists the DERP relay server and the Android client as open source. So 'only client and daemon' is too strong. The Terms of Service (last updated Aug 25, 2026) define the admin console and coordination server as 'Hosted Software' in the proprietary 'Tailscale Solution', so the hosted service falls under the ToS, not BSD-3.
- **T7-18** 'Tailscale' is a registered trademark of Tailscale Inc., and 'WireGuard' is a registered trademark of Jason A. Donenfeld, as stated in Tailscale's legal and press page footers.
  - Source: <https://tailscale.com/legal> — Footer: 'Tailscale is a registered trademark of Tailscale Inc. \| WireGuard is a registered trademark of Jason A. Donenfeld.'
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T7-19** No public Tailscale brand or trademark usage guidelines were found. tailscale.com/brand and /brand-guidelines return 404, the Legal Hub lists no brand document, and the press page offers only a media kit ZIP and press@tailscale.com.
  - Source: <https://tailscale.com/press> — The press page has 'Download our media kit' and a press contact, with no stated usage rules. The Legal Hub lists no trademark or brand guideline.
  - Confidence: medium · load-bearing for our design · Verification: not independently re-checked
- **T7-20** The Channel Partner Agreement (last updated Jan 31, 2025) bars partners from registering any domain or trademark containing Tailscale Marks. It also bars using marks confusingly similar to, or a reference to, those marks. This is a signal of how Tailscale treats third-party naming.
  - Source: <https://tailscale.com/channel-partner-agreement> — Section 2.6: Partner will not register a domain or trademark containing Tailscale Marks, or use anything 'confusingly similar to, or a reference to' them.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T7-21** WireGuard's trademark policy forbids using WireGuard marks in a company or product name. Without authorization, it also lists using them to indicate 'similarity, compatibility, or relatedness' as prohibited. Permission is available by email.
  - Source: <https://www.wireguard.com/trademark-policy/> — Section B, 'When can't I use The WireGuard Marks?', includes product names and compatibility claims. Authorization is 'easily available' via wireguard-trademark-usage@zx2c4.com.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T7-22** The Tailscale Terms of Service (last updated Aug 25, 2026) apply to Self-Serve and Free customers. A person accepts them by creating or administering a Tailscale account and using the Services. The Terms are versioned publicly on GitHub.
  - Source: <https://raw.githubusercontent.com/tailscale/terms-and-conditions/main/terms/index.md> — Preamble: 'By creating or administering a Tailscale account and accessing or using our Services, you agree to be bound by these Terms'. Applies to 'Free customers'.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T7-23** ToS 2.1 grants use of the Services only for the Customer's own personal use or internal business purposes. The Client Software license is limited, revocable and non-transferable, and only for use with the Tailscale Solution.
  - Source: <https://tailscale.com/terms> — Access 'solely for your own personal use or internal business purposes', plus a non-transferable license to use Client Software on Client Endpoints.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T7-24** ToS 2.3 forbids commercially exploiting any part of the Services; modifying, copying or creating derivative works of the Services; framing, mirroring, selling, reselling, renting or leasing use of the Services; and building competing products.
  - Source: <https://tailscale.com/terms> — Section 2.3: 'commercially exploit any part of the Services; ... create derivative works from ... frame, mirror, sell, resell, rent or lease use of the Services'.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T7-25** The ToS has no explicit carve-out for open-source components. It defines the Tailscale Solution, including Client Software, as 'proprietary software systems'. Its only open-source mention is Tailscale's 5.4 promise not to introduce copyleft that affects customer data.
  - Source: <https://raw.githubusercontent.com/tailscale/terms-and-conditions/main/terms/index.md> — The Tailscale Solution definition covers Hosted Software and Client Software. Searching for 'open source' found only 5.4 (copyleft commitment).
  - Confidence: medium · load-bearing for our design · Verification: not independently re-checked
- **T7-26** The ToS defines 'End Users' as individuals whose communications or activity are routed through or processed by the Services. 'Permitted User' means an individual the Customer authorizes to access the Customer's tailnets. A friend of an owner would fall into one of these categories under that owner's account.
  - Source: <https://tailscale.com/terms> — End Users: individuals whose communications 'are ... routed through ... or otherwise processed by the Services'. Permitted User: 'authorized by Customer to access ... Customer tailnets'.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T7-27** Under ToS 2.4 the Customer (tailnet owner) alone is responsible for its tailnet, for obtaining consents from Permitted and End Users, for any legally required notices, and for those users' acts. Under 2.5, a Permitted User's breach counts as the Customer's breach.
  - Source: <https://tailscale.com/terms> — 2.4: responsible for 'ensuring ... consents (including from Permitted Users and End Users)' and 'the acts of your Permitted Users and End Users'.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T7-28** The ToS defines 'Integrations' to include third-party software that connects to or embeds Tailscale functionality through an API or SDK. Such Third Party Services are governed by the Customer's agreement with that provider, and Tailscale takes no responsibility for them.
  - Source: <https://raw.githubusercontent.com/tailscale/terms-and-conditions/main/terms/index.md> — 1.8: software that will 'connect to or integrate with the Tailscale Solution ... including via an API or SDK'. 2.6: governed by your agreements with that provider.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T7-29** The enterprise Main Service Agreement (last updated Aug 25, 2026) is narrower: use is solely for internal business purposes under an Order Form, with the same 2.3 restrictions on resale, renting and commercial exploitation.
  - Source: <https://tailscale.com/msa> — 2.1: access 'solely for your internal business purposes'. 2.3 repeats 'frame, mirror, sell, resell, rent or lease use of the Services'.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T7-30** The free Personal plan is described as only for non-commercial use, and 'playing games with friends' is an example use. It allows up to 6 users and includes 50 tagged resources, with extras at $1/month each. Tailnets on a custom domain count as business use.
  - Source: <https://tailscale.com/pricing> — Personal plan 'only suitable for non-commercial use' such as 'playing games with friends'. '50 tagged resources included; add more for $1/month each'.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T7-31** On Apr 8, 2026 Tailscale retired Personal Plus and folded it into the free Personal plan (6 users, single tailnet, node sharing supported). The free-plans page does not itself state the non-commercial restriction.
  - Source: <https://tailscale.com/docs/account/manage-plans/free-plans-discounts> — The page gives 6 free users in a single tailnet and was last updated April 8, 2026. The pricing-v4 blog (Apr 8, 2026) announces the Personal Plus retirement.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T7-32** A user takes a paid or limited seat when they first log in to the admin console or authenticate a device. A tagged resource is a device owned by a tag rather than a user and is counted separately from seats.
  - Source: <https://tailscale.com/pricing> — FAQ: 'A user occupies a vacant seat when they first log in ... or when they first authenticate a device'. A tagged resource is 'owned by a tag rather than a user identity'.
  - Confidence: medium · load-bearing for our design · Verification: not independently re-checked
- **T7-33** The Acceptable Use Policy (last updated June 30, 2025) has no specific clauses on reselling, proxying for others, multiple accounts, automated account creation or game servers. It covers illegal and harmful activity.
  - Source: <https://tailscale.com/tailscale-aup> — No clauses on resale, VPN/proxy for others, account multiplication or distributed software. The policy focuses on phishing, malware and unauthorized access.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T7-34** Operating Tailscale for third parties has a formal channel: the Channel Partner Agreement defines an MSP as a partner that sells and provides Managed Services (administration, management, billing, support) to and on behalf of Customers.
  - Source: <https://tailscale.com/channel-partner-agreement> — MSP: 'a Partner that sells and provides Managed Services to and on behalf of any Customer'. Managed Services include 'administration, management, billing, or support'.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T7-35** The Tailnets API is in alpha and available on all plans. An organization can have at most 10 tailnets in total, and more needs a sales contract. API-only tailnets have no human users, support only tagged devices, and require an OAuth client with the 'tailnets' scope.
  - Source: <https://tailscale.com/docs/features/tailnets-api> — 'currently in alpha', 'available for all plans', 'up to ten total tailnets', more 'requires a sales contract', only tagged devices. Last updated Aug 25, 2026.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T7-36** This comes from Tailscale's own blog (Oct 29, 2025), not a formal policy: early testers embed Tailscale in their products by creating one tailnet per customer.
  - Source: <https://tailscale.com/blog/multiple-tailnets-alpha> — Early testers 'embed Tailscale directly into their own products, spinning up a tailnet per customer'. API tailnets contain only tagged devices.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T7-37** Node sharing cannot serve friends who have no Tailscale account. Recipients must be an Owner, Admin or IT admin of their own tailnet, which means accepting the ToS, and machines cannot be shared with tags or tagged machines.
  - Source: <https://tailscale.com/kb/1084/sharing> — Recipient must be 'an Owner, Admin, or IT admin of a tailnet'. 'A machine cannot be shared with a tag ... only users can accept machine shares.' Updated Jan 5, 2026.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T7-38** Tailscale collects metadata from every connected node (device name, OS, hostname, IP, public key, connection logs). For customer tailnets the Customer is the data controller and Tailscale the processor.
  - Source: <https://tailscale.com/privacy-policy> — Collects 'device name; ... host name; IP address; cryptographic public key; ... logs describing connections'. 'Customers are the data controllers'. Updated Aug 25, 2026.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T7-39** Only tailnet Owners, Admins, IT admins or Network admins can generate auth keys. Tailscale recommends OAuth clients for creating keys programmatically through the API. Tagged devices have key expiry disabled by default.
  - Source: <https://tailscale.com/kb/1085/auth-keys> — Key creation is limited to Owner, Admin, IT admin and Network admin roles, and OAuth clients are the programmatic alternative. Last updated June 30, 2026.
  - Confidence: medium · load-bearing for our design · Verification: not independently re-checked
- **T7-40** The Special Terms (sections dated 2024-11-29 and 2025-03-25) cover plan discounts, Mullvad and Community Projects only. They say nothing about tsnet, OAuth or API credentials, resellers, or running tailnets for others.
  - Source: <https://tailscale.com/special-terms> — Sections are Plan Discounts, Mullvad VPN Service and Community Projects. No embedding, OAuth or MSP terms.
  - Confidence: medium · Verification: not re-checked (not load-bearing)

### Additional facts found by the verifier

- Tailscale's ToS (last updated Aug 25, 2026) grants use only for 'your own personal use or internal business purposes'. Section 2.3 bans commercially exploiting any part of the Services and selling, reselling, renting or leasing use of them. A single 1Salem-owned tailnet or OAuth credential that mints keys for many unrelated owners and friends therefore risks breaching the ToS. A model where each owner brings their own tailnet and credentials fits the terms better. The ToS also defines third-party 'Integrations' that a customer chooses to activate.
  - Source: <https://tailscale.com/terms> — Section 2.1 limits use to personal or internal business purposes. Section 2.3 prohibits 'commercially exploit any part of the Services' and 'sell, resell, rent or lease use'.
- The free Personal plan is described as only suitable for non-commercial use, and it names 'playing games with friends' as an example. It allows up to 6 users and 3 ACL groups, 50 tagged resources to start, and 1,000 minutes per month for ephemeral resources. Tailnets on public domains (Gmail, Apple, personal GitHub) are auto-enrolled in it. Tagged ephemeral friend nodes would draw on these per-owner quotas.
  - Source: <https://tailscale.com/pricing> — Plan highlights: Up to 6 users, Up to 50 tagged resources to start, 1,000 mins per month for ephemeral resources. FAQ: free plan 'only suitable for non-commercial use'.
- The attribution or NOTICE bundle must be generated from 1Salem's own GOOS=windows sidecar build, for example with go-licenses as Tailscale does. It should not be copied from Tailscale's inventory. tsnet on Windows links golang.zx2c4.com/wintun and wireguard/windows/tunnel/winipcfg (MIT), tailscale/certstore (MIT) and huin/goupnp (BSD-2-Clause, (c) 2013 John Beisley). goupnp is absent from licenses/tailscale.md, and the inventory versions lag go.mod.
  - Source: <https://raw.githubusercontent.com/tailscale/tailscale/main/tsnet/depaware.txt> — depaware lists 'W golang.zx2c4.com/wintun', 'W winipcfg' and 'github.com/huin/goupnp from ...internetgateway2'. go.mod requires huin/goupnp v1.3.0. The licenses README says the lists come from go-licenses and are updated roughly weekly.
- If the design ever sets tsnet.Server.Tun or uses a real Windows TUN, it must ship wintun.dll under the Wintun Prebuilt Binaries License. That license allows only unmodified official binaries, redistributed alongside software that uses the permitted API, and bars modification or reverse engineering. Staying on the default gVisor netstack avoids this extra license and the driver entirely.
  - Source: <https://git.zx2c4.com/wintun/tree/prebuilt-binaries-license.txt> — Tailscale's licenses/windows.md lists Wintun under 'Prebuilt Binaries License'. That license requires unmodified official wintun.dll, distributed only with software using the Permitted API.
- Tailscale's CLI and systray dependency set includes github.com/golang/freetype, which is dual-licensed under the FreeType License or GPL v2 or later (shown as 'Unknown' in the inventory). The tsnet dependency graph does not include it. The sidecar should import only tsnet and related library packages, not tailscale CLI or systray packages, to avoid pulling in dual or GPL-option code.
  - Source: <https://raw.githubusercontent.com/golang/freetype/master/LICENSE> — The freetype LICENSE offers a choice of the FreeType License or GPL v2 or later. tailscale.md lists freetype/raster and truetype as Unknown. A search of tsnet/depaware.txt found no 'freetype'.
- The Tailscale ToS defines the 'Tailscale Solution', including the 'Client Software' installed on endpoints, as proprietary. It does not explicitly carve out open-source code from section 2.3's no-modify and no-derivative-works restrictions. How BSD-3 code rights interact with ToS obligations when a modified or embedded client (tsnet) connects to the hosted coordination server is not addressed. This is an open question for counsel before shipping.
  - Source: <https://tailscale.com/terms> — Section 1.17 covers proprietary software 'including ... Hosted Software' (admin console and coordination server) and 'Client Software'. No open-source carve-out was found in the ToS text.

### Our reading (proposed, not an official fact)

- The code licenses are not a blocker. Everything tsnet pulls in is permissive (BSD-2/3, MIT, ISC, Apache-2.0), with no GPL, LGPL or MPL found (T7-10, T7-11). So statically linking tsnet into a closed-source sidecar is allowed, as long as the notices ship with it.
- Ship a THIRD-PARTY-NOTICES file with the installer and link it from an About or Licenses screen. It should include: Tailscale BSD-3 (T7-01/02) plus PATENTS (T7-06), the Go BSD-3 (T7-14), the full gVisor Apache-2.0 LICENSE with its MIT/BSD appendix (T7-12), wireguard-go MIT (T7-13), and every other module in the actual windows/amd64 build. Generate it with google/go-licenses as Tailscale does (T7-15), and re-run it on every tsnet version bump.
- Tailscale publishes no brand guidelines (T7-19), and BSD-3 bars endorsement use of its name (T7-03). So keep 'Tailscale' and 'WireGuard' out of the product name, feature name, logo and domain. At most, use a plain-text reference such as 'networking powered by the open-source Tailscale library'. Include a trademark attribution line, and do not use the Tailscale logo without written permission (T7-18, T7-20, T7-21). '1Salem Connect' as a name is fine.
- Avoid naming WireGuard in UI or marketing unless permission is obtained by email. WireGuard's policy lists compatibility or relatedness claims as needing authorization (T7-21).
- The code license (BSD) and the right to use Tailscale's hosted coordination server (ToS) are separate things. Every tsnet node that talks to Tailscale's control plane is using the Services under the terms of whoever owns that tailnet (T7-17, T7-22, T7-23).
- The option most consistent with the ToS is for each owner to use their own tailnet and own Tailscale account. The owner is then the Customer, and 1Salem is an 'Integration' or Third Party Service (T7-28). The owner remains responsible for friend consents and notices and for friends' conduct (T7-26, T7-27).
- A design where 1Salem holds a single vendor OAuth credential, or one vendor tailnet, that brokers connectivity for many unrelated owners is high risk. It conflicts with 2.1 ('own personal use or internal business purposes') and 2.3 ('commercially exploit', 'resell, rent or lease use of the Services') (T7-23, T7-24). It would need a commercial agreement with Tailscale: a sales contract for more than 10 API tailnets, or a Channel Partner/MSP arrangement (T7-34, T7-35).
- The Personal plan is non-commercial only. A private owner hosting games for friends matches Tailscale's own example (T7-30). 1Salem itself must not run production control-plane credentials on a free Personal tailnet if 1Salem is a commercial product.
- Friends get in as tagged devices minted from the owner's tailnet, so they need no account and no Tailscale install. Node sharing cannot do this because recipients must administer their own tailnet (T7-37). Tagged friend nodes count against the owner's 50 tagged-resource allowance on Personal (T7-30, T7-32). Ephemeral or one-off keys and cleanup matter for that limit.
- Friends' device metadata (hostname, IP, OS, connection logs) goes to Tailscale under the owner's account (T7-38). The friend-side UI should show a short notice, and the owner-side UI should remind owners of their consent and notice duties under ToS 2.4 (T7-27).
- Storing owners' OAuth client secrets in the Cloudflare Worker and minting keys for them looks like 'administration/management' done on their behalf (T7-34, T7-39). Limit OAuth scopes to the minimum, keep the owner in control of revocation, and get a legal read on whether this makes 1Salem an MSP.
- Do not bundle Wintun or wireguard-windows. The userspace tsnet sidecar does not need them, which avoids the separate Wintun Prebuilt Binaries License (T7-05, T7-16).

### Risks noted

- ToS 2.3 forbids modifying, copying or creating derivative works 'of the Services', and 'Services' includes Client Software described as proprietary. There is no explicit open-source carve-out (T7-24, T7-25). The BSD-3 license clearly permits using the code, but the ToS and BSD-3 overlap for BSD-3 builds that connect to Tailscale's coordination server. Get legal review, or written confirmation from Tailscale, before commercial launch.
- 'Commercially exploit any part of the Services' (T7-24) is broad. A paid 1Salem product whose feature depends on Tailscale's hosted service could be read as commercial exploitation even when each owner is the Customer. The docs are silent on third-party apps built on tsnet.
- A vendor-owned tailnet or credential serving many owners likely breaches 2.1 and 2.3 without a contract. The Tailnets API caps self-serve use at 10 tailnets, and more requires a sales contract (T7-35). That blocks a 'one API tailnet per owner' design at any real scale without Tailscale sales involvement.
- The Personal plan's non-commercial restriction applies to the tailnet owner (T7-30). An owner who runs a monetized or business game server may need a paid plan, and 1Salem cannot vouch for owners' plan compliance.
- No public Tailscale brand guidelines exist (T7-19). Any use of the name beyond plain factual reference carries trademark risk, and there is no published safe harbor.
- The tagged-resource quota (50 on Personal, $1/month each beyond that, T7-30) could be exhausted if friend nodes are not ephemeral or cleaned up. That would put unexpected charges on owners.
- The tsnet dependency set changes weekly (T7-08, T7-15). A stale notices file becomes a license-compliance gap, so regenerating it in CI is required.

### Open questions

- Is a friend running a tagged tsnet node in an owner's tailnet a 'Permitted User' (which might count toward the 6-user limit or seat billing) or only an 'End User'? The ToS definitions (T7-26) and pricing FAQ (T7-32) do not settle this for tagged devices a person controls.
- Does anyone besides the tailnet owner, such as the friend, have to accept Tailscale's ToS? The preamble binds people who create or administer an account 'and' use the Services. The docs do not address non-account individuals running tagged nodes.
- Does a commercial vendor that stores owners' OAuth credentials and mints auth keys for them count as providing 'Managed Services' (MSP) under the Channel Partner Agreement? That agreement describes partners who sell the Tailscale Solution, which 1Salem would not do.
- Would Tailscale consider a paid third-party app built on tsnet, using each owner's own tailnet, to be 'commercially exploiting' the Services? Neither the ToS nor the tsnet docs discuss third-party distribution of tsnet apps. Written confirmation from Tailscale is advisable.
- Tailscale's media kit ZIP (linked from tailscale.com/press) may contain logo usage rules. It was not downloaded, because downloading files needs the user's explicit permission. Contacting press@tailscale.com is the alternative.
- What is the exact license set for a GOOS=windows build of the 1Salem sidecar? The depaware platform markers and full transitive list came through a summarizer. Run go-licenses on the real build to confirm there is no copyleft dependency, for example in Windows-only modules.
- Pricing and terms for Tailnets API use beyond 10 tailnets, and whether API-only tailnets can be used to serve third-party end users, are not published (T7-35).
- Does the PATENTS grant (T7-06) cover builds that use tsnet unmodified but combine it with 1Salem code? It covers 'this implementation' and excludes further modifications. This needs a counsel read if patent risk matters.
- Did the ToS entity change (Tailscale US Inc. for accounts created after Sept 3, 2024) affect anything relevant? It was seen only in a summarizer paraphrase and was not verified verbatim.

## T8. Minecraft, Palworld, Windows named pipes, network inspection

_Scope as researched:_ T8: Game protocols (Minecraft Java, Palworld), Windows named-pipe IPC (Win32, go-winio, .NET 8), tsnet UDP surface, and read-only Windows network/proxy inspection

### Verified official facts

- **T8-01** Minecraft Java server-port defaults to 25565 and is the TCP port the server listens on. The wiki page reflects Java Edition 26.3.
  - Source: <https://minecraft.wiki/w/Server.properties> — server-port described as the TCP port the server listens on, default 25565. Page reflects Java Edition 26.3.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-02** Query is a separate UDP protocol (GameSpy4/UT3-compatible). It is off by default (enable-query=false), and query.port defaults to the same number as server-port.
  - Source: <https://minecraft.wiki/w/Query> — Query is a UDP protocol meant to be UT3/GameSpy compatible. Enable it with enable-query=true. query.port defaults to server-port. Page last edited 27 Dec 2025.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T8-03** Minecraft RCON is a separate TCP port, 25575 by default. It is unencrypted and not recommended over untrusted networks. It will not start without a password.
  - Source: <https://minecraft.wiki/w/Server.properties> — rcon.port default 25575. The page warns against RCON over untrusted networks because it is unencrypted. A blank rcon.password stops RCON from starting as a safeguard.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-04** Java Edition runs over TCP. After the Handshake, the Status, Login, Configuration and Play states all use the same TCP connection. Current protocol version is 777 (Minecraft 26.3).
  - Source: <https://minecraft.wiki/w/Java_Edition_protocol/Packets> — The server accepts TCP clients and exchanges packets over the TCP connection. The Handshake intent switches state on that connection. Protocol 777 = 26.3. Page edited Sept 2026.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-05** Server List Ping (the multiplayer-menu status) opens its own short-lived TCP connection to the game port with handshake intent 1, exchanges status and ping, then closes. Clients may also resolve _minecraft._tcp SRV records.
  - Source: <https://minecraft.wiki/w/Java_Edition_protocol/Server_List_Ping> — Client opens TCP to 25565, sends Handshake state 1, Status Request/Response JSON, optional ping/pong, then disconnects. SRV _minecraft._tcp supported. Edited 23 Sep 2026.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-06** The Handshake's Server Address field holds the hostname or IP the client typed (e.g. 127.0.0.1). The vanilla server does not use it, so a friend connecting to a localhost relay address still works on vanilla.
  - Source: <https://minecraft.wiki/w/Java_Edition_protocol/Packets> — Server Address field: hostname or IP (e.g. localhost/127.0.0.1) used to connect. Note says the vanilla server does not use this information.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: The field does not always hold what the user typed. The Packets page says it is usually the name after SRV record resolution. It is the literal host the user typed only in 1.17 and during server list ping (MC-278651). From 1.17.1 on, if the user typed a literal IP, the client tries a reverse DNS lookup and puts the result in the field if one comes back, so '127.0.0.1' may arrive as 'localhost' or a machine name. The conclusion still stands: the vanilla server does not use Server Address or Server Port, so a localhost relay works on vanilla. Proxies or plugins that route by hostname could behave differently (inference).
- **T8-07** Online-mode encryption runs end-to-end between client and server. The client encrypts the shared secret with the server's RSA key and then both sides use AES/CFB8. Authentication is client-to-sessionserver 'join' plus server-to-sessionserver 'hasJoined'. A byte-preserving TCP relay only carries ciphertext, so it needs no protocol awareness (the relay conclusion is an inference).
  - Source: <https://minecraft.wiki/w/Java_Edition_protocol/Encryption> — Shared secret is RSA-encrypted with the server's public key and then AES/CFB8 on both sides. Client POSTs to sessionserver join; server GETs hasJoined with username, serverId and an optional ip.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-08** hasJoined carries the ip parameter only when prevent-proxy-connections=true. With that set, a player is kicked if the ISP/AS the server sees differs from the one seen by the auth server. Behind a relay the server sees the bridge's address, so that setting would kick relayed friends (inference).
  - Source: <https://minecraft.wiki/w/Java_Edition_protocol/Encryption> — The ip parameter is included by vanilla only when prevent-proxy-connections is true in server.properties. Server.properties: kicks on ISP/AS mismatch with the auth server.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-09** Since 1.20.5 there is a Transfer mechanism: a clientbound Transfer packet tells the client to connect to a different host and port, and the next Handshake uses intent 3. This would bypass a single-port relay if a server or plugin uses it.
  - Source: <https://minecraft.wiki/w/Java_Edition_protocol/Packets> — Handshake intent 3 means the client is connecting because of a Transfer packet. The clientbound Transfer packet names a different host and port.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T8-10** Palworld's dedicated server uses UDP port 8211 by default (changeable). Router port forwarding and separate Windows firewall settings are needed. Docs version 1.0.4.
  - Source: <https://docs.palworldgame.com/getting-started/requirements> — Requirements list 'UDP Port 8211 (Default, Changeable)', router port forwarding, and separate Windows firewall and router settings. Guide version 1.0.4.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-11** Palworld -port=8211 changes the listen port. -publicip and -publicport only advertise an address or port for community servers and do not change the listening port. -publiclobby makes the server a community server. The page never says TCP or UDP.
  - Source: <https://docs.palworldgame.com/settings-and-operation/arguments> — -port changes the listening port. -publicport does not change the port the server listens on. -publiclobby sets up a community server. Neither TCP nor UDP is mentioned.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-12** Palworld RCON is enabled in the config file, with default port 25575. It is deprecated and scheduled to stop working in an upcoming update. The docs say it is not designed for direct Internet exposure.
  - Source: <https://docs.palworldgame.com/api/rcon/> — Page says to change the RCON port from the default 25575 if needed, that RCON is deprecated and will stop functioning, to use the REST API, and not to expose it to the Internet.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T8-13** The Palworld REST API is enabled with RESTAPIEnabled=True and uses HTTP Basic Auth. It is not designed to be exposed directly to the Internet and is recommended for LAN use only.
  - Source: <https://docs.palworldgame.com/api/rest-api/palwold-rest-api> — Security scheme is HTTP Basic Auth. Warns that Internet exposure may allow unauthorized manipulation and recommends LAN use.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-14** The official Palworld docs (1.0.4, 0.7.3, 0.3.12.0) list RESTAPIPort and RCONPort but give no numeric REST default. 8212 comes only from third-party guides and this repo's existing defaults.
  - Source: <https://docs.palworldgame.com/settings-and-operation/configuration> — RESTAPIPort described only as 'Listening port for the REST API', with no default shown. Third-party guides (e.g. xgamingserver.com) state TCP 8212.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T8-15** UDP 27015 (Steam query/server browser) is reported only by third-party sources (LAST RESORT: blogs/forums/hosting guides), not by the official Palworld docs.
  - Source: <https://xgamingserver.com/docs/palworld/ports-reference> — Third-party hosting docs and Steam forum threads say UDP 27015 serves Steam query/community list. A search of docs.palworldgame.com found no mention.
  - Confidence: low · Verification: not re-checked (not load-bearing)
- **T8-16** tsnet v1.102.4 (published 10 Sep 2026): Listen is tailnet-only. ListenPacket needs network udp/udp4/udp6 and an explicit IP:port, with no wildcard. Dial connects to tailnet addresses. A UDP bridge must bind the node's Tailscale IP explicitly.
  - Source: <https://pkg.go.dev/tailscale.com/tsnet> — ListenPacket: network must be udp/udp4/udp6, addr must be ip:port, 'IP must be specified'. Listen announces only on the Tailscale network.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-17** With a NULL security descriptor, CreateNamedPipe's default ACL gives full control to LocalSystem, Administrators and the creator owner. It also gives read access to Everyone and Anonymous.
  - Source: <https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea> — lpSecurityAttributes NULL means default SD: full control to LocalSystem, administrators, creator owner, plus read access for Everyone and anonymous. Doc updated 2025-07-01.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-18** FILE_FLAG_FIRST_PIPE_INSTANCE (0x00080000): creating the first instance succeeds, and creating another instance with the flag fails with ERROR_ACCESS_DENIED. This is the built-in defence against pipe-name squatting.
  - Source: <https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea> — If you create multiple instances with this flag, the first succeeds and the next fails with ERROR_ACCESS_DENIED.
  - Confidence: high · load-bearing for our design · Verification: **corrected** by independent check: The flag behaviour is confirmed: 0x00080000; the first instance succeeds and the next fails with ERROR_ACCESS_DENIED. Microsoft does not call it a defence against squatting; that is an inference. It only detects a squatter: if another process created the pipe name first, the legitimate server's create fails, which is a denial of service but not an impersonation. ERROR_ACCESS_DENIED is also returned when instances use different type, access, instance count or timeout values, so the error alone does not prove squatting. In .NET, NamedPipeServerStream adds this flag automatically when maxNumberOfServerInstances == 1, or via PipeOptions.FirstPipeInstance (524288).
- **T8-19** PIPE_REJECT_REMOTE_CLIENTS (0x8) rejects remote clients automatically. The default, PIPE_ACCEPT_REMOTE_CLIENTS (0x0), accepts them and checks only the security descriptor.
  - Source: <https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea> — PIPE_ACCEPT_REMOTE_CLIENTS 0x0: remote connections accepted and checked against the SD. PIPE_REJECT_REMOTE_CLIENTS 0x8: remote clients automatically rejected.
  - Confidence: high · load-bearing for our design · Verification: **confirmed** by independent check
- **T8-20** If the Server service (SMB) is running, all named pipes can be reached remotely. For local-only pipes Microsoft says to deny NT AUTHORITY\NETWORK or use local RPC. Clients can open remote pipes as \\ServerName\pipe\name.
  - Source: <https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipes> — If the server service is running, all named pipes are accessible remotely. For local-only use, deny NT AUTHORITY\NETWORK or switch to local RPC.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T8-21** Creating a server instance of an existing pipe requires FILE_CREATE_PIPE_INSTANCE in its DACL. FILE_GENERIC_WRITE includes that right because FILE_APPEND_DATA shares the same bit, so grant individual rights. The logon SID in the DACL blocks remote users and other sessions.
  - Source: <https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights> — The DACL must allow FILE_CREATE_PIPE_INSTANCE. Because FILE_APPEND_DATA equals FILE_CREATE_PIPE_INSTANCE, FILE_GENERIC_WRITE allows creating the pipe. Use the logon SID to stop remote or other-session users.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T8-22** SDDL layout is O:owner G:group D:flags(ACE)(ACE)… The D flag 'P' sets SE_DACL_PROTECTED. Well-known SID constants include NU (network logon user), SY (LocalSystem), BA (Administrators), OW (Owner Rights), WD (Everyone), AN (Anonymous) and IU (interactive).
  - Source: <https://learn.microsoft.com/en-us/windows/win32/secauthz/security-descriptor-string-format> — Format tokens O:, G:, D:, S:, where dacl flag 'P' means SE_DACL_PROTECTED. The SID Strings page defines NU as the group added for network logons (LOGON32_LOGON_NETWORK).
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T8-23** go-winio v0.6.2 (published 9 Apr 2024) API: PipeConfig{SecurityDescriptor string (SDDL), MessageMode bool, InputBufferSize, OutputBufferSize int32}. ListenPipe(path, *PipeConfig) returns (net.Listener, error) and the pipe must not already exist. DialPipe(path, *time.Duration) defaults to a 2s timeout. DialPipeContext, DialPipeAccess and DialPipeAccessImpLevel are also provided.
  - Source: <https://raw.githubusercontent.com/microsoft/go-winio/main/pipe.go> — SecurityDescriptor holds an SDDL string. MessageMode matters mainly for CloseWrite. ListenPipe doc says the pipe must not already exist. Default dial timeout 2 seconds. Version from pkg.go.dev.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T8-24** go-winio's ListenPipe creates the first instance with FILE_CREATE disposition, which fails if the name exists and so blocks squatting. Every instance gets FILE_PIPE_REJECT_REMOTE_CLIENTS. An empty SecurityDescriptor falls back to the system default ACL via RtlDefaultNpAcl.
  - Source: <https://raw.githubusercontent.com/microsoft/go-winio/main/pipe.go> — makeServerPipeHandle: if first, disposition FILE_CREATE. typ starts as FILE_PIPE_REJECT_REMOTE_CLIENTS. With no SD, it calls rtlDefaultNpAcl. Read from the main branch, not the v0.6.2 tag.
  - Confidence: medium · load-bearing for our design · Verification: not independently re-checked
- **T8-25** .NET 8 NamedPipeServerStreamAcl.Create(pipeName, direction, maxInstances, transmissionMode, options, inBuf, outBuf, PipeSecurity?, inheritability, additionalAccessRights) is in System.IO.Pipes.AccessControl.dll. If options includes CurrentUserOnly, the pipeSecurity argument is ignored and replaced with an ACL where the current user is sole owner with full control.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstreamacl.create?view=net-8.0> — Remarks: with CurrentUserOnly, pipeSecurity is ignored and a custom PipeSecurity is used, with the current Windows user as only owner with full control. Applies to .NET 6-11.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T8-26** PipeOptions.CurrentUserOnly: a server accepts only clients created by the same user, and a client connects only to a server created by the same user. On Windows it checks both user account and elevation level. The implementation uses the token Owner SID, and the client compares it with the pipe's owner SID.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-8.0> — Docs: 'On Windows, it verifies both the user account and elevation level.' Runtime source (release/8.0) uses WindowsIdentity.GetCurrent().Owner. The client's ValidateRemotePipeUser compares owner SIDs.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T8-27** .NET 8 NamedPipeServerStream on Windows never sets PIPE_REJECT_REMOTE_CLIENTS: pipeModes contains only transmission-mode bits. FILE_FLAG_FIRST_PIPE_INSTANCE is added only when maxNumberOfServerInstances == 1 or PipeOptions.FirstPipeInstance (0x00080000) is passed.
  - Source: <https://raw.githubusercontent.com/dotnet/runtime/release/8.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Windows.cs> — pipeModes = transmissionMode<<2 \| transmissionMode<<1. openMode ORs FIRST_PIPE_INSTANCE only when maxInstances==1, plus (int)options. PipeOptions.cs defines FirstPipeInstance=0x00080000.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T8-28** Get-NetRoute (NetTCPIP module) reads the IP routing table without changing it. Filters include -AddressFamily, -InterfaceIndex/-Alias, -PolicyStore (ActiveStore/PersistentStore) and -DestinationPrefix, where 0.0.0.0/0 or ::/0 selects default-gateway routes.
  - Source: <https://learn.microsoft.com/en-us/powershell/module/nettcpip/get-netroute> — Gets IP route info including prefixes, next hops and metrics. Example 4 reads NextHop for the default route 0.0.0.0/0.
  - Confidence: high · load-bearing for our design · Verification: not independently re-checked
- **T8-29** Get-DnsClientServerAddress (DnsClient module) returns the DNS server IPs configured on each interface and can filter by -AddressFamily, -InterfaceAlias or -InterfaceIndex. .NET offers NetworkInterface.GetIPProperties().DnsAddresses and GatewayAddresses (IPv4 gateways) as a read-only alternative.
  - Source: <https://learn.microsoft.com/en-us/powershell/module/dnsclient/get-dnsclientserveraddress> — Gets DNS server IPs from interface TCP/IP properties. IPInterfaceProperties docs list DnsAddresses and GatewayAddresses (IPv4).
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T8-30** netsh winhttp 'show proxy' and 'set proxy' are now marked deprecated; the replacements are 'show advproxy' and 'set advproxy'. 'import proxy source=ie', 'reset proxy' and 'set advproxy' modify state and must be avoided in a read-only diagnostic.
  - Source: <https://learn.microsoft.com/en-us/windows/win32/winhttp/netsh-exe-commands> — show proxy is deprecated in favour of show advproxy. reset proxy sets WinHTTP to DIRECT. import proxy copies the IE settings. Doc date 2023-12-11.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T8-31** WinHttpGetIEProxyConfigForCurrentUser reads the user's IE/WinINet proxy settings (auto-detect, PAC URL, proxy, bypass) for the active connection. It should not be called from a service that is not impersonating a logged-on user.
  - Source: <https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpgetieproxyconfigforcurrentuser> — Retrieves the IE proxy config for the current user and active connection. Should not be used in a non-impersonating service. The caller must GlobalFree the strings.
  - Confidence: high · Verification: not re-checked (not load-bearing)
- **T8-32** The WinINet per-user proxy values are under HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings: ProxyEnable (DWORD), ProxyServer, ProxyOverride (<local> = bypass local) and AutoConfigURL. Source is an archived 2015 Microsoft blog.
  - Source: <https://learn.microsoft.com/en-us/archive/blogs/askie/how-to-configure-proxy-settings-for-ie10-and-ie11-as-iem-is-not-available> — Lists key path and values: AutoConfigURL REG_SZ, ProxyEnable REG_DWORD 0/1, ProxyServer 'name:port', ProxyOverride exclusions plus <local>.
  - Confidence: medium · Verification: not re-checked (not load-bearing)
- **T8-33** In .NET 8 on Windows, HttpClient.DefaultProxy reads HTTP_PROXY, HTTPS_PROXY, ALL_PROXY and NO_PROXY first and otherwise the user's proxy settings. Proxy URLs may be http, https, socks4, socks4a or socks5.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.defaultproxy?view=net-8.0> — On Windows, uses environment variables or, if they are unset, the user's proxy settings. socks5://[user:pass@]host[:port] is supported.
  - Confidence: high · Verification: not re-checked (not load-bearing)

### Additional facts found by the verifier

- Palworld: friends on Xbox or PS5 cannot join a dedicated server by typing an IP and port. The server has to be deployed as a community server for them. The docs group Xbox with the Windows PC version from the Microsoft Store. A localhost relay that friends reach by IP will therefore only work for Steam and Mac clients.
  - Source: <https://docs.palworldgame.com/getting-started/about-server/> — Docs 1.0.4: dedicated servers are joined by IP and port, but Xbox or PS5 players need a community server. The Xbox group includes the Microsoft Store PC version.
- Palworld RCON is officially deprecated and scheduled to stop working in an upcoming update. Server management should use the REST API, which is LAN-only and uses Basic Auth. RCON port 25575 is the same number as Minecraft's RCON default.
  - Source: <https://docs.palworldgame.com/api/rcon/> — Docs 1.0.4 banner: RCON is deprecated; use the REST API; RCON is scheduled to stop functioning in an upcoming update. Default port 25575, changeable.
- On a named pipe, FILE_GENERIC_WRITE includes FILE_CREATE_PIPE_INSTANCE, because FILE_APPEND_DATA uses the same bit. A pipe DACL that gives the client generic write therefore also lets it create server instances and impersonate the server. Microsoft says to grant the individual rights instead. It also recommends the logon SID in the DACL to keep remote users and other terminal sessions out.
  - Source: <https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights> — FILE_GENERIC_WRITE enables permission to create the pipe, so use individual rights. Use the logon SID to block remote or other-session users.
- .NET PipeOptions.CurrentUserOnly limits server and client to the same user, and on Windows it also checks elevation level. If the elevated .NET manager and a non-elevated Go sidecar (or the reverse) use CurrentUserOnly, they will not connect. The Go side cannot use this .NET flag, so it needs its own owner or ACL check.
  - Source: <https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-8.0> — CurrentUserOnly (536870912): the server accepts only clients created by the same user, and the client connects only to such servers. On Windows it checks both the user account and elevation level.
- On Windows, .NET 8 NamedPipeServerStream never sets PIPE_REJECT_REMOTE_CLIENTS. The pipe mode comes only from the transmission mode, and PipeOptions go into the open mode. Remote SMB clients are therefore accepted by default and only the DACL keeps them out; the DACL should deny NETWORK or use the logon SID. FIRST_PIPE_INSTANCE is added only when maxNumberOfServerInstances == 1. Source code, not a documented API promise.
  - Source: <https://raw.githubusercontent.com/dotnet/runtime/release/8.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Windows.cs> — pipeModes = transmissionMode<<2 \| transmissionMode<<1 with no reject flag. openMode adds FILE_FLAG_FIRST_PIPE_INSTANCE when maxNumberOfServerInstances == 1. CurrentUserOnly builds a FullControl ACL for the owner SID only.
- Minecraft server-ip is blank by default, so the game port listens on all interfaces (LAN and any NIC), whatever relay is used. Query, when enabled, is a separate UDP listener on 25565 by default, the same number as the TCP game port. A TCP-only relay will not carry Query. Keeping the server off the LAN would mean setting server-ip=127.0.0.1, which goes against the wiki's advice to leave it blank.
  - Source: <https://minecraft.wiki/w/Server.properties> — server-ip blank means it listens on all available IP addresses; the wiki recommends leaving it empty. query.port 25565 is 'The UDP port number query listens on'.

### Our reading (proposed, not an official fact)

- Minecraft (T8-01, T8-04 to T8-07): a plain TCP relay of server-port is enough for a friend to join a vanilla server. It must allow several concurrent or back-to-back connections, because each status ping and each login is its own TCP connection. It must never forward query (UDP) or RCON (TCP 25575).
- Minecraft (T8-08): check that the owner's server.properties has prevent-proxy-connections=false before offering Connect for that server, or warn the owner. The server will see every friend as the bridge's source address, so IP bans and IP logs stop working. Put the per-friend identity and revocation in the sidecar or session-ticket layer, not in the game.
- Minecraft (T8-09): Transfer packets or proxy networks (Velocity/Bungee) can send a client to another host:port that the single-port bridge cannot reach. Treat that as unsupported.
- Palworld (T8-10, T8-16): the game traffic is UDP 8211, so both sidecars need a UDP bridge. The friend side binds 127.0.0.1:<port> UDP and keeps a session table keyed by the client's source address, with idle timeouts. The owner side must call tsnet ListenPacket on its explicit Tailscale IP, because wildcards are rejected.
- Palworld (T8-12, T8-13): the bridge allow-list must be only the game port. Never forward the REST API (Basic Auth, 8212 in this repo) or RCON (25575). Pocketpair says neither is built for Internet exposure, and RCON is being removed.
- Existing IPC: src\ServerManager.Agent\NamedPipeAgentServer.cs grants BUILTIN\Users ReadWrite\|CreateNewInstance and uses MaxAllowedServerInstances, so FILE_FLAG_FIRST_PIPE_INSTANCE is never set. .NET also never sets PIPE_REJECT_REMOTE_CLIENTS (T8-27). As a result, any local user can create competing instances of the pipe (squatting). If the Server service is running, network logons by any account in BUILTIN\Users are checked only against that ACL (T8-20). Worth hardening before new Connect IPC reuses this pattern.
- Go sidecar IPC: winio.ListenPipe already gives squatting protection (FILE_CREATE) and rejects remote clients (T8-24). Pair it with an explicit protected SDDL, e.g. D:P(A;;GA;;;<owner user SID>)(A;;GA;;;SY) plus an explicit deny for NU, rather than the default ACL, which gives Everyone and Anonymous read (T8-17, T8-22).
- .NET side of the same IPC: use PipeOptions.FirstPipeInstance on the first instance, an explicit PipeSecurity with no BUILTIN\Users and a deny for NETWORK (NU), and only the individual rights needed rather than GENERIC_WRITE (T8-21). Only rely on CurrentUserOnly if both processes always run at the same elevation (T8-26).
- Proving 'no whole-PC VPN': show a read-only before/after of Get-NetRoute (default routes), Get-DnsClientServerAddress, netsh winhttp show advproxy and the HKCU Internet Settings values. Never call set, import or reset (T8-28 to T8-32).
- Control-plane HTTP from .NET follows HTTP(S)_PROXY and the user's proxy (T8-33). Worker calls may go through a corporate or system proxy, and diagnostics should report which path was used.

### Risks noted

- CurrentUserOnly checks elevation level (T8-26). If the Agent runs elevated or as a service and the Go sidecar or UI runs unelevated, the connection is refused. An explicit user-SID SDDL avoids this.
- Existing named-pipe ACL (BUILTIN\Users + CreateNewInstance, no FIRST_PIPE_INSTANCE, remote clients not rejected) allows pipe squatting by any local user and exposes the pipe to SMB network logons when the Server service runs.
- The official Palworld docs never say what transport the -port argument uses and give no default REST port. 8212 and the Steam query port 27015 are only confirmed by third-party sources.
- Palworld's UDP behaviour through a localhost relay is undocumented: whether the client accepts 127.0.0.1 direct connect, whether it uses one 5-tuple, and how it handles NAT rebinding. UDP bridging also has to handle session tables, idle expiry and possible MTU or fragmentation limits inside tsnet/WireGuard, which were not researched here.
- prevent-proxy-connections=true on a Minecraft server will kick every relayed friend. All friends share the bridge's IP, so vanilla ban-ip or any per-IP throttling would hit them all together.
- go-winio's latest tagged release is v0.6.2 (April 2024). The FILE_CREATE and REJECT_REMOTE behaviour was read from the main branch and must be re-checked at the pinned version.
- netsh winhttp show proxy is deprecated. Diagnostics should prefer show advproxy but fall back gracefully on older builds that lack it.

### Open questions

- Palworld: what is the official default RESTAPIPort (third-party says TCP 8212)? Is UDP 27015 (Steam query) needed for direct IP:port joins, or only for the community server list? Official docs 1.0.4 are silent on both.
- Palworld: does the game client join through a UDP relay at 127.0.0.1:<port>? Does the server tolerate every player arriving from the same bridge source IP with different ports? Needs a lab test; there is no documentation.
- Minecraft: does the vanilla server apply per-IP connection or login throttling that could trip when several friends share one bridge IP? The wiki pages fetched do not say.
- Minecraft modded stacks (Forge/NeoForge handshake address suffix, Velocity/Bungee forwarding): do any require the Handshake Server Address or the real client IP? Only vanilla behaviour is documented here.
- Do Get-NetRoute and Get-DnsClientServerAddress need admin rights on Windows 11? Microsoft Learn does not say. Verify as a standard user.
- .NET 8 has no documented managed way to set PIPE_REJECT_REMOTE_CLIENTS on NamedPipeServerStream. Is an ACL deny for NETWORK (NU) enough, or should a P/Invoke to CreateNamedPipe plus SafePipeHandle be used?
- Should the server side also check the connecting client process (GetNamedPipeClientProcessId / image path) in addition to the ACL? Not researched in this pass.
- How well does UDP work through tsnet userspace networking when the path falls back to DERP (latency, MTU)? This needs the Tailscale KB topic (T-other) and a lab measurement.
