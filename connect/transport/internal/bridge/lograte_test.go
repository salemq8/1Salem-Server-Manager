package bridge

import (
	"testing"
	"time"
)

// RefusalLogBurst lets the external tests bound the refusal lines they see.
const RefusalLogBurst = refusalLogBurst

func TestLogLimiter(t *testing.T) {
	now := time.Unix(1_700_000_000, 0)
	l := newLogLimiter(3, time.Minute)
	l.now = func() time.Time { return now }

	for i := 0; i < 3; i++ {
		if ok, n := l.allow(); !ok || n != 0 {
			t.Fatalf("line %d: %v, %d", i, ok, n)
		}
	}
	for i := 0; i < 5; i++ {
		if ok, _ := l.allow(); ok {
			t.Fatalf("line %d past the burst was allowed", i)
		}
	}
	now = now.Add(59 * time.Second)
	if ok, _ := l.allow(); ok {
		t.Fatal("allowed before the window ended")
	}
	// The next window reports what the last one dropped, once.
	now = now.Add(time.Second)
	if ok, n := l.allow(); !ok || n != 6 {
		t.Fatalf("new window: %v, %d suppressed; want 6", ok, n)
	}
	if ok, n := l.allow(); !ok || n != 0 {
		t.Fatalf("second line of the window: %v, %d", ok, n)
	}
}
