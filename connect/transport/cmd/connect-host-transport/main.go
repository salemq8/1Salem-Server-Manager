//go:build windows

// Command connect-host-transport is 1Salem.Connect.Host.Transport.exe, the
// host bridge data plane (CONNECT_ARCHITECTURE.md §3, §10, §11). It listens on
// the host bridge address, asks the Agent about every connection over
// \\.\pipe\1Salem.Connect.HostAuthz.v1, and splices allowed connections to the
// loopback endpoint the Agent returns. It serves a small control pipe of its
// own for enrolling the host node (§7) and for status.
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

	"1salem.app/connect/transport/internal/authz"
	"1salem.app/connect/transport/internal/bridge"
	"1salem.app/connect/transport/internal/host"
	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/redact"
	"1salem.app/connect/transport/internal/sidecar"
	"1salem.app/connect/transport/internal/transport"
	"1salem.app/connect/transport/internal/winsec"
)

// version is set by build.ps1 through -ldflags "-X main.version=...".
var version = "dev"

// defaultTsnetBridge is the bridge port the owner's tailnet policy opens to
// friends (§8): every tailnet address of the host node, port 7780.
const defaultTsnetBridge = ":7780"

// fakeHostNodeID labels the host in fake mode. The host never dials, so the
// id is only ever shown in status.
const fakeHostNodeID = "fake-host"

func main() {
	// First, before anything else runs: an auth key or control-server
	// override must only ever arrive over the pipe (§11).
	transport.ScrubEnvironment()
	log := sidecar.NewLogger()
	if err := run(log, os.Args[1:]); err != nil {
		log.Printf("connect-host-transport: %v", err)
		os.Exit(1)
	}
}

type options struct {
	mode         netpolicy.Mode
	pipe         string
	stateDir     string
	bridgeListen string
	authzPipe    string
	authzOwners  []string
}

func parseFlags(args []string) (options, error) {
	fs := flag.NewFlagSet("connect-host-transport", flag.ContinueOnError)
	mode := fs.String("mode", "", `"fake" or "tsnet" (required)`)
	pipeName := fs.String("pipe", "", `control pipe to serve (default \\.\pipe\1Salem.Connect.Host.Transport.<account SID>)`)
	stateDir := fs.String("state-dir", "", `absolute directory for node state; the node lives in <dir>\nodes\host (tsnet mode, required)`)
	bridgeListen := fs.String("bridge-listen", "", `bridge address: ":port" or a Tailscale ip:port in tsnet mode (default ":7780"); a loopback ip:port in fake mode (required)`)
	authzPipe := fs.String("authz-pipe", pipe.HostAuthzPipe, "the Agent's authorization pipe")
	expectedOwner := fs.String("expected-authz-owner", "", "SID that must own the authorization pipe (default SYSTEM in tsnet mode, the current user in fake mode)")
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
	o := options{mode: m, pipe: *pipeName, stateDir: *stateDir, bridgeListen: *bridgeListen, authzPipe: *authzPipe}
	switch m {
	case netpolicy.Fake:
		if o.stateDir != "" {
			return options{}, errors.New("--state-dir applies to tsnet mode only")
		}
		if o.bridgeListen == "" {
			return options{}, errors.New("fake mode requires --bridge-listen <loopback ip:port>")
		}
	case netpolicy.Tsnet:
		if o.stateDir == "" {
			return options{}, errors.New("tsnet mode requires --state-dir")
		}
		if o.bridgeListen == "" {
			o.bridgeListen = defaultTsnetBridge
		}
	}
	if err := transport.CheckListenAddr(m, o.bridgeListen); err != nil {
		return options{}, fmt.Errorf("--bridge-listen %q: %w", o.bridgeListen, err)
	}
	if err := pipe.ValidateName(o.authzPipe); err != nil {
		return options{}, fmt.Errorf("--authz-pipe: %w", err)
	}
	if o.authzOwners, err = authzOwners(m, *expectedOwner); err != nil {
		return options{}, err
	}
	if o.pipe == "" {
		sid, err := winsec.CurrentUserSID()
		if err != nil {
			return options{}, err
		}
		o.pipe = pipe.HostTransportPipeName(sid)
	}
	if err := pipe.ValidateName(o.pipe); err != nil {
		return options{}, err
	}
	return o, nil
}

// authzOwners returns the accounts allowed to own the Agent's pipe (§11): the
// configured SID, else SYSTEM (the Agent's service account), else in fake
// mode the current user, who runs the development Agent.
//
// Objects a LocalSystem process creates without an explicit owner can be
// owned by BUILTIN\Administrators, the SYSTEM token's default owner, so that
// group is accepted alongside SYSTEM. Only SYSTEM or an elevated administrator
// can create a pipe owned by either, and both already control the machine.
func authzOwners(mode netpolicy.Mode, configured string) ([]string, error) {
	var sid string
	switch {
	case configured != "":
		canonical, err := winsec.ParseSID(configured)
		if err != nil {
			return nil, fmt.Errorf("--expected-authz-owner: %w", err)
		}
		sid = canonical
	case mode == netpolicy.Fake:
		self, err := winsec.CurrentUserSID()
		if err != nil {
			return nil, err
		}
		sid = self
	default:
		sid = winsec.SystemSID
	}
	if sid == winsec.SystemSID {
		return []string{winsec.SystemSID, winsec.AdministratorsSID}, nil
	}
	return []string{sid}, nil
}

func run(log *redact.Logger, args []string) error {
	o, err := parseFlags(args)
	if err != nil {
		return err
	}
	if err := sidecar.CheckBuild(o.mode); err != nil {
		return err
	}
	nodes, err := newNodes(o, log)
	if err != nil {
		return err
	}
	defer nodes.Close()
	client, err := authz.New(o.authzPipe, o.authzOwners, log.Printf)
	if err != nil {
		return err
	}
	defer client.Close()
	br := bridge.New(bridge.Config{Authorizer: client, Logf: log.Printf})
	svc := &host.Service{
		Mode:      o.mode,
		Version:   version,
		Nodes:     nodes,
		Bridge:    br,
		Listen:    o.bridgeListen,
		AuthzPipe: o.authzPipe,
		Log:       log,
	}

	ctx, cancel := sidecar.SignalContext()
	defer cancel()
	log.Printf("connect-host-transport %s starting in %s mode; Agent pipe %s must be owned by %v", version, o.mode, o.authzPipe, o.authzOwners)
	if o.mode == netpolicy.Fake {
		log.Printf("connect-host-transport: FAKE MODE: loopback stands in for the tailnet and peer node ids are self-asserted; not a security boundary")
	}
	go client.Subscribe(ctx, br)
	if err := svc.Start(ctx); err != nil {
		return err
	}
	err = sidecar.ServePipe(ctx, o.pipe, svc.Handle, log)
	if errors.Is(err, context.Canceled) {
		log.Printf("connect-host-transport: stopping")
		return nil
	}
	return err
}

func newNodes(o options, log *redact.Logger) (transport.NodeSet, error) {
	if o.mode == netpolicy.Fake {
		return transport.NewFakeNodes(fakeHostNodeID)
	}
	return transport.NewTsnetNodes(o.stateDir, log.Printf, log.Verbose)
}
