//go:build windows

// Package e2e runs the whole Phase 1 data plane in one process, offline:
// friend manager -> fake tailnet -> host bridge -> Agent pipe -> loopback game
// server. The Agent here is a stand-in that performs the checks §9/§10 assign
// to the real one (ticket, node binding, proof, replay) so that the Go side is
// exercised against a strict counterpart.
package e2e

import (
	"bufio"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"sync"
	"testing"
	"time"

	"1salem.app/connect/transport/internal/authz"
	"1salem.app/connect/transport/internal/bridge"
	"1salem.app/connect/transport/internal/friend"
	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/preamble"
	"1salem.app/connect/transport/internal/proof"
	"1salem.app/connect/transport/internal/testkit"
	"1salem.app/connect/transport/internal/ticket"
	"1salem.app/connect/transport/internal/transport"
	"1salem.app/connect/transport/internal/winsec"
)

const friendNodeID = "nFAKE0001CNTRL" // testkit.Claims binds tickets to this nid

type agent struct {
	name     string
	verifier *ticket.Verifier
	endpoint string

	mu     sync.Mutex
	seen   map[string]bool
	subs   []net.Conn
	nextID int
	closed chan bridge.ClosedReport
}

func startAgent(t *testing.T, verifier *ticket.Verifier, endpoint string) *agent {
	var b [8]byte
	rand.Read(b[:])
	a := &agent{
		verifier: verifier, endpoint: endpoint,
		name:   `\\.\pipe\1Salem.Connect.Test.E2E.` + hex.EncodeToString(b[:]),
		seen:   make(map[string]bool),
		closed: make(chan bridge.ClosedReport, 16),
	}
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

type agentReq struct {
	ID       int64              `json:"id"`
	Op       string             `json:"op"`
	Preamble *preamble.Preamble `json:"preamble"`
	Peer     *bridge.Peer       `json:"peer"`
	bridge.ClosedReport
}

func (a *agent) serve(c net.Conn) {
	defer c.Close()
	sc := bufio.NewScanner(c)
	for sc.Scan() {
		var req agentReq
		if err := json.Unmarshal(sc.Bytes(), &req); err != nil {
			return
		}
		body := ""
		switch req.Op {
		case "hello":
			body = `,"v":1`
		case "authorize":
			body = a.authorize(req)
		case "closed":
			a.closed <- req.ClosedReport
		case "subscribe":
			a.mu.Lock()
			a.subs = append(a.subs, c)
			a.mu.Unlock()
		}
		fmt.Fprintf(c, "{\"id\":%d,\"ok\":true%s}\n", req.ID, body)
	}
}

// authorize is the §9/§10 host check list, minus the parts that need the
// broker (revocation lists, server catalog).
func (a *agent) authorize(req agentReq) string {
	const deny = `,"decision":"deny"`
	if req.Preamble == nil || req.Peer == nil {
		return deny
	}
	tk, err := a.verifier.Verify(req.Preamble.T)
	if err != nil || tk.NodeID != req.Peer.NodeID {
		return deny
	}
	if d := time.Since(time.Unix(req.Preamble.TS, 0)); d > time.Minute || d < -time.Minute {
		return deny
	}
	if proof.Verify(tk.SessionKey, tk.JTI, req.Preamble.N, req.Preamble.TS, tk.ServerID, req.Preamble.P) != nil {
		return deny
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	key := tk.JTI + "|" + req.Preamble.N
	if a.seen[key] {
		return deny
	}
	a.seen[key] = true
	a.nextID++
	return fmt.Sprintf(`,"decision":"allow","endpoint":%q,"connId":"e2e-%d"`, a.endpoint, a.nextID)
}

func (a *agent) revoke(connIDs ...string) {
	line, _ := json.Marshal(map[string]any{"event": "close", "connIds": connIDs})
	a.mu.Lock()
	defer a.mu.Unlock()
	for _, c := range a.subs {
		c.Write(append(line, '\n'))
	}
}

func echoServer(t *testing.T) string {
	ln, err := net.Listen("tcp4", "127.0.0.1:0")
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
			go func() { defer c.Close(); io.Copy(c, c) }()
		}
	}()
	return ln.Addr().String()
}

func freeLoopback(t *testing.T) string {
	l, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	return l.Addr().String()
}

func TestDataPlane(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	signer := testkit.NewSigner(t, "k1")
	keys, err := ticket.ParseKeyset(testkit.KeysetJSON(t, signer))
	if err != nil {
		t.Fatal(err)
	}
	verifier := &ticket.Verifier{Keys: keys, Mode: netpolicy.Fake}
	game := echoServer(t)
	ag := startAgent(t, verifier, game)

	// Host side.
	self, _ := winsec.CurrentUserSID()
	client, err := authz.New(ag.name, []string{self}, t.Logf)
	if err != nil {
		t.Fatal(err)
	}
	br := bridge.New(bridge.Config{Authorizer: client, Logf: t.Logf})
	hostTr, _ := transport.NewFake("")
	hb := freeLoopback(t)
	go br.Serve(ctx, hostTr, hb)
	go client.Subscribe(ctx, br)
	deadline := time.Now().Add(5 * time.Second)
	for st := br.Status(); st.Listen == "" || !st.Revocation; st = br.Status() {
		if time.Now().After(deadline) {
			t.Fatalf("host not ready: %+v", st)
		}
		time.Sleep(10 * time.Millisecond)
	}

	// Friend side.
	nodes, _ := transport.NewFakeNodes(friendNodeID)
	mgr := friend.NewManager(friend.Config{Verifier: verifier, Nodes: nodes, Logf: t.Logf})
	defer mgr.CloseAll()
	sessionKey := testkit.NewKey(t)
	raw := signer.Mint(t, signer.Header(), testkit.Claims(t, time.Now(), hb, &sessionKey.PublicKey))
	info, err := mgr.Open(ctx, "owner1", raw, testkit.PKCS8(t, sessionKey), 0)
	if err != nil {
		t.Fatal(err)
	}

	dial := func() net.Conn {
		c, err := net.DialTimeout("tcp4", info.Local, 2*time.Second)
		if err != nil {
			t.Fatal(err)
		}
		c.SetDeadline(time.Now().Add(5 * time.Second))
		return c
	}
	roundTrip := func(c net.Conn, msg string) error {
		if _, err := c.Write([]byte(msg)); err != nil {
			return err
		}
		buf := make([]byte, len(msg))
		if _, err := io.ReadFull(c, buf); err != nil {
			return err
		}
		if string(buf) != msg {
			return fmt.Errorf("echo %q", buf)
		}
		return nil
	}

	// A ping and then a login, each authorized separately.
	ping := dial()
	if err := roundTrip(ping, "status request"); err != nil {
		t.Fatalf("ping: %v", err)
	}
	ping.Close()
	if rep := <-ag.closed; rep.ConnID != "e2e-1" || rep.BytesIn != 14 || rep.BytesOut != 14 {
		t.Fatalf("closed report %+v", rep)
	}
	login := dial()
	if err := roundTrip(login, "login start"); err != nil {
		t.Fatalf("login: %v", err)
	}

	// Revocation ends the live connection and the Agent hears it closed.
	ag.revoke("e2e-2")
	if _, err := login.Read(make([]byte, 1)); err == nil {
		t.Fatal("revoked connection still open")
	}
	if rep := <-ag.closed; rep.ConnID != "e2e-2" {
		t.Fatalf("closed report %+v", rep)
	}

	// A ticket bound to another node is refused by the Agent: the local
	// client is simply disconnected.
	claims := testkit.Claims(t, time.Now(), hb, &sessionKey.PublicKey)
	claims["nid"] = "nSOMEONEELSE"
	if err := mgr.Refresh(info.SessionID, signer.Mint(t, signer.Header(), claims), testkit.PKCS8(t, sessionKey)); err != nil {
		t.Fatal(err)
	}
	stolen := dial()
	defer stolen.Close()
	if err := roundTrip(stolen, "x"); err == nil {
		t.Fatal("a ticket for another node was accepted")
	}
}
