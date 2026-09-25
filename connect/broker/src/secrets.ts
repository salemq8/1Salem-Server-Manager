// Worker secrets, parsed once per isolate. A missing or malformed secret surfaces as a 503
// "not_configured" on the routes that need it; nothing falls back to a default key.

import { ES256, hmacSha256, importHmacKey, sha256 } from "./crypto";
import { base64StdDecode, base64UrlDecode, base64UrlEncode, hex, utf8 } from "./encoding";
import type { Env } from "./env";
import { notConfigured } from "./http";

const P256 = { name: "ECDSA", namedCurve: "P-256" } as const;
const MIN_PEPPER_BYTES = 32;

export interface TicketSigner {
  /** First 16 chars of base64url(SHA-256(SPKI DER)). */
  kid: string;
  /** base64url SPKI DER of the public key, as published by GET /v1/keys. */
  spki: string;
  /** Non-extractable. */
  privateKey: CryptoKey;
}

let signerCache: { source: string; signer: Promise<TicketSigner> } | undefined;
let pepperCache: { source: string; key: Promise<CryptoKey> } | undefined;

export function ticketSigner(env: Env): Promise<TicketSigner> {
  const source = env.TICKET_SIGNING_KEY;
  if (source === undefined || source.trim() === "") {
    return Promise.reject(notConfigured("TICKET_SIGNING_KEY is not set"));
  }
  if (signerCache?.source !== source) signerCache = { source, signer: loadSigner(source) };
  return signerCache.signer;
}

async function loadSigner(source: string): Promise<TicketSigner> {
  const der = base64StdDecode(source);
  if (der === null) throw notConfigured("TICKET_SIGNING_KEY is not standard base64");
  try {
    // Web Crypto cannot derive the public key from a non-extractable private key. A transient
    // extractable import yields the public point once; the key kept for signing is a separate
    // non-extractable import, so nothing later in the isolate can export the private scalar.
    const transient = await crypto.subtle.importKey("pkcs8", der, P256, true, ["sign"]);
    const jwk = (await crypto.subtle.exportKey("jwk", transient)) as JsonWebKey;
    const publicKey = await crypto.subtle.importKey(
      "jwk",
      { kty: "EC", crv: "P-256", x: jwk.x, y: jwk.y },
      P256,
      true,
      ["verify"],
    );
    const spki = new Uint8Array((await crypto.subtle.exportKey("spki", publicKey)) as ArrayBuffer);
    const privateKey = await crypto.subtle.importKey("pkcs8", der, P256, false, ["sign"]);
    await assertSignerWorks(privateKey, publicKey);
    return {
      kid: base64UrlEncode(await sha256(spki)).slice(0, 16),
      spki: base64UrlEncode(spki),
      privateKey,
    };
  } catch {
    throw notConfigured("TICKET_SIGNING_KEY is not a PKCS#8 ECDSA P-256 private key");
  } finally {
    der.fill(0);
  }
}

// Guards against a PKCS#8 whose embedded public key does not match its private scalar: we would
// otherwise publish a key that cannot verify our own tickets.
async function assertSignerWorks(privateKey: CryptoKey, publicKey: CryptoKey): Promise<void> {
  const probe = utf8("1salem-connect-broker signer self-test");
  const signature = await crypto.subtle.sign(ES256, privateKey, probe);
  if (!(await crypto.subtle.verify(ES256, publicKey, signature, probe))) {
    throw new Error("signing key self-test failed");
  }
}

function pepperKey(env: Env): Promise<CryptoKey> {
  const source = env.INVITE_PEPPER;
  if (source === undefined || source.trim() === "") {
    return Promise.reject(notConfigured("INVITE_PEPPER is not set"));
  }
  if (pepperCache?.source !== source) pepperCache = { source, key: loadPepper(source) };
  return pepperCache.key;
}

async function loadPepper(source: string): Promise<CryptoKey> {
  const raw = base64UrlDecode(source.trim());
  if (raw === null || raw.byteLength < MIN_PEPPER_BYTES) {
    throw notConfigured(`INVITE_PEPPER must be base64url of at least ${MIN_PEPPER_BYTES} random bytes`);
  }
  try {
    return await importHmacKey(raw);
  } finally {
    raw.fill(0);
  }
}

/** Hex HMAC-SHA256(INVITE_PEPPER, secret bytes): the only form in which an invite is stored. */
export async function inviteSecretHmac(env: Env, secret: Uint8Array): Promise<string> {
  return hex(await hmacSha256(await pepperKey(env), secret));
}

/**
 * Pseudonym for a client network (see clientip.ts) in rate-limit buckets, so D1 never holds raw
 * addresses. The message is domain-separated from invite secrets (those are exactly 32 raw bytes;
 * this is a tagged string).
 */
export async function ipPseudonym(env: Env, network: string): Promise<string> {
  const mac = await hmacSha256(await pepperKey(env), utf8(`1SALEM-RATE-IP\n${network}`));
  return base64UrlEncode(mac.subarray(0, 16));
}
