//go:build windows

package authz_test

import (
	"bufio"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"1salem.app/connect/transport/internal/authz"
	"1salem.app/connect/transport/internal/b64"
	"1salem.app/connect/transport/internal/bridge"
	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/preamble"
	"1salem.app/connect/transport/internal/winsec"
)

// fakeAgent serves the HostAuthz protocol on a test pipe, the way the Agent
// does, and can push close events to subscribers.
type fakeAgent struct {
	name     string
	decision string
	// authorizeDelay holds back every authorize answer.
	authorizeDelay time.Duration

	mu          sync.Mutex
	lines       []map[string]any
	subscribers []net.Conn
}

func startAgent(t *testing.T, decision string) *fakeAgent {
	t.Helper()
	return serveAgent(t, &fakeAgent{decision: decision})
}

// startSlowAgent answers authorize only after delay, like an Agent whose
// decision comes after the host transport stopped waiting.
func startSlowAgent(t *testing.T, decision string, delay time.Duration) *fakeAgent {
	t.Helper()
	return serveAgent(t, &fakeAgent{decision: decision, authorizeDelay: delay})
}

func serveAgent(t *testing.T, a *fakeAgent) *fakeAgent {
	t.Helper()
	var b [8]byte
	rand.Read(b[:])
	a.name = `\\.\pipe\1Salem.Connect.Test.Authz.` + hex.EncodeToString(b[:])
	ln, err := pipe.Listen(a.name)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { ln.Close() })
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go a.serve(c)
		}
	}()
	return a
}

func (a *fakeAgent) serve(c net.Conn) {
	defer c.Close()
	sc := bufio.NewScanner(c)
	for sc.Scan() {
		var req map[string]any
		if err := json.Unmarshal(sc.Bytes(), &req); err != nil {
			return
		}
		a.mu.Lock()
		a.lines = append(a.lines, req)
		a.mu.Unlock()
		id := int64(req["id"].(float64))
		var body string
		switch req["op"] {
		case "hello":
			body = `,"v":1`
		case "authorize":
			time.Sleep(a.authorizeDelay)
			switch a.decision {
			case "allow":
				body = `,"decision":"allow","endpoint":"127.0.0.1:25565","connId":"c-1"`
			case "deny-with-connid":
				// The Agent never sends this; it pins that only an allow is ever
				// reported closed, whatever else a late answer carries.
				body = `,"decision":"deny","connId":"c-1"`
			default:
				body = `,"decision":"` + a.decision + `"`
			}
		case "subscribe":
			a.mu.Lock()
			a.subscribers = append(a.subscribers, c)
			a.mu.Unlock()
		}
		fmt.Fprintf(c, "{\"id\":%d,\"ok\":true%s}\n", id, body)
	}
}

func (a *fakeAgent) push(t *testing.T, line string) {
	t.Helper()
	a.mu.Lock()
	defer a.mu.Unlock()
	for _, c := range a.subscribers {
		fmt.Fprintf(c, "%s\n", line)
	}
}

func (a *fakeAgent) dropSubscribers() {
	a.mu.Lock()
	defer a.mu.Unlock()
	for _, c := range a.subscribers {
		c.Close()
	}
	a.subscribers = nil
}

func (a *fakeAgent) received() []map[string]any {
	a.mu.Lock()
	defer a.mu.Unlock()
	return slices.Clone(a.lines)
}

// closedReports returns the "closed" requests received, ordered by connId.
func (a *fakeAgent) closedReports() []bridge.ClosedReport {
	var reports []bridge.ClosedReport
	for _, l := range a.received() {
		if l["op"] != "closed" {
			continue
		}
		id, _ := l["connId"].(string)
		bytesIn, _ := l["bytesIn"].(float64)
		bytesOut, _ := l["bytesOut"].(float64)
		reports = append(reports, bridge.ClosedReport{ConnID: id, BytesIn: int64(bytesIn), BytesOut: int64(bytesOut)})
	}
	slices.SortFunc(reports, func(x, y bridge.ClosedReport) int { return strings.Compare(x.ConnID, y.ConnID) })
	return reports
}

func request() bridge.AuthorizeRequest {
	return bridge.AuthorizeRequest{
		Preamble: preamble.Preamble{
			T:  "eyJhbGciOiJFUzI1NiJ9.eyJzaWQiOiJ4In0.c2ln",
			N:  b64.Encode(make([]byte, 16)),
			TS: 1_790_000_000,
			P:  b64.Encode(make([]byte, 64)),
		},
		Peer: bridge.Peer{NodeID: "nFRIEND0001CNTRL", Addr: "100.100.1.2:40000"},
	}
}

func self(t *testing.T) []string {
	sid, err := winsec.CurrentUserSID()
	if err != nil {
		t.Fatal(err)
	}
	return []string{sid}
}

func TestAuthorizeAndClosed(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	for _, tc := range []struct {
		decision string
		want     bridge.Decision
		err      error
	}{
		{"allow", bridge.Decision{Allow: true, Endpoint: "127.0.0.1:25565", ConnID: "c-1"}, nil},
		{"deny", bridge.Decision{}, nil},
		{"maybe", bridge.Decision{}, authz.ErrBadResponse},
	} {
		a := startAgent(t, tc.decision)
		c, err := authz.New(a.name, self(t), t.Logf)
		if err != nil {
			t.Fatal(err)
		}
		d, err := c.Authorize(ctx, request())
		if d != tc.want || !errors.Is(err, tc.err) {
			t.Errorf("%s: decision %+v, err %v", tc.decision, d, err)
		}
		if err := c.Closed(ctx, bridge.ClosedReport{ConnID: "c-1", BytesIn: 10, BytesOut: 20}); err != nil {
			t.Errorf("closed: %v", err)
		}
		c.Close()

		lines := a.received()
		if len(lines) != 3 || lines[0]["op"] != "hello" || lines[0]["v"] != float64(1) {
			t.Fatalf("%s: agent received %v", tc.decision, lines)
		}
		// The authorize request is exactly {preamble:{t,n,ts,p}, peer:{nodeId, addr}}.
		auth := lines[1]
		if auth["op"] != "authorize" || len(auth) != 4 {
			t.Fatalf("authorize request %v", auth)
		}
		p, _ := auth["preamble"].(map[string]any)
		peer, _ := auth["peer"].(map[string]any)
		if len(p) != 4 || p["t"] == nil || p["n"] == nil || p["ts"] == nil || p["p"] == nil ||
			len(peer) != 2 || peer["nodeId"] != "nFRIEND0001CNTRL" || peer["addr"] != "100.100.1.2:40000" {
			t.Fatalf("authorize body %v", auth)
		}
		closed := lines[2]
		if closed["op"] != "closed" || closed["connId"] != "c-1" || closed["bytesIn"] != float64(10) || closed["bytesOut"] != float64(20) {
			t.Fatalf("closed request %v", closed)
		}
	}
}

func TestRefusesAPipeWithAnUnexpectedOwner(t *testing.T) {
	a := startAgent(t, "allow")
	c, err := authz.New(a.name, []string{winsec.SystemSID, winsec.AdministratorsSID}, t.Logf)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if _, err := c.Authorize(ctx, request()); !errors.Is(err, winsec.ErrUntrustedOwner) {
		t.Fatalf("err = %v, want ErrUntrustedOwner", err)
	}
	time.Sleep(50 * time.Millisecond)
	if lines := a.received(); len(lines) != 0 {
		t.Fatalf("sent %v to a pipe owned by someone else", lines)
	}
	if _, err := authz.New(a.name, nil, nil); err == nil {
		t.Fatal("a client without an expected owner was created")
	}
}

// logRecorder collects log lines. Unlike t.Logf it may still be called by a
// goroutine that outlives the test.
type logRecorder struct {
	mu    sync.Mutex
	lines []string
}

func (r *logRecorder) logf(format string, args ...any) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.lines = append(r.lines, fmt.Sprintf(format, args...))
}

func (r *logRecorder) contains(s string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return slices.ContainsFunc(r.lines, func(l string) bool { return strings.Contains(l, s) })
}

// An allow that arrives after the host transport's authorize timeout cannot be
// used: the bridge has refused the friend and never registered the
// connection. §11: it is reported closed with no bytes, so the Agent does not
// track it until its next subscription loss. A late deny needs nothing, even
// one that carries a well-formed connId.
func TestLateAllowIsReportedClosed(t *testing.T) {
	const delay = time.Second
	for _, decision := range []string{"allow", "deny", "deny-with-connid"} {
		a := startSlowAgent(t, decision, delay)
		var log logRecorder
		c, err := authz.New(a.name, self(t), log.logf)
		if err != nil {
			t.Fatal(err)
		}
		ctx, cancel := context.WithTimeout(context.Background(), delay/3)
		_, err = c.Authorize(ctx, request())
		cancel()
		if !errors.Is(err, context.DeadlineExceeded) {
			t.Fatalf("%s: Authorize past its deadline: %v", decision, err)
		}
		// The Agent answers one connection's requests in order, so once this
		// call returns the late answer has reached the client.
		ctx, cancel = context.WithTimeout(context.Background(), 5*time.Second)
		err = c.Closed(ctx, bridge.ClosedReport{ConnID: "marker"})
		cancel()
		if err != nil {
			t.Fatalf("%s: closed: %v", decision, err)
		}

		want := []bridge.ClosedReport{{ConnID: "marker"}}
		if decision == "allow" {
			want = []bridge.ClosedReport{{ConnID: "c-1", BytesIn: 0, BytesOut: 0}, {ConnID: "marker"}}
		}
		// The report of the late allow is sent from its own goroutine.
		deadline := time.Now().Add(5 * time.Second)
		for len(a.closedReports()) < len(want) && time.Now().Before(deadline) {
			time.Sleep(10 * time.Millisecond)
		}
		if decision != "allow" {
			// A wrong report would come from its own goroutine; give it time to land.
			time.Sleep(300 * time.Millisecond)
		}
		if got := a.closedReports(); !slices.Equal(got, want) {
			t.Fatalf("%s: closed reports %+v, want %+v", decision, got, want)
		}
		if logged := log.contains("allowed connection c-1 after the bridge stopped waiting"); logged != (decision == "allow") {
			t.Fatalf("%s: late allow logged = %v", decision, logged)
		}
		c.Close()
	}
}

// sink records what the subscription delivers.
type sink struct {
	mu     sync.Mutex
	closed [][]string
	states []bool
	change chan struct{}
}

func (s *sink) CloseConns(ids []string) int {
	s.mu.Lock()
	s.closed = append(s.closed, ids)
	s.mu.Unlock()
	s.notify()
	return len(ids)
}

func (s *sink) SetRevocationChannel(up bool) {
	s.mu.Lock()
	s.states = append(s.states, up)
	s.mu.Unlock()
	s.notify()
}

func (s *sink) notify() {
	select {
	case s.change <- struct{}{}:
	default:
	}
}

func (s *sink) wait(t *testing.T, cond func() bool) {
	t.Helper()
	deadline := time.After(5 * time.Second)
	for {
		s.mu.Lock()
		ok := cond()
		s.mu.Unlock()
		if ok {
			return
		}
		select {
		case <-s.change:
		case <-deadline:
			t.Fatal("timed out waiting for the subscription")
		}
	}
}

func TestSubscribe(t *testing.T) {
	a := startAgent(t, "allow")
	c, err := authz.New(a.name, self(t), t.Logf)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	s := &sink{change: make(chan struct{}, 64)}
	done := make(chan error, 1)
	go func() { done <- c.Subscribe(ctx, s) }()

	s.wait(t, func() bool { return len(s.states) > 0 && s.states[len(s.states)-1] })
	a.push(t, `{"event":"future-thing","x":1}`)
	a.push(t, `{"event":"close","connIds":["c-1","c-7"]}`)
	s.wait(t, func() bool { return len(s.closed) == 1 })
	if !slices.Equal(s.closed[0], []string{"c-1", "c-7"}) {
		t.Fatalf("closed %v", s.closed)
	}

	// Losing the stream reports the channel down, then it is re-established.
	a.dropSubscribers()
	s.wait(t, func() bool { return slices.Contains(s.states, false) })
	s.wait(t, func() bool { return s.states[len(s.states)-1] })

	cancel()
	select {
	case err := <-done:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("Subscribe returned %v", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("Subscribe did not stop")
	}
	if s.states[len(s.states)-1] {
		t.Fatal("revocation channel still reported up after Subscribe stopped")
	}
}
