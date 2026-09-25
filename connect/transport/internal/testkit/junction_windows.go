package testkit

import (
	"os/exec"
	"testing"
)

// Junction creates a directory junction at link pointing at target. Unlike a
// symbolic link it needs no privilege, so tests of the state-directory checks
// always have a reparse point to present instead of skipping.
func Junction(t testing.TB, link, target string) {
	t.Helper()
	out, err := exec.Command("cmd", "/c", "mklink", "/J", link, target).CombinedOutput()
	if err != nil {
		t.Fatalf("mklink /J %s %s: %v: %s", link, target, err, out)
	}
}
