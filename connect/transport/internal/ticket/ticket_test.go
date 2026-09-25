package ticket_test

import (
	"errors"
	"strings"
	"testing"
	"time"

	"1salem.app/connect/transport/internal/b64"
	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/testkit"
	"1salem.app/connect/transport/internal/ticket"
)

var now = time.Unix(1_790_000_000, 0)

type fixture struct {
	signer   *testkit.Signer
	verifier *ticket.Verifier
}

func newFixture(t *testing.T, mode netpolicy.Mode) *fixture {
	t.Helper()
	s := testkit.NewSigner(t, "k1")
	ks, err := ticket.ParseKeyset(testkit.KeysetJSON(t, s))
	if err != nil {
		t.Fatal(err)
	}
	return &fixture{signer: s, verifier: &ticket.Verifier{Keys: ks, Mode: mode, Now: func() time.Time { return now }}}
}

func (f *fixture) claims(t *testing.T, hb string) map[string]any {
	return testkit.Claims(t, now, hb, &testkit.NewKey(t).PublicKey)
}

func TestVerifyGood(t *testing.T) {
	f := newFixture(t, netpolicy.Tsnet)
	c := f.claims(t, "100.101.102.103:7780")
	raw := f.signer.Mint(t, f.signer.Header(), c)
	tk, err := f.verifier.Verify(raw)
	if err != nil {
		t.Fatalf("Verify: %v", err)
	}
	if tk.Raw != raw || tk.Kid != "k1" || tk.JTI != c["jti"] || tk.ServerID != testkit.ServerID ||
		tk.HostBridge.String() != "100.101.102.103:7780" || tk.NodeID != "nFAKE0001CNTRL" {
		t.Fatalf("unexpected ticket %+v", tk)
	}
	if !tk.ExpiresAt.Equal(now.Add(600 * time.Second)) {
		t.Fatalf("exp = %v", tk.ExpiresAt)
	}
}

func TestVerifyTampered(t *testing.T) {
	f := newFixture(t, netpolicy.Tsnet)
	raw := f.signer.Mint(t, f.signer.Header(), f.claims(t, "100.101.102.103:7780"))
	parts := strings.Split(raw, ".")

	// Re-encode the payload with a different hb but keep the old signature.
	other := f.claims(t, "100.64.0.9:7780")
	forged := testkit.SigningInput(t, f.signer.Header(), other)
	forgedPayload := strings.Split(forged, ".")[1]
	cases := map[string]string{
		"payload":   parts[0] + "." + forgedPayload + "." + parts[2],
		"signature": parts[0] + "." + parts[1] + "." + flipLast(parts[2]),
		"header":    b64.Encode([]byte(`{"alg":"ES256","typ":"1salem-ticket+jwt","kid":"k1" }`)) + "." + parts[1] + "." + parts[2],
	}
	for name, tampered := range cases {
		if _, err := f.verifier.Verify(tampered); err == nil {
			t.Errorf("%s: tampered ticket verified", name)
		}
	}
}

// flipLast changes the last base64url character while keeping the encoding
// canonical (the last char of a 64-byte value carries 4 significant bits).
func flipLast(s string) string {
	last := s[len(s)-1]
	repl := byte('A')
	if last == 'A' {
		repl = 'Q'
	}
	return s[:len(s)-1] + string(repl)
}

func TestVerifyWrongKid(t *testing.T) {
	f := newFixture(t, netpolicy.Tsnet)
	h := f.signer.Header()
	h["kid"] = "k2"
	raw := f.signer.Mint(t, h, f.claims(t, "100.101.102.103:7780"))
	if _, err := f.verifier.Verify(raw); !errors.Is(err, ticket.ErrUnknownKey) {
		t.Fatalf("err = %v, want ErrUnknownKey", err)
	}

	// A key that is pinned, but not the one that signed.
	other := testkit.NewSigner(t, "k1")
	raw = other.Mint(t, other.Header(), f.claims(t, "100.101.102.103:7780"))
	if _, err := f.verifier.Verify(raw); !errors.Is(err, ticket.ErrSignature) {
		t.Fatalf("err = %v, want ErrSignature", err)
	}
}

func TestVerifyTime(t *testing.T) {
	f := newFixture(t, netpolicy.Tsnet)
	cases := []struct {
		name  string
		shift time.Duration
		want  error
	}{
		{"expired", -700 * time.Second, ticket.ErrExpired},
		{"expired within skew is still valid", -620 * time.Second, nil},
		{"not yet valid", 120 * time.Second, ticket.ErrNotYetValid},
	}
	for _, tc := range cases {
		c := testkit.Claims(t, now.Add(tc.shift), "100.101.102.103:7780", &testkit.NewKey(t).PublicKey)
		_, err := f.verifier.Verify(f.signer.Mint(t, f.signer.Header(), c))
		if !errors.Is(err, tc.want) && !(tc.want == nil && err == nil) {
			t.Errorf("%s: err = %v, want %v", tc.name, err, tc.want)
		}
	}

	c := f.claims(t, "100.101.102.103:7780")
	c["exp"] = c["iat"].(int64) + 901
	if _, err := f.verifier.Verify(f.signer.Mint(t, f.signer.Header(), c)); !errors.Is(err, ticket.ErrClaims) {
		t.Errorf("lifetime 901 s: err = %v, want ErrClaims", err)
	}
}

func TestVerifySignatureEncoding(t *testing.T) {
	f := newFixture(t, netpolicy.Tsnet)
	h, c := f.signer.Header(), f.claims(t, "100.101.102.103:7780")

	// A correct signature over the same bytes, but DER-encoded.
	if _, err := f.verifier.Verify(f.signer.MintDER(t, h, c)); !errors.Is(err, ticket.ErrSignature) {
		t.Fatalf("DER signature: err = %v, want ErrSignature", err)
	}

	raw := f.signer.Mint(t, h, c)
	input := raw[:strings.LastIndexByte(raw, '.')]
	sig, err := b64.Decode(raw[len(input)+1:])
	if err != nil || len(sig) != 64 {
		t.Fatalf("minted signature is %d bytes, %v", len(sig), err)
	}
	for _, n := range []int{63, 65} {
		bad := make([]byte, n)
		copy(bad, sig)
		if _, err := f.verifier.Verify(input + "." + b64.Encode(bad)); !errors.Is(err, ticket.ErrSignature) {
			t.Errorf("%d-byte signature: err = %v, want ErrSignature", n, err)
		}
	}
}

func TestVerifyHeader(t *testing.T) {
	f := newFixture(t, netpolicy.Tsnet)
	c := f.claims(t, "100.101.102.103:7780")
	cases := map[string]map[string]any{
		"alg none":  {"alg": "none", "typ": "1salem-ticket+jwt", "kid": "k1"},
		"alg HS256": {"alg": "HS256", "typ": "1salem-ticket+jwt", "kid": "k1"},
		"alg ES384": {"alg": "ES384", "typ": "1salem-ticket+jwt", "kid": "k1"},
		"typ JWT":   {"alg": "ES256", "typ": "JWT", "kid": "k1"},
		"extra jku": {"alg": "ES256", "typ": "1salem-ticket+jwt", "kid": "k1", "jku": "https://example.invalid"},
	}
	for name, h := range cases {
		if _, err := f.verifier.Verify(f.signer.Mint(t, h, c)); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}

func TestVerifyDuplicateClaim(t *testing.T) {
	f := newFixture(t, netpolicy.Tsnet)
	c := f.claims(t, "100.101.102.103:7780")
	good := strings.Split(testkit.SigningInput(t, f.signer.Header(), c), ".")
	payload, _ := b64.Decode(good[1])
	// Append a second "hb" pointing somewhere else. encoding/json would take
	// the last one; strictjson must refuse the document.
	dup := strings.TrimSuffix(string(payload), "}") + `,"hb":"100.64.0.1:22"}`
	input := good[0] + "." + b64.Encode([]byte(dup))
	if _, err := f.verifier.Verify(input + "." + f.signer.Sign(t, input)); !errors.Is(err, ticket.ErrMalformed) {
		t.Fatalf("err = %v, want ErrMalformed", err)
	}
}

func TestHostBridgePolicy(t *testing.T) {
	cases := []struct {
		hb    string
		tsnet bool
		fake  bool
	}{
		{"100.64.0.1:7780", true, false},
		{"100.127.255.254:7780", true, false},
		{"[fd7a:115c:a1e0::1]:7780", true, false},
		{"[fd7a:115c:a1e0:ffff::1]:7780", true, false},
		{"127.0.0.1:17780", false, true},
		{"[::1]:17780", false, true},
		{"100.63.255.255:7780", false, false},
		{"100.128.0.1:7780", false, false},
		{"[fd7a:115c:a1e1::1]:7780", false, false},
		{"10.0.0.5:7780", false, false},
		{"192.168.1.10:7780", false, false},
		{"0.0.0.0:7780", false, false},
		{"8.8.8.8:53", false, false},
		{"[::ffff:127.0.0.1]:7780", false, false},
		{"[::ffff:100.64.0.1]:7780", false, false},
		{"[fe80::1%eth0]:7780", false, false},
		{"100.64.0.1:0", false, false},
		{"localhost:7780", false, false},
		{"host.example:7780", false, false},
		{"100.64.0.1", false, false},
	}
	for _, mode := range []netpolicy.Mode{netpolicy.Tsnet, netpolicy.Fake} {
		f := newFixture(t, mode)
		for _, tc := range cases {
			want := tc.tsnet
			if mode == netpolicy.Fake {
				want = tc.fake
			}
			_, err := f.verifier.Verify(f.signer.Mint(t, f.signer.Header(), f.claims(t, tc.hb)))
			if want && err != nil {
				t.Errorf("%s mode, hb %q: rejected: %v", mode, tc.hb, err)
			}
			if !want && !errors.Is(err, ticket.ErrDestination) {
				t.Errorf("%s mode, hb %q: err = %v, want ErrDestination", mode, tc.hb, err)
			}
		}
	}
}

func TestParseKeyset(t *testing.T) {
	s := testkit.NewSigner(t, "k1")
	spki := testkit.SPKI(t, &s.Key.PublicKey)
	bad := map[string]string{
		"empty":         `{"keys":[]}`,
		"wrong alg":     `{"keys":[{"kid":"k1","alg":"RS256","spki":"` + spki + `"}]}`,
		"duplicate kid": `{"keys":[{"kid":"k1","alg":"ES256","spki":"` + spki + `"},{"kid":"k1","alg":"ES256","spki":"` + spki + `"}]}`,
		"bad kid":       `{"keys":[{"kid":"k 1","alg":"ES256","spki":"` + spki + `"}]}`,
		"bad spki":      `{"keys":[{"kid":"k1","alg":"ES256","spki":"AAAA"}]}`,
		"duplicate key": `{"keys":[],"keys":[{"kid":"k1","alg":"ES256","spki":"` + spki + `"}]}`,
	}
	for name, doc := range bad {
		if _, err := ticket.ParseKeyset([]byte(doc)); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	ks, err := ticket.ParseKeyset(testkit.KeysetJSON(t, s, testkit.NewSigner(t, "k0")))
	if err != nil {
		t.Fatal(err)
	}
	if got := ks.Kids(); len(got) != 2 || got[0] != "k0" || got[1] != "k1" {
		t.Fatalf("Kids = %v", got)
	}
}
