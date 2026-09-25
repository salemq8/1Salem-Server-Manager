package bridge

import (
	"errors"
	"net"
	"net/netip"
	"testing"
)

// DefaultMaxPendingPerSource lets the external tests fill a source's pending
// budget at its default size.
const DefaultMaxPendingPerSource = defaultMaxPendingPerSource

func tcpAddr(s string) net.Addr {
	return net.TCPAddrFromAddrPort(netip.MustParseAddrPort(s))
}

func TestSourceKey(t *testing.T) {
	cases := []struct {
		addr net.Addr
		want string
	}{
		{tcpAddr("100.101.102.103:40001"), "100.101.102.103"},
		{tcpAddr("[fd7a:115c:a1e0::5]:40001"), "fd7a:115c:a1e0::5"},
		{tcpAddr("[::ffff:100.101.102.103]:40001"), "100.101.102.103"},
		{tcpAddr("127.0.0.1:40001"), "127.0.0.1"},
		{nil, unknownSource},
		{&net.UnixAddr{Name: "x", Net: "unix"}, unknownSource},
	}
	for _, tc := range cases {
		if got := sourceKey(tc.addr); got != tc.want {
			t.Errorf("sourceKey(%v) = %q, want %q", tc.addr, got, tc.want)
		}
	}
}

func TestSourceLimits(t *testing.T) {
	s := newSourceLimits(2, 3)
	peer, other := tcpAddr("100.64.0.1:1000"), tcpAddr("100.64.0.2:1000")
	admit := func(addr net.Addr) *admission {
		t.Helper()
		a, err := s.admit(addr)
		if err != nil {
			t.Fatalf("admit %v: %v", addr, err)
		}
		return a
	}

	// Pending cap: the port does not matter, the address does.
	a1, a2 := admit(peer), admit(tcpAddr("100.64.0.1:1001"))
	if _, err := s.admit(peer); !errors.Is(err, errSourcePending) {
		t.Fatalf("third pending: %v", err)
	}
	admit(other).release()

	// Going live frees a pending slot; the live cap then applies.
	if err := a1.promote(); err != nil {
		t.Fatal(err)
	}
	if err := a2.promote(); err != nil {
		t.Fatal(err)
	}
	a3 := admit(peer)
	if err := a3.promote(); err != nil {
		t.Fatal(err)
	}
	if _, err := s.admit(peer); !errors.Is(err, errSourceLive) {
		t.Fatalf("admit at the live cap: %v", err)
	}

	// Promotion re-checks the cap for connections admitted below it.
	a3.release()
	a4, a5 := admit(peer), admit(peer)
	if err := a4.promote(); err != nil {
		t.Fatal(err)
	}
	if err := a5.promote(); !errors.Is(err, errSourceLive) {
		t.Fatalf("promotion past the live cap: %v", err)
	}

	for _, a := range []*admission{a1, a2, a4, a5} {
		a.release()
	}
	if len(s.use) != 0 {
		t.Fatalf("released sources still tracked: %v", s.use)
	}
}
