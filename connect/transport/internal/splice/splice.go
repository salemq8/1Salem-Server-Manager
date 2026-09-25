// Package splice copies bytes between two connections in both directions.
package splice

import (
	"io"
	"net"
	"sync"
)

type closeWriter interface {
	CloseWrite() error
}

// Pipe copies a->b and b->a until both directions have finished, then closes
// both connections. It returns the byte counts of each direction.
//
// A clean end of stream in one direction is forwarded as a half-close, so a
// peer that finishes sending can still receive the reply. Any error, or a
// connection that cannot half-close, tears down both sides so neither copy
// goroutine is left blocked. Closing either connection from outside (for
// example on revocation) ends the splice the same way.
func Pipe(a, b net.Conn) (aToB, bToA int64) {
	var wg sync.WaitGroup
	wg.Add(2)
	go func() {
		defer wg.Done()
		aToB = copyHalf(b, a)
	}()
	go func() {
		defer wg.Done()
		bToA = copyHalf(a, b)
	}()
	wg.Wait()
	a.Close()
	b.Close()
	return aToB, bToA
}

func copyHalf(dst, src net.Conn) int64 {
	n, err := io.Copy(dst, src)
	if err == nil {
		if cw, ok := dst.(closeWriter); ok && cw.CloseWrite() == nil {
			return n
		}
	}
	dst.Close()
	src.Close()
	return n
}
