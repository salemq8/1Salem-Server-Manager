// Package preamble encodes and decodes the connection preamble the friend
// transport sends before any game byte (CONNECT_ARCHITECTURE.md §10):
//
//	"1SC" 0x01 | uint16 big-endian length N (1..4096) | N bytes UTF-8 JSON
//	{"t":"<ticket>","n":"<nonce>","ts":<unix seconds>,"p":"<proof>"}
//
// followed by one status byte from the host: 0x00 accepted, 0x01 refused.
package preamble

import (
	"encoding/binary"
	"encoding/json"
	"errors"
	"io"
	"regexp"

	"1salem.app/connect/transport/internal/b64"
	"1salem.app/connect/transport/internal/es256"
	"1salem.app/connect/transport/internal/proof"
	"1salem.app/connect/transport/internal/strictjson"
)

const (
	// Magic starts every preamble.
	Magic = "1SC"
	// Version is the only preamble version this code speaks.
	Version byte = 0x01
	// MaxPayload is the largest JSON body accepted.
	MaxPayload = 4096
	// HeaderLen is magic + version + length.
	HeaderLen = len(Magic) + 1 + 2

	// StatusOK tells the friend the raw stream follows.
	StatusOK byte = 0x00
	// StatusRefused is the single, generic refusal.
	StatusRefused byte = 0x01
)

var (
	// ErrFrame covers a bad magic, version or length.
	ErrFrame = errors.New("preamble: bad frame")
	// ErrBody covers JSON that is malformed or has invalid fields.
	ErrBody = errors.New("preamble: bad body")
)

var ticketShape = regexp.MustCompile(`^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$`)

// Preamble is the JSON body. Field order matches the contract.
type Preamble struct {
	T  string `json:"t"`
	N  string `json:"n"`
	TS int64  `json:"ts"`
	P  string `json:"p"`
}

// Validate checks field shapes. It does not verify the ticket or the proof;
// that is the Agent's job. It only guarantees the Agent receives a
// well-formed request of bounded size.
func (p Preamble) Validate() error {
	if len(p.T) == 0 || len(p.T) > MaxPayload || !ticketShape.MatchString(p.T) {
		return ErrBody
	}
	if _, err := b64.DecodeLen(p.N, proof.NonceLen); err != nil {
		return ErrBody
	}
	if p.TS <= 0 {
		return ErrBody
	}
	if _, err := b64.DecodeLen(p.P, es256.SignatureLen); err != nil {
		return ErrBody
	}
	return nil
}

// Marshal validates p and returns the full frame.
func Marshal(p Preamble) ([]byte, error) {
	if err := p.Validate(); err != nil {
		return nil, err
	}
	body, err := json.Marshal(p)
	if err != nil {
		return nil, err
	}
	if len(body) > MaxPayload {
		return nil, ErrFrame
	}
	frame := make([]byte, HeaderLen, HeaderLen+len(body))
	copy(frame, Magic)
	frame[len(Magic)] = Version
	binary.BigEndian.PutUint16(frame[len(Magic)+1:], uint16(len(body)))
	return append(frame, body...), nil
}

// Write sends the frame in a single write.
func Write(w io.Writer, p Preamble) error {
	frame, err := Marshal(p)
	if err != nil {
		return err
	}
	_, err = w.Write(frame)
	return err
}

// Read reads exactly one frame and nothing more: the raw stream that follows
// must stay in the connection for the splice. The length is checked before
// the body is allocated or read, so a peer cannot make the host buffer more
// than MaxPayload bytes. Callers set a read deadline.
func Read(r io.Reader) (Preamble, error) {
	var hdr [HeaderLen]byte
	if _, err := io.ReadFull(r, hdr[:]); err != nil {
		return Preamble{}, ErrFrame
	}
	if string(hdr[:len(Magic)]) != Magic || hdr[len(Magic)] != Version {
		return Preamble{}, ErrFrame
	}
	n := int(binary.BigEndian.Uint16(hdr[len(Magic)+1:]))
	if n == 0 || n > MaxPayload {
		return Preamble{}, ErrFrame
	}
	body := make([]byte, n)
	if _, err := io.ReadFull(r, body); err != nil {
		return Preamble{}, ErrFrame
	}
	var p Preamble
	if err := strictjson.Decode(body, &p); err != nil {
		return Preamble{}, ErrBody
	}
	if err := p.Validate(); err != nil {
		return Preamble{}, err
	}
	return p, nil
}
