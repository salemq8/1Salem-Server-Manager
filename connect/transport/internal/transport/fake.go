package transport

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"regexp"
	"sync"

	"1salem.app/connect/transport/internal/netpolicy"
)

// The fake transport stands in for the tailnet with loopback TCP. The dialer
// announces its node id in a "FAKENODE <id>\n" line and the listener takes it
// at face value.
//
// THIS IS NOT A SECURITY BOUNDARY. Any local process can claim any node id.
// It exists only for tests and the disposable proof harness, and it can only
// ever reach loopback addresses.

const fakeHeaderPrefix = "FAKENODE "

// maxFakeHeader is the prefix, the longest node id and the newline.
const maxFakeHeader = len(fakeHeaderPrefix) + 128 + 1

var fakeNodeIDPattern = regexp.MustCompile(`^[A-Za-z0-9._:-]{1,128}$`)

var errFakeHeader = errors.New("transport: bad FAKENODE header")

// Fake is the loopback stand-in transport.
type Fake struct {
	nodeID string
}

// NewFake returns a fake transport whose dials claim nodeID. A host that
// only listens may pass an empty nodeID, which disables Dial.
func NewFake(nodeID string) (*Fake, error) {
	if nodeID != "" && !fakeNodeIDPattern.MatchString(nodeID) {
		return nil, errors.New("transport: invalid fake node id")
	}
	return &Fake{nodeID: nodeID}, nil
}

// Dial connects to a loopback address and announces the fake node id.
func (f *Fake) Dial(ctx context.Context, addr netip.AddrPort) (net.Conn, error) {
	if f.nodeID == "" {
		return nil, errors.New("transport: fake transport has no node id to dial with")
	}
	if netpolicy.RequireLoopback(addr) != nil {
		return nil, ErrDestination
	}
	var d net.Dialer
	conn, err := d.DialContext(ctx, "tcp", addr.String())
	if err != nil {
		return nil, err
	}
	if deadline, ok := ctx.Deadline(); ok {
		conn.SetWriteDeadline(deadline)
	}
	if _, err := conn.Write([]byte(fakeHeaderPrefix + f.nodeID + "\n")); err != nil {
		conn.Close()
		return nil, err
	}
	conn.SetWriteDeadline(noDeadline)
	return conn, nil
}

// Listen listens on a loopback ip:port only.
func (f *Fake) Listen(addr string) (net.Listener, error) {
	if err := CheckListenAddr(netpolicy.Fake, addr); err != nil {
		return nil, err
	}
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return nil, err
	}
	return &fakeListener{Listener: ln}, nil
}

// PeerNodeID returns the node id the dialer claimed. The FAKENODE line is
// read lazily, under whatever deadline the caller set on the connection.
func (f *Fake) PeerNodeID(ctx context.Context, conn net.Conn) (string, error) {
	fc, ok := conn.(*fakeConn)
	if !ok {
		return "", errors.New("transport: connection did not come from the fake listener")
	}
	if err := ctx.Err(); err != nil {
		return "", err
	}
	return fc.peer()
}

// Close is a no-op; the fake transport owns no shared resources.
func (f *Fake) Close() error { return nil }

type fakeListener struct {
	net.Listener
}

// Accept does not read the FAKENODE header itself, so one slow client cannot
// stall the accept loop; the header is consumed on first use.
func (l *fakeListener) Accept() (net.Conn, error) {
	c, err := l.Listener.Accept()
	if err != nil {
		return nil, err
	}
	return &fakeConn{Conn: c}, nil
}

type fakeConn struct {
	net.Conn
	once   sync.Once
	nodeID string
	err    error
}

func (c *fakeConn) peer() (string, error) {
	c.once.Do(c.readHeader)
	return c.nodeID, c.err
}

// readHeader reads the header one byte at a time so that not a single byte of
// the preamble behind it is consumed.
func (c *fakeConn) readHeader() {
	buf := make([]byte, 0, maxFakeHeader)
	var b [1]byte
	for len(buf) < maxFakeHeader {
		if _, err := c.Conn.Read(b[:]); err != nil {
			c.err = err
			return
		}
		if b[0] == '\n' {
			line := string(buf)
			if len(line) <= len(fakeHeaderPrefix) || line[:len(fakeHeaderPrefix)] != fakeHeaderPrefix {
				c.err = errFakeHeader
				return
			}
			id := line[len(fakeHeaderPrefix):]
			if !fakeNodeIDPattern.MatchString(id) {
				c.err = errFakeHeader
				return
			}
			c.nodeID = id
			return
		}
		buf = append(buf, b[0])
	}
	c.err = errFakeHeader
}

func (c *fakeConn) Read(p []byte) (int, error) {
	if _, err := c.peer(); err != nil {
		return 0, err
	}
	return c.Conn.Read(p)
}

// CloseWrite forwards half-close so splices behave like real TCP.
func (c *fakeConn) CloseWrite() error {
	if cw, ok := c.Conn.(interface{ CloseWrite() error }); ok {
		return cw.CloseWrite()
	}
	return errors.New("transport: half-close unsupported")
}

// FakeNodes is the NodeSet for fake mode: every node name maps to the same
// loopback transport and the same self-asserted node id.
type FakeNodes struct {
	t     *Fake
	mu    sync.Mutex
	nodes map[string]struct{}
}

// NewFakeNodes returns a fake node set that dials as nodeID.
func NewFakeNodes(nodeID string) (*FakeNodes, error) {
	if nodeID == "" {
		return nil, errors.New("transport: fake mode requires a node id")
	}
	t, err := NewFake(nodeID)
	if err != nil {
		return nil, err
	}
	return &FakeNodes{t: t, nodes: make(map[string]struct{})}, nil
}

// Enroll records the node and returns the fake node id. The auth key must pass
// the same check as in tsnet mode (so callers exercise the real rule) and is
// discarded immediately: nothing is enrolled anywhere.
func (n *FakeNodes) Enroll(ctx context.Context, node, authKey, hostname string) (string, error) {
	if err := ValidateNodeName(node); err != nil {
		return "", err
	}
	if err := ValidateHostname(hostname); err != nil {
		return "", err
	}
	if err := ValidateAuthKey(authKey); err != nil {
		return "", err
	}
	n.mu.Lock()
	n.nodes[node] = struct{}{}
	n.mu.Unlock()
	return n.t.nodeID, nil
}

// Get returns the shared fake transport. The fake keeps no state across
// process runs, so it does not require an earlier Enroll.
func (n *FakeNodes) Get(ctx context.Context, node string) (Transport, error) {
	if err := ValidateNodeName(node); err != nil {
		return nil, err
	}
	n.mu.Lock()
	n.nodes[node] = struct{}{}
	n.mu.Unlock()
	return n.t, nil
}

// List describes the nodes used so far.
func (n *FakeNodes) List() []NodeInfo {
	n.mu.Lock()
	defer n.mu.Unlock()
	out := make([]NodeInfo, 0, len(n.nodes))
	for node := range n.nodes {
		out = append(out, NodeInfo{Node: node, NodeID: n.t.nodeID, State: "fake"})
	}
	sortNodes(out)
	return out
}

// Close closes the shared transport.
func (n *FakeNodes) Close() error { return n.t.Close() }
