// Package redact is the only logger the sidecars use. Every line passes
// through String before it is written anywhere, so a secret that reaches a
// log call by mistake (ours or tsnet's) is masked instead of persisted.
package redact

import (
	"encoding/json"
	"fmt"
	"io"
	"regexp"
	"strings"
	"sync"
	"time"
)

var rules = []struct {
	re   *regexp.Regexp
	repl string
}{
	// Tailscale auth keys and OAuth client secrets, including the
	// "?ephemeral=...&baseURL=..." suffix tsnet accepts on client secrets.
	{regexp.MustCompile(`tskey-[A-Za-z0-9_\-?=&.:/]+`), "tskey-[REDACTED]"},
	// Compact JWS/JWT (tickets). Headers are base64url JSON, so they start "eyJ".
	{regexp.MustCompile(`eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]*\.[A-Za-z0-9_-]*`), "[REDACTED-JWS]"},
	// Base64 PKCS#8 EC private keys (session keys) start with "MIG".
	{regexp.MustCompile(`MIG[A-Za-z0-9_+/=-]{60,}`), "[REDACTED-KEY]"},
	// Tailscale private key text forms.
	{regexp.MustCompile(`(privkey|nlpriv):[0-9A-Fa-f]+`), "$1:[REDACTED]"},
	// Interactive login URLs: whoever opens one can log this node into their
	// own account, so the path token is a secret.
	{regexp.MustCompile(`(https?://[^\s/]+/a/)[A-Za-z0-9]+`), "${1}[REDACTED]"},
	// PEM private keys.
	{regexp.MustCompile(`-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----`), "[REDACTED-PEM]"},
}

// String masks every known secret shape in s.
func String(s string) string {
	for _, r := range rules {
		s = r.re.ReplaceAllString(s, r.repl)
	}
	return s
}

// Logger writes redacted, timestamped lines to an io.Writer and keeps the
// most recent lines in memory for the "diag" pipe operation.
type Logger struct {
	mu   sync.Mutex
	w    io.Writer
	ring []string
	next int
	full bool
}

// New returns a Logger writing to w (nil discards) that remembers ringSize lines.
func New(w io.Writer, ringSize int) *Logger {
	if ringSize < 1 {
		ringSize = 1
	}
	return &Logger{w: w, ring: make([]string, ringSize)}
}

// Printf logs one redacted line to the writer and the ring.
func (l *Logger) Printf(format string, args ...any) {
	l.log(true, format, args...)
}

// Verbose logs one redacted line to the ring only. tsnet's backend log is
// far too chatty for stderr but useful in Diagnostics.
func (l *Logger) Verbose(format string, args ...any) {
	l.log(false, format, args...)
}

func (l *Logger) log(toWriter bool, format string, args ...any) {
	msg := String(fmt.Sprintf(format, args...))
	msg = strings.TrimRight(msg, "\r\n")
	line := time.Now().UTC().Format(time.RFC3339) + " " + msg
	l.mu.Lock()
	defer l.mu.Unlock()
	l.ring[l.next] = line
	l.next = (l.next + 1) % len(l.ring)
	if l.next == 0 {
		l.full = true
	}
	if toWriter && l.w != nil {
		io.WriteString(l.w, line+"\n")
	}
}

// Recent returns the remembered lines, oldest first.
func (l *Logger) Recent() []string {
	l.mu.Lock()
	defer l.mu.Unlock()
	if !l.full {
		return append([]string(nil), l.ring[:l.next]...)
	}
	out := make([]string, 0, len(l.ring))
	out = append(out, l.ring[l.next:]...)
	return append(out, l.ring[:l.next]...)
}

// RecentWithin returns the newest remembered lines, oldest first, whose JSON
// string encodings (plus one separating comma each) add up to at most
// maxBytes. A "diag" response carries the log inside one pipe line, and a
// ring of long lines (tsnet's are) would otherwise push that line past the
// pipe's limit, so the response could not be sent at all.
func (l *Logger) RecentWithin(maxBytes int) []string {
	lines := l.Recent()
	start, size := len(lines), 0
	for start > 0 {
		// Measured encoded, because escaping ("<" becomes <) can make a
		// line several times longer on the wire. Marshalling a string cannot fail.
		encoded, _ := json.Marshal(lines[start-1])
		if size+len(encoded)+1 > maxBytes {
			break
		}
		size += len(encoded) + 1
		start--
	}
	return lines[start:]
}
