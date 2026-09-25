// Package testkit mints broker keys, session keys and tickets for tests. It
// plays the broker's part so the transport's verifiers can be exercised
// offline, and creates the reparse points the state-directory tests plant.
// Only _test.go files import it; it is never linked into a binary.
package testkit

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/json"
	"testing"
	"time"

	"1salem.app/connect/transport/internal/b64"
	"1salem.app/connect/transport/internal/es256"
)

// ServerID is a valid lowercase GUID used as the ticket "sid".
const ServerID = "0f8fad5b-d9cb-469f-a165-70867728950e"

// Signer is a broker ticket key.
type Signer struct {
	Kid string
	Key *ecdsa.PrivateKey
}

// NewKey returns a fresh P-256 key.
func NewKey(t testing.TB) *ecdsa.PrivateKey {
	t.Helper()
	k, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return k
}

// NewSigner returns a broker key with the given kid.
func NewSigner(t testing.TB, kid string) *Signer {
	return &Signer{Kid: kid, Key: NewKey(t)}
}

// SPKI returns the base64url SPKI of a public key.
func SPKI(t testing.TB, pub *ecdsa.PublicKey) string {
	t.Helper()
	s, err := es256.EncodePublicKey(pub)
	if err != nil {
		t.Fatal(err)
	}
	return s
}

// PKCS8 returns the base64url PKCS#8 DER of a private key, the form a session
// key takes on the pipe.
func PKCS8(t testing.TB, k *ecdsa.PrivateKey) string {
	t.Helper()
	der, err := x509.MarshalPKCS8PrivateKey(k)
	if err != nil {
		t.Fatal(err)
	}
	return b64.Encode(der)
}

// KeysetJSON returns a /v1/keys document pinning the given signers.
func KeysetJSON(t testing.TB, signers ...*Signer) []byte {
	t.Helper()
	type key struct {
		Kid  string `json:"kid"`
		Alg  string `json:"alg"`
		SPKI string `json:"spki"`
	}
	var doc struct {
		Keys []key `json:"keys"`
	}
	for _, s := range signers {
		doc.Keys = append(doc.Keys, key{Kid: s.Kid, Alg: "ES256", SPKI: SPKI(t, &s.Key.PublicKey)})
	}
	out, err := json.Marshal(doc)
	if err != nil {
		t.Fatal(err)
	}
	return out
}

// Header returns the contract header for this signer.
func (s *Signer) Header() map[string]any {
	return map[string]any{"alg": "ES256", "typ": "1salem-ticket+jwt", "kid": s.Kid}
}

// Claims returns a valid claim set issued at now for host bridge hb and the
// session public key.
func Claims(t testing.TB, now time.Time, hb string, session *ecdsa.PublicKey) map[string]any {
	t.Helper()
	var jti [16]byte
	rand.Read(jti[:])
	iat := now.Unix()
	return map[string]any{
		"iss":   "1salem-connect-broker",
		"aud":   "own_abcdefghijklmnopqrstuvwxyz",
		"jti":   b64.Encode(jti[:]),
		"sub":   "dev_abcdefghijklmnopqrstuvwxyz",
		"mid":   "mem_0123456789",
		"sid":   ServerID,
		"proto": "tcp",
		"nid":   "nFAKE0001CNTRL",
		"skp":   SPKI(t, session),
		"hb":    hb,
		"av":    1,
		"iat":   iat,
		"nbf":   iat,
		"exp":   iat + 600,
	}
}

// Mint returns a compact JWS with a P1363 signature over the given header and
// claims, exactly as the broker produces it.
func (s *Signer) Mint(t testing.TB, header, claims map[string]any) string {
	t.Helper()
	input := SigningInput(t, header, claims)
	return input + "." + s.Sign(t, input)
}

// Sign returns the base64url P1363 signature of an arbitrary signing input,
// for tests that hand-craft a payload.
func (s *Signer) Sign(t testing.TB, input string) string {
	t.Helper()
	sig, err := es256.Sign(s.Key, []byte(input))
	if err != nil {
		t.Fatal(err)
	}
	return b64.Encode(sig)
}

// MintDER is Mint with an ASN.1 DER signature, which verifiers must refuse.
func (s *Signer) MintDER(t testing.TB, header, claims map[string]any) string {
	t.Helper()
	input := SigningInput(t, header, claims)
	digest := sha256.Sum256([]byte(input))
	der, err := ecdsa.SignASN1(rand.Reader, s.Key, digest[:])
	if err != nil {
		t.Fatal(err)
	}
	return input + "." + b64.Encode(der)
}

// SigningInput returns base64url(header) "." base64url(claims).
func SigningInput(t testing.TB, header, claims map[string]any) string {
	t.Helper()
	h, err := json.Marshal(header)
	if err != nil {
		t.Fatal(err)
	}
	c, err := json.Marshal(claims)
	if err != nil {
		t.Fatal(err)
	}
	return b64.Encode(h) + "." + b64.Encode(c)
}
