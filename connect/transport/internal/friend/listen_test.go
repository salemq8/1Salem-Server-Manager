//go:build windows

package friend

import (
	"context"
	"net"
	"net/netip"
	"strconv"
	"syscall"
	"testing"
	"time"

	"golang.org/x/sys/windows"
)

// occupy binds the first port of the friend range that no listener holds on
// any address, standing in for some other program that got there first. A
// wildcard bind could share a port with someone's 127.0.0.1 listener (the e2e
// test's session, run in parallel, or a running 1Salem Connect), and the
// reachability check below would then reach that listener instead.
func occupy(t *testing.T, network, host string) (net.Listener, int) {
	t.Helper()
	busy, err := listeningPorts()
	if err != nil {
		t.Fatal(err)
	}
	for port := PortMin; port < PortMax; port++ {
		if busy[uint16(port)] {
			continue
		}
		occ, err := net.Listen(network, net.JoinHostPort(host, strconv.Itoa(port)))
		if err != nil {
			continue
		}
		return occ, port
	}
	t.Skip("no free port in 18211-18299 on this machine")
	return nil, 0
}

func TestBindLocalLoopbackOnlyAndFallsBack(t *testing.T) {
	for _, occ := range []struct{ network, host string }{
		{"tcp4", "127.0.0.1"},
		{"tcp4", "0.0.0.0"},
		{"tcp6", "::"},
		{"tcp6", "::1"},
	} {
		t.Run("occupier on "+occ.host, func(t *testing.T) {
			ln0, port := occupy(t, occ.network, occ.host)
			defer ln0.Close()

			ln, err := bindLocal(port)
			if err != nil {
				t.Fatalf("bindLocal(%d): %v", port, err)
			}
			defer ln.Close()
			got := netip.MustParseAddrPort(ln.Addr().String())
			if got.Addr() != netip.MustParseAddr("127.0.0.1") {
				t.Fatalf("bound %s, want 127.0.0.1 only", got)
			}
			if int(got.Port()) == port || got.Port() < PortMin || got.Port() > PortMax {
				t.Fatalf("bound port %d; occupied port was %d", got.Port(), port)
			}
			if occ.network == "tcp6" {
				// Go's tcp6 listeners are IPv6-only, so no IPv4 client
				// reaches them either way; skipping the port is the check.
				return
			}

			// The occupier still receives its own local clients: our listener
			// neither took the port over nor shadowed it.
			accepted := make(chan error, 1)
			go func() {
				c, err := ln0.Accept()
				if err == nil {
					c.Close()
				}
				accepted <- err
			}()
			c, err := net.DialTimeout("tcp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)), 2*time.Second)
			if err != nil {
				t.Fatalf("occupier unreachable: %v", err)
			}
			c.Close()
			select {
			case err := <-accepted:
				if err != nil {
					t.Fatalf("occupier accept: %v", err)
				}
			case <-time.After(2 * time.Second):
				t.Fatal("the connection to the occupied port did not reach the occupier")
			}
		})
	}
}

func TestListeningPortsSeesListeners(t *testing.T) {
	l4, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l4.Close()
	l6, err := net.Listen("tcp6", "[::1]:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l6.Close()
	ports, err := listeningPorts()
	if err != nil {
		t.Fatal(err)
	}
	for _, l := range []net.Listener{l4, l6} {
		p := netip.MustParseAddrPort(l.Addr().String()).Port()
		if !ports[p] {
			t.Errorf("listener on %s not in the table", l.Addr())
		}
	}
}

func TestBindLocalRefusesHijack(t *testing.T) {
	ln, err := bindLocal(DefaultPort)
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	// Another process trying to share our exact address with SO_REUSEADDR,
	// the classic port-hijack, is refused.
	lc := net.ListenConfig{Control: func(network, address string, c syscall.RawConn) error {
		var sockErr error
		c.Control(func(fd uintptr) {
			sockErr = windows.SetsockoptInt(windows.Handle(fd), windows.SOL_SOCKET, windows.SO_REUSEADDR, 1)
		})
		return sockErr
	}}
	if l2, err := lc.Listen(context.Background(), "tcp4", ln.Addr().String()); err == nil {
		l2.Close()
		t.Fatalf("a SO_REUSEADDR socket bound %s over ours", ln.Addr())
	}
}
