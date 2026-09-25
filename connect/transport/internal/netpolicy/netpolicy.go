// Package netpolicy holds the address rules every dial and listen in the
// transport must pass. It is the single place that decides what a "tailnet
// address" and a "loopback address" are.
package netpolicy

import (
	"errors"
	"fmt"
	"net/netip"
)

// Mode selects which data plane the sidecar runs on.
type Mode int

const (
	// Tsnet is the real Tailscale userspace node.
	Tsnet Mode = iota + 1
	// Fake is loopback TCP standing in for the tailnet. Not a security boundary.
	Fake
)

// ParseMode parses the --mode flag value.
func ParseMode(s string) (Mode, error) {
	switch s {
	case "tsnet":
		return Tsnet, nil
	case "fake":
		return Fake, nil
	}
	return 0, fmt.Errorf("mode must be \"fake\" or \"tsnet\", not %q", s)
}

func (m Mode) String() string {
	switch m {
	case Tsnet:
		return "tsnet"
	case Fake:
		return "fake"
	}
	return "invalid"
}

var (
	// ErrMalformed covers anything that is not a literal ip:port.
	ErrMalformed = errors.New("netpolicy: address must be a literal ip:port")
	// ErrNotTailnet is returned for addresses outside the Tailscale ranges.
	ErrNotTailnet = errors.New("netpolicy: address is not a Tailscale address")
	// ErrNotLoopback is returned for addresses that are not loopback.
	ErrNotLoopback = errors.New("netpolicy: address is not a loopback address")
)

// Tailscale assigns node addresses only from these ranges.
var (
	tailnetV4 = netip.MustParsePrefix("100.64.0.0/10")
	tailnetV6 = netip.MustParsePrefix("fd7a:115c:a1e0::/48")
)

// maxAddrLen comfortably fits "[ipv6]:port" and bounds parsing work.
const maxAddrLen = 64

// ParseAddrPort parses a literal "ip:port". Host names are never accepted,
// because resolving one would let DNS decide where bytes go. IPv6 zones,
// IPv4-mapped IPv6 forms and port 0 are refused so each destination has one
// unambiguous meaning.
func ParseAddrPort(s string) (netip.AddrPort, error) {
	if len(s) == 0 || len(s) > maxAddrLen {
		return netip.AddrPort{}, ErrMalformed
	}
	ap, err := netip.ParseAddrPort(s)
	if err != nil {
		return netip.AddrPort{}, ErrMalformed
	}
	if err := checkShape(ap); err != nil {
		return netip.AddrPort{}, err
	}
	return ap, nil
}

func checkShape(ap netip.AddrPort) error {
	a := ap.Addr()
	if !a.IsValid() || a.Zone() != "" || a.Is4In6() || ap.Port() == 0 {
		return ErrMalformed
	}
	return nil
}

// IsTailnet reports whether a is inside a Tailscale node address range.
func IsTailnet(a netip.Addr) bool {
	return a.Zone() == "" && (tailnetV4.Contains(a) || tailnetV6.Contains(a))
}

// RequireTailnet accepts only a Tailscale node address. tsnet's Dial falls
// back to the host network for any other destination, so this check is what
// keeps game traffic, tickets and proofs off the friend's LAN and the Internet.
func RequireTailnet(ap netip.AddrPort) error {
	if err := checkShape(ap); err != nil {
		return err
	}
	if !IsTailnet(ap.Addr()) {
		return ErrNotTailnet
	}
	return nil
}

// RequireLoopback accepts only 127.0.0.0/8 or ::1.
func RequireLoopback(ap netip.AddrPort) error {
	if err := checkShape(ap); err != nil {
		return err
	}
	if !ap.Addr().IsLoopback() {
		return ErrNotLoopback
	}
	return nil
}

// CheckDestination applies the rule for where a sidecar in mode m may dial:
// tailnet addresses in tsnet mode, loopback addresses in fake mode.
func (m Mode) CheckDestination(ap netip.AddrPort) error {
	switch m {
	case Tsnet:
		return RequireTailnet(ap)
	case Fake:
		return RequireLoopback(ap)
	}
	return errors.New("netpolicy: invalid mode")
}
