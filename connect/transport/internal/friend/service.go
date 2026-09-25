package friend

import (
	"context"
	"errors"

	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/redact"
	"1salem.app/connect/transport/internal/ticket"
	"1salem.app/connect/transport/internal/transport"
)

// ProtocolVersion is the "v" of the friend pipe's hello.
const ProtocolVersion = 1

// Error codes of the friend pipe, beyond the generic pipe codes.
const (
	CodeVersion        = "unsupported_version"
	CodeBadNode        = "bad_node"
	CodeBadHostname    = "bad_hostname"
	CodeBadAuthKey     = "bad_auth_key"
	CodeAlreadyEnroll  = "already_enrolled"
	CodeNotEnrolled    = "not_enrolled"
	CodeEnrollFailed   = "enroll_failed"
	CodeTicket         = "ticket_rejected"
	CodeSessionKey     = "session_key_rejected"
	CodeTicketMismatch = "ticket_mismatch"
	CodeBadPort        = "bad_port"
	CodeNoFreePort     = "no_free_port"
	CodeNoSession      = "no_session"
	CodeShuttingDown   = "shutting_down"
)

// Service serves the friend pipe (§11 "Friend UI ↔ friend transport"). No
// request type has a destination field, and strict decoding refuses any field
// a request does not define, so there is no way to name an address to dial.
type Service struct {
	Mode    netpolicy.Mode
	Version string
	Keys    *ticket.Keyset
	Nodes   transport.NodeSet
	Manager *Manager
	Log     *redact.Logger
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

type statusResp struct {
	Nodes    []transport.NodeInfo `json:"nodes"`
	Sessions []SessionInfo        `json:"sessions"`
}

type enrollReq struct {
	pipe.Envelope
	Node     string `json:"node"`
	AuthKey  string `json:"authKey"`
	Hostname string `json:"hostname"`
}

type enrollResp struct {
	NodeID string `json:"nodeId"`
}

type openReq struct {
	pipe.Envelope
	Node          string `json:"node"`
	Ticket        string `json:"ticket"`
	SessionKey    string `json:"sessionKey"`
	PreferredPort int    `json:"preferredPort"`
}

type refreshReq struct {
	pipe.Envelope
	SessionID  string `json:"sessionId"`
	Ticket     string `json:"ticket"`
	SessionKey string `json:"sessionKey"`
}

type closeReq struct {
	pipe.Envelope
	SessionID string `json:"sessionId"`
}

type diagResp struct {
	Mode     string               `json:"mode"`
	Version  string               `json:"version"`
	Kids     []string             `json:"kids"`
	Nodes    []transport.NodeInfo `json:"nodes"`
	Sessions []SessionDiag        `json:"sessions"`
	Log      []string             `json:"log"`
}

// Handle implements pipe.Handler.
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
		return statusResp{Nodes: nonNil(s.Nodes.List()), Sessions: nonNil(s.Manager.Status())}, nil

	case "enroll":
		var req enrollReq
		if err := pipe.DecodeRequest(line, &req); err != nil {
			return nil, err
		}
		// Checked here as well as in the node set, so the rule holds whatever
		// NodeSet is plugged in: only a one-off "tskey-auth-" key is accepted,
		// never an OAuth client secret (§11).
		if err := transport.ValidateAuthKey(req.AuthKey); err != nil {
			return nil, pipe.Fail(CodeBadAuthKey)
		}
		// The key is used for this call only. The pipe server wipes the
		// request line and the node set drops its reference once tsnet has
		// consumed it; the decoded string itself cannot be zeroed in Go.
		nodeID, err := s.Nodes.Enroll(ctx, req.Node, req.AuthKey, req.Hostname)
		if err != nil {
			return nil, mapError(err)
		}
		return enrollResp{NodeID: nodeID}, nil

	case "open":
		var req openReq
		if err := pipe.DecodeRequest(line, &req); err != nil {
			return nil, err
		}
		info, err := s.Manager.Open(ctx, req.Node, req.Ticket, req.SessionKey, req.PreferredPort)
		if err != nil {
			return nil, mapError(err)
		}
		return struct {
			SessionID string `json:"sessionId"`
			Local     string `json:"local"`
		}{info.SessionID, info.Local}, nil

	case "refresh":
		var req refreshReq
		if err := pipe.DecodeRequest(line, &req); err != nil {
			return nil, err
		}
		return nil, mapError(s.Manager.Refresh(req.SessionID, req.Ticket, req.SessionKey))

	case "close":
		var req closeReq
		if err := pipe.DecodeRequest(line, &req); err != nil {
			return nil, err
		}
		return nil, mapError(s.Manager.Close(req.SessionID))

	case "diag":
		if err := pipe.DecodeRequest(line, &emptyReq{}); err != nil {
			return nil, err
		}
		d := diagResp{
			Mode:     s.Mode.String(),
			Version:  s.Version,
			Kids:     s.Keys.Kids(),
			Nodes:    nonNil(s.Nodes.List()),
			Sessions: nonNil(s.Manager.Diag()),
			Log:      []string{},
		}
		if s.Log != nil {
			d.Log = s.Log.RecentWithin(pipe.DiagLogBytes)
		}
		return d, nil
	}
	return nil, pipe.Fail(pipe.CodeUnknownOp)
}

// mapError turns a known failure into its pipe code. Anything else becomes
// "internal"; its detail is logged locally by the pipe server.
func mapError(err error) error {
	if err == nil {
		return nil
	}
	codes := []struct {
		err  error
		code string
	}{
		{transport.ErrInvalidNode, CodeBadNode},
		{transport.ErrInvalidHostname, CodeBadHostname},
		{transport.ErrInvalidAuthKey, CodeBadAuthKey},
		{transport.ErrAlreadyEnrolled, CodeAlreadyEnroll},
		{transport.ErrNotEnrolled, CodeNotEnrolled},
		{transport.ErrEnrollFailed, CodeEnrollFailed},
		{ErrTicket, CodeTicket},
		{ErrSessionKey, CodeSessionKey},
		{ErrTicketMismatch, CodeTicketMismatch},
		{ErrPort, CodeBadPort},
		{ErrNoFreePort, CodeNoFreePort},
		{ErrNoSession, CodeNoSession},
		{ErrClosed, CodeShuttingDown},
	}
	for _, c := range codes {
		if errors.Is(err, c.err) {
			return pipe.Fail(c.code)
		}
	}
	return err
}

// nonNil makes empty lists encode as [] rather than null.
func nonNil[T any](s []T) []T {
	if s == nil {
		return []T{}
	}
	return s
}
