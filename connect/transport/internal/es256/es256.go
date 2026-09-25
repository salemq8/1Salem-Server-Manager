// Package es256 implements the one signature scheme 1Salem Connect uses:
// ECDSA P-256 with SHA-256, signatures in IEEE P1363 form (r||s, 64 bytes),
// public keys as base64url SubjectPublicKeyInfo DER and private keys as
// base64url PKCS#8 DER.
//
// P1363 is used because Web Crypto (the broker) and .NET produce it natively.
// ASN.1/DER signatures are deliberately never accepted: allowing two encodings
// of one signature invites malleability and verifier disagreements.
package es256

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"errors"
	"math/big"

	"1salem.app/connect/transport/internal/b64"
)

// SignatureLen is the exact P1363 length of an ES256 signature.
const SignatureLen = 64

const scalarLen = SignatureLen / 2

// maxKeyText bounds the base64url key text we are willing to decode. A P-256
// SPKI is 91 bytes and a PKCS#8 key well under 200, so this is generous.
const maxKeyText = 1024

var (
	// ErrKey is returned for any key that is not a valid P-256 key.
	ErrKey = errors.New("es256: not a valid P-256 key")
	// ErrSignature is returned when a signature is malformed or does not verify.
	ErrSignature = errors.New("es256: invalid signature")
)

// ParsePublicKey decodes a base64url SPKI DER public key and requires P-256.
func ParsePublicKey(spki string) (*ecdsa.PublicKey, error) {
	if len(spki) > maxKeyText {
		return nil, ErrKey
	}
	der, err := b64.Decode(spki)
	if err != nil {
		return nil, ErrKey
	}
	parsed, err := x509.ParsePKIXPublicKey(der)
	if err != nil {
		return nil, ErrKey
	}
	pub, ok := parsed.(*ecdsa.PublicKey)
	if !ok || pub.Curve != elliptic.P256() {
		return nil, ErrKey
	}
	return pub, nil
}

// EncodePublicKey returns the base64url SPKI DER form of pub.
func EncodePublicKey(pub *ecdsa.PublicKey) (string, error) {
	der, err := x509.MarshalPKIXPublicKey(pub)
	if err != nil {
		return "", err
	}
	return b64.Encode(der), nil
}

// ParsePrivateKey decodes a base64url PKCS#8 DER private key and requires
// P-256. The decoded DER is zeroed before returning; the parsed key itself
// necessarily stays in memory for as long as the caller holds it.
func ParsePrivateKey(pkcs8 string) (*ecdsa.PrivateKey, error) {
	if len(pkcs8) > maxKeyText {
		return nil, ErrKey
	}
	der, err := b64.Decode(pkcs8)
	if err != nil {
		return nil, ErrKey
	}
	defer clear(der)
	parsed, err := x509.ParsePKCS8PrivateKey(der)
	if err != nil {
		return nil, ErrKey
	}
	priv, ok := parsed.(*ecdsa.PrivateKey)
	if !ok || priv.Curve != elliptic.P256() {
		return nil, ErrKey
	}
	return priv, nil
}

// Sign hashes message with SHA-256 and returns the 64-byte P1363 signature.
func Sign(priv *ecdsa.PrivateKey, message []byte) ([]byte, error) {
	if priv == nil || priv.Curve != elliptic.P256() {
		return nil, ErrKey
	}
	digest := sha256.Sum256(message)
	r, s, err := ecdsa.Sign(rand.Reader, priv, digest[:])
	if err != nil {
		return nil, err
	}
	sig := make([]byte, SignatureLen)
	r.FillBytes(sig[:scalarLen])
	s.FillBytes(sig[scalarLen:])
	return sig, nil
}

// Verify checks a P1363 signature over SHA-256(message). Anything other than
// exactly 64 bytes is rejected before any math happens, which is what refuses
// DER-encoded signatures.
func Verify(pub *ecdsa.PublicKey, message, sig []byte) error {
	if pub == nil || pub.Curve != elliptic.P256() {
		return ErrKey
	}
	if len(sig) != SignatureLen {
		return ErrSignature
	}
	r := new(big.Int).SetBytes(sig[:scalarLen])
	s := new(big.Int).SetBytes(sig[scalarLen:])
	digest := sha256.Sum256(message)
	if !ecdsa.Verify(pub, digest[:], r, s) {
		return ErrSignature
	}
	return nil
}
