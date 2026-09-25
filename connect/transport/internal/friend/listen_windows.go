//go:build windows

package friend

import (
	"context"
	"encoding/binary"
	"errors"
	"net"
	"net/netip"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

// soExclusiveAddrUse is SO_EXCLUSIVEADDRUSE, defined by Winsock as
// ((int)(~SO_REUSEADDR)); x/sys/windows does not export it.
const soExclusiveAddrUse = ^windows.SO_REUSEADDR

var loopbackV4 = netip.MustParseAddr("127.0.0.1")

// listenLoopback binds 127.0.0.1:port with SO_EXCLUSIVEADDRUSE, which stops
// another process from binding the same address with SO_REUSEADDR and taking
// our connections over.
//
// It does not protect a program that got there first on the wildcard
// address: Windows lets a specific bind coexist with a wildcard one and hands
// loopback clients to the specific socket, so binding here would silently
// steal a game server's local players. bindLocal therefore skips every port
// that listeningPorts reports before calling this.
func listenLoopback(port int) (net.Listener, error) {
	lc := net.ListenConfig{
		Control: func(network, address string, c syscall.RawConn) error {
			var sockErr error
			err := c.Control(func(fd uintptr) {
				sockErr = windows.SetsockoptInt(windows.Handle(fd), windows.SOL_SOCKET, soExclusiveAddrUse, 1)
			})
			if err != nil {
				return err
			}
			return sockErr
		},
	}
	return lc.Listen(context.Background(), "tcp4", netip.AddrPortFrom(loopbackV4, uint16(port)).String())
}

var procGetExtendedTcpTable = windows.NewLazySystemDLL("iphlpapi.dll").NewProc("GetExtendedTcpTable")

// Table layout constants from iphlpapi.h / tcpmib.h.
const (
	tcpTableOwnerPIDListener = 3 // TCP_TABLE_OWNER_PID_LISTENER
	tcpRowV4Len              = 24
	tcpRowV4LocalPort        = 8
	tcpRowV6Len              = 56
	tcpRowV6LocalPort        = 20
)

// listeningPorts returns every local port a TCP listener holds, IPv4 or IPv6,
// on any address. Reading the table touches nobody; probing a port by
// connecting to it would.
func listeningPorts() (map[uint16]bool, error) {
	ports := make(map[uint16]bool)
	for _, t := range []struct {
		family         uint32
		rowLen, portAt int
	}{
		{windows.AF_INET, tcpRowV4Len, tcpRowV4LocalPort},
		{windows.AF_INET6, tcpRowV6Len, tcpRowV6LocalPort},
	} {
		table, err := tcpListenerTable(t.family)
		if err != nil {
			return nil, err
		}
		if len(table) < 4 {
			return nil, errors.New("friend: short TCP listener table")
		}
		n := int(binary.LittleEndian.Uint32(table))
		if n < 0 || 4+n*t.rowLen > len(table) {
			return nil, errors.New("friend: malformed TCP listener table")
		}
		for i := range n {
			// dwLocalPort holds the port in network byte order in its low
			// 16 bits.
			at := 4 + i*t.rowLen + t.portAt
			ports[binary.BigEndian.Uint16(table[at:at+2])] = true
		}
	}
	return ports, nil
}

func tcpListenerTable(family uint32) ([]byte, error) {
	size := uint32(16 * 1024)
	for range 4 {
		buf := make([]byte, size)
		r, _, _ := procGetExtendedTcpTable.Call(
			uintptr(unsafe.Pointer(&buf[0])),
			uintptr(unsafe.Pointer(&size)),
			0,
			uintptr(family),
			tcpTableOwnerPIDListener,
			0,
		)
		switch windows.Errno(r) {
		case windows.ERROR_SUCCESS:
			return buf[:min(int(size), len(buf))], nil
		case windows.ERROR_INSUFFICIENT_BUFFER:
			size += 4096 // the table can grow between the two calls
			continue
		default:
			return nil, windows.Errno(r)
		}
	}
	return nil, errors.New("friend: TCP listener table keeps growing")
}
