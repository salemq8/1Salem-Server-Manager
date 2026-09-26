//go:build windows

package transport

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"time"

	"golang.org/x/sys/windows"

	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/testkit"
	"1salem.app/connect/transport/internal/winsec"
)

func TestScrubEnvironment(t *testing.T) {
	contract := []string{"TS_AUTHKEY", "TS_AUTH_KEY", "TS_CLIENT_SECRET", "TS_CONTROL_URL", "TSNET_FORCE_LOGIN"}
	for _, name := range append(contract, "TS_CLIENT_ID", "TS_ID_TOKEN", "TS_AUDIENCE") {
		t.Setenv(name, "tskey-auth-planted")
	}
	t.Setenv("ONESALEM_UNRELATED", "kept")
	ScrubEnvironment()
	for _, name := range scrubbedEnv {
		if v, ok := os.LookupEnv(name); ok {
			t.Errorf("%s survived scrubbing: %q", name, v)
		}
	}
	for _, name := range contract {
		if !strings.Contains(strings.Join(scrubbedEnv, ","), name) {
			t.Errorf("%s from §11 is not in the scrub list", name)
		}
	}
	if os.Getenv("ONESALEM_UNRELATED") != "kept" {
		t.Error("an unrelated variable was removed")
	}
}

func TestValidateAuthKey(t *testing.T) {
	for _, bad := range []string{
		"", "tskey-client-kABC-secret", "tskey-auth-", "tskey-kABC-legacy", "tskey-auth-abc def",
		"tskey-auth-abc\n", "xtskey-auth-abc", "tskey-auth-" + strings.Repeat("a", 201),
		"tskey-client-kABC-secret?ephemeral=false",
	} {
		if !errors.Is(ValidateAuthKey(bad), ErrInvalidAuthKey) {
			t.Errorf("%q accepted", bad)
		}
	}
	if err := ValidateAuthKey("tskey-auth-kABCDEF1CNTRL-0123456789abcdef"); err != nil {
		t.Errorf("valid auth key refused: %v", err)
	}
}

func TestTsnetServerConfiguration(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "nodes", "owner1")
	ts := newTsnet(tsnetConfig{Dir: dir, Hostname: "friend-pc", AuthKey: "tskey-auth-kX-y"})
	srv := ts.srv
	if srv.Tun != nil {
		t.Error("Tun is set; the node must use tsnet's userspace netstack (no adapter, route or DNS change)")
	}
	if srv.RunWebClient {
		t.Error("RunWebClient is on")
	}
	if srv.Ephemeral {
		t.Error("friend and host nodes must be persistent, not ephemeral")
	}
	if srv.Dir != dir || srv.Hostname != "friend-pc" {
		t.Errorf("Dir %q Hostname %q", srv.Dir, srv.Hostname)
	}
	if srv.ControlURL != "" {
		t.Errorf("ControlURL %q; only Tailscale's default control plane is used", srv.ControlURL)
	}
	if srv.ClientSecret != "" || srv.ClientID != "" || srv.IDToken != "" || srv.Audience != "" {
		t.Error("an OAuth or workload-identity credential is configured")
	}
	// Never started, so closing must not touch tsnet at all.
	if err := ts.Close(); err != nil {
		t.Fatal(err)
	}
}

func TestTsnetNodesDirectoryPerNode(t *testing.T) {
	root := t.TempDir()
	if _, err := NewTsnetNodes("relative\\state", nil, nil); err == nil {
		t.Fatal("a relative state directory was accepted")
	}
	n, err := NewTsnetNodes(root, t.Logf, t.Logf)
	if err != nil {
		t.Fatal(err)
	}
	a, b := n.nodeDir("owner-a"), n.nodeDir("owner-b")
	if a != filepath.Join(root, "nodes", "owner-a") || b != filepath.Join(root, "nodes", "owner-b") || a == b {
		t.Fatalf("node dirs %q %q", a, b)
	}

	// Refused keys and names never create state or reserve the node. This is
	// the step before tsnet exists, not Enroll: a regression here must not be
	// able to start a node and reach the network.
	for _, tc := range []struct{ node, key, host string }{
		{"owner-a", "tskey-client-kX-secret", "friend-pc"},
		{"owner-a", "", "friend-pc"},
		{"../escape", "tskey-auth-kX-y", "friend-pc"},
		{"owner-a", "tskey-auth-kX-y", "Not A Label"},
	} {
		if _, _, err := n.beginEnroll(tc.node, tc.key, tc.host); err == nil {
			t.Errorf("beginEnroll(%q, %q, %q) accepted", tc.node, tc.key, tc.host)
		}
	}
	if entries, _ := os.ReadDir(root); len(entries) != 0 {
		t.Fatalf("refused enrollments created %v", entries)
	}
	if len(n.nodes) != 0 {
		t.Fatalf("refused enrollments reserved %v", n.nodes)
	}
	if _, err := n.Get(context.Background(), "owner-a"); !errors.Is(err, ErrNotEnrolled) {
		t.Fatalf("Get of an unenrolled node: %v", err)
	}
	if nodes := n.List(); len(nodes) != 0 {
		t.Fatalf("List = %v", nodes)
	}
}

func TestFakeNodesForgetAndReenroll(t *testing.T) {
	n, err := NewFakeNodes("nFAKE0001CNTRL")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := n.Enroll(context.Background(), "owner-a", testAuthKey, testHostname); err != nil {
		t.Fatal(err)
	}
	if err := n.Forget("owner-a"); err != nil {
		t.Fatal(err)
	}
	if got := n.List(); len(got) != 0 {
		t.Fatalf("List after forget = %v", got)
	}
	if err := n.Forget("owner-a"); !errors.Is(err, ErrNotEnrolled) {
		t.Fatalf("second forget: %v", err)
	}
	if _, err := n.Enroll(context.Background(), "owner-a", testAuthKey, testHostname); err != nil {
		t.Fatalf("re-enroll after forget: %v", err)
	}
}

func TestProtectedStateDirectory(t *testing.T) {
	root := t.TempDir()
	n, _ := NewTsnetNodes(filepath.Join(root, "state"), nil, nil)
	dir := n.nodeDir("owner-a")
	if err := n.prepareDirs(dir); err != nil {
		t.Fatal(err)
	}
	self, _ := winsec.CurrentUserSID()
	for _, d := range []string{filepath.Join(root, "state"), filepath.Dir(dir), dir} {
		sd, err := windows.GetNamedSecurityInfo(d, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
		if err != nil {
			t.Fatal(err)
		}
		got := sd.String()
		if !strings.HasPrefix(got, "D:P") || strings.Contains(got, ";;;WD)") || strings.Contains(got, ";;;BU)") ||
			strings.Contains(got, ";;;AU)") || !strings.Contains(got, self) {
			t.Errorf("%s DACL %s", d, got)
		}
	}
	// A junction planted where a node directory goes is refused.
	if err := os.Remove(dir); err != nil {
		t.Fatal(err)
	}
	testkit.Junction(t, dir, t.TempDir())
	if err := n.prepareDirs(dir); !errors.Is(err, winsec.ErrNotPlainDir) {
		t.Errorf("a junction as the node directory: %v", err)
	}
}

// An existing --state-dir root may carry an ACL its creator chose; it is
// checked, not rewritten. A junction in its place is refused.
func TestExistingStateRoot(t *testing.T) {
	self, _ := winsec.CurrentUserSID()
	root := filepath.Join(t.TempDir(), "state")
	if err := winsec.EnsureProtectedDir(root); err != nil {
		t.Fatal(err)
	}
	chosen := "D:P(A;OICI;FA;;;" + self + ")(A;OICI;FA;;;SY)(A;OICI;FR;;;BU)"
	sd, err := windows.SecurityDescriptorFromString(chosen)
	if err != nil {
		t.Fatal(err)
	}
	dacl, _, _ := sd.DACL()
	if err := windows.SetNamedSecurityInfo(root, windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, nil, nil, dacl, nil); err != nil {
		t.Fatal(err)
	}
	before := daclString(t, root)

	n, _ := NewTsnetNodes(root, nil, nil)
	dir := n.nodeDir("owner-a")
	if err := n.prepareDirs(dir); err != nil {
		t.Fatal(err)
	}
	if after := daclString(t, root); after != before {
		t.Errorf("root DACL rewritten from %s to %s", before, after)
	}
	if got := daclString(t, dir); strings.Contains(got, ";;;BU)") {
		t.Errorf("node directory DACL %s", got)
	}

	jroot := filepath.Join(t.TempDir(), "state")
	testkit.Junction(t, jroot, root)
	nj, _ := NewTsnetNodes(jroot, nil, nil)
	if err := nj.prepareDirs(nj.nodeDir("owner-a")); !errors.Is(err, winsec.ErrNotPlainDir) {
		t.Errorf("a junction as the state root: %v", err)
	}
}

const (
	testAuthKey  = "tskey-auth-kX-y"
	testHostname = "friend-pc"
)

func writeFile(t *testing.T, path, data string) {
	t.Helper()
	if err := os.WriteFile(path, []byte(data), 0o600); err != nil {
		t.Fatal(err)
	}
}

func writeMarker(t *testing.T, dir, nodeID string) {
	t.Helper()
	data, err := json.Marshal(nodeMarker{NodeID: nodeID, Hostname: testHostname})
	if err != nil {
		t.Fatal(err)
	}
	writeFile(t, filepath.Join(dir, markerFile), string(data))
}

// A first enrollment killed before it finished (the app ends the sidecar on
// exit) leaves tsnet's files and no marker. The node's next first enrollment
// must neither be locked out by them nor build on them: tsnet would adopt a
// tailscaled.state it finds, planted or left over.
func TestFirstEnrollClearsWhatAKilledAttemptLeft(t *testing.T) {
	n, _ := NewTsnetNodes(filepath.Join(t.TempDir(), "state"), t.Logf, t.Logf)
	dir := n.nodeDir("owner-a")
	if err := n.prepareDirs(dir); err != nil {
		t.Fatal(err)
	}
	for name, data := range map[string]string{
		"tailscaled.state":    `{"_machinekey":"left-behind"}`,
		"tailscaled.log.conf": `{}`,
		"tailscaled.log1.txt": "log",
		"tailscaled.log2.txt": "log",
	} {
		writeFile(t, filepath.Join(dir, name), data)
	}
	if err := os.MkdirAll(filepath.Join(dir, "certs", "sub"), 0o700); err != nil {
		t.Fatal(err)
	}
	writeFile(t, filepath.Join(dir, "certs", "sub", "key.pem"), "key")
	// A junction inside the directory is removed; what it points at is not.
	outside := t.TempDir()
	kept := filepath.Join(outside, "kept.txt")
	writeFile(t, kept, "kept")
	testkit.Junction(t, filepath.Join(dir, "elsewhere"), outside)

	entry, got, err := n.beginEnroll("owner-a", testAuthKey, testHostname)
	if err != nil {
		t.Fatalf("the next first enrollment was refused: %v", err)
	}
	if got != dir || entry.state != "enrolling" || n.nodes["owner-a"] != entry {
		t.Fatalf("dir %q, entry %+v, reserved %v", got, entry, n.nodes)
	}
	if left, _ := os.ReadDir(dir); len(left) != 0 {
		t.Fatalf("left in the node directory: %v", left)
	}
	if _, err := os.Stat(kept); err != nil {
		t.Fatalf("the junction's target was emptied: %v", err)
	}
	if err := winsec.CheckDir(dir); err != nil {
		t.Fatalf("the node directory itself: %v", err)
	}
}

// Cleanup that does not succeed, for example because a sidecar that is still
// running holds its log open, refuses the enrollment without reserving the
// node. Once the file is released the enrollment proceeds.
func TestFirstEnrollFailsClosedWhenTheNodeDirCannotBeEmptied(t *testing.T) {
	n, _ := NewTsnetNodes(filepath.Join(t.TempDir(), "state"), t.Logf, t.Logf)
	dir := n.nodeDir("owner-a")
	if err := n.prepareDirs(dir); err != nil {
		t.Fatal(err)
	}
	held := filepath.Join(dir, "tailscaled.log1.txt")
	writeFile(t, held, "log")
	// Go opens files without FILE_SHARE_DELETE, so this one cannot be
	// deleted while it is open.
	f, err := os.Open(held)
	if err != nil {
		t.Fatal(err)
	}
	_, _, err = n.beginEnroll("owner-a", testAuthKey, testHostname)
	f.Close()
	if !errors.Is(err, errNodeDirInUse) {
		t.Fatalf("beginEnroll with an entry that cannot be removed: %v", err)
	}
	if len(n.nodes) != 0 {
		t.Fatalf("a refused enrollment reserved %v", n.nodes)
	}
	if _, _, err := n.beginEnroll("owner-a", testAuthKey, testHostname); err != nil {
		t.Fatalf("after the file was released: %v", err)
	}
}

// The directory checks come before the marker is read: a marker behind a
// junction does not pass for an enrolled node. An enrolled node's directory,
// on the other hand, is never emptied.
func TestFirstEnrollChecksTheDirectoryBeforeTheMarker(t *testing.T) {
	n, _ := NewTsnetNodes(filepath.Join(t.TempDir(), "state"), t.Logf, t.Logf)
	enrolled := n.nodeDir("owner-a")
	if err := n.prepareDirs(enrolled); err != nil {
		t.Fatal(err)
	}
	writeMarker(t, enrolled, "nOWNERA0001CNTRL")
	writeFile(t, filepath.Join(enrolled, "tailscaled.state"), `{}`)
	if _, _, err := n.beginEnroll("owner-a", testAuthKey, testHostname); !errors.Is(err, ErrAlreadyEnrolled) {
		t.Fatalf("an enrolled node: %v", err)
	}
	if left, _ := os.ReadDir(enrolled); len(left) != 2 {
		t.Fatalf("an enrolled node's directory was changed: %v", left)
	}

	elsewhere := t.TempDir()
	writeMarker(t, elsewhere, "nPLANTED0001CNTRL")
	testkit.Junction(t, n.nodeDir("owner-b"), elsewhere)
	if _, _, err := n.beginEnroll("owner-b", testAuthKey, testHostname); !errors.Is(err, winsec.ErrNotPlainDir) {
		t.Fatalf("a junction to a directory with a marker: %v", err)
	}
	if _, err := readMarker(elsewhere); err != nil {
		t.Fatalf("the junction's target was changed: %v", err)
	}
	if len(n.nodes) != 0 {
		t.Fatalf("refused enrollments reserved %v", n.nodes)
	}
}

func TestForgetRemovesOnlyTheRequestedTsnetNode(t *testing.T) {
	n, _ := NewTsnetNodes(filepath.Join(t.TempDir(), "state"), t.Logf, t.Logf)
	for node, id := range map[string]string{"owner-a": "nOWNERA0001CNTRL", "owner-b": "nOWNERB0001CNTRL"} {
		dir := n.nodeDir(node)
		if err := n.prepareDirs(dir); err != nil {
			t.Fatal(err)
		}
		writeMarker(t, dir, id)
		writeFile(t, filepath.Join(dir, "tailscaled.state"), `{"node":"`+node+`"}`)
	}
	if err := n.Forget("owner-a"); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Lstat(n.nodeDir("owner-a")); !errors.Is(err, fs.ErrNotExist) {
		t.Fatalf("forgotten directory still exists: %v", err)
	}
	if _, err := readMarker(n.nodeDir("owner-b")); err != nil {
		t.Fatalf("other node marker was changed: %v", err)
	}
	if got := n.List(); !slices.Equal(got, []NodeInfo{{Node: "owner-b", NodeID: "nOWNERB0001CNTRL", State: "enrolled"}}) {
		t.Fatalf("List after forget = %v", got)
	}
	if _, _, err := n.beginEnroll("owner-a", testAuthKey, testHostname); err != nil {
		t.Fatalf("fresh enrollment after forget: %v", err)
	}
}

func TestForgetTsnetNodeFailsClosedOnUntrustedOrIncompleteState(t *testing.T) {
	n, _ := NewTsnetNodes(filepath.Join(t.TempDir(), "state"), t.Logf, t.Logf)
	if err := n.Forget("missing"); !errors.Is(err, ErrNotEnrolled) {
		t.Fatalf("missing node: %v", err)
	}
	if _, err := os.Lstat(n.root); !errors.Is(err, fs.ErrNotExist) {
		t.Fatalf("forget created the state root: %v", err)
	}

	partial := n.nodeDir("partial")
	if err := n.prepareDirs(partial); err != nil {
		t.Fatal(err)
	}
	writeFile(t, filepath.Join(partial, "tailscaled.state"), "kept")
	if err := n.Forget("partial"); !errors.Is(err, ErrNotEnrolled) {
		t.Fatalf("marker-less node: %v", err)
	}
	if _, err := os.Stat(filepath.Join(partial, "tailscaled.state")); err != nil {
		t.Fatalf("marker-less state was changed: %v", err)
	}

	outside := t.TempDir()
	writeMarker(t, outside, "nOUTSIDE0001CNTRL")
	targetMarker := filepath.Join(outside, markerFile)
	testkit.Junction(t, n.nodeDir("junction"), outside)
	if err := n.Forget("junction"); !errors.Is(err, ErrForgetFailed) {
		t.Fatalf("junction node: %v", err)
	}
	if _, err := os.Stat(targetMarker); err != nil {
		t.Fatalf("junction target was changed: %v", err)
	}
}

func TestForgetTsnetNodeCanRecoverAfterAFileIsReleased(t *testing.T) {
	n, _ := NewTsnetNodes(filepath.Join(t.TempDir(), "state"), t.Logf, t.Logf)
	dir := n.nodeDir("owner-a")
	if err := n.prepareDirs(dir); err != nil {
		t.Fatal(err)
	}
	writeMarker(t, dir, "nOWNERA0001CNTRL")
	held := filepath.Join(dir, "tailscaled.log1.txt")
	writeFile(t, held, "log")
	f, err := os.Open(held)
	if err != nil {
		t.Fatal(err)
	}
	err = n.Forget("owner-a")
	f.Close()
	if !errors.Is(err, ErrForgetFailed) {
		t.Fatalf("forget with held file: %v", err)
	}
	if markerExists(dir) {
		t.Fatal("failed cleanup left the enrollment marker in place")
	}
	if _, _, err := n.beginEnroll("owner-a", testAuthKey, testHostname); err != nil {
		t.Fatalf("fresh enrollment after the file was released: %v", err)
	}
}

func TestForgetRefusesANodeWhileItIsEnrolling(t *testing.T) {
	n, _ := NewTsnetNodes(filepath.Join(t.TempDir(), "state"), t.Logf, t.Logf)
	n.nodes["owner-a"] = &tsnetNode{state: "enrolling"}
	if err := n.Forget("owner-a"); !errors.Is(err, ErrNodeBusy) {
		t.Fatalf("Forget while enrolling: %v", err)
	}
	if n.nodes["owner-a"] == nil {
		t.Fatal("Forget removed an enrollment in progress")
	}
}

// List reports a marker only from directories that pass the reparse and owner
// checks, since the app binds the node id it reports; the others are logged.
func TestListSkipsDirectoriesThatFailTheChecks(t *testing.T) {
	var logged []string
	logf := func(format string, args ...any) { logged = append(logged, fmt.Sprintf(format, args...)) }
	loggedAbout := func(s string) bool {
		return slices.ContainsFunc(logged, func(l string) bool { return strings.Contains(l, s) })
	}

	missing, _ := NewTsnetNodes(filepath.Join(t.TempDir(), "state"), logf, t.Logf)
	if got := missing.List(); len(got) != 0 || len(logged) != 0 {
		t.Fatalf("no state directory yet: List = %v, logged %q", got, logged)
	}

	n, _ := NewTsnetNodes(filepath.Join(t.TempDir(), "state"), logf, t.Logf)
	for node, id := range map[string]string{"owner-a": "nOWNERA0001CNTRL", "owner-b": "nOWNERB0001CNTRL"} {
		dir := n.nodeDir(node)
		if err := n.prepareDirs(dir); err != nil {
			t.Fatal(err)
		}
		writeMarker(t, dir, id)
	}
	b := n.nodeDir("owner-b")
	elsewhere := t.TempDir()
	writeMarker(t, elsewhere, "nPLANTED0001CNTRL")
	testkit.Junction(t, n.nodeDir("owner-c"), elsewhere)
	// owner-b's directory stands in for one another account owns.
	n.checkDir = func(p string) error {
		if p == b {
			return fmt.Errorf("%w: %s", winsec.ErrUntrustedDirOwner, p)
		}
		return winsec.CheckDir(p)
	}
	want := []NodeInfo{{Node: "owner-a", NodeID: "nOWNERA0001CNTRL", State: "enrolled"}}
	if got := n.List(); !slices.Equal(got, want) {
		t.Fatalf("List = %v, want %v", got, want)
	}
	if !loggedAbout("owner-b") || !loggedAbout("owner-c") {
		t.Fatalf("skipped nodes not logged: %q", logged)
	}

	// A junction in place of the nodes directory: nothing behind it is listed.
	logged = nil
	root := filepath.Join(t.TempDir(), "state")
	if err := winsec.EnsureProtectedDir(root); err != nil {
		t.Fatal(err)
	}
	testkit.Junction(t, filepath.Join(root, "nodes"), filepath.Join(n.root, "nodes"))
	nj, _ := NewTsnetNodes(root, logf, t.Logf)
	if got := nj.List(); len(got) != 0 {
		t.Fatalf("List through a junctioned nodes directory = %v", got)
	}
	if !loggedAbout("not listing enrolled nodes") {
		t.Fatalf("the junctioned nodes directory was not logged: %q", logged)
	}
}

// A failed enrollment does not leave the keys tsnet generated on disk.
func TestFailedEnrollRemovesWhatTsnetWrote(t *testing.T) {
	n, _ := NewTsnetNodes(filepath.Join(t.TempDir(), "state"), t.Logf, t.Logf)
	dir := n.nodeDir("owner-a")
	if err := n.prepareDirs(dir); err != nil {
		t.Fatal(err)
	}
	// What tsnet leaves behind when the node does not come up.
	writeFile(t, filepath.Join(dir, "tailscaled.state"), `{}`)
	if err := os.Mkdir(filepath.Join(dir, "certs"), 0o700); err != nil {
		t.Fatal(err)
	}
	n.discardFailedEnroll("owner-a", dir)
	if _, err := os.Lstat(dir); !errors.Is(err, fs.ErrNotExist) {
		t.Fatalf("the node directory is still there: %v", err)
	}
}

func TestRequireNetstackSource(t *testing.T) {
	self4 := netip.MustParseAddr("100.101.102.103")
	self6 := netip.MustParseAddr("fd7a:115c:a1e0::1234:5678")
	tcp := func(s string) net.Addr { return net.TCPAddrFromAddrPort(netip.MustParseAddrPort(s)) }
	cases := []struct {
		name  string
		local net.Addr
		self  []netip.Addr
		ok    bool
	}{
		{"netstack v4", tcp("100.101.102.103:51000"), []netip.Addr{self4, self6}, true},
		{"netstack v6", tcp("[fd7a:115c:a1e0::1234:5678]:51000"), []netip.Addr{self4, self6}, true},
		// A system Tailscale adapter on the same PC: a tailnet address, not ours.
		{"system tailscale", tcp("100.64.0.7:51000"), []netip.Addr{self4, self6}, false},
		{"system tailscale v6", tcp("[fd7a:115c:a1e0::99]:51000"), []netip.Addr{self4, self6}, false},
		{"host LAN", tcp("192.168.1.20:51000"), []netip.Addr{self4, self6}, false},
		{"host loopback", tcp("127.0.0.1:51000"), []netip.Addr{self4, self6}, false},
		// No network map yet: the node has no addresses, nothing matches.
		{"no self addresses", tcp("100.101.102.103:51000"), []netip.Addr{{}, {}}, false},
		{"unspecified", tcp("0.0.0.0:51000"), []netip.Addr{{}, {}}, false},
		{"no local address", nil, []netip.Addr{self4, self6}, false},
		{"typed nil", (*net.TCPAddr)(nil), []netip.Addr{self4, self6}, false},
		{"not ip:port", &net.UnixAddr{Name: "x", Net: "unix"}, []netip.Addr{self4, self6}, false},
	}
	for _, tc := range cases {
		err := requireNetstackSource(tc.local, tc.self...)
		if (err == nil) != tc.ok {
			t.Errorf("%s: %v", tc.name, err)
		}
		if err != nil && !errors.Is(err, ErrDestination) {
			t.Errorf("%s: %v does not wrap ErrDestination", tc.name, err)
		}
	}
}

func daclString(t *testing.T, path string) string {
	t.Helper()
	sd, err := windows.GetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatal(err)
	}
	return sd.String()
}

func TestCheckListenAddr(t *testing.T) {
	cases := []struct {
		addr        string
		fake, tsnet bool
	}{
		{"127.0.0.1:17780", true, false},
		{"[::1]:17780", true, false},
		{":7780", false, true},
		{"100.64.0.1:7780", false, true},
		{"[fd7a:115c:a1e0::5]:7780", false, true},
		{"0.0.0.0:7780", false, false},
		{"192.168.1.2:7780", false, false},
		{"localhost:7780", false, false},
		{":0", false, false},
		{"127.0.0.1:0", false, false},
		{"", false, false},
	}
	for _, tc := range cases {
		if got := CheckListenAddr(netpolicy.Fake, tc.addr) == nil; got != tc.fake {
			t.Errorf("fake %q: accepted=%v", tc.addr, got)
		}
		if got := CheckListenAddr(netpolicy.Tsnet, tc.addr) == nil; got != tc.tsnet {
			t.Errorf("tsnet %q: accepted=%v", tc.addr, got)
		}
	}
}

func TestTsnetDialRefusesNonTailnetBeforeStarting(t *testing.T) {
	ts := newTsnet(tsnetConfig{Dir: t.TempDir(), Hostname: "friend-pc"})
	for _, addr := range []string{"127.0.0.1:25565", "10.0.0.1:7780", "8.8.8.8:443"} {
		if _, err := ts.Dial(context.Background(), netip.MustParseAddrPort(addr)); !errors.Is(err, ErrDestination) {
			t.Errorf("Dial(%s): %v", addr, err)
		}
	}
	if ts.started {
		t.Fatal("a refused dial started the node")
	}
}

func TestFakeTransport(t *testing.T) {
	host, _ := NewFake("")
	friend, err := NewFake("nFRIEND0001CNTRL")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := NewFake("bad id\n"); err == nil {
		t.Fatal("invalid fake node id accepted")
	}
	if _, err := friend.Dial(context.Background(), netip.MustParseAddrPort("100.64.0.1:7780")); !errors.Is(err, ErrDestination) {
		t.Fatalf("fake dial to a tailnet address: %v", err)
	}
	l, _ := net.Listen("tcp4", "127.0.0.1:0")
	addr := l.Addr().String()
	l.Close()
	ln, err := host.Listen(addr)
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	go func() {
		c, err := friend.Dial(context.Background(), netip.MustParseAddrPort(addr))
		if err == nil {
			c.Write([]byte("DATA"))
			c.Close()
		}
	}()
	c, err := ln.Accept()
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(5 * time.Second))
	id, err := host.PeerNodeID(context.Background(), c)
	if err != nil || id != "nFRIEND0001CNTRL" {
		t.Fatalf("peer %q, %v", id, err)
	}
	// Not a byte of what follows the header is consumed.
	data, _ := io.ReadAll(c)
	if string(data) != "DATA" {
		t.Fatalf("data %q", data)
	}
}
