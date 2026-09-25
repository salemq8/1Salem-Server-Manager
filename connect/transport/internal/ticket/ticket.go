// Package ticket verifies broker session tickets on the friend side
// (CONNECT_ARCHITECTURE.md §9).
//
// A ticket is a compact JWS signed with ES256 by a pinned broker key. The
// friend transport checks the signature, typ, iss and the time window, and it
// checks that the host bridge address "hb" is somewhere the transport is
// allowed to dial. The host (the Agent) performs the authoritative checks;
// the friend-side checks exist so that the only address the transport ever
// dials comes from a ticket whose signature it verified itself.
package ticket

import (
	"crypto/ecdsa"
	"errors"
	"fmt"
	"net/netip"
	"regexp"
	"strings"
	"time"

	"1salem.app/connect/transport/internal/b64"
	"1salem.app/connect/transport/internal/es256"
	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/strictjson"
)

// Contract constants (§9).
const (
	Algorithm = "ES256"
	Type      = "1salem-ticket+jwt"
	Issuer    = "1salem-connect-broker"
	ProtoTCP  = "tcp"

	// MaxLifetime is the largest allowed exp - iat.
	MaxLifetime = 900 * time.Second
	// ClockSkew is the tolerance applied to iat, nbf and exp.
	ClockSkew = 30 * time.Second
	// MaxLen bounds the compact form; a ticket must fit in a 4096-byte
	// preamble together with the nonce, time and proof.
	MaxLen = 3584

	jtiBytes = 16
)

var (
	// ErrMalformed covers structural problems: shape, encoding, JSON.
	ErrMalformed = errors.New("ticket: malformed")
	// ErrHeader covers a header with the wrong alg or typ.
	ErrHeader = errors.New("ticket: unacceptable header")
	// ErrUnknownKey is returned when kid names no pinned key.
	ErrUnknownKey = errors.New("ticket: unknown key id")
	// ErrSignature is returned when the signature does not verify.
	ErrSignature = errors.New("ticket: bad signature")
	// ErrClaims covers missing or invalid claims.
	ErrClaims = errors.New("ticket: invalid claims")
	// ErrExpired is returned for a ticket past exp (plus skew).
	ErrExpired = errors.New("ticket: expired")
	// ErrNotYetValid is returned for a ticket before nbf or iat (minus skew).
	ErrNotYetValid = errors.New("ticket: not yet valid")
	// ErrDestination is returned when hb is not a permitted dial target.
	ErrDestination = errors.New("ticket: host bridge address not permitted")
)

var (
	sidPattern   = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)
	tokenPattern = regexp.MustCompile(`^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$`)
)

// Ticket is a verified ticket.
type Ticket struct {
	// Raw is the compact form exactly as received; it is forwarded verbatim
	// in the preamble so the host verifies the same bytes.
	Raw string
	Kid string

	Issuer       string
	Audience     string
	JTI          string
	Subject      string
	MembershipID string
	ServerID     string
	Proto        string
	NodeID       string
	AuthVersion  int64

	// SessionKey is the session public key from "skp".
	SessionKey *ecdsa.PublicKey
	// HostBridge is "hb", already checked against the verifier's mode.
	HostBridge netip.AddrPort

	IssuedAt  time.Time
	NotBefore time.Time
	ExpiresAt time.Time
}

// Expired reports whether the ticket can no longer start connections at now.
func (t *Ticket) Expired(now time.Time) bool {
	return !now.Before(t.ExpiresAt.Add(ClockSkew))
}

// Verifier checks tickets against pinned keys and a destination policy.
type Verifier struct {
	Keys *Keyset
	Mode netpolicy.Mode
	// Now returns the current time; nil means time.Now.
	Now func() time.Time
}

type header struct {
	Alg string `json:"alg"`
	Typ string `json:"typ"`
	Kid string `json:"kid"`
}

type claims struct {
	Iss   string `json:"iss"`
	Aud   string `json:"aud"`
	Jti   string `json:"jti"`
	Sub   string `json:"sub"`
	Mid   string `json:"mid"`
	Sid   string `json:"sid"`
	Proto string `json:"proto"`
	Nid   string `json:"nid"`
	Skp   string `json:"skp"`
	Hb    string `json:"hb"`
	Av    *int64 `json:"av"`
	Iat   *int64 `json:"iat"`
	Nbf   *int64 `json:"nbf"`
	Exp   *int64 `json:"exp"`
}

// Verify parses and verifies a compact JWS ticket.
//
// The order matters: shape, then the header (alg pinned to ES256, exact typ,
// kid pinned), then the signature, and only then is the payload parsed. The
// payload of an unauthenticated token is never interpreted.
func (v *Verifier) Verify(raw string) (*Ticket, error) {
	if v.Keys == nil {
		return nil, errors.New("ticket: verifier has no keys")
	}
	if len(raw) == 0 || len(raw) > MaxLen || !tokenPattern.MatchString(raw) {
		return nil, ErrMalformed
	}
	parts := strings.Split(raw, ".")
	headerB64, payloadB64, sigB64 := parts[0], parts[1], parts[2]

	headerJSON, err := b64.Decode(headerB64)
	if err != nil {
		return nil, ErrMalformed
	}
	var h header
	// The header layout is fixed by the contract. Unknown parameters (crit,
	// jku, jwk, b64, ...) are refused rather than ignored.
	if err := strictjson.Decode(headerJSON, &h); err != nil {
		return nil, ErrMalformed
	}
	if h.Alg != Algorithm || h.Typ != Type {
		return nil, ErrHeader
	}
	pub, ok := v.Keys.lookup(h.Kid)
	if !ok {
		return nil, ErrUnknownKey
	}

	sig, err := b64.Decode(sigB64)
	if err != nil || len(sig) != es256.SignatureLen {
		return nil, ErrSignature
	}
	signingInput := raw[:len(headerB64)+1+len(payloadB64)]
	if err := es256.Verify(pub, []byte(signingInput), sig); err != nil {
		return nil, ErrSignature
	}

	payloadJSON, err := b64.Decode(payloadB64)
	if err != nil {
		return nil, ErrMalformed
	}
	var c claims
	// Additional claims are ignored (RFC 7519 §4), duplicates are not.
	if err := strictjson.DecodeAllowUnknown(payloadJSON, &c); err != nil {
		return nil, ErrMalformed
	}
	t, err := v.checkClaims(c)
	if err != nil {
		return nil, err
	}
	t.Raw = raw
	t.Kid = h.Kid
	return t, nil
}

func (v *Verifier) checkClaims(c claims) (*Ticket, error) {
	if c.Iss != Issuer {
		return nil, fmt.Errorf("%w: iss", ErrClaims)
	}
	for name, val := range map[string]string{"aud": c.Aud, "sub": c.Sub, "mid": c.Mid, "nid": c.Nid} {
		if len(val) == 0 || len(val) > 128 {
			return nil, fmt.Errorf("%w: %s", ErrClaims, name)
		}
	}
	// jti and sid are part of the connection-proof signing input, whose
	// fields are newline separated; strict formats keep that input unambiguous.
	if _, err := b64.DecodeLen(c.Jti, jtiBytes); err != nil {
		return nil, fmt.Errorf("%w: jti", ErrClaims)
	}
	if !sidPattern.MatchString(c.Sid) {
		return nil, fmt.Errorf("%w: sid", ErrClaims)
	}
	if c.Proto != ProtoTCP {
		return nil, fmt.Errorf("%w: proto", ErrClaims)
	}
	if c.Av == nil || c.Iat == nil || c.Nbf == nil || c.Exp == nil {
		return nil, fmt.Errorf("%w: missing numeric claim", ErrClaims)
	}
	skp, err := es256.ParsePublicKey(c.Skp)
	if err != nil {
		return nil, fmt.Errorf("%w: skp", ErrClaims)
	}

	iat, nbf, exp := time.Unix(*c.Iat, 0), time.Unix(*c.Nbf, 0), time.Unix(*c.Exp, 0)
	if !exp.After(iat) || exp.Sub(iat) > MaxLifetime {
		return nil, fmt.Errorf("%w: lifetime", ErrClaims)
	}
	now := time.Now()
	if v.Now != nil {
		now = v.Now()
	}
	if now.Add(ClockSkew).Before(nbf) || now.Add(ClockSkew).Before(iat) {
		return nil, ErrNotYetValid
	}
	if !now.Before(exp.Add(ClockSkew)) {
		return nil, ErrExpired
	}

	hb, err := netpolicy.ParseAddrPort(c.Hb)
	if err != nil {
		return nil, ErrDestination
	}
	if err := v.Mode.CheckDestination(hb); err != nil {
		return nil, ErrDestination
	}

	return &Ticket{
		Issuer:       c.Iss,
		Audience:     c.Aud,
		JTI:          c.Jti,
		Subject:      c.Sub,
		MembershipID: c.Mid,
		ServerID:     c.Sid,
		Proto:        c.Proto,
		NodeID:       c.Nid,
		AuthVersion:  *c.Av,
		SessionKey:   skp,
		HostBridge:   hb,
		IssuedAt:     iat,
		NotBefore:    nbf,
		ExpiresAt:    exp,
	}, nil
}
