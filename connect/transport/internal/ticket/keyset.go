package ticket

import (
	"crypto/ecdsa"
	"errors"
	"fmt"
	"io"
	"os"
	"regexp"
	"slices"

	"1salem.app/connect/transport/internal/es256"
	"1salem.app/connect/transport/internal/strictjson"
)

// maxKeysetBytes bounds the keyset file; a real one holds a handful of keys.
const maxKeysetBytes = 64 * 1024

var kidPattern = regexp.MustCompile(`^[A-Za-z0-9._-]{1,64}$`)

// Keyset is the set of broker ticket keys this transport trusts, each pinned
// to ES256. It is loaded once from the JSON the broker publishes at
// GET /v1/keys: {"keys":[{"kid","alg","spki"}]}.
type Keyset struct {
	keys map[string]*ecdsa.PublicKey
}

type keysetDoc struct {
	Keys []keyDoc `json:"keys"`
}

type keyDoc struct {
	Kid  string `json:"kid"`
	Alg  string `json:"alg"`
	SPKI string `json:"spki"`
}

// LoadKeyset reads and parses a keyset file.
func LoadKeyset(path string) (*Keyset, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	data, err := io.ReadAll(io.LimitReader(f, maxKeysetBytes+1))
	if err != nil {
		return nil, err
	}
	if len(data) > maxKeysetBytes {
		return nil, errors.New("ticket: keyset file too large")
	}
	return ParseKeyset(data)
}

// ParseKeyset parses keyset JSON. Every key must be ES256 over P-256; a key
// with any other algorithm fails the whole keyset rather than being skipped,
// so a misconfigured keyset is noticed instead of silently trusting less (or
// something else) than intended. Unknown fields are ignored so the raw
// /v1/keys response can be saved as-is.
func ParseKeyset(data []byte) (*Keyset, error) {
	var doc keysetDoc
	if err := strictjson.DecodeAllowUnknown(data, &doc); err != nil {
		return nil, fmt.Errorf("ticket: keyset: %w", err)
	}
	if len(doc.Keys) == 0 {
		return nil, errors.New("ticket: keyset has no keys")
	}
	ks := &Keyset{keys: make(map[string]*ecdsa.PublicKey, len(doc.Keys))}
	for _, k := range doc.Keys {
		if !kidPattern.MatchString(k.Kid) {
			return nil, errors.New("ticket: keyset has an invalid kid")
		}
		if k.Alg != Algorithm {
			return nil, fmt.Errorf("ticket: key %q is not %s", k.Kid, Algorithm)
		}
		if _, dup := ks.keys[k.Kid]; dup {
			return nil, fmt.Errorf("ticket: duplicate kid %q", k.Kid)
		}
		pub, err := es256.ParsePublicKey(k.SPKI)
		if err != nil {
			return nil, fmt.Errorf("ticket: key %q: %w", k.Kid, err)
		}
		ks.keys[k.Kid] = pub
	}
	return ks, nil
}

// Kids returns the pinned key ids in sorted order, for diagnostics.
func (ks *Keyset) Kids() []string {
	kids := make([]string, 0, len(ks.keys))
	for kid := range ks.keys {
		kids = append(kids, kid)
	}
	slices.Sort(kids)
	return kids
}

func (ks *Keyset) lookup(kid string) (*ecdsa.PublicKey, bool) {
	pub, ok := ks.keys[kid]
	return pub, ok
}
