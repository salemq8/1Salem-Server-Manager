//go:build windows && tsnetsmoke

package transport

// The probe of the real tsnet smoke test (connect/proof/TSNET_SMOKE.md). It is compiled only
// with -tags tsnetsmoke, and each test skips unless the smoke driver (connect/proof/TsnetSmoke)
// started it with its inputs in the environment, so "go test -tags tsnetsmoke ./..." passes
// offline. It lives in package transport so that what it exercises is the production Tsnet,
// TsnetNodes and requireNetstackSource, not a copy. It writes only its result file and its own
// state directory.

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"tailscale.com/client/local"

	"1salem.app/connect/transport/internal/preamble"
	"1salem.app/connect/transport/internal/redact"
)

// Inputs, set by the driver in this process's environment only. None starts with TS_ or
// TSNET_: tsnet reads those, and ScrubEnvironment removes them.
const (
	envProbeKey     = "ONESALEM_SMOKE_PROBE_KEY"
	envDeparted     = "ONESALEM_SMOKE_DEPARTED"
	envStateDir     = "ONESALEM_SMOKE_STATE_DIR"
	envHostname     = "ONESALEM_SMOKE_HOSTNAME"
	envHost         = "ONESALEM_SMOKE_HOST"
	envFrames       = "ONESALEM_SMOKE_FRAMES_HEX"
	envBlockedPorts = "ONESALEM_SMOKE_BLOCKED_PORTS"
	envResult       = "ONESALEM_SMOKE_RESULT"
)

const (
	// smokeNode names the probe's node directory, <state dir>\nodes\probe.
	smokeNode = "probe"
	// smokeBridgePort is the host bridge port (connect-host-transport's default ":7780").
	smokeBridgePort = 7780
	// smokeRunTimeout bounds one probe test; the driver's -test.timeout is longer.
	smokeRunTimeout = 4 * time.Minute
	// smokeStepTimeout bounds one dial or one status byte. The host answers within its 10 s
	// preamble deadline plus the Agent's 8 s decision.
	smokeStepTimeout = 20 * time.Second
	// smokeBlockedDialTimeout bounds a dial to a port that must not answer at all.
	smokeBlockedDialTimeout = 5 * time.Second
	// smokeDepartedWait bounds the retries of the departed-peer dial (see TestRealTsnetDepartedPeer).
	smokeDepartedWait = 60 * time.Second
)

// TestRealTsnetProbe enrolls a throwaway tag:onesalem-client node with a one-off key and uses it
// as an attacker on the owner's tailnet would: a real ticket from the wrong node, a forged
// ServerId, other ports on the host, destinations outside the tailnet, an address no peer owns,
// and a genuine tsnet host-network connection for the fallback guard.
func TestRealTsnetProbe(t *testing.T) {
	key := os.Getenv(envProbeKey)
	if key == "" {
		t.Skip(envProbeKey + " is not set: this probe runs only under the tsnet smoke driver")
	}
	// Before anything else, as the sidecars do: the key leaves the environment at once, and no
	// other credential or control-server override may reach tsnet.
	os.Unsetenv(envProbeKey)
	ScrubEnvironment()
	in := readProbeInputs(t)
	run := newSmokeRun(t, in.result)
	defer run.write()

	ctx, cancel := context.WithTimeout(context.Background(), smokeRunTimeout)
	defer cancel()
	nodes := newSmokeNodes(t, in.stateDir)
	nodeID, err := nodes.Enroll(ctx, smokeNode, key, in.hostname)
	if err != nil {
		run.record("probe node enrolls", false, "%v", err)
		return
	}
	run.result.NodeID = nodeID
	// Written at once as well as at the end: the driver deletes only nodes whose id it has, and a
	// panic in a tsnet goroutine, the -test.timeout panic or the driver's kill skip the deferred write.
	run.write()
	ts, err := getTsnet(ctx, nodes)
	if err != nil {
		run.record("probe node enrolls", false, "enrolled as %s but not usable: %v", nodeID, err)
		return
	}
	ip4, ip6 := ts.srv.TailscaleIPs()
	run.result.TailscaleIPs = []string{ip4.String(), ip6.String()}
	run.record("probe node enrolls", ip4.IsValid(), "node %s with addresses %s and %s", nodeID, ip4, ip6)

	bridge := netip.AddrPortFrom(in.host, smokeBridgePort)
	reached := probeWrongPeer(ctx, run, ts, bridge, in.frames["valid"], ip4, ip6)
	probeForged(ctx, run, ts, bridge, in.frames["forged"])
	probeBlockedPorts(ctx, run, ts, in.host, in.blockedPorts, reached)
	probeNonTailnet(ctx, run, ts)
	probeUnknownPeer(ctx, run, ts, in.host, ip4)
	probeHostNetworkGuard(ctx, run, ts, ip4, ip6)
}

// TestRealTsnetDepartedPeer restarts the enrolled probe node from its state, with no key, after
// the driver has deleted the host node, and dials the host bridge again. WhoIs no longer knows
// the host, so the production Dial must refuse before dialing anything.
//
// A node may start from a network map it cached on disk, when control allows that, and the
// deleted host then still looks like a peer until the fresh map arrives. So the dial is retried
// until it is refused or smokeDepartedWait passes; an attempt that connected, or that tsnet made
// over the host network, fails the check at once.
func TestRealTsnetDepartedPeer(t *testing.T) {
	if os.Getenv(envDeparted) == "" {
		t.Skip(envDeparted + " is not set: this probe runs only under the tsnet smoke driver")
	}
	ScrubEnvironment()
	stateDir := requireEnv(t, envStateDir)
	host := requireHost(t)
	run := newSmokeRun(t, requireEnv(t, envResult))
	defer run.write()

	const name = "a departed peer is refused before any byte"
	ctx, cancel := context.WithTimeout(context.Background(), smokeRunTimeout)
	defer cancel()
	nodes := newSmokeNodes(t, stateDir)
	ts, err := getTsnet(ctx, nodes)
	if err != nil {
		run.record(name, false, "the probe node did not start from its state: %v", err)
		return
	}
	for _, n := range nodes.List() {
		if n.Node == smokeNode {
			run.result.NodeID = n.NodeID
		}
	}
	bridge := netip.AddrPortFrom(host, smokeBridgePort)
	start := time.Now()
	for attempt := 1; ; attempt++ {
		conn, err := dialStep(ctx, ts, bridge, smokeStepTimeout)
		elapsed := time.Since(start).Round(time.Second)
		// Compared by identity on purpose: Dial returns ErrDestination itself when it refuses
		// before dialing, and errHostNetworkDial (which wraps it) only after tsnet has dialed.
		switch {
		case err == nil:
			conn.Close()
			run.record(name, false, "attempt %d: Dial(%s) CONNECTED to a host whose node was deleted", attempt, bridge)
			return
		case errors.Is(err, errHostNetworkDial):
			run.record(name, false, "attempt %d: WhoIs still knew the host; tsnet dialed %s over the host network and the guard closed it before any byte", attempt, bridge)
			return
		case err == ErrDestination:
			run.record(name, true, "attempt %d, after %s: Dial(%s): %v; WhoIs no longer knows the deleted host, so nothing was dialed", attempt, elapsed, bridge, err)
			return
		case time.Since(start) >= smokeDepartedWait || ctx.Err() != nil:
			run.record(name, false, "attempt %d, after %s: Dial(%s): %v", attempt, elapsed, bridge, err)
			return
		}
		time.Sleep(2 * time.Second)
	}
}

// probeWrongPeer dials the host bridge through the production Dial and presents a real ticket
// that the broker bound to the FRIEND's node. The host learns this node's identity from WhoIs,
// so it must refuse; the driver checks the host's log for the reason. It returns whether the
// bridge was reached through the tailnet, which the port checks depend on.
func probeWrongPeer(ctx context.Context, run *smokeRun, ts *Tsnet, bridge netip.AddrPort, frame []byte, self ...netip.Addr) bool {
	conn, err := dialStep(ctx, ts, bridge, smokeStepTimeout)
	if err != nil {
		run.record("netstack dial reaches the host bridge", false, "Dial(%s): %v", bridge, err)
		run.record("host refuses a valid ticket from the wrong node", false, "not run: no connection to the host bridge")
		return false
	}
	defer conn.Close()
	reached := requireNetstackSource(conn.LocalAddr(), self...) == nil
	run.record("netstack dial reaches the host bridge", reached,
		"connected to %s from %s; this node's addresses are %v", bridge, conn.LocalAddr(), self)
	answer := presentFrame(conn, frame)
	run.record("host refuses a valid ticket from the wrong node", answer.refused, "%s", answer.text)
	return reached
}

// probeForged presents the same ticket with its ServerId edited, so its signature no longer holds.
func probeForged(ctx context.Context, run *smokeRun, ts *Tsnet, bridge netip.AddrPort, frame []byte) {
	const name = "host refuses a ticket edited to another ServerId"
	conn, err := dialStep(ctx, ts, bridge, smokeStepTimeout)
	if err != nil {
		run.record(name, false, "Dial(%s): %v", bridge, err)
		return
	}
	defer conn.Close()
	answer := presentFrame(conn, frame)
	run.record(name, answer.refused, "%s", answer.text)
}

// probeBlockedPorts dials ports a friend node must never reach on the host: game, Agent, RDP and
// SMB ports and the test service's own port. Two different things stop such a dial, and only
// one of them is the tailnet policy. The host's tsnet has no listener on these ports and never
// forwards to localhost, so a SYN that gets through is answered with a reset; a SYN the policy
// drops is never answered, and the dial times out. So a port passes the policy check only on a
// timeout, and the host passes when no port connected. Only the listed ports are tried. Nothing
// is tried unless the bridge was reached through the tailnet first: otherwise every dial fails
// for an unrelated reason (the host is not a peer at all) and would prove nothing.
func probeBlockedPorts(ctx context.Context, run *smokeRun, ts *Tsnet, host netip.Addr, ports []uint16, bridgeReached bool) {
	allTried, noneConnected := bridgeReached, true
	var outcomes []string
	for _, port := range ports {
		name := fmt.Sprintf("the tailnet policy drops TCP %d to the host", port)
		if !bridgeReached {
			run.record(name, false, "not run: the probe never reached the host bridge through the tailnet")
			continue
		}
		outcome, detail := dialBlocked(ctx, ts, netip.AddrPortFrom(host, port))
		run.record(name, outcome == portDropped, "%s", detail)
		allTried = allTried && outcome != portNotTried
		noneConnected = noneConnected && outcome != portConnected
		outcomes = append(outcomes, fmt.Sprintf("%d %s", port, outcome))
	}
	run.record("the host node accepted none of the tested ports", allTried && noneConnected,
		"bridge reached first: %t; %s", bridgeReached, strings.Join(outcomes, ", "))
}

type portOutcome string

const (
	portDropped   portOutcome = "dropped"
	portRefused   portOutcome = "refused"
	portConnected portOutcome = "CONNECTED"
	portNotTried  portOutcome = "not tried"
)

// dialBlocked classifies one dial to a port that must not answer.
func dialBlocked(ctx context.Context, ts *Tsnet, target netip.AddrPort) (portOutcome, string) {
	stepCtx, cancel := context.WithTimeout(ctx, smokeBlockedDialTimeout)
	defer cancel()
	conn, err := ts.Dial(stepCtx, target)
	var netErr net.Error
	// Compared by identity on purpose, as in TestRealTsnetDepartedPeer.
	switch {
	case err == nil:
		conn.Close()
		return portConnected, fmt.Sprintf("Dial(%s) CONNECTED", target)
	case ctx.Err() != nil:
		return portNotTried, fmt.Sprintf("not tried: the probe's own time ran out: %v", err)
	case err == ErrDestination:
		return portNotTried, fmt.Sprintf("not tried: Dial(%s) was refused before dialing (%v), so the host is not a peer of this node", target, err)
	case errors.Is(err, errHostNetworkDial):
		return portNotTried, fmt.Sprintf("not tried: tsnet dialed %s over the host network: %v", target, err)
	case errors.Is(stepCtx.Err(), context.DeadlineExceeded) || (errors.As(err, &netErr) && netErr.Timeout()):
		return portDropped, fmt.Sprintf("Dial(%s) got no answer within %s, so the policy dropped the SYN: %v", target, smokeBlockedDialTimeout, err)
	default:
		return portRefused, fmt.Sprintf("Dial(%s): %v: the SYN was answered, so the policy let it through (the host's tsnet has no listener there)", target, err)
	}
}

// probeNonTailnet asks the production Dial for a loopback address. It must refuse before dialing
// anything, so the witness listener accepts no connection.
func probeNonTailnet(ctx context.Context, run *smokeRun, ts *Tsnet) {
	w := newWitness(run.t)
	conn, err := dialStep(ctx, ts, w.addr(), smokeStepTimeout)
	if conn != nil {
		conn.Close()
	}
	conns, _, settled := w.settle()
	run.record("a non-tailnet destination is refused before any dial", err == ErrDestination && settled && conns == 0,
		"Dial(%s): %v; the listener accepted %d connection(s) (settled: %t)", w.addr(), err, conns, settled)
}

// probeUnknownPeer dials a tailnet address that no peer owns. tsnet itself would dial it over the
// host network; the production Dial must refuse it at WhoIs, before dialing.
func probeUnknownPeer(ctx context.Context, run *smokeRun, ts *Tsnet, host, self netip.Addr) {
	const name = "an address no peer owns is refused at WhoIs, before any dial"
	target, err := unusedTailnetAddr(ctx, ts, host, self)
	if err != nil {
		run.record(name, false, "no unused address found: %v", err)
		return
	}
	dest := netip.AddrPortFrom(target, smokeBridgePort)
	conn, err := dialStep(ctx, ts, dest, smokeStepTimeout)
	if conn != nil {
		conn.Close()
	}
	run.record(name, err == ErrDestination, "Dial(%s): %v", dest, err)
}

// probeHostNetworkGuard shows the fallback guard on a genuine tsnet connection. tsnet's own Dial,
// which the production Dial wraps, dials a loopback address over the host network.
// requireNetstackSource, the check the production Dial runs before handing out a connection, must
// classify that connection as a host-network one. Nothing is written, so the witness must
// receive no byte.
func probeHostNetworkGuard(ctx context.Context, run *smokeRun, ts *Tsnet, self ...netip.Addr) {
	const name = "the fallback guard classifies a real tsnet host-network connection"
	w := newWitness(run.t)
	dialCtx, cancel := context.WithTimeout(ctx, smokeStepTimeout)
	conn, err := ts.srv.Dial(dialCtx, "tcp", w.addr().String())
	cancel()
	if err != nil {
		run.record(name, false, "tsnet did not dial %s over the host network: %v", w.addr(), err)
		return
	}
	from := conn.LocalAddr()
	guard := requireNetstackSource(from, self...)
	conn.Close()
	conns, bytes, settled := w.settle()
	run.record(name, errors.Is(guard, errHostNetworkDial) && settled && conns == 1 && bytes == 0,
		"tsnet dialed %s over the host network from %s; guard: %v; the listener saw %d connection(s) and %d byte(s) (settled: %t)",
		w.addr(), from, guard, conns, bytes, settled)
}

// unusedTailnetAddr varies the last octet of the host's address until WhoIs reports that no node
// owns it.
func unusedTailnetAddr(ctx context.Context, ts *Tsnet, host, self netip.Addr) (netip.Addr, error) {
	lc, err := ts.srv.LocalClient()
	if err != nil {
		return netip.Addr{}, err
	}
	octets := host.As4()
	for last := 1; last < 255; last++ {
		octets[3] = byte(last)
		candidate := netip.AddrFrom4(octets)
		if candidate == host || candidate == self {
			continue
		}
		_, err := lc.WhoIs(ctx, candidate.String())
		if errors.Is(err, local.ErrPeerNotFound) {
			return candidate, nil
		}
		if err != nil {
			return netip.Addr{}, err
		}
	}
	return netip.Addr{}, errors.New("every address next to the host's belongs to a peer")
}

func dialStep(ctx context.Context, ts *Tsnet, addr netip.AddrPort, timeout time.Duration) (net.Conn, error) {
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	return ts.Dial(ctx, addr)
}

type bridgeAnswer struct {
	refused bool
	text    string
}

// presentFrame writes one preamble frame and reads the host's single status byte. The host
// refuses with 0x01 and then closes; a close without the byte is a refusal as well.
func presentFrame(conn net.Conn, frame []byte) bridgeAnswer {
	conn.SetDeadline(time.Now().Add(smokeStepTimeout))
	if _, err := conn.Write(frame); err != nil {
		return bridgeAnswer{text: fmt.Sprintf("could not send the frame: %v", err)}
	}
	status := make([]byte, 1)
	_, err := io.ReadFull(conn, status)
	switch {
	case err == nil && status[0] == preamble.StatusRefused:
		return bridgeAnswer{refused: true, text: "refused (status byte 0x01)"}
	case err == nil:
		return bridgeAnswer{text: fmt.Sprintf("NOT refused: status byte 0x%02x", status[0])}
	case errors.Is(err, os.ErrDeadlineExceeded):
		return bridgeAnswer{text: fmt.Sprintf("no answer within %s", smokeStepTimeout)}
	default:
		return bridgeAnswer{refused: true, text: fmt.Sprintf("closed without a status byte: %v", err)}
	}
}

// witness is a loopback listener that counts the connections and bytes that reach it, so a check
// can show that a dial never happened, or carried nothing.
type witness struct {
	ln net.Listener

	mu     sync.Mutex
	seen   map[string]bool
	conns  int
	closed int
	bytes  int64
}

func newWitness(t *testing.T) *witness {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("witness listener: %v", err)
	}
	t.Cleanup(func() { ln.Close() })
	w := &witness{ln: ln, seen: make(map[string]bool)}
	go w.serve()
	return w
}

func (w *witness) addr() netip.AddrPort {
	return netip.MustParseAddrPort(w.ln.Addr().String())
}

func (w *witness) serve() {
	for {
		c, err := w.ln.Accept()
		if err != nil {
			return
		}
		w.mu.Lock()
		w.seen[c.RemoteAddr().String()] = true
		w.conns++
		w.mu.Unlock()
		go func() {
			n, _ := io.Copy(io.Discard, c)
			c.Close()
			w.mu.Lock()
			w.bytes += n
			w.closed++
			w.mu.Unlock()
		}()
	}
}

// settle returns what reached the witness before this call, not counting its own connection. It
// connects once more itself and waits until that connection has been accepted and every
// connection has ended: the listener accepts in arrival order, so everything that arrived
// earlier has been counted by then. settled is false if that did not happen in time.
func (w *witness) settle() (conns int, bytes int64, settled bool) {
	sentinel, err := net.Dial("tcp", w.ln.Addr().String())
	if err != nil {
		return 0, 0, false
	}
	mark := sentinel.LocalAddr().String()
	sentinel.Close()
	for deadline := time.Now().Add(smokeStepTimeout); time.Now().Before(deadline); time.Sleep(20 * time.Millisecond) {
		w.mu.Lock()
		done := w.seen[mark] && w.closed == w.conns
		conns, bytes = w.conns-1, w.bytes
		w.mu.Unlock()
		if done {
			return conns, bytes, true
		}
	}
	return conns, bytes, false
}

type probeInputs struct {
	stateDir     string
	hostname     string
	result       string
	host         netip.Addr
	frames       map[string][]byte
	blockedPorts []uint16
}

// readProbeInputs reads the driver's inputs. A malformed item is named, never echoed: the frames
// carry a ticket.
func readProbeInputs(t *testing.T) probeInputs {
	t.Helper()
	in := probeInputs{
		stateDir: requireEnv(t, envStateDir),
		hostname: requireEnv(t, envHostname),
		result:   requireEnv(t, envResult),
		host:     requireHost(t),
		frames:   make(map[string][]byte),
	}
	for _, item := range strings.Split(requireEnv(t, envFrames), ",") {
		name, text, ok := strings.Cut(item, ":")
		frame, err := hex.DecodeString(text)
		if !ok || err != nil || len(frame) == 0 {
			t.Fatalf("%s: item %q is malformed", envFrames, name)
		}
		in.frames[name] = frame
	}
	for _, name := range []string{"valid", "forged"} {
		if in.frames[name] == nil {
			t.Fatalf("%s has no %q frame", envFrames, name)
		}
	}
	for _, text := range strings.Split(requireEnv(t, envBlockedPorts), ",") {
		port, err := strconv.ParseUint(text, 10, 16)
		if err != nil || port == 0 {
			t.Fatalf("%s: %q is not a port", envBlockedPorts, text)
		}
		in.blockedPorts = append(in.blockedPorts, uint16(port))
	}
	return in
}

func requireEnv(t *testing.T, name string) string {
	t.Helper()
	value := os.Getenv(name)
	if value == "" {
		t.Fatalf("%s is required", name)
	}
	return value
}

func requireHost(t *testing.T) netip.Addr {
	t.Helper()
	host, err := netip.ParseAddr(requireEnv(t, envHost))
	if err != nil || !host.Is4() {
		t.Fatalf("%s must be the host node's IPv4 address", envHost)
	}
	return host
}

// newSmokeNodes returns the production node set on stateDir. tsnet's lines go through the
// production redacting logger to stderr, which the driver keeps as the probe's log. t.Logf cannot
// take them: tsnet's goroutines may still log after the test has returned.
func newSmokeNodes(t *testing.T, stateDir string) *TsnetNodes {
	t.Helper()
	log := redact.New(os.Stderr, 1)
	nodes, err := NewTsnetNodes(stateDir, log.Printf, log.Printf)
	if err != nil {
		t.Fatalf("NewTsnetNodes: %v", err)
	}
	t.Cleanup(func() { nodes.Close() })
	return nodes
}

func getTsnet(ctx context.Context, nodes *TsnetNodes) (*Tsnet, error) {
	tr, err := nodes.Get(ctx, smokeNode)
	if err != nil {
		return nil, err
	}
	ts, ok := tr.(*Tsnet)
	if !ok {
		return nil, fmt.Errorf("the node set returned %T, not *Tsnet", tr)
	}
	return ts, nil
}

// smokeRun records each check and writes them all to the result file, however the test ends.
type smokeRun struct {
	t      *testing.T
	path   string
	result smokeResult
}

type smokeResult struct {
	NodeID       string       `json:"nodeId,omitempty"`
	TailscaleIPs []string     `json:"tailscaleIPs,omitempty"`
	Checks       []smokeCheck `json:"checks"`
}

type smokeCheck struct {
	Name   string `json:"name"`
	Passed bool   `json:"passed"`
	Detail string `json:"detail"`
}

func newSmokeRun(t *testing.T, path string) *smokeRun {
	return &smokeRun{t: t, path: path, result: smokeResult{Checks: []smokeCheck{}}}
}

func (r *smokeRun) record(name string, passed bool, format string, args ...any) {
	detail := redact.String(fmt.Sprintf(format, args...))
	r.result.Checks = append(r.result.Checks, smokeCheck{Name: name, Passed: passed, Detail: detail})
	if passed {
		r.t.Logf("PASS  %s  --  %s", name, detail)
	} else {
		r.t.Errorf("FAIL  %s  --  %s", name, detail)
	}
}

func (r *smokeRun) write() {
	data, err := json.MarshalIndent(r.result, "", "  ")
	if err == nil {
		err = os.WriteFile(r.path, data, 0o600)
	}
	if err != nil {
		r.t.Errorf("writing %s: %v", r.path, err)
	}
}
