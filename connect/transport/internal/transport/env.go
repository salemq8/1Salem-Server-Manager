package transport

import "os"

// scrubbedEnv lists every variable tsnet would read as a credential, a
// control-server override or a forced login (§11). The last three are the
// workload-identity inputs tsnet also reads; they are scrubbed for the same
// reason even though the contract does not list them.
var scrubbedEnv = []string{
	"TS_AUTHKEY",
	"TS_AUTH_KEY",
	"TS_CLIENT_SECRET",
	"TS_CONTROL_URL",
	"TSNET_FORCE_LOGIN",
	"TS_CLIENT_ID",
	"TS_ID_TOKEN",
	"TS_AUDIENCE",
}

// ScrubEnvironment removes the variables above from this process. Both
// sidecars call it first thing in main, before any tsnet code runs, so the
// only way a credential reaches tsnet is the enroll pipe operation.
func ScrubEnvironment() {
	for _, name := range scrubbedEnv {
		os.Unsetenv(name)
	}
}
