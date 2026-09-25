package es256_test

import (
	"crypto/ecdsa"
	"crypto/rand"
	"crypto/sha256"
	"errors"
	"testing"

	"1salem.app/connect/transport/internal/es256"
	"1salem.app/connect/transport/internal/testkit"
)

func TestSignIsP1363AndVerifies(t *testing.T) {
	key := testkit.NewKey(t)
	msg := []byte("1SALEM-REQ-V1\nPOST\n/v1/sessions")
	for range 32 {
		sig, err := es256.Sign(key, msg)
		if err != nil {
			t.Fatal(err)
		}
		// Fixed-width r||s even when r or s has leading zero bytes.
		if len(sig) != es256.SignatureLen {
			t.Fatalf("signature is %d bytes", len(sig))
		}
		if err := es256.Verify(&key.PublicKey, msg, sig); err != nil {
			t.Fatalf("Verify: %v", err)
		}
	}
}

func TestVerifyRejectsDERAndWrongLengths(t *testing.T) {
	key := testkit.NewKey(t)
	msg := []byte("message")
	digest := sha256.Sum256(msg)
	der, err := ecdsa.SignASN1(rand.Reader, key, digest[:])
	if err != nil {
		t.Fatal(err)
	}
	if err := es256.Verify(&key.PublicKey, msg, der); !errors.Is(err, es256.ErrSignature) {
		t.Fatalf("DER signature: err = %v, want ErrSignature", err)
	}
	sig, _ := es256.Sign(key, msg)
	for _, s := range [][]byte{nil, sig[:63], append(append([]byte{}, sig...), 0)} {
		if err := es256.Verify(&key.PublicKey, msg, s); !errors.Is(err, es256.ErrSignature) {
			t.Errorf("%d-byte signature: err = %v", len(s), err)
		}
	}
	if err := es256.Verify(&key.PublicKey, []byte("other"), sig); !errors.Is(err, es256.ErrSignature) {
		t.Errorf("other message: err = %v", err)
	}
}

func TestPublicKeyRoundTrip(t *testing.T) {
	key := testkit.NewKey(t)
	spki := testkit.SPKI(t, &key.PublicKey)
	pub, err := es256.ParsePublicKey(spki)
	if err != nil {
		t.Fatal(err)
	}
	if !pub.Equal(&key.PublicKey) {
		t.Fatal("round trip changed the key")
	}
	// Padded or standard-alphabet forms are not the canonical encoding.
	for _, bad := range []string{spki + "=", spki + "\n", "", testkit.PKCS8(t, key)} {
		if _, err := es256.ParsePublicKey(bad); err == nil {
			t.Errorf("%q: accepted", bad)
		}
	}
}
