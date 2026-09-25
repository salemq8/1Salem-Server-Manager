// Package proof produces and checks the per-connection proof of possession
// (CONNECT_ARCHITECTURE.md §10): an ES256 signature by the session private
// key over "1SALEM-CONN-V1\n{jti}\n{n}\n{ts}\n{sid}".
//
// The session private key reaches the transport over the pipe as base64url
// PKCS#8 DER and lives only in this process's memory. A captured ticket alone
// therefore cannot open a connection.
package proof

import (
	"crypto/ecdsa"
	"crypto/rand"
	"errors"
	"strconv"
	"strings"

	"1salem.app/connect/transport/internal/b64"
	"1salem.app/connect/transport/internal/es256"
)

// Context is the domain-separation tag that starts the signing input.
const Context = "1SALEM-CONN-V1"

// NonceLen is the size of the per-connection random nonce.
const NonceLen = 16

// ErrInput is returned when a signing-input field could make the
// newline-separated input ambiguous.
var ErrInput = errors.New("proof: invalid signing input")

// SigningInput builds the exact bytes that are signed. Fields must not contain
// '\n'; callers pass values that were already validated (a base64url jti and
// nonce, a GUID sid), and this function refuses anything else as a backstop.
func SigningInput(jti, nonce string, ts int64, sid string) ([]byte, error) {
	for _, f := range []string{jti, nonce, sid} {
		if f == "" || strings.ContainsAny(f, "\r\n") {
			return nil, ErrInput
		}
	}
	if ts <= 0 {
		return nil, ErrInput
	}
	var b strings.Builder
	b.WriteString(Context)
	b.WriteByte('\n')
	b.WriteString(jti)
	b.WriteByte('\n')
	b.WriteString(nonce)
	b.WriteByte('\n')
	b.WriteString(strconv.FormatInt(ts, 10))
	b.WriteByte('\n')
	b.WriteString(sid)
	return []byte(b.String()), nil
}

// NewNonce returns 16 fresh random bytes as base64url.
func NewNonce() (string, error) {
	var n [NonceLen]byte
	if _, err := rand.Read(n[:]); err != nil {
		return "", err
	}
	return b64.Encode(n[:]), nil
}

// Sign returns the base64url P1363 proof for one connection.
func Sign(key *ecdsa.PrivateKey, jti, nonce string, ts int64, sid string) (string, error) {
	input, err := SigningInput(jti, nonce, ts, sid)
	if err != nil {
		return "", err
	}
	sig, err := es256.Sign(key, input)
	if err != nil {
		return "", err
	}
	return b64.Encode(sig), nil
}

// Verify checks a base64url P1363 proof. The friend transport never needs
// this; it exists so tests (and the fake host used in the proof harness) can
// check exactly what the Agent will check.
func Verify(pub *ecdsa.PublicKey, jti, nonce string, ts int64, sid, proof string) error {
	input, err := SigningInput(jti, nonce, ts, sid)
	if err != nil {
		return err
	}
	sig, err := b64.DecodeLen(proof, es256.SignatureLen)
	if err != nil {
		return es256.ErrSignature
	}
	return es256.Verify(pub, input, sig)
}

// ParseSessionKey decodes the session private key handed over the pipe
// (base64url PKCS#8 DER, P-256 only).
func ParseSessionKey(encoded string) (*ecdsa.PrivateKey, error) {
	return es256.ParsePrivateKey(encoded)
}
