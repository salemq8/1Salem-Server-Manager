package pipe

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"net"
	"strconv"
	"sync"
	"time"

	"1salem.app/connect/transport/internal/strictjson"
)

// RemoteError is an {"ok":false,"error":code} response.
type RemoteError struct {
	Code string
}

func (e *RemoteError) Error() string { return "pipe: remote error " + e.Code }

// ErrClosed is returned by calls on a client whose connection has ended.
var ErrClosed = errors.New("pipe: connection closed")

// Client speaks the JSON-lines protocol over one connection. Calls may be
// concurrent; responses are matched to requests by id. Lines carrying an
// "event" field (the Agent's subscribe stream) are delivered on Events.
type Client struct {
	conn net.Conn

	wmu sync.Mutex // serializes writes

	mu      sync.Mutex
	nextID  int64
	pending map[int64]chan []byte
	late    func(line []byte)

	events    chan []byte
	closed    chan struct{} // closed by Close
	closeOnce sync.Once
	done      chan struct{} // closed when the read loop ends
}

// NewClient wraps an established connection.
func NewClient(conn net.Conn) *Client {
	c := &Client{
		conn:    conn,
		pending: make(map[int64]chan []byte),
		events:  make(chan []byte, 16),
		closed:  make(chan struct{}),
		done:    make(chan struct{}),
	}
	go c.readLoop()
	return c
}

// Events delivers event lines. Only a subscribed connection receives any.
func (c *Client) Events() <-chan []byte { return c.events }

// Done is closed when the connection has ended.
func (c *Client) Done() <-chan struct{} { return c.done }

// HandleLateResponses passes fn every response that no call is waiting for:
// mostly answers that arrived after their call's context ended. Without it
// they are dropped, and a late answer can matter, for example an allow the
// caller no longer uses. fn runs on the read loop, so it must not block.
func (c *Client) HandleLateResponses(fn func(line []byte)) {
	c.mu.Lock()
	c.late = fn
	c.mu.Unlock()
}

// Close ends the connection.
func (c *Client) Close() error {
	c.closeOnce.Do(func() {
		close(c.closed)
		c.conn.Close()
	})
	return nil
}

// Call sends one request and decodes the response fields into resp (which
// may be nil). req must marshal to a JSON object or be nil.
func (c *Client) Call(ctx context.Context, op string, req any, resp any) error {
	select {
	case <-c.done:
		return ErrClosed
	default:
	}
	c.mu.Lock()
	c.nextID++
	id := c.nextID
	ch := make(chan []byte, 1)
	c.pending[id] = ch
	c.mu.Unlock()

	line, err := encodeRequest(id, op, req)
	if err != nil {
		c.stopWaiting(id)
		return err
	}
	c.wmu.Lock()
	if deadline, ok := ctx.Deadline(); ok {
		c.conn.SetWriteDeadline(deadline)
	}
	_, err = c.conn.Write(line)
	c.conn.SetWriteDeadline(time.Time{})
	c.wmu.Unlock()
	clear(line)
	if err != nil {
		c.stopWaiting(id)
		c.Close()
		return err
	}

	select {
	case respLine := <-ch:
		return DecodeResponse(respLine, resp)
	case <-c.done:
		err = ErrClosed
	case <-ctx.Done():
		err = ctx.Err()
	}
	// A response delivered just as the call gave up is used, not lost; one
	// that arrives after this point goes to the late handler.
	if !c.stopWaiting(id) {
		return DecodeResponse(<-ch, resp)
	}
	return err
}

// stopWaiting removes the waiter for id and reports whether it was still
// there. It is gone once the read loop delivered the response, which is then
// in the call's channel.
func (c *Client) stopWaiting(id int64) bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	_, waiting := c.pending[id]
	delete(c.pending, id)
	return waiting
}

// deliver hands a response to the call waiting for it. Taking the waiter out
// in the same critical section as the send means every response goes either
// to its call or, returned here, to the late handler, never to neither.
func (c *Client) deliver(id int64, line []byte) (late func(line []byte)) {
	c.mu.Lock()
	defer c.mu.Unlock()
	ch, waiting := c.pending[id]
	if !waiting {
		return c.late
	}
	delete(c.pending, id)
	ch <- line // buffered, and this is the only send for id
	return nil
}

func (c *Client) readLoop() {
	defer close(c.done)
	sc := bufio.NewScanner(c.conn)
	sc.Buffer(make([]byte, 0, 4096), MaxLine+1)
	for sc.Scan() {
		line := append([]byte(nil), sc.Bytes()...)
		var env struct {
			ID    *int64 `json:"id"`
			Event string `json:"event"`
		}
		if err := strictjson.DecodeAllowUnknown(line, &env); err != nil {
			// A server that sends garbage is not one we keep talking to.
			c.conn.Close()
			return
		}
		switch {
		case env.Event != "":
			select {
			case c.events <- line:
			case <-c.closed:
				return
			}
		case env.ID != nil:
			if late := c.deliver(*env.ID, line); late != nil {
				late(line)
			}
		}
	}
}

func encodeRequest(id int64, op string, req any) ([]byte, error) {
	body := []byte("{}")
	if req != nil {
		var err error
		if body, err = json.Marshal(req); err != nil {
			return nil, err
		}
		if len(body) < 2 || body[0] != '{' {
			return nil, errors.New("pipe: request is not a JSON object")
		}
	}
	opJSON, err := json.Marshal(op)
	if err != nil {
		return nil, err
	}
	out := make([]byte, 0, len(body)+len(opJSON)+32)
	out = append(out, `{"id":`...)
	out = strconv.AppendInt(out, id, 10)
	out = append(out, `,"op":`...)
	out = append(out, opJSON...)
	if len(body) > 2 {
		out = append(out, ',')
		out = append(out, body[1:]...)
	} else {
		out = append(out, '}')
	}
	clear(body)
	if len(out) > MaxLine {
		return nil, errors.New("pipe: request exceeds the line limit")
	}
	return append(out, '\n'), nil
}

// DecodeResponse turns a response line into an error for {"ok":false} and
// otherwise decodes its fields into resp (which may be nil).
func DecodeResponse(line []byte, resp any) error {
	var env struct {
		OK    *bool  `json:"ok"`
		Error string `json:"error"`
	}
	if err := strictjson.DecodeAllowUnknown(line, &env); err != nil || env.OK == nil {
		return errors.New("pipe: malformed response")
	}
	if !*env.OK {
		code := env.Error
		if code == "" {
			code = "unknown"
		}
		return &RemoteError{Code: code}
	}
	if resp == nil {
		return nil
	}
	return strictjson.DecodeAllowUnknown(line, resp)
}
