// Package b64 implements the unpadded base64url encoding used by every
// 1Salem Connect wire format: tickets, SPKI keys, nonces and signatures.
package b64

import (
	"encoding/base64"
	"errors"
)

// ErrInvalid is returned for any string that is not canonical unpadded base64url.
var ErrInvalid = errors.New("b64: invalid base64url")

var strict = base64.RawURLEncoding.Strict()

// Encode returns the unpadded base64url form of b.
func Encode(b []byte) string {
	return base64.RawURLEncoding.EncodeToString(b)
}

// Decode decodes unpadded base64url and rejects every non-canonical spelling.
//
// The standard decoder silently skips '\r' and '\n', and without Strict it
// accepts non-zero trailing bits, so several different strings would decode to
// the same bytes. A signature, nonce or key must have exactly one textual form,
// otherwise caches keyed on the text (such as the host's nonce cache) can be
// sidestepped, so both are rejected here.
func Decode(s string) ([]byte, error) {
	if !IsAlphabet(s) {
		return nil, ErrInvalid
	}
	b, err := strict.DecodeString(s)
	if err != nil {
		return nil, ErrInvalid
	}
	return b, nil
}

// DecodeLen decodes s and requires exactly n bytes.
func DecodeLen(s string, n int) ([]byte, error) {
	if len(s) != base64.RawURLEncoding.EncodedLen(n) {
		return nil, ErrInvalid
	}
	b, err := Decode(s)
	if err != nil || len(b) != n {
		return nil, ErrInvalid
	}
	return b, nil
}

// IsAlphabet reports whether every byte of s is in the base64url alphabet.
func IsAlphabet(s string) bool {
	for i := 0; i < len(s); i++ {
		if !inAlphabet(s[i]) {
			return false
		}
	}
	return true
}

func inAlphabet(c byte) bool {
	return c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_'
}
