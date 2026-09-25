//go:build ts_omit_oauthkey

package sidecar

// OAuthKeyOmitted is true: this build excludes tailscale.com/feature/oauthkey,
// so tsnet cannot turn a "tskey-client-" secret into minted keys.
const OAuthKeyOmitted = true
