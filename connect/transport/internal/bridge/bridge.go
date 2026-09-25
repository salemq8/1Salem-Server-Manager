// Package bridge is the host side of the data plane (CONNECT_ARCHITECTURE.md
// §10, §11). It accepts friend connections on the host bridge address, reads
// the preamble, asks the Agent, and on "allow" splices the connection to the
// loopback endpoint the Agent named. It never decides what a friend may reach:
// the only destination it dials is the Agent's answer, and only when that
// answer is a loopback ip:port.
package bridge

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"regexp"
	"sync"
	"sync/atomic"
	"time"

	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/preamble"
	"1salem.app/connect/transport/internal/splice"
	"1salem.app/connect/transport/internal/transport"
)

const (
	// DefaultPreambleTimeout is the §10 deadline for receiving the preamble.
	DefaultPreambleTimeout = 10 * time.Second
	// peerLookupTimeout bounds WhoIs; it answers from memory.
	peerLookupTimeout = 5 * time.Second
	// authorizeTimeout bounds the Agent's decision. Together with the
	// preamble deadline it stays inside the friend's 20 s status wait.
	authorizeTimeout = 8 * time.Second
	// dialTimeout bounds reaching the local game server.
	dialTimeout = 5 * time.Second
	// statusWriteTimeout bounds writing the one status byte.
	statusWriteTimeout = 2 * time.Second
	// reportTimeout bounds a "closed" report to the Agent.
	reportTimeout = 5 * time.Second
	// defaultMaxConns bounds connections in any phase, from all peers
	// together, so the host's resources stay bounded.
	defaultMaxConns = 256
	// defaultMaxPendingPerSource bounds one source's connections between
	// accept and the Agent's answer. A friend's server list pings all of an
	// owner's servers at once, and all of them go through the one node the
	// friend has for that owner, so a real burst can be this deep within one
	// Agent decision; far more means a peer holding slots it will not use. It
	// is checked before a global slot is taken, so one peer cannot lock every
	// friend out.
	defaultMaxPendingPerSource = 16
	// defaultMaxLivePerSource bounds one source's allowed connections.
	defaultMaxLivePerSource = 32
)

// connIDPattern keeps connection ids printable and bounded; they are map keys
// and appear in logs and in "closed" reports.
var connIDPattern = regexp.MustCompile(`^[A-Za-z0-9._:-]{1,128}$`)

// ValidConnID reports whether id is a connection id the bridge accepts from
// the Agent.
func ValidConnID(id string) bool {
	return connIDPattern.MatchString(id)
}

// Peer identifies the connecting node. NodeID is what the transport reports
// (WhoIs in tsnet mode); Addr is for the Agent's logs only.
type Peer struct {
	NodeID string `json:"nodeId"`
	Addr   string `json:"addr"`
}

// AuthorizeRequest is the body of the Agent's "authorize" operation. There is
// deliberately no destination in it: the Agent maps the ticket's ServerId to
// an endpoint itself.
type AuthorizeRequest struct {
	Preamble preamble.Preamble `json:"preamble"`
	Peer     Peer              `json:"peer"`
}

// Decision is the Agent's answer. Endpoint is passed through exactly as
// received; the bridge validates it before use.
type Decision struct {
	Allow    bool
	Endpoint string
	ConnID   string
}

// ClosedReport is the body of the Agent's "closed" operation. BytesIn counts
// bytes from the friend to the game server, BytesOut the reverse.
type ClosedReport struct {
	ConnID   string `json:"connId"`
	BytesIn  int64  `json:"bytesIn"`
	BytesOut int64  `json:"bytesOut"`
}

// Authorizer is the Agent, seen from the bridge.
type Authorizer interface {
	Authorize(ctx context.Context, req AuthorizeRequest) (Decision, error)
	Closed(ctx context.Context, rep ClosedReport) error
}

// Config configures a Bridge.
type Config struct {
	Authorizer Authorizer
	Logf       func(format string, args ...any)
	// DialLocal dials an allowed endpoint; nil means a plain TCP dial. It is
	// only ever called with an address that passed the loopback check.
	DialLocal func(ctx context.Context, addr netip.AddrPort) (net.Conn, error)
	// PreambleTimeout overrides DefaultPreambleTimeout (tests only).
	PreambleTimeout time.Duration
	// MaxConns overrides defaultMaxConns.
	MaxConns int
	// MaxPendingPerSource overrides defaultMaxPendingPerSource.
	MaxPendingPerSource int
	// MaxLivePerSource overrides defaultMaxLivePerSource.
	MaxLivePerSource int
}

// Status is a snapshot for the "status" and "diag" operations.
type Status struct {
	Listen     string `json:"listen,omitempty"`
	Revocation bool   `json:"revocationChannel"`
	Live       int    `json:"live"`
	Accepted   uint64 `json:"accepted"`
	Allowed    uint64 `json:"allowed"`
	Refused    uint64 `json:"refused"`
}

// Bridge carries authorized friend connections to local game servers.
type Bridge struct {
	cfg        Config
	sem        chan struct{}
	sources    *sourceLimits
	refusalLog *logLimiter

	accepted, allowed, refused atomic.Uint64

	mu     sync.Mutex
	listen string
	// revocable is true while the Agent's close-event stream is up. Without
	// it the Agent could not end a connection, so none may start and the
	// live ones are ended when it drops.
	revocable bool
	live      map[string]*liveConn
}

var (
	errDuplicateConn = errors.New("bridge: the Agent reused a live connection id")
	errNoRevocation  = errors.New("bridge: no revocation channel to the Agent")
	errEndpoint      = errors.New("bridge: endpoint is not a loopback ip:port")
)

// New returns a Bridge. It refuses every connection until
// SetRevocationChannel(true) is called.
func New(cfg Config) *Bridge {
	if cfg.PreambleTimeout <= 0 {
		cfg.PreambleTimeout = DefaultPreambleTimeout
	}
	if cfg.MaxConns <= 0 {
		cfg.MaxConns = defaultMaxConns
	}
	if cfg.MaxPendingPerSource <= 0 {
		cfg.MaxPendingPerSource = defaultMaxPendingPerSource
	}
	if cfg.MaxLivePerSource <= 0 {
		cfg.MaxLivePerSource = defaultMaxLivePerSource
	}
	if cfg.DialLocal == nil {
		cfg.DialLocal = func(ctx context.Context, addr netip.AddrPort) (net.Conn, error) {
			var d net.Dialer
			return d.DialContext(ctx, "tcp", addr.String())
		}
	}
	return &Bridge{
		cfg:        cfg,
		sem:        make(chan struct{}, cfg.MaxConns),
		sources:    newSourceLimits(cfg.MaxPendingPerSource, cfg.MaxLivePerSource),
		refusalLog: newLogLimiter(refusalLogBurst, refusalLogWindow),
		live:       make(map[string]*liveConn),
	}
}

func (b *Bridge) logf(format string, args ...any) {
	if b.cfg.Logf != nil {
		b.cfg.Logf(format, args...)
	}
}

// logRefusal logs why a connection was refused, within refusalLogBurst lines
// per refusalLogWindow. The refused counter still counts every refusal.
func (b *Bridge) logRefusal(format string, args ...any) {
	ok, suppressed := b.refusalLog.allow()
	if !ok {
		return
	}
	if suppressed > 0 {
		b.logf("bridge: %d more refusal(s) were not logged", suppressed)
	}
	b.logf(format, args...)
}

// Serve listens on addr through tr and handles connections until ctx is done
// or the listener fails. When ctx is done every live connection is closed.
func (b *Bridge) Serve(ctx context.Context, tr transport.Transport, addr string) error {
	ln, err := tr.Listen(addr)
	if err != nil {
		return err
	}
	b.mu.Lock()
	b.listen = ln.Addr().String()
	b.mu.Unlock()
	b.logf("bridge: listening on %s", ln.Addr())
	stop := context.AfterFunc(ctx, func() {
		ln.Close()
		b.closeAll()
	})
	defer stop()

	var wg sync.WaitGroup
	defer wg.Wait()
	for {
		conn, err := ln.Accept()
		if err != nil {
			if ctx.Err() != nil {
				return ctx.Err()
			}
			if errors.Is(err, net.ErrClosed) {
				return err
			}
			b.logf("bridge: accept failed: %v", err)
			time.Sleep(50 * time.Millisecond)
			continue
		}
		b.accepted.Add(1)
		adm, err := b.sources.admit(conn.RemoteAddr())
		if err != nil {
			b.refused.Add(1)
			b.logRefusal("bridge: refusing %s: %v", conn.RemoteAddr(), err)
			conn.Close()
			continue
		}
		select {
		case b.sem <- struct{}{}:
		default:
			adm.release()
			b.refused.Add(1)
			b.logRefusal("bridge: refusing %s, %d connections already open", conn.RemoteAddr(), b.cfg.MaxConns)
			conn.Close()
			continue
		}
		wg.Add(1)
		go func() {
			defer wg.Done()
			defer func() { <-b.sem }()
			defer adm.release()
			b.handle(ctx, tr, conn, adm)
		}()
	}
}

// SetRevocationChannel records whether the Agent's close-event stream is up.
// Going down ends every live connection at once and refuses new ones until it
// is back: a connection the Agent cannot end must not exist.
func (b *Bridge) SetRevocationChannel(up bool) {
	b.mu.Lock()
	b.revocable = up
	b.mu.Unlock()
	if !up {
		if n := b.closeAll(); n > 0 {
			b.logf("bridge: revocation channel lost; closed %d live connection(s)", n)
		}
	}
}

// CloseConns ends the live connections with the given ids (the Agent's close
// event) and returns how many it found.
func (b *Bridge) CloseConns(connIDs []string) int {
	var found []*liveConn
	b.mu.Lock()
	for _, id := range connIDs {
		if lc, ok := b.live[id]; ok {
			found = append(found, lc)
		}
	}
	b.mu.Unlock()
	for _, lc := range found {
		lc.kill()
	}
	b.logf("bridge: Agent closed %d of %d listed connection(s)", len(found), len(connIDs))
	return len(found)
}

// Status returns a snapshot.
func (b *Bridge) Status() Status {
	b.mu.Lock()
	defer b.mu.Unlock()
	return Status{
		Listen:     b.listen,
		Revocation: b.revocable,
		Live:       len(b.live),
		Accepted:   b.accepted.Load(),
		Allowed:    b.allowed.Load(),
		Refused:    b.refused.Load(),
	}
}

func (b *Bridge) closeAll() int {
	b.mu.Lock()
	all := make([]*liveConn, 0, len(b.live))
	for _, lc := range b.live {
		all = append(all, lc)
	}
	b.mu.Unlock()
	for _, lc := range all {
		lc.kill()
	}
	return len(all)
}

func (b *Bridge) isRevocable() bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.revocable
}

// handle runs one friend connection from preamble to close. adm is its share
// of its source's budget, pending until the connection is registered live.
func (b *Bridge) handle(ctx context.Context, tr transport.Transport, friend net.Conn, adm *admission) {
	defer friend.Close()
	peerAddr := friend.RemoteAddr().String()

	// The preamble is small and bounded; a peer that cannot deliver it
	// within the deadline is holding a slot for nothing.
	friend.SetDeadline(time.Now().Add(b.cfg.PreambleTimeout))
	p, err := preamble.Read(friend)
	if err != nil {
		b.refuse(friend, "no valid preamble from %s: %v", peerAddr, err)
		return
	}
	friend.SetDeadline(time.Time{})

	idCtx, cancel := context.WithTimeout(ctx, peerLookupTimeout)
	nodeID, err := tr.PeerNodeID(idCtx, friend)
	cancel()
	if err != nil {
		b.refuse(friend, "could not identify the node behind %s: %v", peerAddr, err)
		return
	}
	if !b.isRevocable() {
		b.refuse(friend, "refused node %s: %v", nodeID, errNoRevocation)
		return
	}

	authCtx, cancel := context.WithTimeout(ctx, authorizeTimeout)
	d, err := b.cfg.Authorizer.Authorize(authCtx, AuthorizeRequest{
		Preamble: p,
		Peer:     Peer{NodeID: nodeID, Addr: peerAddr},
	})
	cancel()
	if err != nil {
		b.refuse(friend, "no decision for node %s: %v", nodeID, err)
		return
	}
	if !d.Allow {
		b.refuse(friend, "Agent denied node %s", nodeID)
		return
	}
	b.serveAllowed(ctx, friend, d, nodeID, adm)
}

// serveAllowed dials the Agent's endpoint and splices. Every path after the
// connection id is registered reports "closed", so the Agent's view of live
// connections stays exact even when the bridge refuses an allowed one.
func (b *Bridge) serveAllowed(ctx context.Context, friend net.Conn, d Decision, nodeID string, adm *admission) {
	if !ValidConnID(d.ConnID) {
		b.refuse(friend, "Agent allowed node %s without a usable connId", nodeID)
		return
	}
	lc, err := b.register(d.ConnID, friend, adm)
	switch {
	case errors.Is(err, errDuplicateConn):
		// The live connection owns this id and will report it.
		b.refuse(friend, "%v (%s)", err, d.ConnID)
		return
	case err != nil:
		b.refuse(friend, "connection %s refused: %v", d.ConnID, err)
		b.report(ClosedReport{ConnID: d.ConnID})
		return
	}
	var in, out int64
	defer func() {
		b.unregister(d.ConnID)
		b.report(ClosedReport{ConnID: d.ConnID, BytesIn: in, BytesOut: out})
	}()

	// §11: re-check the Agent's answer. Only a literal loopback ip:port is
	// dialed; host names, other interfaces and the wildcard are refused.
	ep, perr := netpolicy.ParseAddrPort(d.Endpoint)
	if perr == nil {
		perr = netpolicy.RequireLoopback(ep)
	}
	if perr != nil {
		b.refuse(friend, "connection %s refused: %v", d.ConnID, errEndpoint)
		return
	}

	dctx, cancel := context.WithTimeout(ctx, dialTimeout)
	game, derr := b.cfg.DialLocal(dctx, ep)
	cancel()
	if derr != nil {
		b.refuse(friend, "connection %s: local endpoint %s unreachable: %v", d.ConnID, ep, derr)
		return
	}
	if !lc.attach(game) {
		game.Close()
		b.refused.Add(1)
		b.logf("bridge: connection %s was closed by the Agent before it started", d.ConnID)
		return
	}
	if err := writeStatus(friend, preamble.StatusOK); err != nil {
		game.Close()
		b.refused.Add(1)
		return
	}
	b.allowed.Add(1)
	b.logf("bridge: connection %s from node %s bridged to %s", d.ConnID, nodeID, ep)
	in, out = splice.Pipe(friend, game)
	b.logf("bridge: connection %s ended (%d bytes in, %d bytes out)", d.ConnID, in, out)
}

// register records an allowed connection so a close event can reach it from
// this point on, including while the endpoint is being dialed. From here on
// the connection counts against its source's live cap.
func (b *Bridge) register(id string, friend net.Conn, adm *admission) (*liveConn, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if _, dup := b.live[id]; dup {
		return nil, errDuplicateConn
	}
	if !b.revocable {
		return nil, errNoRevocation
	}
	if err := adm.promote(); err != nil {
		return nil, err
	}
	lc := &liveConn{conns: []net.Conn{friend}}
	b.live[id] = lc
	return lc, nil
}

func (b *Bridge) unregister(id string) {
	b.mu.Lock()
	delete(b.live, id)
	b.mu.Unlock()
}

func (b *Bridge) report(rep ClosedReport) {
	ctx, cancel := context.WithTimeout(context.Background(), reportTimeout)
	defer cancel()
	if err := b.cfg.Authorizer.Closed(ctx, rep); err != nil {
		b.logf("bridge: could not report connection %s closed: %v", rep.ConnID, err)
	}
}

// refuse sends the single generic refusal byte and logs the real reason
// locally. The friend learns nothing about why.
func (b *Bridge) refuse(friend net.Conn, format string, args ...any) {
	b.refused.Add(1)
	b.logRefusal("bridge: "+format, args...)
	writeStatus(friend, preamble.StatusRefused)
	friend.Close()
}

func writeStatus(c net.Conn, status byte) error {
	c.SetWriteDeadline(time.Now().Add(statusWriteTimeout))
	_, err := c.Write([]byte{status})
	c.SetWriteDeadline(time.Time{})
	return err
}

// liveConn is one allowed connection: the friend side and, once dialed, the
// game side.
type liveConn struct {
	mu     sync.Mutex
	conns  []net.Conn
	killed bool
}

// attach adds the game connection unless the connection was already closed.
func (l *liveConn) attach(c net.Conn) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.killed {
		return false
	}
	l.conns = append(l.conns, c)
	return true
}

func (l *liveConn) kill() {
	l.mu.Lock()
	l.killed = true
	conns := l.conns
	l.mu.Unlock()
	for _, c := range conns {
		c.Close()
	}
}
