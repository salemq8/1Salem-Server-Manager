package proof_test

import (
	"errors"
	"testing"

	"1salem.app/connect/transport/internal/b64"
	"1salem.app/connect/transport/internal/es256"
	"1salem.app/connect/transport/internal/proof"
	"1salem.app/connect/transport/internal/testkit"
)

const (
	jti = "AAECAwQFBgcICQoLDA0ODw"
	sid = testkit.ServerID
	ts  = int64(1_790_000_000)
)

func TestSigningInputIsTheContractString(t *testing.T) {
	got, err := proof.SigningInput(jti, "bm9uY2Vub25jZW5vbmNlMQ", ts, sid)
	if err != nil {
		t.Fatal(err)
	}
	want := "1SALEM-CONN-V1\n" + jti + "\nbm9uY2Vub25jZW5vbmNlMQ\n1790000000\n" + sid
	if string(got) != want {
		t.Fatalf("signing input\n got %q\nwant %q", got, want)
	}
}

func TestSignVerify(t *testing.T) {
	key := testkit.NewKey(t)
	// The session key crosses the pipe as base64url PKCS#8.
	parsed, err := proof.ParseSessionKey(testkit.PKCS8(t, key))
	if err != nil {
		t.Fatal(err)
	}
	nonce, err := proof.NewNonce()
	if err != nil {
		t.Fatal(err)
	}
	if _, err := b64.DecodeLen(nonce, proof.NonceLen); err != nil {
		t.Fatalf("nonce %q is not 16 bytes of base64url", nonce)
	}
	p, err := proof.Sign(parsed, jti, nonce, ts, sid)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := b64.DecodeLen(p, es256.SignatureLen); err != nil {
		t.Fatalf("proof is not a 64-byte P1363 signature: %v", err)
	}
	if err := proof.Verify(&key.PublicKey, jti, nonce, ts, sid, p); err != nil {
		t.Fatalf("Verify: %v", err)
	}

	other, _ := proof.NewNonce()
	mismatches := map[string]func() error{
		"other key":   func() error { return proof.Verify(&testkit.NewKey(t).PublicKey, jti, nonce, ts, sid, p) },
		"other jti":   func() error { return proof.Verify(&key.PublicKey, "AAECAwQFBgcICQoLDA0OEA", nonce, ts, sid, p) },
		"other nonce": func() error { return proof.Verify(&key.PublicKey, jti, other, ts, sid, p) },
		"other ts":    func() error { return proof.Verify(&key.PublicKey, jti, nonce, ts+1, sid, p) },
		"other sid": func() error {
			return proof.Verify(&key.PublicKey, jti, nonce, ts, "1f8fad5b-d9cb-469f-a165-70867728950e", p)
		},
	}
	for name, verify := range mismatches {
		if err := verify(); !errors.Is(err, es256.ErrSignature) {
			t.Errorf("%s: err = %v, want ErrSignature", name, err)
		}
	}
}

func TestSigningInputRefusesAmbiguousFields(t *testing.T) {
	bad := []struct {
		jti, nonce, sid string
		ts              int64
	}{
		{"a\nb", "n", sid, ts},
		{jti, "n\r", sid, ts},
		{jti, "n", sid + "\n", ts},
		{"", "n", sid, ts},
		{jti, "n", sid, 0},
	}
	for _, b := range bad {
		if _, err := proof.SigningInput(b.jti, b.nonce, b.ts, b.sid); !errors.Is(err, proof.ErrInput) {
			t.Errorf("%+v: err = %v, want ErrInput", b, err)
		}
	}
}

func TestParseSessionKeyRejectsOtherCurves(t *testing.T) {
	for _, s := range []string{"", "AAAA", "not base64!", testkit.SPKI(t, &testkit.NewKey(t).PublicKey)} {
		if _, err := proof.ParseSessionKey(s); err == nil {
			t.Errorf("%q: accepted", s)
		}
	}
}
