package preamble_test

import (
	"bytes"
	"encoding/binary"
	"errors"
	"io"
	"strings"
	"testing"

	"1salem.app/connect/transport/internal/b64"
	"1salem.app/connect/transport/internal/preamble"
)

func valid() preamble.Preamble {
	return preamble.Preamble{
		T:  "eyJhbGciOiJFUzI1NiJ9.eyJpc3MiOiJ4In0.c2ln",
		N:  b64.Encode(make([]byte, 16)),
		TS: 1_790_000_000,
		P:  b64.Encode(make([]byte, 64)),
	}
}

// frame builds a frame with an arbitrary header and body.
func frame(magic string, version byte, length int, body []byte) []byte {
	out := []byte(magic)
	out = append(out, version)
	out = binary.BigEndian.AppendUint16(out, uint16(length))
	return append(out, body...)
}

func TestRoundTripLeavesTheStreamAlone(t *testing.T) {
	f, err := preamble.Marshal(valid())
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.HasPrefix(f, []byte{'1', 'S', 'C', 0x01}) {
		t.Fatalf("frame starts %x", f[:4])
	}
	// Game bytes that follow the preamble must stay unread for the splice.
	r := bytes.NewReader(append(f, "GAME"...))
	got, err := preamble.Read(r)
	if err != nil {
		t.Fatal(err)
	}
	if got != valid() {
		t.Fatalf("got %+v", got)
	}
	rest, _ := io.ReadAll(r)
	if string(rest) != "GAME" {
		t.Fatalf("Read consumed stream bytes; rest = %q", rest)
	}
}

// countingReader fails the test if more than limit bytes are read.
type countingReader struct {
	r     io.Reader
	n     int
	limit int
	t     *testing.T
}

func (c *countingReader) Read(p []byte) (int, error) {
	n, err := c.r.Read(p)
	c.n += n
	if c.n > c.limit {
		c.t.Fatalf("read %d bytes, limit %d", c.n, c.limit)
	}
	return n, err
}

func TestLimits(t *testing.T) {
	body := []byte(`{"t":"a.b.c","n":"AAAAAAAAAAAAAAAAAAAAAA","ts":1,"p":"` + strings.Repeat("A", 86) + `"}`)

	// A length over 4096 is refused from the header alone, before any body
	// byte is read or allocated.
	over := frame("1SC", 0x01, preamble.MaxPayload+1, bytes.Repeat([]byte{'x'}, 8192))
	cr := &countingReader{r: bytes.NewReader(over), limit: preamble.HeaderLen, t: t}
	if _, err := preamble.Read(cr); !errors.Is(err, preamble.ErrFrame) {
		t.Fatalf("oversize: err = %v, want ErrFrame", err)
	}

	bad := map[string][]byte{
		"zero length":   frame("1SC", 0x01, 0, nil),
		"bad magic":     frame("1SX", 0x01, len(body), body),
		"bad version":   frame("1SC", 0x02, len(body), body),
		"short body":    frame("1SC", 0x01, len(body)+10, body),
		"short header":  []byte("1SC"),
		"empty":         nil,
		"trailing json": frame("1SC", 0x01, len(body)+3, append(append([]byte{}, body...), " {}"...)),
	}
	for name, in := range bad {
		if _, err := preamble.Read(bytes.NewReader(in)); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}

	// Well-formed JSON with invalid fields.
	fields := map[string]string{
		"unknown field":  `{"t":"a.b.c","n":"AAAAAAAAAAAAAAAAAAAAAA","ts":1,"p":"` + strings.Repeat("A", 86) + `","hb":"127.0.0.1:1"}`,
		"duplicate t":    `{"t":"a.b.c","t":"d.e.f","n":"AAAAAAAAAAAAAAAAAAAAAA","ts":1,"p":"` + strings.Repeat("A", 86) + `"}`,
		"short nonce":    `{"t":"a.b.c","n":"AAAA","ts":1,"p":"` + strings.Repeat("A", 86) + `"}`,
		"short proof":    `{"t":"a.b.c","n":"AAAAAAAAAAAAAAAAAAAAAA","ts":1,"p":"AAAA"}`,
		"zero ts":        `{"t":"a.b.c","n":"AAAAAAAAAAAAAAAAAAAAAA","ts":0,"p":"` + strings.Repeat("A", 86) + `"}`,
		"ticket shape":   `{"t":"a.b","n":"AAAAAAAAAAAAAAAAAAAAAA","ts":1,"p":"` + strings.Repeat("A", 86) + `"}`,
		"padded nonce":   `{"t":"a.b.c","n":"AAAAAAAAAAAAAAAAAAAAAA==","ts":1,"p":"` + strings.Repeat("A", 86) + `"}`,
		"null":           `null`,
		"missing fields": `{}`,
	}
	for name, js := range fields {
		in := frame("1SC", 0x01, len(js), []byte(js))
		if _, err := preamble.Read(bytes.NewReader(in)); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}

	if _, err := preamble.Read(bytes.NewReader(frame("1SC", 0x01, len(body), body))); err != nil {
		t.Fatalf("minimal valid body refused: %v", err)
	}
}

func TestMarshalRefusesOversizeTicket(t *testing.T) {
	p := valid()
	p.T = strings.Repeat("a", 3000) + "." + strings.Repeat("b", 1000) + ".c"
	if _, err := preamble.Marshal(p); err == nil {
		t.Fatal("a ticket longer than the frame limit was marshalled")
	}
}
