//go:build windows

package pipe_test

import (
	"bufio"
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"strings"
	"testing"
	"time"
	"unsafe"

	"github.com/Microsoft/go-winio"
	"golang.org/x/sys/windows"

	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/winsec"
)

func testPipeName(t *testing.T) string {
	t.Helper()
	var b [8]byte
	rand.Read(b[:])
	return `\\.\pipe\1Salem.Connect.Test.` + hex.EncodeToString(b[:])
}

func TestSecondListenerOnTheSameNameFails(t *testing.T) {
	name := testPipeName(t)
	ln, err := pipe.Listen(name)
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	if ln2, err := pipe.Listen(name); err == nil {
		ln2.Close()
		t.Fatal("a second server created an instance of an existing pipe name")
	}
}

func TestValidateName(t *testing.T) {
	for _, bad := range []string{
		`\\server\pipe\x`, `\\.\pipe\a\b`, `\\.\pipe\`, `\\?\pipe\x`, `\\.\pipe\x y`, `pipe\x`, `\\.\PIPE\..\x`,
	} {
		if pipe.ValidateName(bad) == nil {
			t.Errorf("%q accepted", bad)
		}
	}
	if err := pipe.ValidateName(pipe.FriendPipeName("S-1-5-21-1-2-3-1001")); err != nil {
		t.Errorf("friend pipe name refused: %v", err)
	}
}

type ace struct {
	typ  byte
	mask uint32
	sid  string
}

func dacl(t *testing.T, h windows.Handle) (aces []ace, protected bool) {
	t.Helper()
	sd, err := windows.GetSecurityInfo(h, windows.SE_KERNEL_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatal(err)
	}
	control, _, err := sd.Control()
	if err != nil {
		t.Fatal(err)
	}
	acl, _, err := sd.DACL()
	if err != nil || acl == nil {
		t.Fatalf("no DACL: %v", err)
	}
	for i := range uint32(acl.AceCount) {
		var a *windows.ACCESS_ALLOWED_ACE
		if err := windows.GetAce(acl, i, &a); err != nil {
			t.Fatal(err)
		}
		sid := (*windows.SID)(unsafe.Pointer(&a.SidStart))
		aces = append(aces, ace{typ: a.Header.AceType, mask: uint32(a.Mask), sid: sid.String()})
	}
	return aces, control&windows.SE_DACL_PROTECTED != 0
}

func TestPipeSecurity(t *testing.T) {
	self, err := winsec.CurrentUserSID()
	if err != nil {
		t.Fatal(err)
	}
	sddl := winsec.PipeSDDL(self)
	for _, forbidden := range []string{";;;WD)", ";;;BU)", ";;;AU)", ";;;IU)", ";;;AN)"} {
		if strings.Contains(sddl, forbidden) {
			t.Fatalf("SDDL %s grants %s", sddl, forbidden)
		}
	}
	if !strings.HasPrefix(sddl, "O:"+self+"D:P(D;;GA;;;NU)") {
		t.Fatalf("SDDL %s does not start with the owner and a protected deny for NU", sddl)
	}

	// Check what Windows actually applied to a live pipe.
	name := testPipeName(t)
	ln, err := pipe.Listen(name)
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	go func() {
		if c, err := ln.Accept(); err == nil {
			defer c.Close()
			time.Sleep(time.Second)
		}
	}()
	conn, err := winio.DialPipe(name, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	h, err := pipe.Handle(conn)
	if err != nil {
		t.Fatal(err)
	}
	owner, err := winsec.HandleOwnerSID(h)
	if err != nil || owner != self {
		t.Fatalf("pipe owner %q (%v), want %s", owner, err, self)
	}
	aces, protected := dacl(t, h)
	if !protected {
		t.Error("DACL is not protected; inherited ACEs could widen it")
	}
	if len(aces) == 0 || aces[0].typ != windows.ACCESS_DENIED_ACE_TYPE || aces[0].sid != "S-1-5-2" || aces[0].mask == 0 {
		t.Fatalf("first ACE %+v, want a deny for NETWORK (S-1-5-2)", aces)
	}
	for _, a := range aces[1:] {
		if a.typ != windows.ACCESS_ALLOWED_ACE_TYPE || (a.sid != self && a.sid != winsec.SystemSID) {
			t.Errorf("unexpected ACE %+v; only the owner and SYSTEM may be granted access", a)
		}
	}
	for _, a := range aces {
		switch a.sid {
		case "S-1-1-0", "S-1-5-32-545", "S-1-5-11", "S-1-5-4", "S-1-5-7":
			t.Errorf("ACE for a broad group: %+v", a)
		}
	}
}

func TestDialVerifiesTheOwnerBeforeWriting(t *testing.T) {
	self, _ := winsec.CurrentUserSID()
	name := testPipeName(t)
	ln, err := pipe.Listen(name)
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	received := make(chan string, 4)
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go func() {
				defer c.Close()
				sc := bufio.NewScanner(c)
				for sc.Scan() {
					received <- sc.Text()
				}
			}()
		}
	}()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	if _, err := pipe.Dial(ctx, name, nil); !errors.Is(err, pipe.ErrNoExpectedOwner) {
		t.Fatalf("no expected owner: err = %v", err)
	}
	if _, err := pipe.Dial(ctx, name, []string{winsec.SystemSID}); !errors.Is(err, winsec.ErrUntrustedOwner) {
		t.Fatalf("pipe owned by %s accepted as SYSTEM's: err = %v", self, err)
	}
	c, err := pipe.Dial(ctx, name, []string{self})
	if err != nil {
		t.Fatalf("pipe owned by the expected account refused: %v", err)
	}
	callCtx, cancelCall := context.WithTimeout(ctx, 200*time.Millisecond)
	c.Call(callCtx, "hello", map[string]int{"v": 1}, nil)
	cancelCall()
	c.Close()

	if got := <-received; !strings.Contains(got, `"op":"hello"`) {
		t.Fatalf("server received %q", got)
	}
	select {
	case extra := <-received:
		t.Fatalf("the rejected connection sent %q", extra)
	case <-time.After(100 * time.Millisecond):
	}
}

// A response that arrives after its call gave up goes to the late handler
// instead of being dropped; a response in time goes to its call only.
func TestLateResponsesReachTheHandler(t *testing.T) {
	self, _ := winsec.CurrentUserSID()
	name := testPipeName(t)
	ln, err := pipe.Listen(name)
	if err != nil {
		t.Fatal(err)
	}
	release := make(chan struct{})
	srv := &pipe.Server{Logf: t.Logf, Handler: func(ctx context.Context, op string, line []byte) (any, error) {
		if op == "slow" {
			<-release
		}
		return map[string]string{"op": op}, nil
	}}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go srv.Serve(ctx, ln)

	c, err := pipe.Dial(ctx, name, []string{self})
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	late := make(chan []byte, 4)
	c.HandleLateResponses(func(line []byte) { late <- line })

	callCtx, cancelCall := context.WithTimeout(ctx, 100*time.Millisecond)
	err = c.Call(callCtx, "slow", nil, nil)
	cancelCall()
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("slow call: %v", err)
	}
	close(release)
	var resp struct {
		Op string `json:"op"`
	}
	select {
	case line := <-late:
		if err := pipe.DecodeResponse(line, &resp); err != nil || resp.Op != "slow" {
			t.Fatalf("late response %s: %v", line, err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("the late response was dropped")
	}

	if err := c.Call(ctx, "fast", nil, &resp); err != nil || resp.Op != "fast" {
		t.Fatalf("fast call: %q, %v", resp.Op, err)
	}
	select {
	case line := <-late:
		t.Fatalf("a response its call received also went to the late handler: %s", line)
	default:
	}
}

func TestServerRoundTrip(t *testing.T) {
	self, _ := winsec.CurrentUserSID()
	name := testPipeName(t)
	ln, err := pipe.Listen(name)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	srv := &pipe.Server{Logf: t.Logf, Handler: func(ctx context.Context, op string, line []byte) (any, error) {
		switch op {
		case "echo":
			var req struct {
				pipe.Envelope
				Text string `json:"text"`
			}
			if err := pipe.DecodeRequest(line, &req); err != nil {
				return nil, err
			}
			return map[string]string{"text": req.Text}, nil
		case "boom":
			panic("handler bug")
		}
		return nil, pipe.Fail(pipe.CodeUnknownOp)
	}}
	go srv.Serve(ctx, ln)

	c, err := pipe.Dial(ctx, name, []string{self})
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	var resp struct {
		Text string `json:"text"`
	}
	if err := c.Call(ctx, "echo", map[string]string{"text": "hi"}, &resp); err != nil || resp.Text != "hi" {
		t.Fatalf("echo: %q, %v", resp.Text, err)
	}
	var re *pipe.RemoteError
	if err := c.Call(ctx, "echo", map[string]any{"text": "hi", "extra": 1}, nil); !errors.As(err, &re) || re.Code != pipe.CodeBadRequest {
		t.Fatalf("unknown field: %v", err)
	}
	if err := c.Call(ctx, "boom", nil, nil); !errors.As(err, &re) || re.Code != pipe.CodeInternal {
		t.Fatalf("panic: %v", err)
	}
	if err := c.Call(ctx, "nope", nil, nil); !errors.As(err, &re) || re.Code != pipe.CodeUnknownOp {
		t.Fatalf("unknown op: %v", err)
	}
	// The connection survives handler failures.
	if err := c.Call(ctx, "echo", map[string]string{"text": "again"}, &resp); err != nil || resp.Text != "again" {
		t.Fatalf("after failures: %q, %v", resp.Text, err)
	}
	if err := c.Call(ctx, "echo", map[string]string{"text": strings.Repeat("x", pipe.MaxLine)}, nil); err == nil {
		t.Fatal("an over-long request was sent")
	}
}
