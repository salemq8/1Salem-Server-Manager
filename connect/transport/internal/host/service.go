// Package host runs the host transport: the single host node, the bridge on
// top of it, and the control pipe the Agent uses to enroll the node and read
// status (CONNECT_ARCHITECTURE.md §7, §11).
package host

import (
	"context"
	"errors"
	"sync"

	"1salem.app/connect/transport/internal/bridge"
	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/redact"
	"1salem.app/connect/transport/internal/transport"
)

// NodeName is the host's only node. It names the state directory
// <state-dir>\nodes\host in tsnet mode.
const NodeName = "host"

// ProtocolVersion is the "v" of the host control pipe's hello.
const ProtocolVersion = 1

// Error codes of the host control pipe, beyond the generic pipe codes.
const (
	CodeVersion       = "unsupported_version"
	CodeBadHostname   = "bad_hostname"
	CodeBadAuthKey    = "bad_auth_key"
	CodeAlreadyEnroll = "already_enrolled"
	CodeEnrollFailed  = "enroll_failed"
)

// Bridge states reported by status.
const (
	StateWaitingForEnroll = "waiting-for-enroll"
	StateRunning          = "running"
	StateStopped          = "stopped"
	StateFailed           = "failed"
)

// Service owns the host node and its bridge.
type Service struct {
	Mode      netpolicy.Mode
	Version   string
	Nodes     transport.NodeSet
	Bridge    *bridge.Bridge
	Listen    string
	AuthzPipe string
	Log       *redact.Logger

	mu    sync.Mutex
	ctx   context.Context // the service lifetime, set by Start
	state string
}

// Start records ctx as the service lifetime and brings the bridge up when the
// node is available: always in fake mode, and in tsnet mode once the node has
// been enrolled. Otherwise the bridge waits for the enroll operation.
func (s *Service) Start(ctx context.Context) error {
	s.mu.Lock()
	s.ctx = ctx
	s.mu.Unlock()
	return s.startBridge()
}

func (s *Service) startBridge() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	ctx := s.ctx
	if ctx == nil {
		return errors.New("host: service not started")
	}
	if s.state == StateRunning {
		return nil
	}
	tr, err := s.Nodes.Get(ctx, NodeName)
	if errors.Is(err, transport.ErrNotEnrolled) {
		s.state = StateWaitingForEnroll
		s.logf("host: node not enrolled yet; the bridge starts after enroll")
		return nil
	}
	if err != nil {
		s.state = StateFailed
		return err
	}
	s.state = StateRunning
	go func() {
		err := s.Bridge.Serve(ctx, tr, s.Listen)
		s.mu.Lock()
		defer s.mu.Unlock()
		if ctx.Err() != nil {
			s.state = StateStopped
			return
		}
		s.state = StateFailed
		s.logf("host: bridge stopped: %v", err)
	}()
	return nil
}

func (s *Service) logf(format string, args ...any) {
	if s.Log != nil {
		s.Log.Printf(format, args...)
	}
}

func (s *Service) bridgeState() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.state
}

type helloReq struct {
	pipe.Envelope
	V int `json:"v"`
}

type helloResp struct {
	V       int    `json:"v"`
	Mode    string `json:"mode"`
	Version string `json:"version"`
}

type emptyReq struct {
	pipe.Envelope
}

// enrollReq has no node field: the host has exactly one node.
type enrollReq struct {
	pipe.Envelope
	AuthKey  string `json:"authKey"`
	Hostname string `json:"hostname"`
}

type enrollResp struct {
	NodeID string `json:"nodeId"`
}

type bridgeInfo struct {
	State string `json:"state"`
	bridge.Status
}

type statusResp struct {
	Nodes  []transport.NodeInfo `json:"nodes"`
	Bridge bridgeInfo           `json:"bridge"`
}

type diagResp struct {
	Mode      string               `json:"mode"`
	Version   string               `json:"version"`
	AuthzPipe string               `json:"authzPipe"`
	Nodes     []transport.NodeInfo `json:"nodes"`
	Bridge    bridgeInfo           `json:"bridge"`
	Log       []string             `json:"log"`
}

// Handle implements pipe.Handler for the host control pipe.
func (s *Service) Handle(ctx context.Context, op string, line []byte) (any, error) {
	switch op {
	case "hello":
		var req helloReq
		if err := pipe.DecodeRequest(line, &req); err != nil {
			return nil, err
		}
		if req.V != ProtocolVersion {
			return nil, pipe.Fail(CodeVersion)
		}
		return helloResp{V: ProtocolVersion, Mode: s.Mode.String(), Version: s.Version}, nil

	case "status":
		if err := pipe.DecodeRequest(line, &emptyReq{}); err != nil {
			return nil, err
		}
		return statusResp{Nodes: nonNil(s.Nodes.List()), Bridge: s.bridgeInfo()}, nil

	case "enroll":
		var req enrollReq
		if err := pipe.DecodeRequest(line, &req); err != nil {
			return nil, err
		}
		// Only a one-off "tskey-auth-" key, never an OAuth client secret (§11).
		if err := transport.ValidateAuthKey(req.AuthKey); err != nil {
			return nil, pipe.Fail(CodeBadAuthKey)
		}
		nodeID, err := s.Nodes.Enroll(ctx, NodeName, req.AuthKey, req.Hostname)
		if err != nil {
			return nil, mapError(err)
		}
		if err := s.startBridge(); err != nil {
			s.logf("host: bridge did not start after enroll: %v", err)
		}
		return enrollResp{NodeID: nodeID}, nil

	case "diag":
		if err := pipe.DecodeRequest(line, &emptyReq{}); err != nil {
			return nil, err
		}
		d := diagResp{
			Mode:      s.Mode.String(),
			Version:   s.Version,
			AuthzPipe: s.AuthzPipe,
			Nodes:     nonNil(s.Nodes.List()),
			Bridge:    s.bridgeInfo(),
			Log:       []string{},
		}
		if s.Log != nil {
			d.Log = s.Log.RecentWithin(pipe.DiagLogBytes)
		}
		return d, nil
	}
	return nil, pipe.Fail(pipe.CodeUnknownOp)
}

func (s *Service) bridgeInfo() bridgeInfo {
	return bridgeInfo{State: s.bridgeState(), Status: s.Bridge.Status()}
}

func mapError(err error) error {
	switch {
	case errors.Is(err, transport.ErrInvalidHostname):
		return pipe.Fail(CodeBadHostname)
	case errors.Is(err, transport.ErrInvalidAuthKey):
		return pipe.Fail(CodeBadAuthKey)
	case errors.Is(err, transport.ErrAlreadyEnrolled):
		return pipe.Fail(CodeAlreadyEnroll)
	case errors.Is(err, transport.ErrEnrollFailed):
		return pipe.Fail(CodeEnrollFailed)
	}
	return err
}

func nonNil[T any](s []T) []T {
	if s == nil {
		return []T{}
	}
	return s
}
