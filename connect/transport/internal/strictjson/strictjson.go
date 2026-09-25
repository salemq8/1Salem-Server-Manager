// Package strictjson decodes JSON received from another process without the
// permissive corners of encoding/json.
//
// encoding/json lets the last of two duplicate keys win, matches keys
// case-insensitively, replaces invalid UTF-8 and happily decodes "null" into a
// struct. Each of those lets two parsers (Go here, C# or TypeScript on the
// other side) read different values from the same bytes, which is exactly what
// an attacker wants from a ticket or a preamble. This package rejects all of
// them before the value reaches encoding/json.
package strictjson

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"unicode/utf8"
)

// maxDepth bounds nesting so a hostile document cannot drive deep recursion.
const maxDepth = 16

var (
	errNotObject     = errors.New("strictjson: top-level value must be an object")
	errInvalidUTF8   = errors.New("strictjson: invalid UTF-8")
	errDuplicateKey  = errors.New("strictjson: duplicate key")
	errNonASCIIKey   = errors.New("strictjson: non-ASCII key")
	errTooDeep       = errors.New("strictjson: nesting too deep")
	errTrailingBytes = errors.New("strictjson: trailing data")
)

// Decode decodes a single JSON object into v and rejects unknown fields.
func Decode(data []byte, v any) error {
	return decode(data, v, true)
}

// DecodeAllowUnknown is Decode for messages whose contract allows additive,
// forward-compatible fields (for example ticket claims). Duplicate keys and
// the other ambiguities are still rejected.
func DecodeAllowUnknown(data []byte, v any) error {
	return decode(data, v, false)
}

func decode(data []byte, v any, disallowUnknown bool) error {
	if err := Check(data); err != nil {
		return err
	}
	dec := json.NewDecoder(bytes.NewReader(data))
	if disallowUnknown {
		dec.DisallowUnknownFields()
	}
	return dec.Decode(v)
}

// Check validates that data is one well-formed JSON object with valid UTF-8,
// no duplicate keys (compared ASCII case-insensitively, the way encoding/json
// matches struct fields), only ASCII keys, bounded nesting and no trailing data.
func Check(data []byte) error {
	if !utf8.Valid(data) {
		return errInvalidUTF8
	}
	trimmed := bytes.TrimLeft(data, " \t\r\n")
	if len(trimmed) == 0 || trimmed[0] != '{' {
		return errNotObject
	}
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.UseNumber()
	if err := walk(dec, 0); err != nil {
		return err
	}
	if _, err := dec.Token(); err != io.EOF {
		return errTrailingBytes
	}
	return nil
}

func walk(dec *json.Decoder, depth int) error {
	if depth > maxDepth {
		return errTooDeep
	}
	tok, err := dec.Token()
	if err != nil {
		return err
	}
	delim, ok := tok.(json.Delim)
	if !ok {
		return nil
	}
	switch delim {
	case '{':
		seen := make(map[string]struct{})
		for dec.More() {
			keyTok, err := dec.Token()
			if err != nil {
				return err
			}
			key, _ := keyTok.(string)
			folded, ok := foldASCII(key)
			if !ok {
				return errNonASCIIKey
			}
			if _, dup := seen[folded]; dup {
				return errDuplicateKey
			}
			seen[folded] = struct{}{}
			if err := walk(dec, depth+1); err != nil {
				return err
			}
		}
	case '[':
		for dec.More() {
			if err := walk(dec, depth+1); err != nil {
				return err
			}
		}
	}
	_, err = dec.Token() // the matching close delimiter
	return err
}

// foldASCII lower-cases an ASCII key. Non-ASCII keys are refused outright:
// encoding/json folds a few non-ASCII runes (for example the Kelvin sign) onto
// ASCII letters, and no 1Salem contract uses non-ASCII keys.
func foldASCII(s string) (string, bool) {
	b := []byte(s)
	for i, c := range b {
		if c >= utf8.RuneSelf {
			return "", false
		}
		if c >= 'A' && c <= 'Z' {
			b[i] = c + ('a' - 'A')
		}
	}
	return string(b), true
}
