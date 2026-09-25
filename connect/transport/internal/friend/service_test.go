package friend_test

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"testing"

	"1salem.app/connect/transport/internal/friend"
	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/redact"
	"1salem.app/connect/transport/internal/testkit"
	"1salem.app/connect/transport/internal/ticket"
	"1salem.app/connect/transport/internal/transport"
)

// countingNodes fails loudly if Enroll is reached with a key that should
// have been refused earlier.
type countingNodes struct {
	transport.NodeSet
	enrolls int
}

func (c *countingNodes) Enroll(ctx context.Context, node, authKey, hostname string) (string, error) {
	c.enrolls++
	return c.NodeSet.Enroll(ctx, node, authKey, hostname)
}

func newService(t *testing.T) (*friend.Service, *countingNodes) {
	t.Helper()
	signer := testkit.NewSigner(t, "k1")
	ks, err := ticket.ParseKeyset(testkit.KeysetJSON(t, signer))
	if err != nil {
		t.Fatal(err)
	}
	fake, err := transport.NewFakeNodes(friendNodeID)
	if err != nil {
		t.Fatal(err)
	}
	nodes := &countingNodes{NodeSet: fake}
	mgr := friend.NewManager(friend.Config{Verifier: &ticket.Verifier{Keys: ks, Mode: netpolicy.Fake}, Nodes: nodes, Logf: t.Logf})
	t.Cleanup(mgr.CloseAll)
	return &friend.Service{Mode: netpolicy.Fake, Version: "test", Keys: ks, Nodes: nodes, Manager: mgr}, nodes
}

func call(t *testing.T, s *friend.Service, line string) (map[string]any, string) {
	t.Helper()
	var env struct {
		Op string `json:"op"`
	}
	if err := json.Unmarshal([]byte(line), &env); err != nil {
		t.Fatalf("test line %s: %v", line, err)
	}
	res, err := s.Handle(context.Background(), env.Op, []byte(line))
	if err != nil {
		var pe *pipe.Error
		if !errors.As(err, &pe) {
			return nil, "internal:" + err.Error()
		}
		return nil, pe.Code
	}
	out := map[string]any{}
	if res != nil {
		b, _ := json.Marshal(res)
		json.Unmarshal(b, &out)
	}
	return out, ""
}

func TestEnrollAcceptsOnlyAuthKeys(t *testing.T) {
	s, nodes := newService(t)
	refused := []string{
		"tskey-client-kXXXXXXCNTRL-secretsecretsecret",
		"tskey-client-kXXXXXXCNTRL-secret?ephemeral=false&preauthorized=true",
		"tskey-kXXXXXXCNTRL-legacy",
		"tskey-auth-",
		"tskey-auth-bad key",
		"TSKEY-AUTH-kXXXX",
		" tskey-auth-kXXXX",
		"",
	}
	for _, key := range refused {
		line, _ := json.Marshal(map[string]any{"id": 1, "op": "enroll", "node": "owner1", "authKey": key, "hostname": "friend-pc"})
		if _, code := call(t, s, string(line)); code != friend.CodeBadAuthKey {
			t.Errorf("authKey %q: code %q, want %q", key, code, friend.CodeBadAuthKey)
		}
	}
	if nodes.enrolls != 0 {
		t.Fatalf("a refused key reached the node set %d time(s)", nodes.enrolls)
	}
	res, code := call(t, s, `{"id":2,"op":"enroll","node":"owner1","authKey":"tskey-auth-kXXXXXXCNTRL-abcdef","hostname":"friend-pc"}`)
	if code != "" || res["nodeId"] != friendNodeID {
		t.Fatalf("valid auth key: %v %q", res, code)
	}
}

func TestNoOperationTakesADestination(t *testing.T) {
	s, _ := newService(t)
	// Every field that could name somewhere to connect is refused outright
	// by strict decoding, before any operation logic runs.
	lines := []string{
		`{"id":1,"op":"open","node":"owner1","ticket":"a.b.c","sessionKey":"x","preferredPort":0,"destination":"10.0.0.1:25565"}`,
		`{"id":1,"op":"open","node":"owner1","ticket":"a.b.c","sessionKey":"x","hb":"100.64.0.1:7780"}`,
		`{"id":1,"op":"open","node":"owner1","ticket":"a.b.c","sessionKey":"x","host":"example.com","port":25565}`,
		`{"id":1,"op":"refresh","sessionId":"s","ticket":"a.b.c","sessionKey":"x","hostBridge":"127.0.0.1:1"}`,
		`{"id":1,"op":"enroll","node":"owner1","authKey":"tskey-auth-k1","hostname":"h","controlUrl":"https://example.invalid"}`,
		`{"id":1,"op":"status","addr":"127.0.0.1:1"}`,
		`{"id":1,"op":"close","sessionId":"s","endpoint":"127.0.0.1:1"}`,
		`{"id":1,"op":"diag","target":"x"}`,
		`{"id":1,"op":"hello","v":1,"dest":"x"}`,
	}
	for _, line := range lines {
		if _, code := call(t, s, line); code != pipe.CodeBadRequest {
			t.Errorf("%s: code %q, want %q", line, code, pipe.CodeBadRequest)
		}
	}
}

func TestHelloStatusDiag(t *testing.T) {
	s, _ := newService(t)
	res, code := call(t, s, `{"id":1,"op":"hello","v":1}`)
	if code != "" || res["v"] != float64(1) || res["mode"] != "fake" || res["version"] != "test" {
		t.Fatalf("hello: %v %q", res, code)
	}
	if _, code := call(t, s, `{"id":1,"op":"hello","v":2}`); code != friend.CodeVersion {
		t.Fatalf("hello v2: code %q", code)
	}
	res, code = call(t, s, `{"id":2,"op":"status"}`)
	if code != "" {
		t.Fatal(code)
	}
	if sessions, ok := res["sessions"].([]any); !ok || len(sessions) != 0 {
		t.Fatalf("status sessions = %#v, want []", res["sessions"])
	}
	res, code = call(t, s, `{"id":3,"op":"diag"}`)
	if code != "" || !strings.Contains(strings.Join(toStrings(res["kids"]), ","), "k1") {
		t.Fatalf("diag: %v %q", res, code)
	}
	if _, code := call(t, s, `{"id":4,"op":"connect"}`); code != pipe.CodeUnknownOp {
		t.Fatalf("unknown op: code %q", code)
	}
	if _, code := call(t, s, `{"id":5,"op":"open","node":"owner1","ticket":"a.b.c","sessionKey":"x"}`); code != friend.CodeTicket {
		t.Fatalf("bad ticket: code %q", code)
	}
}

// A full log ring of long lines, as tsnet produces, must not make the diag
// response too large for one pipe line: the pipe server could not send it, and
// the UI would see "internal" instead of the session it is watching.
func TestDiagFitsInOnePipeLine(t *testing.T) {
	s, _ := newService(t)
	s.Log = redact.New(nil, 500)
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
	log := toStrings(decode(t, body)["log"])
	if len(log) == 0 || !strings.Contains(log[len(log)-1], "magicsock: 499 ") {
		t.Fatalf("the newest log lines must be kept; got %d lines", len(log))
	}
}

func decode(t *testing.T, body []byte) map[string]any {
	t.Helper()
	out := map[string]any{}
	if err := json.Unmarshal(body, &out); err != nil {
		t.Fatal(err)
	}
	return out
}

func toStrings(v any) []string {
	var out []string
	list, _ := v.([]any)
	for _, x := range list {
		s, _ := x.(string)
		out = append(out, s)
	}
	return out
}
