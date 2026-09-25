//go:build windows

// Command connect-transport is 1Salem.Connect.Transport.exe, the friend data
// plane (CONNECT_ARCHITECTURE.md §3, §11). It serves the friend pipe for the
// 1Salem Connect UI and carries local game connections to the host bridge
// named in a verified ticket. It never accepts a destination from anyone.
//
// Build it with build.ps1, which adds -tags ts_omit_oauthkey; a binary
// without the tag refuses tsnet mode.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"os"

	"1salem.app/connect/transport/internal/friend"
	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/redact"
	"1salem.app/connect/transport/internal/sidecar"
	"1salem.app/connect/transport/internal/ticket"
	"1salem.app/connect/transport/internal/transport"
	"1salem.app/connect/transport/internal/winsec"
)

// version is set by build.ps1 through -ldflags "-X main.version=...".
var version = "dev"

func main() {
	// First, before anything else runs: an auth key or control-server
	// override must only ever arrive over the pipe (§11).
	transport.ScrubEnvironment()
	log := sidecar.NewLogger()
	if err := run(log, os.Args[1:]); err != nil {
		log.Printf("connect-transport: %v", err)
		os.Exit(1)
	}
}

type options struct {
	mode       netpolicy.Mode
	pipe       string
	keys       string
	stateDir   string
	fakeNodeID string
}

func parseFlags(args []string) (options, error) {
	fs := flag.NewFlagSet("connect-transport", flag.ContinueOnError)
	mode := fs.String("mode", "", `"fake" or "tsnet" (required)`)
	pipeName := fs.String("pipe", "", `pipe to serve (default \\.\pipe\1Salem.Connect.Transport.<user SID>)`)
	keys := fs.String("keys", "", "pinned ticket keyset JSON file, as served by GET /v1/keys (required)")
	stateDir := fs.String("state-dir", "", `absolute directory for node state; each node lives in <dir>\nodes\<node> (tsnet mode, required)`)
	fakeNodeID := fs.String("fake-node-id", "", "node id the fake transport claims (fake mode, required; NOT a security boundary)")
	if err := fs.Parse(args); err != nil {
		return options{}, err
	}
	if fs.NArg() > 0 {
		return options{}, fmt.Errorf("unexpected argument %q", fs.Arg(0))
	}
	m, err := netpolicy.ParseMode(*mode)
	if err != nil {
		return options{}, err
	}
	o := options{mode: m, pipe: *pipeName, keys: *keys, stateDir: *stateDir, fakeNodeID: *fakeNodeID}
	if o.keys == "" {
		return options{}, errors.New("--keys is required")
	}
	switch m {
	case netpolicy.Fake:
		if o.fakeNodeID == "" {
			return options{}, errors.New("fake mode requires --fake-node-id")
		}
		if o.stateDir != "" {
			return options{}, errors.New("--state-dir applies to tsnet mode only")
		}
	case netpolicy.Tsnet:
		if o.stateDir == "" {
			return options{}, errors.New("tsnet mode requires --state-dir")
		}
		// A self-asserted identity must never sit next to a real node.
		if o.fakeNodeID != "" {
			return options{}, errors.New("--fake-node-id applies to fake mode only")
		}
	}
	if o.pipe == "" {
		sid, err := winsec.CurrentUserSID()
		if err != nil {
			return options{}, err
		}
		o.pipe = pipe.FriendPipeName(sid)
	}
	if err := pipe.ValidateName(o.pipe); err != nil {
		return options{}, err
	}
	return o, nil
}

func run(log *redact.Logger, args []string) error {
	o, err := parseFlags(args)
	if err != nil {
		return err
	}
	if err := sidecar.CheckBuild(o.mode); err != nil {
		return err
	}
	keys, err := ticket.LoadKeyset(o.keys)
	if err != nil {
		return err
	}
	nodes, err := newNodes(o, log)
	if err != nil {
		return err
	}
	defer nodes.Close()

	mgr := friend.NewManager(friend.Config{
		Verifier: &ticket.Verifier{Keys: keys, Mode: o.mode},
		Nodes:    nodes,
		Logf:     log.Printf,
	})
	defer mgr.CloseAll()
	svc := &friend.Service{Mode: o.mode, Version: version, Keys: keys, Nodes: nodes, Manager: mgr, Log: log}

	ctx, cancel := sidecar.SignalContext()
	defer cancel()
	log.Printf("connect-transport %s starting in %s mode with ticket keys %v", version, o.mode, keys.Kids())
	if o.mode == netpolicy.Fake {
		log.Printf("connect-transport: FAKE MODE: loopback stands in for the tailnet and node id %q is self-asserted; not a security boundary", o.fakeNodeID)
	}
	err = sidecar.ServePipe(ctx, o.pipe, svc.Handle, log)
	if errors.Is(err, context.Canceled) {
		log.Printf("connect-transport: stopping")
		return nil
	}
	return err
}

func newNodes(o options, log *redact.Logger) (transport.NodeSet, error) {
	if o.mode == netpolicy.Fake {
		return transport.NewFakeNodes(o.fakeNodeID)
	}
	return transport.NewTsnetNodes(o.stateDir, log.Printf, log.Verbose)
}
