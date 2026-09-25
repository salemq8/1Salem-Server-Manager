//go:build windows

// Package authz is the host transport's client for the Agent's authorization
// pipe, \\.\pipe\1Salem.Connect.HostAuthz.v1 (CONNECT_ARCHITECTURE.md §11):
// hello, authorize, closed and subscribe. The Agent is the pipe server and
// the authority; this client only asks, reports and obeys close events.
//
// Every connection is checked for the expected pipe owner before the first
// byte is written (pipe.Dial), because authorize requests carry tickets and
// proofs, and because an impostor answering "allow" could point friends at
// any loopback port.
package authz

import (
	"context"
	"errors"
	"sync"
	"time"

	"1salem.app/connect/transport/internal/bridge"
	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/strictjson"
)

// ProtocolVersion is the "v" exchanged in hello.
const ProtocolVersion = 1

const (
	// dialTimeout bounds connecting, the owner check and hello.
	dialTimeout = 5 * time.Second
	// lateReportTimeout bounds reporting an allow that came too late.
	lateReportTimeout = 5 * time.Second
	// Reconnect backoff for the subscription.
	minBackoff = 250 * time.Millisecond
	maxBackoff = 10 * time.Second
)

var (
	// ErrVersion is returned when the Agent speaks another protocol version.
	ErrVersion = errors.New("authz: Agent pipe protocol version mismatch")
	// ErrBadResponse is returned for a decision that is neither allow nor deny.
	ErrBadResponse = errors.New("authz: malformed response from the Agent")
	// errStreamEnded is how a subscription that was up ends.
	errStreamEnded = errors.New("authz: subscription stream ended")
)

// Sink receives what the subscription reports. *bridge.Bridge implements it.
type Sink interface {
	// CloseConns ends the listed live connections.
	CloseConns(connIDs []string) int
	// SetRevocationChannel reports whether close events can currently arrive.
	SetRevocationChannel(up bool)
}

// Client talks to the Agent's authorization pipe. It is safe for concurrent
// use; authorize and closed share one connection, the subscription has its
// own, and both are re-established after the Agent restarts.
type Client struct {
	name   string
	owners []string
	logf   func(format string, args ...any)

	mu   sync.Mutex
	conn *pipe.Client
}

// New returns a client for the pipe name that accepts only a pipe owned by
// one of allowedOwners. Nothing is dialed yet.
func New(name string, allowedOwners []string, logf func(format string, args ...any)) (*Client, error) {
	if err := pipe.ValidateName(name); err != nil {
		return nil, err
	}
	if len(allowedOwners) == 0 {
		return nil, pipe.ErrNoExpectedOwner
	}
	if logf == nil {
		logf = func(string, ...any) {}
	}
	return &Client{name: name, owners: append([]string(nil), allowedOwners...), logf: logf}, nil
}

type helloMsg struct {
	V int `json:"v"`
}

// dial connects, verifies the owner and exchanges hello.
func (c *Client) dial(ctx context.Context) (*pipe.Client, error) {
	ctx, cancel := context.WithTimeout(ctx, dialTimeout)
	defer cancel()
	pc, err := pipe.Dial(ctx, c.name, c.owners)
	if err != nil {
		return nil, err
	}
	var resp helloMsg
	if err := pc.Call(ctx, "hello", helloMsg{V: ProtocolVersion}, &resp); err != nil {
		pc.Close()
		return nil, err
	}
	if resp.V != ProtocolVersion {
		pc.Close()
		return nil, ErrVersion
	}
	return pc, nil
}

// requestConn returns the shared request connection, redialing it when the
// previous one has ended.
func (c *Client) requestConn(ctx context.Context) (*pipe.Client, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.conn != nil {
		select {
		case <-c.conn.Done():
			c.conn = nil
		default:
			return c.conn, nil
		}
	}
	pc, err := c.dial(ctx)
	if err != nil {
		return nil, err
	}
	pc.HandleLateResponses(c.reportUnusedAllow)
	c.conn = pc
	return pc, nil
}

type decisionMsg struct {
	Decision string `json:"decision"`
	Endpoint string `json:"endpoint"`
	ConnID   string `json:"connId"`
}

// Authorize asks the Agent about one connection. Any failure is an error,
// which the bridge turns into a refusal.
func (c *Client) Authorize(ctx context.Context, req bridge.AuthorizeRequest) (bridge.Decision, error) {
	pc, err := c.requestConn(ctx)
	if err != nil {
		return bridge.Decision{}, err
	}
	var resp decisionMsg
	if err := pc.Call(ctx, "authorize", req, &resp); err != nil {
		return bridge.Decision{}, err
	}
	switch resp.Decision {
	case "allow":
		return bridge.Decision{Allow: true, Endpoint: resp.Endpoint, ConnID: resp.ConnID}, nil
	case "deny":
		return bridge.Decision{}, nil
	}
	return bridge.Decision{}, ErrBadResponse
}

// reportUnusedAllow handles a response that reached the request connection
// after its call stopped waiting. For an authorize past the bridge's timeout
// the bridge has already refused the friend without registering the
// connection, so nothing else would ever report it and the Agent would track
// it until its next subscription loss. §11: an allow the host transport could
// not use is reported closed with no bytes. Only authorize responses carry a
// decision, so every line that is not an allow is ignored. This runs on the
// pipe's read loop; the report goes out from its own goroutine.
func (c *Client) reportUnusedAllow(line []byte) {
	var resp decisionMsg
	if pipe.DecodeResponse(line, &resp) != nil || resp.Decision != "allow" || !bridge.ValidConnID(resp.ConnID) {
		return
	}
	go func() {
		c.logf("authz: the Agent allowed connection %s after the bridge stopped waiting; reporting it closed", resp.ConnID)
		ctx, cancel := context.WithTimeout(context.Background(), lateReportTimeout)
		defer cancel()
		if err := c.Closed(ctx, bridge.ClosedReport{ConnID: resp.ConnID}); err != nil {
			c.logf("authz: could not report unused connection %s closed: %v", resp.ConnID, err)
		}
	}()
}

// Closed reports that an allowed connection ended.
func (c *Client) Closed(ctx context.Context, rep bridge.ClosedReport) error {
	pc, err := c.requestConn(ctx)
	if err != nil {
		return err
	}
	return pc.Call(ctx, "closed", rep, nil)
}

// Close ends the request connection. Subscribe ends with its context.
func (c *Client) Close() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.conn != nil {
		c.conn.Close()
		c.conn = nil
	}
	return nil
}

// Subscribe keeps one subscription open until ctx is done, reconnecting with
// backoff. The sink is told when the stream is up and when it is lost, and
// receives every close event in between.
func (c *Client) Subscribe(ctx context.Context, sink Sink) error {
	backoff := minBackoff
	for {
		err := c.subscribeOnce(ctx, sink)
		sink.SetRevocationChannel(false)
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if errors.Is(err, errStreamEnded) {
			backoff = minBackoff
		}
		c.logf("authz: subscription to the Agent unavailable (%v); retrying in %s", err, backoff)
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(backoff):
		}
		backoff = min(backoff*2, maxBackoff)
	}
}

func (c *Client) subscribeOnce(ctx context.Context, sink Sink) error {
	pc, err := c.dial(ctx)
	if err != nil {
		return err
	}
	defer pc.Close()
	callCtx, cancel := context.WithTimeout(ctx, dialTimeout)
	err = pc.Call(callCtx, "subscribe", nil, nil)
	cancel()
	if err != nil {
		return err
	}
	stop := context.AfterFunc(ctx, func() { pc.Close() })
	defer stop()

	sink.SetRevocationChannel(true)
	c.logf("authz: subscribed to Agent close events")
	for {
		select {
		case line := <-pc.Events():
			ids, ok := parseCloseEvent(line)
			if !ok {
				c.logf("authz: ignoring an event this version does not understand")
				continue
			}
			sink.CloseConns(ids)
		case <-pc.Done():
			return errStreamEnded
		}
	}
}

type eventMsg struct {
	Event   string   `json:"event"`
	ConnIDs []string `json:"connIds"`
}

func parseCloseEvent(line []byte) ([]string, bool) {
	var ev eventMsg
	if err := strictjson.DecodeAllowUnknown(line, &ev); err != nil || ev.Event != "close" {
		return nil, false
	}
	return ev.ConnIDs, true
}
