//go:build windows

package transport

// Everything that touches tailscale.com lives in this file.

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"sync"
	"time"

	"tailscale.com/envknob"
	"tailscale.com/logtail"
	"tailscale.com/tsnet"

	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/winsec"
)

// enrollTimeout bounds how long Enroll waits for the node to reach Running.
const enrollTimeout = 90 * time.Second

// markerFile is written into a node directory once enrollment succeeded. Its
// presence, not tsnet's own state file, decides "already enrolled": tsnet
// writes its state as soon as it starts, so a state file alone may come from
// an attempt that failed or was killed. Enroll empties a node directory that
// has no marker before tsnet sees it, and never builds on what it held.
const markerFile = "1salem-node.json"

var privacyOnce sync.Once

// applyPrivacyDefaults runs before the first tsnet server is created.
func applyPrivacyDefaults() {
	privacyOnce.Do(func() {
		// §17/§19: uploading node logs to Tailscale's log service needs a
		// consent decision that has not been made. Until it is, nothing is
		// uploaded; logs stay in the state directory and in Diagnostics.
		logtail.Disable()
		// §15: the only host sockets are the loopback listener, the WireGuard
		// UDP socket and HTTPS to the control plane. The port mapper would
		// also speak UPnP/NAT-PMP/PCP to the router and ask it to open a port.
		envknob.Setenv("TS_DISABLE_PORTMAPPER", "true")
	})
}

type tsnetConfig struct {
	Dir      string
	Hostname string
	AuthKey  string
	Logf     func(format string, args ...any)
	UserLogf func(format string, args ...any)
}

// Tsnet is one Tailscale userspace node.
type Tsnet struct {
	srv *tsnet.Server

	mu      sync.Mutex
	started bool
	closed  bool
}

func newTsnet(cfg tsnetConfig) *Tsnet {
	applyPrivacyDefaults()
	return &Tsnet{srv: &tsnet.Server{
		Dir:      cfg.Dir,
		Hostname: cfg.Hostname,
		// AuthKey only ever comes from the enroll operation; the environment
		// fallbacks were scrubbed at process start.
		AuthKey:      cfg.AuthKey,
		Logf:         cfg.Logf,
		UserLogf:     cfg.UserLogf,
		RunWebClient: false,
		Ephemeral:    false,
		// Tun stays nil: tsnet then uses its userspace netstack with a fake
		// TUN, so no adapter, route or DNS setting on the PC changes (§15).
	}}
}

// start starts the server under t.mu. tsnet.Server.Close must not be called
// before or concurrently with Start, so every path that could start the
// server comes through here first.
func (t *Tsnet) start() error {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.closed {
		return net.ErrClosed
	}
	t.started = true
	err := t.srv.Start()
	// The auth key has been consumed (or is unusable). Drop our reference;
	// Go strings cannot be zeroed, so this is as far as wiping can go.
	t.srv.AuthKey = ""
	return err
}

// Up starts the node and waits until it is Running, then returns its
// StableNodeID (Status.Self.ID, §7).
func (t *Tsnet) Up(ctx context.Context) (string, error) {
	if err := t.start(); err != nil {
		return "", err
	}
	st, err := t.srv.Up(ctx)
	if err != nil {
		return "", err
	}
	if st.Self == nil || st.Self.ID == "" {
		return "", errors.New("transport: tsnet reported no node id")
	}
	return string(st.Self.ID), nil
}

// Dial connects to a tailnet peer. tsnet routes a destination through the
// tailnet only when a known peer owns it and otherwise silently dials over
// the host network (§9), so two checks come first: the address must be in a
// Tailscale range, and WhoIs must attribute it to a peer.
//
// Those checks cannot close the gap on their own: tsnet decides netstack or
// host network again inside its Dial, from the network map at that moment, so
// a peer dropped in between still gets a host-network connection. The dialed
// connection is therefore checked too, before the caller writes a byte to it.
func (t *Tsnet) Dial(ctx context.Context, addr netip.AddrPort) (net.Conn, error) {
	if netpolicy.RequireTailnet(addr) != nil {
		return nil, ErrDestination
	}
	if err := t.start(); err != nil {
		return nil, err
	}
	// Up returns at once when already Running; before that, WhoIs has no
	// network map to answer from.
	if _, err := t.srv.Up(ctx); err != nil {
		return nil, err
	}
	lc, err := t.srv.LocalClient()
	if err != nil {
		return nil, err
	}
	if _, err := lc.WhoIs(ctx, addr.Addr().String()); err != nil {
		return nil, ErrDestination
	}
	conn, err := t.srv.Dial(ctx, "tcp", addr.String())
	if err != nil {
		return nil, err
	}
	ip4, ip6 := t.srv.TailscaleIPs()
	if err := requireNetstackSource(conn.LocalAddr(), ip4, ip6); err != nil {
		conn.Close()
		return nil, err
	}
	return conn, nil
}

// errHostNetworkDial is returned when tsnet dialed over the host network.
var errHostNetworkDial = fmt.Errorf("%w: the connection did not go through the tailnet", ErrDestination)

// requireNetstackSource accepts a dialed connection only when its local
// address is one of the node's own tailnet addresses (self). tsnet binds every
// netstack dial to exactly those; a host-network dial gets the address of a
// host interface instead. Being inside 100.64.0.0/10 proves nothing: a system
// Tailscale adapter on the same PC has such an address as well.
func requireNetstackSource(local net.Addr, self ...netip.Addr) error {
	if local == nil {
		return errHostNetworkDial
	}
	src, err := netip.ParseAddrPort(local.String())
	if err != nil {
		return errHostNetworkDial
	}
	for _, ip := range self {
		// A node without a network map has no addresses; the zero Addr must
		// never match anything.
		if ip.IsValid() && src.Addr() == ip {
			return nil
		}
	}
	return errHostNetworkDial
}

// Listen listens on the node's tailnet addresses. addr is ":port" (all of
// this node's Tailscale addresses) or "<tailscale ip>:port". tsnet listeners
// never bind a host interface.
func (t *Tsnet) Listen(addr string) (net.Listener, error) {
	if err := CheckListenAddr(netpolicy.Tsnet, addr); err != nil {
		return nil, err
	}
	if err := t.start(); err != nil {
		return nil, err
	}
	return t.srv.Listen("tcp", addr)
}

// PeerNodeID asks the node's own LocalAPI (in memory, no socket) which peer
// owns the connection's source address and returns its StableNodeID.
func (t *Tsnet) PeerNodeID(ctx context.Context, conn net.Conn) (string, error) {
	remote, err := netip.ParseAddrPort(conn.RemoteAddr().String())
	if err != nil || !netpolicy.IsTailnet(remote.Addr()) {
		return "", errors.New("transport: peer is not a tailnet address")
	}
	if err := t.start(); err != nil {
		return "", err
	}
	lc, err := t.srv.LocalClient()
	if err != nil {
		return "", err
	}
	who, err := lc.WhoIs(ctx, remote.String())
	if err != nil {
		return "", err
	}
	if who.Node == nil || who.Node.StableID == "" {
		return "", errors.New("transport: WhoIs returned no node")
	}
	return string(who.Node.StableID), nil
}

// Close stops the node if it was started.
func (t *Tsnet) Close() error {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.closed {
		return nil
	}
	t.closed = true
	if !t.started {
		return nil
	}
	return t.srv.Close()
}

// TsnetNodes is the NodeSet for tsnet mode. Each node has its own state
// directory, <state-dir>\nodes\<node>, because tsnet ignores an auth key
// once state exists: a directory is never reused for a different tailnet.
type TsnetNodes struct {
	root     string
	logf     func(format string, args ...any)
	verbosef func(format string, args ...any)
	// checkDir is winsec.CheckDir. Tests substitute it: a directory owned by
	// another account cannot be created without privileges.
	checkDir func(path string) error

	mu    sync.Mutex
	nodes map[string]*tsnetNode
}

type tsnetNode struct {
	t      *Tsnet
	nodeID string
	state  string
}

type nodeMarker struct {
	NodeID   string `json:"nodeId"`
	Hostname string `json:"hostname"`
}

// NewTsnetNodes returns a node set rooted at stateDir, which must be an
// absolute path. logf receives tsnet's user-facing log lines and verbosef its
// backend log; both must already redact.
func NewTsnetNodes(stateDir string, logf, verbosef func(format string, args ...any)) (*TsnetNodes, error) {
	if !filepath.IsAbs(stateDir) {
		return nil, errors.New("transport: --state-dir must be an absolute path")
	}
	return &TsnetNodes{
		root:     filepath.Clean(stateDir),
		logf:     logf,
		verbosef: verbosef,
		checkDir: winsec.CheckDir,
		nodes:    make(map[string]*tsnetNode),
	}, nil
}

func (n *TsnetNodes) nodeDir(node string) string {
	return filepath.Join(n.root, "nodes", node)
}

// Enroll brings a new node up with a one-time auth key.
func (n *TsnetNodes) Enroll(ctx context.Context, node, authKey, hostname string) (string, error) {
	entry, dir, err := n.beginEnroll(node, authKey, hostname)
	if err != nil {
		return "", err
	}
	t, nodeID, err := n.bringUp(ctx, dir, node, authKey, hostname)
	if err != nil {
		// Still reserved, so no other enrollment of this node can start
		// while the directory is removed.
		n.discardFailedEnroll(node, dir)
	}

	n.mu.Lock()
	defer n.mu.Unlock()
	if err != nil {
		delete(n.nodes, node)
		return "", err
	}
	entry.t, entry.nodeID, entry.state = t, nodeID, "running"
	return nodeID, nil
}

// beginEnroll is everything Enroll does before tsnet exists: it checks the
// arguments, reserves the node and readies its directory. None of it can
// reach the network, so tests exercise this step instead of Enroll itself.
func (n *TsnetNodes) beginEnroll(node, authKey, hostname string) (*tsnetNode, string, error) {
	if err := ValidateNodeName(node); err != nil {
		return nil, "", err
	}
	if err := ValidateHostname(hostname); err != nil {
		return nil, "", err
	}
	if err := ValidateAuthKey(authKey); err != nil {
		return nil, "", err
	}
	n.mu.Lock()
	defer n.mu.Unlock()
	if _, busy := n.nodes[node]; busy {
		return nil, "", ErrAlreadyEnrolled
	}
	dir := n.nodeDir(node)
	if err := n.prepareFirstEnroll(dir); err != nil {
		return nil, "", err
	}
	entry := &tsnetNode{state: "enrolling"}
	n.nodes[node] = entry
	return entry, dir, nil
}

// errNodeDirInUse is returned by Enroll when the directory of a node that was
// never enrolled could not be emptied, for example because a file in it is
// still open.
var errNodeDirInUse = errors.New("transport: the node directory could not be emptied before the node's first enrollment")

// prepareFirstEnroll readies the directory of a node about to be enrolled.
//
// The directory checks come before the marker is looked at: a marker reached
// through a junction, or in a directory another account owns, says nothing
// about this node.
//
// Without a marker nothing in the directory belongs to an enrolled node, so
// all of it goes. It is what tsnet wrote before an attempt was killed (tsnet
// writes tailscaled.state and its log files as soon as it starts, and the
// node name is the same on every attempt), or something planted: tsnet would
// adopt a planted tailscaled.state, and that file keeps its own DACL through
// tsnet's ReplaceFileW writes, handing the node keys to whoever planted it.
func (n *TsnetNodes) prepareFirstEnroll(dir string) error {
	if err := n.prepareDirs(dir); err != nil {
		return err
	}
	if markerExists(dir) {
		return ErrAlreadyEnrolled
	}
	return emptyDir(dir)
}

// emptyDir removes every entry of dir, which has passed the reparse and owner
// checks, and fails unless dir is empty afterwards. os.RemoveAll removes a
// junction or symbolic link itself, never what it points at.
func emptyDir(dir string) error {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return err
	}
	var removeErr error
	for _, e := range entries {
		if err := os.RemoveAll(filepath.Join(dir, e.Name())); err != nil && removeErr == nil {
			removeErr = err
		}
	}
	left, err := os.ReadDir(dir)
	if err != nil {
		return err
	}
	if len(left) == 0 {
		return nil
	}
	err = fmt.Errorf("%w: %s still holds %q", errNodeDirInUse, dir, left[0].Name())
	if removeErr != nil {
		err = fmt.Errorf("%w: %w", err, removeErr)
	}
	return err
}

// discardFailedEnroll removes what a failed attempt left in the node
// directory, so the keys tsnet generated for a node that never came up do not
// stay on disk until the next attempt empties the directory, or for good if
// there is none. The directory was empty and protected when the attempt
// began, so everything in it is tsnet's.
func (n *TsnetNodes) discardFailedEnroll(node, dir string) {
	if err := os.RemoveAll(dir); err != nil {
		n.logf("tsnet: could not remove what the failed enrollment of node %s left behind: %v", node, err)
	}
}

// bringUp starts a node with the auth key and records it as enrolled.
func (n *TsnetNodes) bringUp(ctx context.Context, dir, node, authKey, hostname string) (*Tsnet, string, error) {
	t := newTsnet(tsnetConfig{Dir: dir, Hostname: hostname, AuthKey: authKey, Logf: n.verbosef, UserLogf: n.logf})
	upCtx, cancel := context.WithTimeout(ctx, enrollTimeout)
	defer cancel()
	nodeID, err := t.Up(upCtx)
	if err != nil {
		t.Close()
		n.logf("tsnet: node %s did not come up: %v", node, err)
		return nil, "", ErrEnrollFailed
	}
	marker, err := json.Marshal(nodeMarker{NodeID: nodeID, Hostname: hostname})
	if err == nil {
		err = os.WriteFile(filepath.Join(dir, markerFile), marker, 0o600)
	}
	if err != nil {
		t.Close()
		return nil, "", err
	}
	return t, nodeID, nil
}

// prepareDirs creates the state root if needed and makes sure the nodes
// directory and the node directory carry the protected DACL. An existing
// root keeps its ACL, since it may be a directory the caller chose, but like
// every level below it, it must be a plain directory owned by this account
// (for a SYSTEM service: SYSTEM or Administrators). Whoever owns a level can
// rewrite its DACL, or have planted files in it before we protected it.
func (n *TsnetNodes) prepareDirs(dir string) error {
	_, err := os.Lstat(n.root)
	switch {
	case errors.Is(err, fs.ErrNotExist):
		err = winsec.EnsureProtectedDir(n.root)
	case err == nil:
		err = n.checkDir(n.root)
	}
	if err != nil {
		return err
	}
	if err := winsec.EnsureProtectedDir(filepath.Dir(dir)); err != nil {
		return err
	}
	return winsec.EnsureProtectedDir(dir)
}

// Get returns the running node, starting it from its saved state if needed.
// No auth key is involved: an enrolled node logs in from its state.
func (n *TsnetNodes) Get(ctx context.Context, node string) (Transport, error) {
	if err := ValidateNodeName(node); err != nil {
		return nil, err
	}
	n.mu.Lock()
	defer n.mu.Unlock()
	if e, ok := n.nodes[node]; ok {
		if e.t == nil {
			return nil, ErrNotEnrolled
		}
		return e.t, nil
	}
	dir := n.nodeDir(node)
	m, err := readMarker(dir)
	if err != nil {
		return nil, ErrNotEnrolled
	}
	if err := n.prepareDirs(dir); err != nil {
		return nil, err
	}
	t := newTsnet(tsnetConfig{Dir: dir, Hostname: m.Hostname, Logf: n.verbosef, UserLogf: n.logf})
	if err := t.start(); err != nil {
		t.Close()
		return nil, err
	}
	n.nodes[node] = &tsnetNode{t: t, nodeID: m.NodeID, state: "running"}
	return t, nil
}

// List reports enrolled nodes on disk and their in-process state.
func (n *TsnetNodes) List() []NodeInfo {
	n.mu.Lock()
	defer n.mu.Unlock()
	seen := make(map[string]bool)
	var out []NodeInfo
	for node, e := range n.nodes {
		seen[node] = true
		out = append(out, NodeInfo{Node: node, NodeID: e.nodeID, State: e.state})
	}
	out = append(out, n.enrolledOnDisk(seen)...)
	sortNodes(out)
	return out
}

// enrolledOnDisk reads the markers of the nodes not in seen. A marker is only
// as trustworthy as the directories it is reached through: behind a junction,
// or in a directory another account owns, it could name any node id, and the
// app binds the node id List reports. Such directories are skipped, and the
// log says so, rather than reported.
func (n *TsnetNodes) enrolledOnDisk(seen map[string]bool) []NodeInfo {
	nodesDir := filepath.Join(n.root, "nodes")
	for _, d := range []string{n.root, nodesDir} {
		if err := n.checkDir(d); err != nil {
			// Missing only means nothing was ever enrolled.
			if !errors.Is(err, fs.ErrNotExist) {
				n.logf("tsnet: not listing enrolled nodes: %v", err)
			}
			return nil
		}
	}
	entries, _ := os.ReadDir(nodesDir)
	var out []NodeInfo
	for _, de := range entries {
		node := de.Name()
		if seen[node] || ValidateNodeName(node) != nil {
			continue
		}
		dir := n.nodeDir(node)
		if err := n.checkDir(dir); err != nil {
			n.logf("tsnet: not listing node %s: %v", node, err)
			continue
		}
		if m, err := readMarker(dir); err == nil {
			out = append(out, NodeInfo{Node: node, NodeID: m.NodeID, State: "enrolled"})
		}
	}
	return out
}

// Close stops every running node.
func (n *TsnetNodes) Close() error {
	n.mu.Lock()
	defer n.mu.Unlock()
	var errs []error
	for node, e := range n.nodes {
		if e.t != nil {
			errs = append(errs, e.t.Close())
		}
		delete(n.nodes, node)
	}
	return errors.Join(errs...)
}

func markerExists(dir string) bool {
	_, err := os.Stat(filepath.Join(dir, markerFile))
	return err == nil
}

func readMarker(dir string) (nodeMarker, error) {
	var m nodeMarker
	data, err := os.ReadFile(filepath.Join(dir, markerFile))
	if err != nil {
		return m, err
	}
	if err := json.Unmarshal(data, &m); err != nil {
		return m, err
	}
	if ValidateHostname(m.Hostname) != nil || m.NodeID == "" {
		return m, errors.New("transport: invalid node marker")
	}
	return m, nil
}
