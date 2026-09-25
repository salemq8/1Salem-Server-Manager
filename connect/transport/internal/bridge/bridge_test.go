package bridge_test

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"1salem.app/connect/transport/internal/b64"
	"1salem.app/connect/transport/internal/bridge"
	"1salem.app/connect/transport/internal/preamble"
	"1salem.app/connect/transport/internal/transport"
)

const friendNode = "nFRIEND0001CNTRL"

// agent is a scripted Authorizer.
type agent struct {
	decide func(n int) (bridge.Decision, error)

	mu       sync.Mutex
	requests []bridge.AuthorizeRequest
	closed   chan bridge.ClosedReport
}

func newAgent(decide func(n int) (bridge.Decision, error)) *agent {
	return &agent{decide: decide, closed: make(chan bridge.ClosedReport, 16)}
}

func (a *agent) Authorize(ctx context.Context, req bridge.AuthorizeRequest) (bridge.Decision, error) {
	a.mu.Lock()
	a.requests = append(a.requests, req)
	n := len(a.requests)
	a.mu.Unlock()
	return a.decide(n)
}

func (a *agent) Closed(ctx context.Context, rep bridge.ClosedReport) error {
	a.closed <- rep
	return nil
}

func (a *agent) calls() []bridge.AuthorizeRequest {
	a.mu.Lock()
	defer a.mu.Unlock()
	return append([]bridge.AuthorizeRequest(nil), a.requests...)
}

func allow(endpoint, connID string) func(int) (bridge.Decision, error) {
	return func(int) (bridge.Decision, error) {
		return bridge.Decision{Allow: true, Endpoint: endpoint, ConnID: connID}, nil
	}
}

// dialRecorder records what the bridge dials locally.
type dialRecorder struct {
	mu    sync.Mutex
	addrs []netip.AddrPort
}

func (d *dialRecorder) dial(ctx context.Context, addr netip.AddrPort) (net.Conn, error) {
	d.mu.Lock()
	d.addrs = append(d.addrs, addr)
	d.mu.Unlock()
	var nd net.Dialer
	return nd.DialContext(ctx, "tcp", addr.String())
}

func (d *dialRecorder) dialed() []netip.AddrPort {
	d.mu.Lock()
	defer d.mu.Unlock()
	return append([]netip.AddrPort(nil), d.addrs...)
}

// gameServer is a loopback echo server that counts its connections.
type gameServer struct {
	addr     string
	accepted atomic.Int32
}

func newGameServer(t *testing.T) *gameServer {
	t.Helper()
	ln, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { ln.Close() })
	g := &gameServer{addr: ln.Addr().String()}
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			g.accepted.Add(1)
			go func() {
				defer c.Close()
				io.Copy(c, c)
			}()
		}
	}()
	return g
}

func freeLoopback(t *testing.T) string {
	t.Helper()
	l, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	addr := l.Addr().String()
	l.Close()
	return addr
}

type harness struct {
	br    *bridge.Bridge
	addr  string
	dials *dialRecorder
	agent *agent
}

func startBridge(t *testing.T, a *agent, tweaks ...func(*bridge.Config)) *harness {
	t.Helper()
	host, err := transport.NewFake("")
	if err != nil {
		t.Fatal(err)
	}
	h := &harness{addr: freeLoopback(t), dials: &dialRecorder{}, agent: a}
	cfg := bridge.Config{
		Authorizer:      a,
		Logf:            t.Logf,
		DialLocal:       h.dials.dial,
		PreambleTimeout: 500 * time.Millisecond,
	}
	for _, tweak := range tweaks {
		tweak(&cfg)
	}
	h.br = bridge.New(cfg)
	h.br.SetRevocationChannel(true)
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		h.br.Serve(ctx, host, h.addr)
	}()
	t.Cleanup(func() {
		cancel()
		<-done
	})
	deadline := time.Now().Add(2 * time.Second)
	for h.br.Status().Listen == "" {
		if time.Now().After(deadline) {
			t.Fatal("bridge did not start listening")
		}
		time.Sleep(5 * time.Millisecond)
	}
	return h
}

func validPreamble() preamble.Preamble {
	return preamble.Preamble{
		T:  "eyJhbGciOiJFUzI1NiJ9.eyJzaWQiOiJ4In0.c2ln",
		N:  b64.Encode([]byte("0123456789abcdef")),
		TS: time.Now().Unix(),
		P:  b64.Encode(make([]byte, 64)),
	}
}

// connect dials the bridge as a fake friend node, sends the preamble and
// returns the status byte.
func (h *harness) connect(t *testing.T) (net.Conn, byte) {
	t.Helper()
	tr, err := transport.NewFake(friendNode)
	if err != nil {
		t.Fatal(err)
	}
	c, err := tr.Dial(context.Background(), netip.MustParseAddrPort(h.addr))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { c.Close() })
	c.SetDeadline(time.Now().Add(5 * time.Second))
	if err := preamble.Write(c, validPreamble()); err != nil {
		t.Fatal(err)
	}
	var status [1]byte
	if _, err := io.ReadFull(c, status[:]); err != nil {
		t.Fatalf("no status byte: %v", err)
	}
	return c, status[0]
}

func echo(t *testing.T, c net.Conn, msg string) {
	t.Helper()
	c.SetDeadline(time.Now().Add(5 * time.Second))
	if _, err := c.Write([]byte(msg)); err != nil {
		t.Fatal(err)
	}
	buf := make([]byte, len(msg))
	if _, err := io.ReadFull(c, buf); err != nil || string(buf) != msg {
		t.Fatalf("echo %q, %v", buf, err)
	}
}

func expectClosedConn(t *testing.T, c net.Conn) {
	t.Helper()
	c.SetDeadline(time.Now().Add(3 * time.Second))
	n, err := c.Read(make([]byte, 16))
	if err == nil {
		t.Fatalf("read %d bytes; expected the connection to be closed", n)
	}
	var ne net.Error
	if errors.As(err, &ne) && ne.Timeout() {
		t.Fatal("connection still open")
	}
}

func waitClosed(t *testing.T, a *agent) bridge.ClosedReport {
	t.Helper()
	select {
	case rep := <-a.closed:
		return rep
	case <-time.After(5 * time.Second):
		t.Fatal("no closed report")
	}
	return bridge.ClosedReport{}
}

func TestAllowedReachesOnlyTheReturnedEndpoint(t *testing.T) {
	game, decoy := newGameServer(t), newGameServer(t)
	a := newAgent(allow(game.addr, "conn-1"))
	h := startBridge(t, a)

	c, status := h.connect(t)
	if status != preamble.StatusOK {
		t.Fatalf("status %#x, want accepted", status)
	}
	echo(t, c, "hello")
	c.Close()

	rep := waitClosed(t, a)
	if rep != (bridge.ClosedReport{ConnID: "conn-1", BytesIn: 5, BytesOut: 5}) {
		t.Fatalf("closed report %+v", rep)
	}
	if got := h.dials.dialed(); len(got) != 1 || got[0].String() != game.addr {
		t.Fatalf("dialed %v, want only %s", got, game.addr)
	}
	if game.accepted.Load() != 1 || decoy.accepted.Load() != 0 {
		t.Fatalf("game accepted %d, decoy accepted %d", game.accepted.Load(), decoy.accepted.Load())
	}
	calls := a.calls()
	if len(calls) != 1 {
		t.Fatalf("%d authorize calls", len(calls))
	}
	req := calls[0]
	if req.Peer.NodeID != friendNode || req.Peer.Addr == "" || req.Preamble.T != validPreamble().T {
		t.Fatalf("authorize request %+v", req)
	}
}

func TestDeniedGetsRefusalAndClose(t *testing.T) {
	cases := map[string]func(int) (bridge.Decision, error){
		"deny":        func(int) (bridge.Decision, error) { return bridge.Decision{}, nil },
		"agent error": func(int) (bridge.Decision, error) { return bridge.Decision{}, errors.New("pipe down") },
	}
	for name, decide := range cases {
		t.Run(name, func(t *testing.T) {
			game := newGameServer(t)
			a := newAgent(decide)
			h := startBridge(t, a)
			c, status := h.connect(t)
			if status != preamble.StatusRefused {
				t.Fatalf("status %#x, want refused", status)
			}
			expectClosedConn(t, c)
			if len(h.dials.dialed()) != 0 || game.accepted.Load() != 0 {
				t.Fatal("a denied connection dialed something")
			}
			select {
			case rep := <-a.closed:
				t.Fatalf("closed report %+v for a connection that was never allowed", rep)
			case <-time.After(100 * time.Millisecond):
			}
		})
	}
}

func TestNonLoopbackEndpointRefused(t *testing.T) {
	endpoints := []string{
		"10.0.0.1:25565",
		"192.168.1.20:25565",
		"100.64.0.1:25565",
		"0.0.0.0:25565",
		"[::]:25565",
		"[::ffff:127.0.0.1]:25565",
		"localhost:25565",
		"example.com:25565",
		"127.0.0.1:0",
		"127.0.0.1",
		"",
	}
	for i, ep := range endpoints {
		a := newAgent(allow(ep, "conn-x"))
		h := startBridge(t, a)
		c, status := h.connect(t)
		if status != preamble.StatusRefused {
			t.Errorf("endpoint %q: status %#x, want refused", ep, status)
		}
		expectClosedConn(t, c)
		if got := h.dials.dialed(); len(got) != 0 {
			t.Errorf("endpoint %q: dialed %v", ep, got)
		}
		// The Agent allocated a connection id; it hears that it closed.
		if rep := waitClosed(t, a); rep != (bridge.ClosedReport{ConnID: "conn-x"}) {
			t.Errorf("case %d: closed report %+v", i, rep)
		}
	}
}

func TestUnusableConnIDRefused(t *testing.T) {
	game := newGameServer(t)
	for _, id := range []string{"", "has space", strings.Repeat("x", 129), "a\nb"} {
		a := newAgent(allow(game.addr, id))
		h := startBridge(t, a)
		if _, status := h.connect(t); status != preamble.StatusRefused {
			t.Errorf("connId %q: status %#x, want refused", id, status)
		}
	}
	if game.accepted.Load() != 0 {
		t.Fatal("a connection without a usable id reached the game server")
	}
}

func TestCloseEventKillsTheRightConnection(t *testing.T) {
	game := newGameServer(t)
	a := newAgent(func(n int) (bridge.Decision, error) {
		return bridge.Decision{Allow: true, Endpoint: game.addr, ConnID: []string{"", "conn-a", "conn-b"}[n]}, nil
	})
	h := startBridge(t, a)

	ca, sa := h.connect(t)
	cb, sb := h.connect(t)
	if sa != preamble.StatusOK || sb != preamble.StatusOK {
		t.Fatalf("status %#x %#x", sa, sb)
	}
	echo(t, ca, "a")
	echo(t, cb, "b")

	if n := h.br.CloseConns([]string{"conn-a", "unknown"}); n != 1 {
		t.Fatalf("CloseConns found %d", n)
	}
	expectClosedConn(t, ca)
	if rep := waitClosed(t, a); rep.ConnID != "conn-a" {
		t.Fatalf("closed report for %q, want conn-a", rep.ConnID)
	}
	echo(t, cb, "still alive")
	if st := h.br.Status(); st.Live != 1 {
		t.Fatalf("live = %d, want 1", st.Live)
	}
}

func TestRevocationChannelLoss(t *testing.T) {
	game := newGameServer(t)
	a := newAgent(allow(game.addr, "conn-1"))
	h := startBridge(t, a)
	c, status := h.connect(t)
	if status != preamble.StatusOK {
		t.Fatal("not accepted")
	}

	// The Agent can no longer end connections: the live one ends and new
	// ones are refused without asking.
	h.br.SetRevocationChannel(false)
	expectClosedConn(t, c)
	waitClosed(t, a)
	if _, status := h.connect(t); status != preamble.StatusRefused {
		t.Fatalf("status %#x while the revocation channel is down", status)
	}
	if n := len(a.calls()); n != 1 {
		t.Fatalf("Agent asked %d times, want 1", n)
	}

	h.br.SetRevocationChannel(true)
	if _, status := h.connect(t); status != preamble.StatusOK {
		t.Fatal("not accepted after the channel came back")
	}
}

func TestPreambleDeadlineAndLimits(t *testing.T) {
	a := newAgent(allow("127.0.0.1:1", "conn-1"))
	h := startBridge(t, a)
	tr, _ := transport.NewFake(friendNode)
	ap := netip.MustParseAddrPort(h.addr)

	// Silence after connecting: refused once the deadline passes.
	c, err := tr.Dial(context.Background(), ap)
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	start := time.Now()
	c.SetDeadline(time.Now().Add(5 * time.Second))
	var status [1]byte
	if _, err := io.ReadFull(c, status[:]); err != nil || status[0] != preamble.StatusRefused {
		t.Fatalf("silent peer: status %#x, %v", status[0], err)
	}
	if waited := time.Since(start); waited > 3*time.Second {
		t.Fatalf("silent peer held for %s", waited)
	}

	frames := map[string][]byte{
		"oversize length": {'1', 'S', 'C', 0x01, 0x10, 0x01},
		"wrong magic":     {'H', 'T', 'T', 'P', ' ', '/'},
		"bad json":        append([]byte{'1', 'S', 'C', 0x01, 0x00, 0x02}, "{]"...),
	}
	for name, f := range frames {
		c, err := tr.Dial(context.Background(), ap)
		if err != nil {
			t.Fatal(err)
		}
		c.SetDeadline(time.Now().Add(5 * time.Second))
		c.Write(f)
		if _, err := io.ReadFull(c, status[:]); err != nil || status[0] != preamble.StatusRefused {
			t.Errorf("%s: status %#x, %v", name, status[0], err)
		}
		c.Close()
	}

	// A connection that does not come from a (fake) node cannot be attributed.
	raw, err := net.Dial("tcp", h.addr)
	if err != nil {
		t.Fatal(err)
	}
	defer raw.Close()
	raw.SetDeadline(time.Now().Add(5 * time.Second))
	preamble.Write(raw, validPreamble())
	if _, err := io.ReadFull(raw, status[:]); err != nil || status[0] != preamble.StatusRefused {
		t.Errorf("anonymous peer: status %#x, %v", status[0], err)
	}

	if n := len(a.calls()); n != 0 {
		t.Fatalf("the Agent was asked %d time(s) about malformed connections", n)
	}
}

// Two fake peers. Every fake node dials from loopback, so the bridge tells
// peers apart by source address the way it tells tailnet IPs apart.
const (
	peerA = "127.0.0.1"
	peerB = "127.0.0.2"
)

func allowEach(endpoint string) func(int) (bridge.Decision, error) {
	return func(n int) (bridge.Decision, error) {
		return bridge.Decision{Allow: true, Endpoint: endpoint, ConnID: fmt.Sprintf("conn-%d", n)}, nil
	}
}

// rawFrom opens a TCP connection to the bridge from the loopback address src
// and sends nothing.
func (h *harness) rawFrom(src string) (net.Conn, error) {
	d := net.Dialer{Timeout: 2 * time.Second, LocalAddr: &net.TCPAddr{IP: net.ParseIP(src)}}
	return d.Dial("tcp4", h.addr)
}

func (h *harness) mustRawFrom(t *testing.T, src string) net.Conn {
	t.Helper()
	c, err := h.rawFrom(src)
	if err != nil {
		t.Fatalf("dial from %s: %v", src, err)
	}
	t.Cleanup(func() { c.Close() })
	return c
}

// tryConnectFrom does what connect does, as the fake node at src, and
// reports failures instead of ending the test, for use in goroutines and
// retry loops.
func (h *harness) tryConnectFrom(src string) (net.Conn, byte, error) {
	c, err := h.rawFrom(src)
	if err != nil {
		return nil, 0, err
	}
	c.SetDeadline(time.Now().Add(5 * time.Second))
	// The header transport.Fake's Dial sends.
	if _, err := c.Write([]byte("FAKENODE " + friendNode + "\n")); err != nil {
		c.Close()
		return nil, 0, err
	}
	if err := preamble.Write(c, validPreamble()); err != nil {
		c.Close()
		return nil, 0, err
	}
	var status [1]byte
	if _, err := io.ReadFull(c, status[:]); err != nil {
		c.Close()
		return nil, 0, err
	}
	return c, status[0], nil
}

// connectFrom waits, up to a deadline, until a connection from src is
// accepted; the bridge frees a finished connection's budget just after the
// peer sees it end.
func (h *harness) connectFrom(t *testing.T, src string) net.Conn {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for {
		c, status, err := h.tryConnectFrom(src)
		if err == nil && status == preamble.StatusOK {
			t.Cleanup(func() { c.Close() })
			return c
		}
		if c != nil {
			c.Close()
		}
		if time.Now().After(deadline) {
			t.Fatalf("no accepted connection from %s: status %#x, %v", src, status, err)
		}
		time.Sleep(20 * time.Millisecond)
	}
}

func (h *harness) waitAccepted(t *testing.T, n uint64) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for h.br.Status().Accepted < n {
		if time.Now().After(deadline) {
			t.Fatalf("bridge accepted %d connection(s), want %d", h.br.Status().Accepted, n)
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func TestOnePeerCannotHoldEveryPendingSlot(t *testing.T) {
	game := newGameServer(t)
	a := newAgent(allowEach(game.addr))
	// Long enough that only the per-source cap can explain a quick close.
	h := startBridge(t, a, func(c *bridge.Config) { c.PreambleTimeout = 10 * time.Second })

	// Peer A fills its default pending budget and never sends a preamble.
	var silent []net.Conn
	for range bridge.DefaultMaxPendingPerSource {
		silent = append(silent, h.mustRawFrom(t, peerA))
	}
	h.waitAccepted(t, bridge.DefaultMaxPendingPerSource)

	// Its next one is closed at once; nobody is asked about it.
	expectClosedConn(t, h.mustRawFrom(t, peerA))
	if st := h.br.Status(); st.Refused != 1 {
		t.Fatalf("refused = %d, want 1", st.Refused)
	}

	// Peer B is served meanwhile.
	echo(t, h.connectFrom(t, peerB), "b")

	// Once peer A's idle connections are gone, it is served again.
	for _, c := range silent {
		c.Close()
	}
	echo(t, h.connectFrom(t, peerA), "a")
	if n := len(a.calls()); n != 2 {
		t.Fatalf("Agent asked %d times, want 2", n)
	}
}

// A friend's server list pings all of an owner's servers at once, all through
// the friend's one node for that owner. Under the default limits such a burst
// is served in full even when every connection of it awaits the Agent at the
// same time.
func TestBurstFromOnePeerIsServed(t *testing.T) {
	const burst = 16
	game := newGameServer(t)
	all := make(chan struct{})
	a := newAgent(func(n int) (bridge.Decision, error) {
		if n == burst {
			close(all)
		}
		select {
		case <-all:
		case <-time.After(5 * time.Second):
			return bridge.Decision{}, errors.New("the burst never awaited the Agent in full")
		}
		return allowEach(game.addr)(n)
	})
	h := startBridge(t, a)

	type result struct {
		c      net.Conn
		status byte
		err    error
	}
	results := make(chan result, burst)
	for range burst {
		go func() {
			c, status, err := h.tryConnectFrom(peerA)
			results <- result{c, status, err}
		}()
	}
	for range burst {
		r := <-results
		if r.err != nil {
			t.Fatal(r.err)
		}
		t.Cleanup(func() { r.c.Close() })
		if r.status != preamble.StatusOK {
			t.Fatalf("status %#x: a connection of the burst was refused", r.status)
		}
		echo(t, r.c, "ping")
	}
}

func TestLiveConnectionsCappedPerPeer(t *testing.T) {
	game := newGameServer(t)
	a := newAgent(allowEach(game.addr))
	h := startBridge(t, a, func(c *bridge.Config) { c.MaxLivePerSource = 2 })

	first := h.connectFrom(t, peerA)
	echo(t, first, "1")
	echo(t, h.connectFrom(t, peerA), "2")

	// At its live cap, peer A's next connection is closed before the Agent
	// is asked; peer B is not affected.
	expectClosedConn(t, h.mustRawFrom(t, peerA))
	echo(t, h.connectFrom(t, peerB), "b")
	if n := len(a.calls()); n != 3 {
		t.Fatalf("Agent asked %d times, want 3", n)
	}

	first.Close()
	if rep := waitClosed(t, a); rep.ConnID != "conn-1" {
		t.Fatalf("closed report %+v", rep)
	}
	echo(t, h.connectFrom(t, peerA), "3")
}

// Connections admitted below the live cap can be allowed together; the cap is
// checked again when each goes live, and the Agent hears about the refused
// one so its view of live connections stays exact.
func TestLiveCapRecheckedWhenAllowed(t *testing.T) {
	game := newGameServer(t)
	arrived, proceed := make(chan struct{}, 2), make(chan struct{})
	a := newAgent(func(n int) (bridge.Decision, error) {
		arrived <- struct{}{}
		<-proceed
		return allowEach(game.addr)(n)
	})
	h := startBridge(t, a, func(c *bridge.Config) { c.MaxLivePerSource = 1 })

	type result struct {
		c      net.Conn
		status byte
		err    error
	}
	results := make(chan result, 2)
	for range 2 {
		go func() {
			c, status, err := h.tryConnectFrom(peerA)
			results <- result{c, status, err}
		}()
	}
	for range 2 {
		select {
		case <-arrived:
		case <-time.After(5 * time.Second):
			t.Fatal("the Agent was not asked twice")
		}
	}
	close(proceed)

	var ok, refused int
	for range 2 {
		r := <-results
		if r.err != nil {
			t.Fatal(r.err)
		}
		t.Cleanup(func() { r.c.Close() })
		switch r.status {
		case preamble.StatusOK:
			ok++
		case preamble.StatusRefused:
			refused++
		}
	}
	if ok != 1 || refused != 1 {
		t.Fatalf("%d accepted, %d refused; want one of each", ok, refused)
	}
	if rep := waitClosed(t, a); rep.BytesIn != 0 || rep.BytesOut != 0 || !strings.HasPrefix(rep.ConnID, "conn-") {
		t.Fatalf("closed report %+v", rep)
	}
	if got := h.dials.dialed(); len(got) != 1 {
		t.Fatalf("dialed %v, want the accepted connection's endpoint only", got)
	}
}

func TestRefusalLogIsRateLimited(t *testing.T) {
	var mu sync.Mutex
	var lines []string
	logf := func(format string, args ...any) {
		mu.Lock()
		defer mu.Unlock()
		lines = append(lines, fmt.Sprintf(format, args...))
	}
	a := newAgent(allowEach("127.0.0.1:1"))
	h := startBridge(t, a, func(c *bridge.Config) {
		c.Logf = logf
		c.MaxPendingPerSource = 1
		c.PreambleTimeout = 10 * time.Second
	})

	h.mustRawFrom(t, peerA)
	h.waitAccepted(t, 1)
	const flood = 4 * bridge.RefusalLogBurst
	for range flood {
		expectClosedConn(t, h.mustRawFrom(t, peerA))
	}
	h.waitAccepted(t, 1+flood)
	if st := h.br.Status(); st.Refused != flood {
		t.Fatalf("refused = %d, want %d", st.Refused, flood)
	}

	mu.Lock()
	defer mu.Unlock()
	logged := 0
	for _, l := range lines {
		if strings.Contains(l, "refusing") {
			logged++
		}
	}
	if logged != bridge.RefusalLogBurst {
		t.Fatalf("%d refusal lines logged for %d refusals, want %d", logged, flood, bridge.RefusalLogBurst)
	}
}
