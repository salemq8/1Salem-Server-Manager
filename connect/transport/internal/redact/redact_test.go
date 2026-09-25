package redact_test

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"

	"1salem.app/connect/transport/internal/redact"
)

func TestSecretsAreMasked(t *testing.T) {
	secrets := []string{
		"tskey-auth-kABCDEF1CNTRL-0123456789abcdefghijkl",
		"tskey-client-kABCDEF1CNTRL-secret?ephemeral=false&preauthorized=true",
		"eyJhbGciOiJFUzI1NiIsInR5cCI6IjFzYWxlbS10aWNrZXQrand0In0.eyJpc3MiOiJ4In0.c2lnbmF0dXJl",
		"MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
		"privkey:0123456789abcdef",
		"https://login.tailscale.com/a/1a2b3c4d5e6f",
	}
	var buf bytes.Buffer
	log := redact.New(&buf, 4)
	for _, s := range secrets {
		log.Printf("enroll failed for %s: bad", s)
	}
	out := buf.String() + strings.Join(log.Recent(), "\n")
	for _, s := range secrets {
		if strings.Contains(out, s) {
			t.Errorf("secret leaked: %s", s)
		}
	}
	if !strings.Contains(out, "tskey-[REDACTED]") || !strings.Contains(out, "[REDACTED-JWS]") {
		t.Errorf("unexpected output:\n%s", out)
	}
}

func TestRecentWithinKeepsTheNewestLinesThatFit(t *testing.T) {
	log := redact.New(nil, 10)
	for _, s := range []string{"a", "b", "c", "<<<<"} {
		log.Verbose("%s", s)
	}
	all := log.Recent()
	encoded := func(lines []string) int {
		size := 0
		for _, line := range lines {
			b, _ := json.Marshal(line)
			size += len(b) + 1
		}
		return size
	}
	// Exactly the two newest lines: "c" and "<<<<", whose "<" is escaped to six bytes each.
	budget := encoded(all[2:])
	got := log.RecentWithin(budget)
	if len(got) != 2 || !strings.HasSuffix(got[0], " c") || !strings.HasSuffix(got[1], " <<<<") {
		t.Fatalf("RecentWithin(%d) = %q", budget, got)
	}
	if got := log.RecentWithin(budget - 1); len(got) != 1 || !strings.HasSuffix(got[0], " <<<<") {
		t.Fatalf("RecentWithin(%d) = %q", budget-1, got)
	}
	if got := log.RecentWithin(1 << 20); len(got) != len(all) {
		t.Fatalf("a large budget keeps everything: %q", got)
	}
	if got := log.RecentWithin(0); len(got) != 0 {
		t.Fatalf("a zero budget keeps nothing: %q", got)
	}
}

func TestRingKeepsTheLatestLines(t *testing.T) {
	log := redact.New(nil, 3)
	for _, s := range []string{"a", "b", "c", "d"} {
		log.Verbose("%s", s)
	}
	got := log.Recent()
	if len(got) != 3 || !strings.HasSuffix(got[0], " b") || !strings.HasSuffix(got[2], " d") {
		t.Fatalf("Recent = %q", got)
	}
}
