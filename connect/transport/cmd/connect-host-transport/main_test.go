//go:build windows

package main

import (
	"slices"
	"testing"

	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/pipe"
	"1salem.app/connect/transport/internal/winsec"
)

func TestParseFlags(t *testing.T) {
	self, err := winsec.CurrentUserSID()
	if err != nil {
		t.Fatal(err)
	}

	o, err := parseFlags([]string{"--mode", "tsnet", "--state-dir", `C:\state`})
	if err != nil {
		t.Fatal(err)
	}
	if o.bridgeListen != ":7780" || o.authzPipe != pipe.HostAuthzPipe ||
		!slices.Equal(o.authzOwners, []string{winsec.SystemSID, winsec.AdministratorsSID}) {
		t.Fatalf("tsnet defaults %+v", o)
	}

	o, err = parseFlags([]string{"--mode", "fake", "--bridge-listen", "127.0.0.1:17780"})
	if err != nil {
		t.Fatal(err)
	}
	if o.mode != netpolicy.Fake || !slices.Equal(o.authzOwners, []string{self}) {
		t.Fatalf("fake defaults %+v", o)
	}

	o, err = parseFlags([]string{"--mode", "fake", "--bridge-listen", "127.0.0.1:17780", "--expected-authz-owner", "S-1-5-21-1-2-3-500"})
	if err != nil || !slices.Equal(o.authzOwners, []string{"S-1-5-21-1-2-3-500"}) {
		t.Fatalf("configured owner: %+v, %v", o.authzOwners, err)
	}

	bad := [][]string{
		{"--mode", "fake"},
		{"--mode", "fake", "--bridge-listen", "0.0.0.0:17780"},
		{"--mode", "fake", "--bridge-listen", "100.64.0.1:7780"},
		{"--mode", "tsnet", "--state-dir", `C:\s`, "--bridge-listen", "127.0.0.1:7780"},
		{"--mode", "tsnet", "--state-dir", `C:\s`, "--bridge-listen", "0.0.0.0:7780"},
		{"--mode", "tsnet"},
		{"--mode", "fake", "--bridge-listen", "127.0.0.1:17780", "--state-dir", `C:\s`},
		{"--mode", "fake", "--bridge-listen", "127.0.0.1:17780", "--expected-authz-owner", "Everyone"},
		{"--mode", "fake", "--bridge-listen", "127.0.0.1:17780", "--authz-pipe", `\\host\pipe\x`},
		{"--mode", "fake", "--bridge-listen", "127.0.0.1:17780", "--keys", "k.json"},
		{"--mode", "fake", "--bridge-listen", "127.0.0.1:17780", "--endpoint", "127.0.0.1:25565"},
	}
	for _, args := range bad {
		if _, err := parseFlags(args); err == nil {
			t.Errorf("%q accepted", args)
		}
	}
}
