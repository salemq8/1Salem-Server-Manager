//go:build windows

// Package pipe implements the JSON-lines named-pipe protocol of §11: one UTF-8
// JSON object per line, at most 64 KiB, requests {"id":n,"op":"..."} and
// responses {"id":n,"ok":true,...} or {"id":n,"ok":false,"error":"<code>"}.
package pipe

import (
	"context"
	"errors"
	"net"
	"regexp"

	"github.com/Microsoft/go-winio"
	"golang.org/x/sys/windows"

	"1salem.app/connect/transport/internal/winsec"
)

// MaxLine is the largest accepted line, excluding the newline.
const MaxLine = 64 * 1024

// DiagLogBytes bounds the log inside a "diag" response. The whole response
// must fit in one MaxLine line or it cannot be sent at all; the log is the only
// part that grows with use, so it gets half the line and gives way first.
const DiagLogBytes = MaxLine / 2

// HostAuthzPipe is the Agent's authorization pipe (the Agent is the server).
const HostAuthzPipe = `\\.\pipe\1Salem.Connect.HostAuthz.v1`

// namePattern accepts local pipe names only. "\\server\pipe\..." would reach
// another machine, so it is refused along with nested paths.
var namePattern = regexp.MustCompile(`^\\\\\.\\pipe\\[A-Za-z0-9._-]{1,200}$`)

// ErrName is returned for a pipe name that is not a plain local pipe.
var ErrName = errors.New(`pipe: name must look like \\.\pipe\<name>`)

// ValidateName checks that name is a plain local pipe path.
func ValidateName(name string) error {
	if !namePattern.MatchString(name) {
		return ErrName
	}
	return nil
}

// FriendPipeName is the friend transport's default pipe for a user SID.
func FriendPipeName(userSID string) string {
	return `\\.\pipe\1Salem.Connect.Transport.` + userSID
}

// HostTransportPipeName is the host transport's default control pipe (hello,
// status, enroll, diag) for the account it runs as. §7 hands the host node's
// auth key over "its pipe"; the contract does not name it, so this name
// mirrors the friend pipe's.
func HostTransportPipeName(userSID string) string {
	return `\\.\pipe\1Salem.Connect.Host.Transport.` + userSID
}

// Listen creates the pipe with the protected DACL from winsec.PipeSDDL.
//
// go-winio creates the first instance with FILE_CREATE, so Listen fails if
// the name already exists (no squatting on a name someone else created), and
// sets PIPE_REJECT_REMOTE_CLIENTS on every instance (§11, T8-24).
func Listen(name string) (net.Listener, error) {
	if err := ValidateName(name); err != nil {
		return nil, err
	}
	sid, err := winsec.CurrentUserSID()
	if err != nil {
		return nil, err
	}
	return winio.ListenPipe(name, &winio.PipeConfig{
		SecurityDescriptor: winsec.PipeSDDL(sid),
		InputBufferSize:    MaxLine,
		OutputBufferSize:   MaxLine,
	})
}

// ErrNoExpectedOwner is returned by Dial when no expected owner is given.
var ErrNoExpectedOwner = errors.New("pipe: an expected pipe owner is required")

// Dial connects to a local pipe and checks, before a single byte is written,
// that the pipe is owned by one of allowedOwners (§11). There is no unchecked
// variant: everything this module sends to a pipe (tickets, proofs, decisions
// it acts on) must only ever reach the expected account.
//
// The connection is opened at the Anonymous impersonation level (go-winio's
// default), so the server cannot act with our identity either.
func Dial(ctx context.Context, name string, allowedOwners []string) (*Client, error) {
	if err := ValidateName(name); err != nil {
		return nil, err
	}
	if len(allowedOwners) == 0 {
		return nil, ErrNoExpectedOwner
	}
	conn, err := winio.DialPipeContext(ctx, name)
	if err != nil {
		return nil, err
	}
	if err := VerifyOwner(conn, allowedOwners); err != nil {
		conn.Close()
		return nil, err
	}
	return NewClient(conn), nil
}

// VerifyOwner checks the owner of the pipe behind a connection from
// winio.DialPipe*.
func VerifyOwner(conn net.Conn, allowedOwners []string) error {
	h, err := Handle(conn)
	if err != nil {
		return err
	}
	return winsec.VerifyPipeOwner(h, allowedOwners)
}

// Handle returns the Windows handle behind a go-winio pipe connection.
func Handle(conn net.Conn) (windows.Handle, error) {
	f, ok := conn.(interface{ Fd() uintptr })
	if !ok {
		return 0, errors.New("pipe: cannot inspect pipe handle")
	}
	return windows.Handle(f.Fd()), nil
}
