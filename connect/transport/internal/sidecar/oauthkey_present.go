//go:build !ts_omit_oauthkey

package sidecar

// OAuthKeyOmitted is false: this build links tsnet's OAuth key hook, so
// CheckBuild refuses tsnet mode.
const OAuthKeyOmitted = false
