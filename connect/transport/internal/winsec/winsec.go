//go:build windows

// Package winsec holds the Windows security descriptors and identity checks
// the sidecars depend on: the named-pipe DACL, the tsnet state-directory DACL
// and verification of who owns a pipe we connect to or a state directory we
// reuse.
package winsec

import (
	"errors"
	"fmt"
	"os"
	"slices"
	"unsafe"

	"golang.org/x/sys/windows"
)

// Well-known SIDs the sidecars reason about.
const (
	// SystemSID is NT AUTHORITY\SYSTEM.
	SystemSID = "S-1-5-18"
	// AdministratorsSID is BUILTIN\Administrators.
	AdministratorsSID = "S-1-5-32-544"
)

// CurrentUserSID returns the SID of the account this process runs as.
func CurrentUserSID() (string, error) {
	u, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		return "", err
	}
	return u.User.Sid.String(), nil
}

// PipeSDDL is the security descriptor for every pipe a sidecar serves (§11):
// owned by the serving account, a protected DACL (no inherited ACEs), an
// explicit deny for NETWORK logons (NU) placed first, and full access only for
// the owning account and SYSTEM. Everyone else, including other local users,
// gets nothing.
//
// The owner is set explicitly because clients verify it before sending a
// secret. Left implicit it would be the token's default owner, which for an
// elevated process is BUILTIN\Administrators rather than the user.
func PipeSDDL(userSID string) string {
	return "O:" + userSID + "D:P(D;;GA;;;NU)(A;;GA;;;" + userSID + ")(A;;GA;;;SY)"
}

// DirSDDL is the security descriptor for tsnet state directories: owned by
// the serving account, a protected DACL inherited by every file below, and
// access only for that account and SYSTEM. The node's machine and node
// private keys live in these directories.
//
// The owner is explicit for the same reason as in PipeSDDL: left implicit, an
// elevated process would create directories owned by BUILTIN\Administrators,
// and the owner check would then refuse them once the process runs
// unelevated.
func DirSDDL(userSID string) string {
	return "O:" + userSID + "D:P(A;OICI;FA;;;" + userSID + ")(A;OICI;FA;;;SY)"
}

var (
	// ErrNotPlainDir is returned for a state path that exists but is a file,
	// a junction or a symbolic link. A reparse point planted by someone else
	// could redirect node state somewhere they can read.
	ErrNotPlainDir = errors.New("winsec: not a plain directory")
	// ErrUntrustedDirOwner is returned for a directory owned by an account
	// this process does not trust. The owner can always rewrite the DACL, so
	// no DACL we set would keep the node keys from it; the directory is
	// refused rather than taken over.
	ErrUntrustedDirOwner = errors.New("winsec: directory is owned by an untrusted account")
)

// ownerLookup returns the owner SID of an open file or directory. Tests
// substitute it: a directory owned by another account cannot be created
// without privileges.
type ownerLookup func(h windows.Handle) (string, error)

func fileOwnerSID(h windows.Handle) (string, error) {
	return ownerSID(h, windows.SE_FILE_OBJECT)
}

// EnsureProtectedDir creates path owned by this account with DirSDDL. When
// path already exists it must pass the CheckDir checks, and DirSDDL's DACL is
// re-applied to it; a directory that fails them is refused, never taken
// over. The parent must exist.
func EnsureProtectedDir(path string) error {
	self, err := CurrentUserSID()
	if err != nil {
		return err
	}
	return ensureProtectedDir(path, self, fileOwnerSID)
}

func ensureProtectedDir(path, self string, ownerOf ownerLookup) error {
	sd, err := windows.SecurityDescriptorFromString(DirSDDL(self))
	if err != nil {
		return err
	}
	sa := &windows.SecurityAttributes{
		Length:             uint32(unsafe.Sizeof(windows.SecurityAttributes{})),
		SecurityDescriptor: sd,
	}
	p16, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return err
	}
	err = windows.CreateDirectory(p16, sa)
	if err == nil {
		return nil
	}
	if !errors.Is(err, windows.ERROR_ALREADY_EXISTS) {
		return &os.PathError{Op: "mkdir", Path: path, Err: err}
	}
	h, err := openDir(path, windows.WRITE_DAC)
	if errors.Is(err, windows.ERROR_ACCESS_DENIED) {
		// An owner always has WRITE_DAC, so the directory most likely belongs
		// to someone else. Name the owner when it can be read; either way the
		// directory is refused.
		if cerr := checkDir(path, self, ownerOf); cerr != nil {
			return cerr
		}
	}
	if err != nil {
		return err
	}
	defer windows.CloseHandle(h)
	if err := checkOpenDir(h, path, self, ownerOf); err != nil {
		return err
	}
	dacl, _, err := sd.DACL()
	if err != nil {
		return err
	}
	// Through the handle that was checked, not by name, so a directory
	// swapped in after the checks cannot receive the change instead.
	return windows.SetSecurityInfo(h, windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION,
		nil, nil, dacl, nil)
}

// CheckDir verifies that an existing path is a plain directory, not a
// junction or symbolic link, and is owned by an account this process trusts
// (trustedDirOwner). It changes nothing, for a directory whose DACL belongs to
// whoever chose it.
func CheckDir(path string) error {
	self, err := CurrentUserSID()
	if err != nil {
		return err
	}
	return checkDir(path, self, fileOwnerSID)
}

func checkDir(path, self string, ownerOf ownerLookup) error {
	h, err := openDir(path, 0)
	if err != nil {
		return err
	}
	defer windows.CloseHandle(h)
	return checkOpenDir(h, path, self, ownerOf)
}

// openDir opens path itself, never the target of a junction or symbolic link
// at path, with READ_CONTROL for the owner check plus extra access.
func openDir(path string, extra uint32) (windows.Handle, error) {
	p16, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return 0, err
	}
	h, err := windows.CreateFile(p16,
		windows.READ_CONTROL|windows.FILE_READ_ATTRIBUTES|extra,
		windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE|windows.FILE_SHARE_DELETE,
		nil, windows.OPEN_EXISTING,
		// BACKUP_SEMANTICS is what lets CreateFile open a directory at all.
		windows.FILE_FLAG_BACKUP_SEMANTICS|windows.FILE_FLAG_OPEN_REPARSE_POINT, 0)
	if err != nil {
		return 0, &os.PathError{Op: "open", Path: path, Err: err}
	}
	return h, nil
}

func checkOpenDir(h windows.Handle, path, self string, ownerOf ownerLookup) error {
	var info windows.ByHandleFileInformation
	if err := windows.GetFileInformationByHandle(h, &info); err != nil {
		return &os.PathError{Op: "stat", Path: path, Err: err}
	}
	if info.FileAttributes&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0 ||
		info.FileAttributes&windows.FILE_ATTRIBUTE_DIRECTORY == 0 {
		return fmt.Errorf("%w: %s", ErrNotPlainDir, path)
	}
	owner, err := ownerOf(h)
	if err != nil {
		return fmt.Errorf("winsec: owner of %s: %w", path, err)
	}
	if !trustedDirOwner(owner, self) {
		return fmt.Errorf("%w: %s is owned by %s", ErrUntrustedDirOwner, path, owner)
	}
	return nil
}

// trustedDirOwner reports whether a process running as self can rely on a
// directory owned by owner. Its own account, always. SYSTEM's token names
// BUILTIN\Administrators as the default owner, so for a SYSTEM service that
// group counts as itself: only SYSTEM or an elevated administrator can make
// either the owner, and both already control the machine.
func trustedDirOwner(owner, self string) bool {
	return owner == self || (self == SystemSID && owner == AdministratorsSID)
}

// HandleOwnerSID returns the owner SID of an open kernel object handle, such
// as the client end of a named pipe.
func HandleOwnerSID(h windows.Handle) (string, error) {
	return ownerSID(h, windows.SE_KERNEL_OBJECT)
}

func ownerSID(h windows.Handle, objectType windows.SE_OBJECT_TYPE) (string, error) {
	sd, err := windows.GetSecurityInfo(h, objectType, windows.OWNER_SECURITY_INFORMATION)
	if err != nil {
		return "", err
	}
	owner, _, err := sd.Owner()
	if err != nil {
		return "", err
	}
	if owner == nil {
		return "", errors.New("winsec: object has no owner")
	}
	return owner.String(), nil
}

// ErrUntrustedOwner is returned when a pipe is owned by an unexpected account.
var ErrUntrustedOwner = errors.New("winsec: pipe is owned by an untrusted account")

// VerifyPipeOwner checks that the pipe behind the client handle h is owned by
// one of the allowed accounts (§11). A client connecting to a well-known pipe
// name otherwise cannot tell the real server from a process that created the
// name first; for the host authorization pipe such an impostor could "allow"
// friends onto any loopback port. Only the creator decides a new pipe's owner,
// and Windows lets it name only itself or a group it may own, so a squatter
// running as another account cannot fake the expected owner.
func VerifyPipeOwner(h windows.Handle, allowedSIDs []string) error {
	owner, err := HandleOwnerSID(h)
	if err != nil {
		return err
	}
	if !slices.Contains(allowedSIDs, owner) {
		return fmt.Errorf("%w (%s)", ErrUntrustedOwner, owner)
	}
	return nil
}

// ParseSID checks that s is a valid SID string and returns its canonical form.
func ParseSID(s string) (string, error) {
	sid, err := windows.StringToSid(s)
	if err != nil {
		return "", fmt.Errorf("winsec: %q is not a SID", s)
	}
	return sid.String(), nil
}
