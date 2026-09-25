package pipe

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"strconv"
	"sync"
	"time"

	"1salem.app/connect/transport/internal/strictjson"
)

// Error codes shared by every pipe server. Handlers add their own.
const (
	CodeBadRequest  = "bad_request"
	CodeUnknownOp   = "unknown_op"
	CodeLineTooLong = "line_too_long"
	CodeInternal    = "internal"
)

// Error is a failure reported to the client as {"ok":false,"error":Code}.
type Error struct {
	Code string
}

func (e *Error) Error() string { return "pipe: " + e.Code }

// Fail returns an *Error with the given code.
func Fail(code string) error { return &Error{Code: code} }

// Envelope is embedded in every request struct so that strict decoding of a
// whole request line accepts "id" and "op" and still refuses any other
// field the operation does not define. That is what stops a client from
// smuggling, say, a destination into an operation that has none.
type Envelope struct {
	ID int64  `json:"id"`
	Op string `json:"op"`
}

// DecodeRequest strictly decodes a full request line into v.
func DecodeRequest(line []byte, v any) error {
	if err := strictjson.Decode(line, v); err != nil {
		return Fail(CodeBadRequest)
	}
	return nil
}

// Handler serves one request. line is the whole request line; it is only
// valid during the call. The result is marshalled as the response fields and
// must be a struct, a pointer to one, or nil for an empty response.
type Handler func(ctx context.Context, op string, line []byte) (any, error)

// defaultMaxConns bounds concurrent clients. The UI or the Agent needs one
// or two; the limit only stops a runaway local client from exhausting us.
const defaultMaxConns = 8

// Server serves the JSON-lines protocol on a listener.
type Server struct {
	Handler  Handler
	Logf     func(format string, args ...any)
	MaxConns int
}

// Serve accepts connections until ctx is done or the listener fails, and
// closes the listener and every connection when ctx is done.
func (s *Server) Serve(ctx context.Context, ln net.Listener) error {
	maxConns := s.MaxConns
	if maxConns <= 0 {
		maxConns = defaultMaxConns
	}
	sem := make(chan struct{}, maxConns)
	stop := context.AfterFunc(ctx, func() { ln.Close() })
	defer stop()
	var wg sync.WaitGroup
	defer wg.Wait()
	for {
		conn, err := ln.Accept()
		if err != nil {
			if ctx.Err() != nil || errors.Is(err, net.ErrClosed) {
				return ctx.Err()
			}
			s.logf("pipe: accept failed: %v", err)
			time.Sleep(50 * time.Millisecond)
			continue
		}
		select {
		case sem <- struct{}{}:
		default:
			s.logf("pipe: refusing client, %d connections already open", maxConns)
			conn.Close()
			continue
		}
		wg.Add(1)
		go func() {
			defer wg.Done()
			defer func() { <-sem }()
			s.serveConn(ctx, conn)
		}()
	}
}

func (s *Server) serveConn(ctx context.Context, conn net.Conn) {
	defer conn.Close()
	stop := context.AfterFunc(ctx, func() { conn.Close() })
	defer stop()

	sc := bufio.NewScanner(conn)
	sc.Buffer(make([]byte, 0, 4096), MaxLine+1)
	for sc.Scan() {
		line := sc.Bytes()
		resp, keepOpen := s.handleLine(ctx, line)
		// Request lines can carry an auth key or a session key. Wipe our
		// copy as soon as the handler is done with it.
		clear(line)
		if _, err := conn.Write(resp); err != nil || !keepOpen {
			return
		}
	}
	if errors.Is(sc.Err(), bufio.ErrTooLong) {
		conn.Write(errorLine(nil, CodeLineTooLong))
	}
}

// handleLine returns the response line and whether the connection stays open.
// A line without a usable id cannot be answered correctly, so the connection
// is closed after a generic error.
func (s *Server) handleLine(ctx context.Context, line []byte) ([]byte, bool) {
	var env struct {
		ID *int64 `json:"id"`
		Op string `json:"op"`
	}
	if err := strictjson.DecodeAllowUnknown(line, &env); err != nil || env.ID == nil || env.Op == "" {
		return errorLine(nil, CodeBadRequest), false
	}
	result, err := s.call(ctx, env.Op, line)
	if err != nil {
		var pe *Error
		if errors.As(err, &pe) {
			return errorLine(env.ID, pe.Code), true
		}
		s.logf("pipe: %s failed: %v", env.Op, err)
		return errorLine(env.ID, CodeInternal), true
	}
	resp, err := okLine(*env.ID, result)
	if err != nil {
		s.logf("pipe: %s produced an unusable response: %v", env.Op, err)
		return errorLine(env.ID, CodeInternal), true
	}
	return resp, true
}

// call runs the handler and turns a panic into an internal error, so one bad
// request cannot take the sidecar and every live session down with it.
func (s *Server) call(ctx context.Context, op string, line []byte) (result any, err error) {
	defer func() {
		if r := recover(); r != nil {
			s.logf("pipe: %s panicked: %v", op, r)
			result, err = nil, Fail(CodeInternal)
		}
	}()
	return s.Handler(ctx, op, line)
}

func (s *Server) logf(format string, args ...any) {
	if s.Logf != nil {
		s.Logf(format, args...)
	}
}

func okLine(id int64, result any) ([]byte, error) {
	body := []byte("{}")
	if result != nil {
		var err error
		if body, err = json.Marshal(result); err != nil {
			return nil, err
		}
		if len(body) < 2 || body[0] != '{' {
			return nil, errors.New("result is not a JSON object")
		}
	}
	out := make([]byte, 0, len(body)+32)
	out = append(out, `{"id":`...)
	out = strconv.AppendInt(out, id, 10)
	out = append(out, `,"ok":true`...)
	if len(body) > 2 {
		out = append(out, ',')
		out = append(out, body[1:]...)
	} else {
		out = append(out, '}')
	}
	if len(out) > MaxLine {
		return nil, fmt.Errorf("response of %d bytes exceeds the line limit", len(out))
	}
	return append(out, '\n'), nil
}

func errorLine(id *int64, code string) []byte {
	codeJSON, _ := json.Marshal(code)
	out := []byte{'{'}
	if id != nil {
		out = append(out, `"id":`...)
		out = strconv.AppendInt(out, *id, 10)
		out = append(out, ',')
	}
	out = append(out, `"ok":false,"error":`...)
	out = append(out, codeJSON...)
	return append(out, '}', '\n')
}
