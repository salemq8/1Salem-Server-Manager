package bridge

import (
	"sync"
	"time"
)

const (
	// refusalLogBurst refusal lines are logged per refusalLogWindow; the
	// rest are only counted. A peer can be refused as fast as it can open
	// connections, and each line would otherwise land in the log file and
	// push everything useful out of the Diagnostics ring.
	refusalLogBurst  = 10
	refusalLogWindow = time.Minute
)

// logLimiter lets a burst of lines through per window and counts the rest.
type logLimiter struct {
	burst  int
	window time.Duration
	now    func() time.Time

	mu         sync.Mutex
	start      time.Time
	used       int
	suppressed int
}

func newLogLimiter(burst int, window time.Duration) *logLimiter {
	return &logLimiter{burst: burst, window: window, now: time.Now}
}

// allow reports whether a line may be written now and, when it may, how many
// lines were suppressed since the last one written, so the log says so.
func (l *logLimiter) allow() (ok bool, suppressed int) {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.now()
	if l.start.IsZero() || now.Sub(l.start) >= l.window {
		l.start, l.used = now, 0
	}
	if l.used >= l.burst {
		l.suppressed++
		return false, 0
	}
	l.used++
	suppressed, l.suppressed = l.suppressed, 0
	return true, suppressed
}
