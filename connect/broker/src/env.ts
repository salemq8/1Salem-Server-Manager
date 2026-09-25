/** Bindings of the broker Worker (wrangler.jsonc). Secrets are optional here on purpose: a missing
 *  secret must make the affected routes fail with "not configured", never crash or fake success. */
export interface Env {
  DB: D1Database;
  /** PKCS#8 DER of the ECDSA P-256 ticket signing key, standard base64. */
  TICKET_SIGNING_KEY?: string;
  /** At least 32 random bytes, base64url. Keys the invite HMAC and the IP pseudonyms. */
  INVITE_PEPPER?: string;
  /**
   * Local development only: "allow" lets owners register a loopback host bridge for the fake
   * transport mode. Unset (the only valid state in Cloudflare) means tailnet addresses only.
   */
  CONNECT_DEV_LOOPBACK_BRIDGE?: string;
}
