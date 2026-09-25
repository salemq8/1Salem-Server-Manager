//go:build windows

// Package sidecar holds the startup steps both binaries share: the build
// check, the redacting logger, the pipe server and signal handling.
package sidecar

import (
	"context"
	"errors"
	"fmt"
	"os"
	"os/signal"

	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/redact"
)

// logRing is how many recent log lines the "diag" operation returns.
const logRing = 500

// ErrOAuthHookPresent is returned by CheckBuild for a tsnet-mode start of a
// binary built without the ts_omit_oauthkey tag.
var ErrOAuthHookPresent = errors.New("this binary was built without -tags ts_omit_oauthkey, so tsnet's OAuth key hook is compiled in; tsnet mode is refused (build with connect/transport/build.ps1)")

// CheckBuild refuses tsnet mode in a binary that still contains tsnet's OAuth
// hook (§11). Fake mode never reaches tsnet, so a plain "go build" or
// "go test" stays usable for development.
func CheckBuild(mode netpolicy.Mode) error {
	if mode == netpolicy.Tsnet && !OAuthKeyOmitted {
		return ErrOAuthHookPresent
	}
	return nil
}

// NewLogger returns the redacting logger writing to stderr.
func NewLogger() *redact.Logger {
	return redact.New(os.Stderr, logRing)
}

// SignalContext is cancelled on Ctrl+C / Ctrl+Break.
func SignalContext() (context.Context, context.CancelFunc) {
	return signal.NotifyContext(context.Background(), os.Interrupt)
}

// ServePipe creates the pipe (first instance only, protected DACL) and serves
// h on it until ctx is done.
func ServePipe(ctx context.Context, name string, h pipe.Handler, log *redact.Logger) error {
	ln, err := pipe.Listen(name)
	if err != nil {
		return fmt.Errorf("cannot create pipe %s: %w", name, err)
	}
	log.Printf("pipe: serving %s", name)
	srv := &pipe.Server{Handler: h, Logf: log.Printf}
	return srv.Serve(ctx, ln)
}
