//go:build windows

package main

import (
	"strings"
	"testing"

	"1salem.app/connect/transport/internal/netpolicy"
	"1salem.app/connect/transport/internal/sidecar"
)

func TestParseFlags(t *testing.T) {
	o, err := parseFlags([]string{"--mode", "fake", "--keys", "k.json", "--fake-node-id", "nFAKE1"})
	if err != nil {
		t.Fatal(err)
	}
	if o.mode != netpolicy.Fake || !strings.HasPrefix(o.pipe, `\\.\pipe\1Salem.Connect.Transport.S-1-`) {
		t.Fatalf("options %+v", o)
	}
	if _, err := parseFlags([]string{"--mode", "tsnet", "--keys", "k.json", "--state-dir", `C:\state`}); err != nil {
		t.Fatal(err)
	}

	bad := [][]string{
		{},
		{"--mode", "vpn", "--keys", "k.json"},
		{"--mode", "fake", "--fake-node-id", "n"},
		{"--mode", "fake", "--keys", "k.json"},
		{"--mode", "fake", "--keys", "k.json", "--fake-node-id", "n", "--state-dir", `C:\s`},
		{"--mode", "tsnet", "--keys", "k.json"},
		{"--mode", "tsnet", "--keys", "k.json", "--state-dir", `C:\s`, "--fake-node-id", "n"},
		{"--mode", "fake", "--keys", "k.json", "--fake-node-id", "n", "--pipe", `\\server\pipe\x`},
		{"--mode", "fake", "--keys", "k.json", "--fake-node-id", "n", "--destination", "10.0.0.1:25565"},
		{"--mode", "fake", "--keys", "k.json", "--fake-node-id", "n", "extra"},
		{"--mode", "tsnet", "--keys", "k.json", "--state-dir", `C:\s`, "--authkey", "tskey-auth-x"},
	}
	for _, args := range bad {
		if _, err := parseFlags(args); err == nil {
			t.Errorf("%q accepted", args)
		}
	}
}

func TestTsnetModeRequiresTheOAuthOmitTag(t *testing.T) {
	err := sidecar.CheckBuild(netpolicy.Tsnet)
	if sidecar.OAuthKeyOmitted != (err == nil) {
		t.Fatalf("OAuthKeyOmitted=%v but CheckBuild(tsnet) = %v", sidecar.OAuthKeyOmitted, err)
	}
	if err := sidecar.CheckBuild(netpolicy.Fake); err != nil {
		t.Fatalf("fake mode refused: %v", err)
	}
}
