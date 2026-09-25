// Package friend runs the friend side of 1Salem Connect: local loopback
// listeners that carry game connections to the host bridge named in a
// verified ticket, and the pipe operations the friend UI uses to drive them.
package friend

import (
	"context"
	"crypto/ecdsa"
	"crypto/rand"
	"errors"
	"io"
	"net"
	"net/netip"
	"slices"
	"strings"
	"sync"
	"time"

	"1salem.app/connect/transport/internal/b64"
	"1salem.app/connect/transport/internal/preamble"
	"1salem.app/connect/transport/internal/proof"
	"1salem.app/connect/transport/internal/splice"
	"1salem.app/connect/transport/internal/ticket"
	"1salem.app/connect/transport/internal/transport"
)

// Local endpoint rules (§11 "Friend local endpoint").
const (
	PortMin     = 18211
	PortMax     = 18299
	DefaultPort = PortMin
)

const (
	// dialTimeout covers reaching the host bridge, which in tsnet mode
	// includes waiting for the node to be Running.
	dialTimeout = 20 * time.Second
	// statusTimeout covers sending the preamble and receiving the status
	// byte; the host allows 10 s for the preamble plus the Agent's decision.
	statusTimeout = 20 * time.Second
	// maxConnsPerSession bounds what one local listener can open. Minecraft
	// needs one connection; the limit stops a local process from using the
	// session to flood the host.
	maxConnsPerSession = 64
)

var (
	// ErrTicket means the ticket failed verification. The specific reason is
	// logged locally; the pipe client sees one generic code.
	ErrTicket = errors.New("friend: ticket rejected")
	// ErrSessionKey means the session key is malformed or does not match skp.
	ErrSessionKey = errors.New("friend: session key rejected")
	// ErrPort means preferredPort is outside 18211-18299.
	ErrPort = errors.New("friend: preferred port outside 18211-18299")
	// ErrNoFreePort means every port in 18211-18299 is taken.
	ErrNoFreePort = errors.New("friend: no free port in 18211-18299")
	// ErrNoSession means the session id is unknown.
	ErrNoSession = errors.New("friend: no such session")
	// ErrTicketMismatch means a refresh ticket names another server or host.
	ErrTicketMismatch = errors.New("friend: ticket is for a different server or host bridge")
	// ErrClosed means the manager is shutting down.
	ErrClosed = errors.New("friend: manager closed")

	errRefused  = errors.New("host refused the connection")
	errProtocol = errors.New("host sent an invalid status byte")
)

// NodeSource hands out the transport for an enrolled node.
type NodeSource interface {
	Get(ctx context.Context, node string) (transport.Transport, error)
}

// Config configures a Manager.
type Config struct {
	Verifier *ticket.Verifier
	Nodes    NodeSource
	Logf     func(format string, args ...any)
	// Now returns the current time; nil means time.Now.
	Now func() time.Time
}

// Manager owns the friend's sessions.
type Manager struct {
	cfg Config

	mu       sync.Mutex
	sessions map[string]*session
	closed   bool
}

// SessionInfo is one entry of the "status" response.
type SessionInfo struct {
	SessionID string `json:"sessionId"`
	SID       string `json:"sid"`
	Local     string `json:"local"`
	State     string `json:"state"`
}

// SessionDiag adds technical detail for the "diag" response.
type SessionDiag struct {
	SessionInfo
	Node        string `json:"node"`
	HostBridge  string `json:"hostBridge"`
	Connections int    `json:"connections"`
	ExpiresAt   int64  `json:"expiresAt"`
}

// NewManager returns a Manager.
func NewManager(cfg Config) *Manager {
	return &Manager{cfg: cfg, sessions: make(map[string]*session)}
}

func (m *Manager) now() time.Time {
	if m.cfg.Now != nil {
		return m.cfg.Now()
	}
	return time.Now()
}

func (m *Manager) logf(format string, args ...any) {
	if m.cfg.Logf != nil {
		m.cfg.Logf(format, args...)
	}
}

// Open verifies the ticket and session key, binds a loopback listener and
// starts carrying connections. The only destination it will ever dial is the
// ticket's verified host bridge address; nothing in the request can change it.
func (m *Manager) Open(ctx context.Context, node, rawTicket, sessionKey string, preferredPort int) (SessionInfo, error) {
	if preferredPort == 0 {
		preferredPort = DefaultPort
	}
	if preferredPort < PortMin || preferredPort > PortMax {
		return SessionInfo{}, ErrPort
	}
	tk, key, err := m.credentials(rawTicket, sessionKey)
	if err != nil {
		return SessionInfo{}, err
	}
	tr, err := m.cfg.Nodes.Get(ctx, node)
	if err != nil {
		return SessionInfo{}, err
	}
	ln, err := bindLocal(preferredPort)
	if err != nil {
		return SessionInfo{}, err
	}
	s := &session{
		id:    newSessionID(),
		node:  node,
		sid:   tk.ServerID,
		hb:    tk.HostBridge,
		local: ln.Addr().String(),
		tr:    tr,
		ln:    ln,
		m:     m,
		tk:    tk,
		key:   key,
		conns: make(map[*bridged]struct{}),
	}
	m.mu.Lock()
	if m.closed {
		m.mu.Unlock()
		ln.Close()
		return SessionInfo{}, ErrClosed
	}
	m.sessions[s.id] = s
	m.mu.Unlock()

	go s.acceptLoop()
	m.logf("friend: session %s listening on %s for server %s", s.id, s.local, s.sid)
	return s.info(m.now()), nil
}

// Refresh replaces a session's ticket and session key. The new ticket must
// name the same server and the same host bridge: a refresh may extend a
// session, never redirect it. Established connections are unaffected.
func (m *Manager) Refresh(sessionID, rawTicket, sessionKey string) error {
	s, err := m.get(sessionID)
	if err != nil {
		return err
	}
	tk, key, err := m.credentials(rawTicket, sessionKey)
	if err != nil {
		return err
	}
	if tk.ServerID != s.sid || tk.HostBridge != s.hb {
		return ErrTicketMismatch
	}
	s.mu.Lock()
	s.tk, s.key = tk, key
	s.mu.Unlock()
	m.logf("friend: session %s refreshed", s.id)
	return nil
}

// Close stops a session's listener and ends its live connections.
func (m *Manager) Close(sessionID string) error {
	m.mu.Lock()
	s, ok := m.sessions[sessionID]
	delete(m.sessions, sessionID)
	m.mu.Unlock()
	if !ok {
		return ErrNoSession
	}
	s.close()
	m.logf("friend: session %s closed", s.id)
	return nil
}

// CloseAll ends every session and refuses new ones.
func (m *Manager) CloseAll() {
	m.mu.Lock()
	m.closed = true
	sessions := m.sessions
	m.sessions = make(map[string]*session)
	m.mu.Unlock()
	for _, s := range sessions {
		s.close()
	}
}

// Status lists the sessions, ordered by local address.
func (m *Manager) Status() []SessionInfo {
	now := m.now()
	var out []SessionInfo
	for _, s := range m.snapshot() {
		out = append(out, s.info(now))
	}
	slices.SortFunc(out, func(a, b SessionInfo) int { return strings.Compare(a.Local, b.Local) })
	return out
}

// Diag lists the sessions with technical detail.
func (m *Manager) Diag() []SessionDiag {
	now := m.now()
	var out []SessionDiag
	for _, s := range m.snapshot() {
		s.mu.Lock()
		d := SessionDiag{
			Node:        s.node,
			HostBridge:  s.hb.String(),
			Connections: len(s.conns),
			ExpiresAt:   s.tk.ExpiresAt.Unix(),
		}
		s.mu.Unlock()
		d.SessionInfo = s.info(now)
		out = append(out, d)
	}
	slices.SortFunc(out, func(a, b SessionDiag) int { return strings.Compare(a.Local, b.Local) })
	return out
}

func (m *Manager) snapshot() []*session {
	m.mu.Lock()
	defer m.mu.Unlock()
	out := make([]*session, 0, len(m.sessions))
	for _, s := range m.sessions {
		out = append(out, s)
	}
	return out
}

func (m *Manager) get(id string) (*session, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	s, ok := m.sessions[id]
	if !ok {
		return nil, ErrNoSession
	}
	return s, nil
}

// credentials verifies a ticket and parses the matching session key.
func (m *Manager) credentials(rawTicket, sessionKey string) (*ticket.Ticket, *ecdsa.PrivateKey, error) {
	tk, err := m.cfg.Verifier.Verify(rawTicket)
	if err != nil {
		m.logf("friend: ticket rejected: %v", err)
		return nil, nil, ErrTicket
	}
	key, err := proof.ParseSessionKey(sessionKey)
	if err != nil {
		return nil, nil, ErrSessionKey
	}
	// The host checks every proof against skp; a key that does not match
	// would only produce refused connections later, so refuse it now.
	if !key.PublicKey.Equal(tk.SessionKey) {
		return nil, nil, ErrSessionKey
	}
	return tk, key, nil
}

// bindLocal tries preferred, then the following ports up to PortMax, then
// wraps around to PortMin. It binds 127.0.0.1 only, and it skips every port
// any program already listens on, on any address: a port in use is left
// exactly as it was (§11). A program that starts listening between the table
// read and our bind can still coexist on the wildcard address; that window
// is a few microseconds and needs a program that picked a port in our range.
func bindLocal(preferred int) (net.Listener, error) {
	busy, err := listeningPorts()
	if err != nil {
		return nil, err
	}
	span := PortMax - PortMin + 1
	for i := range span {
		port := PortMin + (preferred-PortMin+i)%span
		if busy[uint16(port)] {
			continue
		}
		if ln, err := listenLoopback(port); err == nil {
			return ln, nil
		}
	}
	return nil, ErrNoFreePort
}

func newSessionID() string {
	var b [12]byte
	rand.Read(b[:])
	return "ses_" + b64.Encode(b[:])
}

type session struct {
	id    string
	node  string
	sid   string
	hb    netip.AddrPort
	local string
	tr    transport.Transport
	ln    net.Listener
	m     *Manager

	mu     sync.Mutex
	tk     *ticket.Ticket
	key    *ecdsa.PrivateKey
	conns  map[*bridged]struct{}
	closed bool
}

// bridged is one local connection and, once dialed, its tailnet peer.
type bridged struct {
	local  net.Conn
	remote net.Conn
}

func (s *session) info(now time.Time) SessionInfo {
	s.mu.Lock()
	expired := s.tk.Expired(now)
	s.mu.Unlock()
	state := "listening"
	if expired {
		// New connections are refused until the UI refreshes the ticket.
		state = "expired"
	}
	return SessionInfo{SessionID: s.id, SID: s.sid, Local: s.local, State: state}
}

func (s *session) acceptLoop() {
	for {
		c, err := s.ln.Accept()
		if err != nil {
			if errors.Is(err, net.ErrClosed) {
				return
			}
			s.m.logf("friend: session %s accept failed: %v", s.id, err)
			time.Sleep(100 * time.Millisecond)
			continue
		}
		go s.handle(c)
	}
}

func (s *session) handle(local net.Conn) {
	b := &bridged{local: local}
	tk, key, ok := s.add(b)
	if !ok {
		local.Close()
		return
	}
	defer s.remove(b)

	now := s.m.now()
	if tk.Expired(now) {
		s.m.logf("friend: session %s ticket expired; connection refused until refresh", s.id)
		local.Close()
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), dialTimeout)
	defer cancel()
	remote, err := s.tr.Dial(ctx, tk.HostBridge)
	if err != nil {
		s.m.logf("friend: session %s could not reach the host bridge: %v", s.id, err)
		local.Close()
		return
	}
	if !s.attach(b, remote) {
		remote.Close()
		local.Close()
		return
	}
	if err := handshake(remote, tk, key, now); err != nil {
		s.m.logf("friend: session %s connection not accepted: %v", s.id, err)
		remote.Close()
		local.Close()
		return
	}
	// The app reads its session state from the newest of these lines, so a successful handshake
	// must be logged too; otherwise an earlier failure stays the newest line for a whole game.
	s.m.logf("friend: session %s connection accepted", s.id)
	up, down := splice.Pipe(local, remote)
	s.m.logf("friend: session %s connection ended (%d bytes up, %d bytes down)", s.id, up, down)
}

// handshake sends the preamble with a fresh proof and reads the status byte.
func handshake(remote net.Conn, tk *ticket.Ticket, key *ecdsa.PrivateKey, now time.Time) error {
	nonce, err := proof.NewNonce()
	if err != nil {
		return err
	}
	ts := now.Unix()
	p, err := proof.Sign(key, tk.JTI, nonce, ts, tk.ServerID)
	if err != nil {
		return err
	}
	remote.SetDeadline(time.Now().Add(statusTimeout))
	if err := preamble.Write(remote, preamble.Preamble{T: tk.Raw, N: nonce, TS: ts, P: p}); err != nil {
		return err
	}
	var status [1]byte
	if _, err := io.ReadFull(remote, status[:]); err != nil {
		return err
	}
	remote.SetDeadline(time.Time{})
	switch status[0] {
	case preamble.StatusOK:
		return nil
	case preamble.StatusRefused:
		return errRefused
	}
	return errProtocol
}

// add registers a connection and returns the credentials it must use. It
// fails once the session is closed or full.
func (s *session) add(b *bridged) (*ticket.Ticket, *ecdsa.PrivateKey, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closed || len(s.conns) >= maxConnsPerSession {
		return nil, nil, false
	}
	s.conns[b] = struct{}{}
	return s.tk, s.key, true
}

// attach records the dialed connection so close can reach it; it fails if
// the session closed while the dial was in flight.
func (s *session) attach(b *bridged, remote net.Conn) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closed {
		return false
	}
	b.remote = remote
	return true
}

func (s *session) remove(b *bridged) {
	s.mu.Lock()
	delete(s.conns, b)
	s.mu.Unlock()
}

func (s *session) close() {
	s.mu.Lock()
	s.closed = true
	conns := make([]*bridged, 0, len(s.conns))
	for b := range s.conns {
		conns = append(conns, b)
	}
	s.mu.Unlock()
	s.ln.Close()
	for _, b := range conns {
		b.local.Close()
		if b.remote != nil {
			b.remote.Close()
		}
	}
}
