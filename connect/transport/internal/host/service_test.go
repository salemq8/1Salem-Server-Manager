package host_test

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"strings"
	"testing"
	"time"

	"1salem.app/connect/transport/internal/bridge"
	"1salem.app/connect/transport/internal/host"
	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/redact"
	"1salem.app/connect/transport/internal/transport"
)

type denyAll struct{}

func (denyAll) Authorize(context.Context, bridge.AuthorizeRequest) (bridge.Decision, error) {
	return bridge.Decision{}, nil
}
func (denyAll) Closed(context.Context, bridge.ClosedReport) error { return nil }

func handle(t *testing.T, s *host.Service, line string) (map[string]any, string) {
	t.Helper()
	var env struct {
		Op string `json:"op"`
	}
	json.Unmarshal([]byte(line), &env)
	res, err := s.Handle(context.Background(), env.Op, []byte(line))
	if err != nil {
		var pe *pipe.Error
		if errors.As(err, &pe) {
			return nil, pe.Code
		}
		return nil, "internal:" + err.Error()
	}
	out := map[string]any{}
	b, _ := json.Marshal(res)
	json.Unmarshal(b, &out)
	return out, ""
}

// A full log ring of long lines must not make the diag response too large
// for one pipe line, or the Agent would get "internal" instead of diagnostics.
func TestDiagFitsInOnePipeLine(t *testing.T) {
	nodes, err := transport.NewFakeNodes("fake-host")
	if err != nil {
		t.Fatal(err)
	}
	s := &host.Service{
		Mode:    netpolicy.Fake,
		Version: "test",
		Nodes:   nodes,
		Bridge:  bridge.New(bridge.Config{Authorizer: denyAll{}, Logf: t.Logf}),
		Log:     redact.New(nil, 500),
	}
	for i := range 500 {
		s.Log.Verbose("magicsock: %03d <%s>", i, strings.Repeat("x", 200))
	}
	res, err := s.Handle(context.Background(), "diag", []byte(`{"id":1,"op":"diag"}`))
	if err != nil {
		t.Fatal(err)
	}
	body, err := json.Marshal(res)
	if err != nil {
		t.Fatal(err)
	}
	if len(body)+64 > pipe.MaxLine {
		t.Fatalf("diag response is %d bytes; a pipe line holds %d", len(body), pipe.MaxLine)
	}
	var out struct {
		Log []string `json:"log"`
	}
	if err := json.Unmarshal(body, &out); err != nil {
		t.Fatal(err)
	}
	if len(out.Log) == 0 || !strings.Contains(out.Log[len(out.Log)-1], "magicsock: 499 ") {
		t.Fatalf("the newest log lines must be kept; got %d lines", len(out.Log))
	}
}

func TestFakeHostServiceStartsTheBridge(t *testing.T) {
	l, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	listen := l.Addr().String()
	l.Close()
	nodes, err := transport.NewFakeNodes("fake-host")
	if err != nil {
		t.Fatal(err)
	}
	s := &host.Service{
		Mode:    netpolicy.Fake,
		Version: "test",
		Nodes:   nodes,
		Bridge:  bridge.New(bridge.Config{Authorizer: denyAll{}, Logf: t.Logf}),
		Listen:  listen,
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if err := s.Start(ctx); err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(2 * time.Second)
	for {
		res, _ := handle(t, s, `{"id":1,"op":"status"}`)
		b, _ := res["bridge"].(map[string]any)
		if b["state"] == host.StateRunning && b["listen"] == listen {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("bridge not running: %v", res)
		}
		time.Sleep(10 * time.Millisecond)
	}

	for _, key := range []string{"tskey-client-kX-secret", "", "tskey-auth-"} {
		line, _ := json.Marshal(map[string]any{"id": 2, "op": "enroll", "authKey": key, "hostname": "owner-pc"})
		if _, code := handle(t, s, string(line)); code != host.CodeBadAuthKey {
			t.Errorf("authKey %q: code %q", key, code)
		}
	}
	if _, code := handle(t, s, `{"id":3,"op":"enroll","node":"other","authKey":"tskey-auth-kX-y","hostname":"owner-pc"}`); code != pipe.CodeBadRequest {
		t.Errorf("enroll with a node field: code %q", code)
	}
	res, code := handle(t, s, `{"id":4,"op":"enroll","authKey":"tskey-auth-kX-y","hostname":"owner-pc"}`)
	if code != "" || res["nodeId"] != "fake-host" {
		t.Errorf("enroll: %v %q", res, code)
	}
	if res, code := handle(t, s, `{"id":5,"op":"hello","v":1}`); code != "" || res["mode"] != "fake" {
		t.Errorf("hello: %v %q", res, code)
	}
	if _, code := handle(t, s, `{"id":6,"op":"authorize"}`); code != pipe.CodeUnknownOp {
		t.Errorf("the host control pipe must not serve Agent operations: %q", code)
	}
}
