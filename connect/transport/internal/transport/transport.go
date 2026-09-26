// Package transport abstracts the private network between a friend node and
// a host node (CONNECT_ARCHITECTURE.md §11 "Transport abstraction").
//
// Two implementations exist:
//
//   - Tsnet: a real Tailscale userspace node (tsnet). No TUN adapter, route or
//     DNS change. Peer identity comes from WhoIs.
//   - Fake: loopback TCP standing in for the tailnet, for tests and the
//     disposable proof harness only. Its peer identity is self-asserted and is
//     NOT a security boundary.
package transport

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"

	"1salem.app/connect/transport/internal/netpolicy"
)

// Transport carries connections between nodes.
type Transport interface {
	// Dial opens a TCP connection to addr. Implementations re-check that addr
	// is a permitted destination for their mode; callers must also only pass
	// addresses from a verified ticket.
	Dial(ctx context.Context, addr netip.AddrPort) (net.Conn, error)
	// Listen accepts TCP connections on addr.
	Listen(addr string) (net.Listener, error)
	// PeerNodeID returns the node id of the remote end of a connection that
	// was accepted from this transport's listener.
	PeerNodeID(ctx context.Context, conn net.Conn) (string, error)
	// Close releases the transport and everything it opened.
	Close() error
}

// NodeInfo describes one node for the "status" and "diag" operations.
type NodeInfo struct {
	Node   string `json:"node"`
	NodeID string `json:"nodeId,omitempty"`
	State  string `json:"state"`
}

// NodeSet owns the nodes a sidecar can use, one per owner tailnet. The friend
// has one node per owner it has joined; the host has exactly one.
type NodeSet interface {
	// Enroll creates the node with a one-time auth key and returns its node
	// id. The auth key is used for this call only and is not retained.
	Enroll(ctx context.Context, node, authKey, hostname string) (nodeID string, err error)
	// Get returns a running transport for an enrolled node.
	Get(ctx context.Context, node string) (Transport, error)
	// List describes the known nodes.
	List() []NodeInfo
	// Forget stops an enrolled node and removes its state, so that the node
	// can be enrolled again with a new key. A friend needs this once the owner
	// has revoked it and deleted its device: the old identity can never log in
	// again, and while its state exists Enroll refuses the node.
	Forget(node string) error
	// Close shuts every node down.
	Close() error
}

var (
	// ErrNotEnrolled is returned by Get for a node that has never enrolled,
	// and by Forget for a node that is not enrolled.
	ErrNotEnrolled = errors.New("transport: node not enrolled")
	// ErrAlreadyEnrolled is returned by Enroll for a node that finished an
	// enrollment (its directory has the marker) or is enrolling in this process
	// right now. tsnet ignores an auth key once state exists, so re-enrolling
	// into that directory would silently keep the old identity. A directory with
	// tsnet state but no marker is left over from a failed or killed attempt; it
	// is emptied and enrolled, not refused.
	ErrAlreadyEnrolled = errors.New("transport: node already enrolled")
	// ErrInvalidNode is returned for a malformed node name.
	ErrInvalidNode = errors.New("transport: invalid node name")
	// ErrInvalidHostname is returned for a malformed hostname.
	ErrInvalidHostname = errors.New("transport: invalid hostname")
	// ErrInvalidAuthKey is returned for an auth key of the wrong shape.
	ErrInvalidAuthKey = errors.New("transport: invalid auth key")
	// ErrEnrollFailed is returned when the node could not come up.
	ErrEnrollFailed = errors.New("transport: enrollment failed")
	// ErrNodeBusy is returned by Forget for a node that is enrolling in this
	// process right now. The enrollment owns the node directory until it ends;
	// forgetting the node then is a separate, later step.
	ErrNodeBusy = errors.New("transport: node is enrolling")
	// ErrForgetFailed is returned by Forget when the node's directory failed
	// the reparse and owner checks, or could not be removed completely. The
	// node is stopped either way, and nothing outside its directory is touched.
	ErrForgetFailed = errors.New("transport: the node could not be forgotten")
	// ErrDestination is returned by Dial for a destination the mode forbids.
	ErrDestination = errors.New("transport: destination not permitted")
	// ErrListenAddr is returned by Listen for an address the mode forbids.
	ErrListenAddr = errors.New("transport: listen address not permitted")
)

var (
	// A node name becomes a directory name, so it is kept to a safe charset.
	nodePattern = regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)
	// Hostnames are presented to the control server as a DNS label.
	hostnamePattern = regexp.MustCompile(`^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$`)
	// authKeyPattern accepts pre-minted auth keys only. An OAuth client
	// secret ("tskey-client-...") is refused: tsnet would use it to mint keys
	// itself (§11), and a sidecar must never hold anything that can mint keys.
	// The binaries are also built without tsnet's OAuth hook; this check is
	// the first of the two layers, and it applies in fake mode too so callers
	// exercise the real rule.
	authKeyPattern = regexp.MustCompile(`^tskey-auth-[A-Za-z0-9_-]{1,200}$`)
)

// ValidateAuthKey accepts only a "tskey-auth-" key.
func ValidateAuthKey(authKey string) error {
	if !authKeyPattern.MatchString(authKey) {
		return ErrInvalidAuthKey
	}
	return nil
}

// ValidateNodeName checks a node name.
func ValidateNodeName(node string) error {
	if !nodePattern.MatchString(node) {
		return ErrInvalidNode
	}
	return nil
}

// ValidateHostname checks a hostname.
func ValidateHostname(hostname string) error {
	if !hostnamePattern.MatchString(hostname) {
		return ErrInvalidHostname
	}
	return nil
}

// CheckListenAddr applies the listen rule of a mode. Fake mode takes a literal
// loopback ip:port. Tsnet mode takes ":port" (every address of the node) or a
// Tailscale ip:port; a tsnet listener never binds a host interface either way.
func CheckListenAddr(mode netpolicy.Mode, addr string) error {
	switch mode {
	case netpolicy.Fake:
		ap, err := netpolicy.ParseAddrPort(addr)
		if err != nil || netpolicy.RequireLoopback(ap) != nil {
			return ErrListenAddr
		}
		return nil
	case netpolicy.Tsnet:
		host, port, err := net.SplitHostPort(addr)
		if err != nil {
			return ErrListenAddr
		}
		p, err := strconv.ParseUint(port, 10, 16)
		if err != nil || p == 0 {
			return ErrListenAddr
		}
		if host == "" {
			return nil
		}
		ip, err := netip.ParseAddr(host)
		if err != nil || !netpolicy.IsTailnet(ip) {
			return ErrListenAddr
		}
		return nil
	}
	return ErrListenAddr
}

// noDeadline clears a connection deadline.
var noDeadline time.Time

func sortNodes(nodes []NodeInfo) {
	slices.SortFunc(nodes, func(a, b NodeInfo) int { return strings.Compare(a.Node, b.Node) })
}
