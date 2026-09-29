//go:build windows && tsnetsmoke

package transport

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
	"testing"
	"time"

	"golang.org/x/sys/windows"
	"tailscale.com/client/local"

	"1salem.app/connect/transport/internal/preamble"
)

// TestPhase2AuthenticatedFrames reopens only the isolated friend's existing node.
// It never enrolls, creates a key, or substitutes for Agent authorization.
func TestPhase2AuthenticatedFrames(t *testing.T) {
	input := os.Getenv("ONESALEM_PHASE2_FRAMES")
	if input == "" {
		t.Skip("isolated Phase 2 driver input is absent")
	}
	os.Unsetenv("ONESALEM_PHASE2_FRAMES")
	ScrubEnvironment()
	var frames []struct {
		Name  string `json:"name"`
		Frame string `json:"frame"`
		Allow bool   `json:"allow"`
	}
	if len(input) > 60000 || json.Unmarshal([]byte(input), &frames) != nil || len(frames) < 2 || len(frames) > 3 {
		t.Fatal("invalid bounded Phase 2 inputs")
	}
	run := newSmokeRun(t, requireEnv(t, envResult))
	defer run.write()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()
	nodes := newSmokeNodes(t, requireEnv(t, envStateDir))
	node := requireEnv(t, "ONESALEM_PHASE2_NODE")
	tr, err := nodes.Get(ctx, node)
	if err != nil {
		run.record("cached friend node starts", false, "could not start cached node")
		return
	}
	ts, ok := tr.(*Tsnet)
	if !ok {
		t.Fatal("expected production tsnet")
	}
	for _, item := range nodes.List() {
		if item.Node == node {
			run.result.NodeID = item.NodeID
		}
	}
	if run.result.NodeID != requireEnv(t, "ONESALEM_PHASE2_NODE_ID") {
		run.record("cached friend identity", false, "node identity differs")
		return
	}
	run.record("cached friend identity", true, "same real node, no enrollment key")
	bridge := netip.AddrPortFrom(requireHost(t), smokeBridgePort)
	ready, detail := phase2WaitForPeer(ctx, ts, bridge.Addr(), run.result.NodeID,
		requireEnv(t, "ONESALEM_PHASE2_HOST_NODE_ID"))
	run.record("exact host peer map ready", ready, "%s", detail)
	if !ready {
		return
	}
	for _, item := range frames {
		frame, err := hex.DecodeString(item.Frame)
		if err != nil || len(frame) > preamble.MaxPayload+preamble.HeaderLen {
			t.Fatal("invalid frame input")
		}
		conn, err := dialStep(ctx, ts, bridge, smokeStepTimeout)
		if err != nil {
			run.record(item.Name, false, "production dial failed; error_class=%s", phase2ProbeErrorClass(err))
			continue
		}
		// Dial waits for Up and a usable network map; Get only starts the cached node.
		ip4, ip6 := ts.srv.TailscaleIPs()
		if requireNetstackSource(conn.LocalAddr(), ip4, ip6) != nil {
			conn.Close()
			run.record(item.Name, false, "connection is not netstack")
			continue
		}
		conn.SetDeadline(time.Now().Add(smokeStepTimeout))
		_, err = conn.Write(frame)
		status := []byte{255}
		if err == nil {
			_, err = io.ReadFull(conn, status)
		}
		conn.Close()
		want := preamble.StatusRefused
		if item.Allow {
			want = preamble.StatusOK
		}
		run.record(item.Name, err == nil && status[0] == want,
			"real Agent status byte, no timeout accepted; status=%d; expected=%d; error_class=%s",
			status[0], want, phase2ProbeErrorClass(err))
	}
}

// Get only starts the cached node, and Up can return with a cached network map.
// Wait for this exact peer before sending any frame. This is a read-only readiness
// gate, not a retry of a connection, authorization decision, or status refusal.
func phase2WaitForPeer(ctx context.Context, ts *Tsnet, host netip.Addr, selfID, hostID string) (bool, string) {
	ctx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	started := time.Now()
	attempts := 0
	result := func(ready bool, phase, class string) (bool, string) {
		return ready, fmt.Sprintf("phase=%s; error_class=%s; whois_attempts=%d; elapsed_ms=%d",
			phase, class, attempts, time.Since(started).Milliseconds())
	}
	actualSelf, err := ts.Up(ctx)
	if err != nil {
		return result(false, "up", phase2ProbeErrorClass(err))
	}
	if actualSelf != selfID {
		return result(false, "up", "self-identity-mismatch")
	}
	ip4, ip6 := ts.srv.TailscaleIPs()
	if !ip4.IsValid() && !ip6.IsValid() {
		return result(false, "up", "self-addresses-missing")
	}
	lc, err := ts.srv.LocalClient()
	if err != nil {
		return result(false, "local-client", phase2ProbeErrorClass(err))
	}
	for {
		if err := ctx.Err(); err != nil {
			return result(false, "whois", phase2ProbeErrorClass(err))
		}
		attempts++
		who, err := lc.WhoIs(ctx, host.String())
		if err == nil {
			if who == nil || who.Node == nil || string(who.Node.StableID) != hostID {
				return result(false, "whois", "host-identity-mismatch")
			}
			return result(true, "whois", "none")
		}
		if !errors.Is(err, local.ErrPeerNotFound) {
			return result(false, "whois", phase2ProbeErrorClass(err))
		}
		timer := time.NewTimer(250 * time.Millisecond)
		select {
		case <-ctx.Done():
			timer.Stop()
			return result(false, "whois", phase2ProbeErrorClass(ctx.Err()))
		case <-timer.C:
		}
	}
}

// Emit a closed set of metadata labels, never an arbitrary error message that
// might contain a ticket, key, HTTP response, state path, or other private data.
func phase2ProbeErrorClass(err error) string {
	var netErr net.Error
	switch {
	case err == nil:
		return "none"
	case errors.Is(err, errHostNetworkDial):
		return "host-network-fallback"
	case errors.Is(err, ErrDestination):
		return "destination-or-peer-map-refused"
	case errors.Is(err, local.ErrPeerNotFound):
		return "peer-not-found"
	case errors.Is(err, context.Canceled):
		return "canceled"
	case errors.Is(err, context.DeadlineExceeded), errors.Is(err, os.ErrDeadlineExceeded):
		return "deadline"
	case errors.As(err, &netErr) && netErr.Timeout():
		return "timeout"
	case errors.Is(err, windows.WSAECONNREFUSED):
		return "connection-refused"
	case errors.Is(err, windows.WSAECONNRESET):
		return "connection-reset"
	case errors.Is(err, windows.WSAEADDRINUSE):
		return "address-in-use"
	case errors.Is(err, net.ErrClosed):
		return "closed"
	case errors.Is(err, io.EOF), errors.Is(err, io.ErrUnexpectedEOF):
		return "eof"
	}
	// gVisor wraps its socket errors in net.OpError with a plain error string,
	// not an errno. Match only its fixed values; never print the string itself.
	var opErr *net.OpError
	if errors.As(err, &opErr) && opErr.Err != nil {
		switch opErr.Err.Error() {
		case "connection was refused":
			return "connection-refused"
		case "connection reset by peer":
			return "connection-reset"
		case "port is in use", "endpoint already bound":
			return "address-in-use"
		}
	}
	return "other"
}

func TestPhase2ProbeErrorClass(t *testing.T) {
	for _, test := range []struct {
		name string
		err  error
		want string
	}{
		{"success", nil, "none"},
		{"fallback-before-destination", fmt.Errorf("wrapped: %w", errHostNetworkDial), "host-network-fallback"},
		{"destination", ErrDestination, "destination-or-peer-map-refused"},
		{"peer-map", local.ErrPeerNotFound, "peer-not-found"},
		{"canceled", context.Canceled, "canceled"},
		{"deadline", context.DeadlineExceeded, "deadline"},
		{"socket-deadline", os.ErrDeadlineExceeded, "deadline"},
		{"windows-refused", &net.OpError{Err: windows.WSAECONNREFUSED}, "connection-refused"},
		{"windows-reset", windows.WSAECONNRESET, "connection-reset"},
		{"windows-port-reuse", windows.WSAEADDRINUSE, "address-in-use"},
		{"netstack-refused", &net.OpError{Err: errors.New("connection was refused")}, "connection-refused"},
		{"netstack-reset", &net.OpError{Err: errors.New("connection reset by peer")}, "connection-reset"},
		{"netstack-port-reuse", &net.OpError{Err: errors.New("port is in use")}, "address-in-use"},
		{"closed", net.ErrClosed, "closed"},
		{"eof", io.EOF, "eof"},
		{"short-status", io.ErrUnexpectedEOF, "eof"},
		{"private-error", errors.New("private-key-or-token-must-not-be-printed"), "other"},
		{"private-network-error", &net.OpError{Err: errors.New("private-key-or-token-must-not-be-printed")}, "other"},
	} {
		t.Run(test.name, func(t *testing.T) {
			if got := phase2ProbeErrorClass(test.err); got != test.want {
				t.Errorf("error class = %q, want %q", got, test.want)
			}
		})
	}
}
