package friend_test

import (
	"context"
	"crypto/ecdsa"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"1salem.app/connect/transport/internal/friend"
	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/preamble"
	"1salem.app/connect/transport/internal/proof"
	"1salem.app/connect/transport/internal/testkit"
	"1salem.app/connect/transport/internal/ticket"
	"1salem.app/connect/transport/internal/transport"
)

const friendNodeID = "nFAKE0001CNTRL"

// recorder wraps the fake transport and records every address dialed.
type recorder struct {
	transport.Transport
	mu    sync.Mutex
	dials []netip.AddrPort
}

func (r *recorder) Dial(ctx context.Context, addr netip.AddrPort) (net.Conn, error) {
	r.mu.Lock()
	r.dials = append(r.dials, addr)
	r.mu.Unlock()
	return r.Transport.Dial(ctx, addr)
}

func (r *recorder) dialed() []netip.AddrPort {
	r.mu.Lock()
	defer r.mu.Unlock()
	return append([]netip.AddrPort(nil), r.dials...)
}

type nodeSource struct{ tr transport.Transport }

func (n nodeSource) Get(ctx context.Context, node string) (transport.Transport, error) {
	return n.tr, nil
}

// freeLoopback returns a loopback address with a port that was free a moment ago.
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

type hostSeen struct {
	nodeID string
	p      preamble.Preamble
	err    error
}

// fakeHost plays the host bridge: it checks the preamble the way the Agent
// would, answers with status, and echoes when it accepts.
func fakeHost(t *testing.T, status byte) (hb string, seen chan hostSeen) {
	t.Helper()
	tr, err := transport.NewFake("")
	if err != nil {
		t.Fatal(err)
	}
	hb = freeLoopback(t)
	ln, err := tr.Listen(hb)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { ln.Close() })
	seen = make(chan hostSeen, 16)
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go func() {
				defer c.Close()
				c.SetDeadline(time.Now().Add(5 * time.Second))
				p, err := preamble.Read(c)
				var node string
				if err == nil {
					node, err = tr.PeerNodeID(context.Background(), c)
				}
				seen <- hostSeen{nodeID: node, p: p, err: err}
				if err != nil {
					return
				}
				c.Write([]byte{status})
				if status == preamble.StatusOK {
					c.SetDeadline(time.Time{})
					io.Copy(c, c)
				}
			}()
		}
	}()
	return hb, seen
}

type setup struct {
	signer *testkit.Signer
	mgr    *friend.Manager
	rec    *recorder
}

func newSetup(t *testing.T) *setup {
	t.Helper()
	signer := testkit.NewSigner(t, "k1")
	ks, err := ticket.ParseKeyset(testkit.KeysetJSON(t, signer))
	if err != nil {
		t.Fatal(err)
	}
	fake, err := transport.NewFake(friendNodeID)
	if err != nil {
		t.Fatal(err)
	}
	rec := &recorder{Transport: fake}
	mgr := friend.NewManager(friend.Config{
		Verifier: &ticket.Verifier{Keys: ks, Mode: netpolicy.Fake},
		Nodes:    nodeSource{rec},
		Logf:     t.Logf,
	})
	t.Cleanup(mgr.CloseAll)
	return &setup{signer: signer, mgr: mgr, rec: rec}
}

func (s *setup) ticket(t *testing.T, hb string, key *ecdsa.PrivateKey) string {
	return s.signer.Mint(t, s.signer.Header(), testkit.Claims(t, time.Now(), hb, &key.PublicKey))
}

func TestSessionDialsOnlyTheTicketHostBridge(t *testing.T) {
	s := newSetup(t)
	hb, seen := fakeHost(t, preamble.StatusOK)
	key := testkit.NewKey(t)
	raw := s.ticket(t, hb, key)

	info, err := s.mgr.Open(context.Background(), "owner1", raw, testkit.PKCS8(t, key), 0)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	local := netip.MustParseAddrPort(info.Local)
	if local.Addr() != netip.MustParseAddr("127.0.0.1") || local.Port() < friend.PortMin || local.Port() > friend.PortMax {
		t.Fatalf("local endpoint %s", local)
	}

	// Two connections, as Minecraft makes for a ping and a login.
	for i := range 2 {
		c, err := net.DialTimeout("tcp", info.Local, 2*time.Second)
		if err != nil {
			t.Fatal(err)
		}
		c.SetDeadline(time.Now().Add(5 * time.Second))
		if _, err := c.Write([]byte("ping")); err != nil {
			t.Fatal(err)
		}
		buf := make([]byte, 4)
		if _, err := io.ReadFull(c, buf); err != nil || string(buf) != "ping" {
			t.Fatalf("connection %d: echo %q, %v", i, buf, err)
		}
		c.Close()

		h := <-seen
		if h.err != nil {
			t.Fatalf("host rejected the preamble: %v", h.err)
		}
		if h.nodeID != friendNodeID || h.p.T != raw {
			t.Fatalf("host saw node %q, ticket forwarded verbatim: %v", h.nodeID, h.p.T == raw)
		}
		tk, _ := (&ticket.Verifier{Keys: mustKeys(t, s.signer), Mode: netpolicy.Fake}).Verify(h.p.T)
		if err := proof.Verify(&key.PublicKey, tk.JTI, h.p.N, h.p.TS, tk.ServerID, h.p.P); err != nil {
			t.Fatalf("proof does not verify against skp: %v", err)
		}
	}

	want := netip.MustParseAddrPort(hb)
	dials := s.rec.dialed()
	if len(dials) != 2 {
		t.Fatalf("dialed %v, want exactly two dials of %s", dials, want)
	}
	for _, d := range dials {
		if d != want {
			t.Fatalf("dialed %s; the only permitted destination is the ticket's hb %s", d, want)
		}
	}
}

func mustKeys(t *testing.T, s *testkit.Signer) *ticket.Keyset {
	ks, err := ticket.ParseKeyset(testkit.KeysetJSON(t, s))
	if err != nil {
		t.Fatal(err)
	}
	return ks
}

// The app shows a session's state from the newest of these log lines, so an
// accepted connection must be logged while it is still open, not only when it
// ends; otherwise an earlier failure stays the newest line for a whole game.
func TestAcceptedConnectionIsLoggedWhileItIsOpen(t *testing.T) {
	var mu sync.Mutex
	var lines []string
	signer := testkit.NewSigner(t, "k1")
	fake, err := transport.NewFake(friendNodeID)
	if err != nil {
		t.Fatal(err)
	}
	mgr := friend.NewManager(friend.Config{
		Verifier: &ticket.Verifier{Keys: mustKeys(t, signer), Mode: netpolicy.Fake},
		Nodes:    nodeSource{&recorder{Transport: fake}},
		Logf: func(format string, args ...any) {
			mu.Lock()
			defer mu.Unlock()
			lines = append(lines, fmt.Sprintf(format, args...))
		},
	})
	t.Cleanup(mgr.CloseAll)

	hb, seen := fakeHost(t, preamble.StatusOK)
	key := testkit.NewKey(t)
	info, err := mgr.Open(context.Background(), "owner1",
		signer.Mint(t, signer.Header(), testkit.Claims(t, time.Now(), hb, &key.PublicKey)), testkit.PKCS8(t, key), 0)
	if err != nil {
		t.Fatal(err)
	}
	c, err := net.DialTimeout("tcp", info.Local, 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(5 * time.Second))
	if _, err := c.Write([]byte("ping")); err != nil {
		t.Fatal(err)
	}
	if _, err := io.ReadFull(c, make([]byte, 4)); err != nil {
		t.Fatal(err)
	}
	<-seen

	logged := func() []string {
		mu.Lock()
		defer mu.Unlock()
		return slices.Clone(lines)
	}

	// The echo came back, so the handshake is done and the stream is spliced.
	accepted := fmt.Sprintf("friend: session %s connection accepted", info.SessionID)
	if !slices.Contains(logged(), accepted) {
		t.Fatalf("log %q has no %q", logged(), accepted)
	}
	for _, l := range logged() {
		if strings.Contains(l, "connection ended") {
			t.Fatalf("the open connection was logged as ended: %q", l)
		}
	}

	// A connection the host refuses is never logged as accepted.
	refusingHB, refusedSeen := fakeHost(t, preamble.StatusRefused)
	refusedKey := testkit.NewKey(t)
	refused, err := mgr.Open(context.Background(), "owner1",
		signer.Mint(t, signer.Header(), testkit.Claims(t, time.Now(), refusingHB, &refusedKey.PublicKey)), testkit.PKCS8(t, refusedKey), 0)
	if err != nil {
		t.Fatal(err)
	}
	rc, err := net.DialTimeout("tcp", refused.Local, 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer rc.Close()
	rc.SetDeadline(time.Now().Add(5 * time.Second))
	rc.Read(make([]byte, 1))
	<-refusedSeen
	notAccepted := fmt.Sprintf("friend: session %s connection not accepted", refused.SessionID)
	deadline := time.Now().Add(5 * time.Second)
	for !slices.ContainsFunc(logged(), func(l string) bool { return strings.HasPrefix(l, notAccepted) }) {
		if time.Now().After(deadline) {
			t.Fatalf("log %q has no %q", logged(), notAccepted)
		}
		time.Sleep(10 * time.Millisecond)
	}
	if slices.Contains(logged(), fmt.Sprintf("friend: session %s connection accepted", refused.SessionID)) {
		t.Fatalf("a refused connection was logged as accepted: %q", logged())
	}
}

func TestRefusedConnectionIsClosed(t *testing.T) {
	s := newSetup(t)
	hb, seen := fakeHost(t, preamble.StatusRefused)
	key := testkit.NewKey(t)
	info, err := s.mgr.Open(context.Background(), "owner1", s.ticket(t, hb, key), testkit.PKCS8(t, key), 0)
	if err != nil {
		t.Fatal(err)
	}
	c, err := net.DialTimeout("tcp", info.Local, 2*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(5 * time.Second))
	if n, err := c.Read(make([]byte, 1)); err == nil {
		t.Fatalf("read %d bytes from a refused connection", n)
	}
	<-seen
}

func TestOpenAndRefreshChecks(t *testing.T) {
	s := newSetup(t)
	hb, _ := fakeHost(t, preamble.StatusOK)
	key := testkit.NewKey(t)
	raw := s.ticket(t, hb, key)
	ctx := context.Background()

	if _, err := s.mgr.Open(ctx, "owner1", raw, testkit.PKCS8(t, key), 80); !errors.Is(err, friend.ErrPort) {
		t.Errorf("port 80: err = %v, want ErrPort", err)
	}
	if _, err := s.mgr.Open(ctx, "owner1", raw, testkit.PKCS8(t, testkit.NewKey(t)), 0); !errors.Is(err, friend.ErrSessionKey) {
		t.Errorf("session key not matching skp: err = %v, want ErrSessionKey", err)
	}
	// In fake mode a tailnet hb is as unacceptable as any non-loopback one.
	if _, err := s.mgr.Open(ctx, "owner1", s.ticket(t, "100.64.0.1:7780", key), testkit.PKCS8(t, key), 0); !errors.Is(err, friend.ErrTicket) {
		t.Errorf("non-loopback hb in fake mode: err = %v, want ErrTicket", err)
	}

	info, err := s.mgr.Open(ctx, "owner1", raw, testkit.PKCS8(t, key), 0)
	if err != nil {
		t.Fatal(err)
	}
	key2 := testkit.NewKey(t)
	if err := s.mgr.Refresh(info.SessionID, s.ticket(t, hb, key2), testkit.PKCS8(t, key2)); err != nil {
		t.Errorf("refresh with same hb and sid: %v", err)
	}
	other := freeLoopback(t)
	if err := s.mgr.Refresh(info.SessionID, s.ticket(t, other, key2), testkit.PKCS8(t, key2)); !errors.Is(err, friend.ErrTicketMismatch) {
		t.Errorf("refresh to another hb: err = %v, want ErrTicketMismatch", err)
	}
	if err := s.mgr.Close(info.SessionID); err != nil {
		t.Fatal(err)
	}
	if _, err := net.DialTimeout("tcp", info.Local, time.Second); err == nil {
		t.Error("closed session still accepts connections")
	}
	if err := s.mgr.Close(info.SessionID); !errors.Is(err, friend.ErrNoSession) {
		t.Errorf("second close: err = %v, want ErrNoSession", err)
	}
}
