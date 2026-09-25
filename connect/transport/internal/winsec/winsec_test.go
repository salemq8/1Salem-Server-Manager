//go:build windows

package winsec

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"unsafe"

	"golang.org/x/sys/windows"

	"1salem.app/connect/transport/internal/testkit"
)

// otherUserSID is a local account that is neither this process nor SYSTEM.
// A directory it owns cannot be created without privileges, so the tests
// present it through the owner lookup instead.
const otherUserSID = "S-1-5-21-1111111111-2222222222-3333333333-1001"

func selfSID(t *testing.T) string {
	t.Helper()
	self, err := CurrentUserSID()
	if err != nil {
		t.Fatal(err)
	}
	return self
}

func ownedBy(sid string) ownerLookup {
	return func(windows.Handle) (string, error) { return sid, nil }
}

// mkdirSDDL creates path with an explicit security descriptor.
func mkdirSDDL(t *testing.T, path, sddl string) {
	t.Helper()
	sd, err := windows.SecurityDescriptorFromString(sddl)
	if err != nil {
		t.Fatal(err)
	}
	sa := &windows.SecurityAttributes{Length: uint32(unsafe.Sizeof(windows.SecurityAttributes{})), SecurityDescriptor: sd}
	p16, err := windows.UTF16PtrFromString(path)
	if err != nil {
		t.Fatal(err)
	}
	if err := windows.CreateDirectory(p16, sa); err != nil {
		t.Fatalf("create %s: %v", path, err)
	}
}

func daclOf(t *testing.T, path string) string {
	t.Helper()
	sd, err := windows.GetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatal(err)
	}
	return sd.String()
}

func TestEnsureProtectedDirCreatesOwnedProtectedDir(t *testing.T) {
	self := selfSID(t)
	dir := filepath.Join(t.TempDir(), "state")
	if err := EnsureProtectedDir(dir); err != nil {
		t.Fatal(err)
	}
	sd, err := windows.GetNamedSecurityInfo(dir, windows.SE_FILE_OBJECT,
		windows.OWNER_SECURITY_INFORMATION|windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatal(err)
	}
	owner, _, err := sd.Owner()
	if err != nil || owner.String() != self {
		t.Fatalf("owner %v, %v; want %s", owner, err, self)
	}
	_, dacl, _ := strings.Cut(sd.String(), "D:")
	flags, aces, _ := strings.Cut(dacl, "(")
	if !strings.Contains(flags, "P") || "("+aces != "(A;OICI;FA;;;"+self+")(A;OICI;FA;;;SY)" {
		t.Fatalf("DACL D:%s; want protected, this account and SYSTEM only", dacl)
	}
	// Existing and owned by this account: accepted as is, by the real lookup.
	if err := EnsureProtectedDir(dir); err != nil {
		t.Fatalf("second EnsureProtectedDir: %v", err)
	}
	if err := CheckDir(dir); err != nil {
		t.Fatalf("CheckDir: %v", err)
	}
}

func TestEnsureProtectedDirReplacesTheDACLOfAnOwnedDir(t *testing.T) {
	self := selfSID(t)
	dir := filepath.Join(t.TempDir(), "state")
	mkdirSDDL(t, dir, "O:"+self+"D:(A;OICI;FA;;;WD)(A;OICI;FA;;;"+self+")")
	if err := EnsureProtectedDir(dir); err != nil {
		t.Fatal(err)
	}
	if got := daclOf(t, dir); !strings.HasPrefix(got, "D:P") || strings.Contains(got, ";;;WD)") {
		t.Fatalf("DACL after EnsureProtectedDir: %s", got)
	}
}

func TestExistingDirWithUntrustedOwnerIsRefusedUntouched(t *testing.T) {
	self := selfSID(t)
	dir := filepath.Join(t.TempDir(), "state")
	mkdirSDDL(t, dir, "O:"+self+"D:(A;OICI;FA;;;WD)(A;OICI;FA;;;"+self+")")
	before := daclOf(t, dir)

	for _, owner := range []string{otherUserSID, SystemSID, AdministratorsSID} {
		if err := ensureProtectedDir(dir, self, ownedBy(owner)); !errors.Is(err, ErrUntrustedDirOwner) {
			t.Errorf("ensureProtectedDir, owner %s: %v", owner, err)
		}
		if err := checkDir(dir, self, ownedBy(owner)); !errors.Is(err, ErrUntrustedDirOwner) {
			t.Errorf("checkDir, owner %s: %v", owner, err)
		}
	}
	// Refused, not repaired: the owner could rewrite any DACL set here.
	if after := daclOf(t, dir); after != before {
		t.Fatalf("DACL changed from %s to %s", before, after)
	}
	lookupFailed := func(windows.Handle) (string, error) { return "", errors.New("no owner") }
	if err := checkDir(dir, self, lookupFailed); err == nil {
		t.Fatal("a directory whose owner could not be read was accepted")
	}
}

// A directory this account cannot open for WRITE_DAC is not its own; the
// refusal names the owner rather than reporting a bare access denial.
func TestEnsureProtectedDirWithoutWriteDACNamesTheOwner(t *testing.T) {
	self := selfSID(t)
	dir := filepath.Join(t.TempDir(), "state")
	// OWNER RIGHTS replaces the owner's implicit WRITE_DAC, which stands in
	// for a directory owned and locked down by another account.
	mkdirSDDL(t, dir, "O:"+self+"D:P(A;;FR;;;OW)(A;;FA;;;SY)")
	if err := ensureProtectedDir(dir, self, ownedBy(otherUserSID)); !errors.Is(err, ErrUntrustedDirOwner) {
		t.Fatalf("ensureProtectedDir: %v", err)
	}
	if err := ensureProtectedDir(dir, self, ownedBy(self)); !errors.Is(err, windows.ERROR_ACCESS_DENIED) {
		t.Fatalf("ensureProtectedDir with a trusted owner: %v, want the access denial", err)
	}
}

func TestTrustedDirOwner(t *testing.T) {
	const user = "S-1-5-21-9-9-9-1000"
	cases := []struct {
		owner, self string
		want        bool
	}{
		{user, user, true},
		{otherUserSID, user, false},
		{SystemSID, user, false},
		{AdministratorsSID, user, false},
		{SystemSID, SystemSID, true},
		// SYSTEM's token names Administrators as the default owner.
		{AdministratorsSID, SystemSID, true},
		{user, SystemSID, false},
		{"", user, false},
	}
	for _, tc := range cases {
		if got := trustedDirOwner(tc.owner, tc.self); got != tc.want {
			t.Errorf("trustedDirOwner(%q, %q) = %v", tc.owner, tc.self, got)
		}
	}
	// Running as SYSTEM, a directory owned by Administrators passes checkDir.
	if err := checkDir(t.TempDir(), SystemSID, ownedBy(AdministratorsSID)); err != nil {
		t.Fatalf("checkDir as SYSTEM: %v", err)
	}
}

func TestReparsePointsAndFilesRefused(t *testing.T) {
	self := selfSID(t)
	root := t.TempDir()
	target := filepath.Join(root, "elsewhere")
	if err := os.Mkdir(target, 0o700); err != nil {
		t.Fatal(err)
	}
	before := daclOf(t, target)
	junction := filepath.Join(root, "junction")
	testkit.Junction(t, junction, target)
	file := filepath.Join(root, "file")
	if err := os.WriteFile(file, nil, 0o600); err != nil {
		t.Fatal(err)
	}
	paths := []string{junction, file}
	symlink := filepath.Join(root, "symlink")
	if err := os.Symlink(target, symlink); err == nil {
		paths = append(paths, symlink)
	} else {
		t.Logf("symbolic link not tested (needs a privilege or developer mode): %v", err)
	}

	for _, p := range paths {
		// Owned by this account, so only the reparse or file check can refuse.
		if err := ensureProtectedDir(p, self, ownedBy(self)); !errors.Is(err, ErrNotPlainDir) {
			t.Errorf("EnsureProtectedDir(%s): %v", filepath.Base(p), err)
		}
		if err := checkDir(p, self, ownedBy(self)); !errors.Is(err, ErrNotPlainDir) {
			t.Errorf("CheckDir(%s): %v", filepath.Base(p), err)
		}
	}
	if err := EnsureProtectedDir(junction); !errors.Is(err, ErrNotPlainDir) {
		t.Errorf("EnsureProtectedDir(junction): %v", err)
	}
	// The link itself was examined, never the directory it points at.
	if after := daclOf(t, target); after != before {
		t.Fatalf("junction target DACL changed from %s to %s", before, after)
	}
}
