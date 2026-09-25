package bridge

import (
	"errors"
	"net"
	"net/netip"
	"sync"
)

var (
	errSourcePending = errors.New("bridge: too many connections from this address are awaiting authorization")
	errSourceLive    = errors.New("bridge: too many live connections from this address")
)

// unknownSource is the shared key of every remote address that cannot be
// parsed, so that such an address never earns a budget of its own.
const unknownSource = "?"

// sourceLimits keeps one remote address from holding the whole bridge. In
// tsnet mode the remote address is a peer's tailnet IP, so the budget is per
// peer node; in fake mode every friend shares the loopback address.
//
// A connection counts as pending from accept until the Agent allows it, and
// as live from then until it ends. The two have separate caps: a peer that
// opens connections and never sends a preamble exhausts only its own small
// pending budget, not the slots every other friend needs.
type sourceLimits struct {
	maxPending, maxLive int

	mu  sync.Mutex
	use map[string]*sourceUse
}

type sourceUse struct {
	pending, live int
}

func newSourceLimits(maxPending, maxLive int) *sourceLimits {
	return &sourceLimits{maxPending: maxPending, maxLive: maxLive, use: make(map[string]*sourceUse)}
}

// admission is one accepted connection's share of its source's budget.
type admission struct {
	limits *sourceLimits
	source string
	live   bool // guarded by limits.mu
}

// sourceKey reduces a remote address to its IP; ports are free to choose.
func sourceKey(remote net.Addr) string {
	if remote == nil {
		return unknownSource
	}
	ap, err := netip.ParseAddrPort(remote.String())
	if err != nil {
		return unknownSource
	}
	return ap.Addr().Unmap().WithZone("").String()
}

// admit counts a newly accepted connection as pending for its source. A
// source already at its live cap is refused too: the connection could not
// go live, so the Agent is not asked about it.
func (s *sourceLimits) admit(remote net.Addr) (*admission, error) {
	key := sourceKey(remote)
	s.mu.Lock()
	defer s.mu.Unlock()
	u := s.use[key]
	if u == nil {
		u = &sourceUse{}
	}
	switch {
	case u.pending >= s.maxPending:
		return nil, errSourcePending
	case u.live >= s.maxLive:
		return nil, errSourceLive
	}
	u.pending++
	s.use[key] = u
	return &admission{limits: s, source: key}, nil
}

// promote moves the connection from pending to live once the Agent allowed
// it. The live cap is checked again here, since the source's other pending
// connections may have gone live in the meantime.
func (a *admission) promote() error {
	s := a.limits
	s.mu.Lock()
	defer s.mu.Unlock()
	u := s.use[a.source]
	if u.live >= s.maxLive {
		return errSourceLive
	}
	u.pending--
	u.live++
	a.live = true
	return nil
}

// release returns the connection's share when it ends, pending or live.
func (a *admission) release() {
	s := a.limits
	s.mu.Lock()
	defer s.mu.Unlock()
	u := s.use[a.source]
	if a.live {
		u.live--
	} else {
		u.pending--
	}
	if u.pending == 0 && u.live == 0 {
		delete(s.use, a.source)
	}
}
