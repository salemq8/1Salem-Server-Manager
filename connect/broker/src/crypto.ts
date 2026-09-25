// Web Crypto wrappers with the algorithms pinned: ECDSA P-256 / SHA-256 (ES256, IEEE P1363
// signatures) for identities and tickets, HMAC-SHA256 for invites. Nothing here negotiates.

import { base32Lower, base64UrlEncode } from "./encoding";

const P256 = { name: "ECDSA", namedCurve: "P-256" } as const;
export const ES256 = { name: "ECDSA", hash: "SHA-256" } as const;

/** ES256 signatures are r||s, 32 bytes each (IEEE P1363), never DER. */
export const ES256_SIGNATURE_BYTES = 64;

export function randomBytes(length: number): Uint8Array {
  return crypto.getRandomValues(new Uint8Array(length));
}

export async function sha256(bytes: Uint8Array): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
}

/** Server-generated ids (invites, memberships): prefix + base32 of 128 random bits (26 chars). */
export function randomId(prefix: "inv_" | "mem_"): string {
  return prefix + base32Lower(randomBytes(16));
}

/** 128-bit random value, base64url (22 chars): ticket jti. */
export function randomToken128(): string {
  return base64UrlEncode(randomBytes(16));
}

export type IdentityKind = "own" | "dev";

/** own_/dev_ + first 26 chars of lowercase unpadded RFC 4648 base32 of SHA-256(SPKI DER). */
export async function identityId(kind: IdentityKind, spkiDer: Uint8Array): Promise<string> {
  return `${kind}_` + base32Lower(await sha256(spkiDer)).slice(0, 26);
}

/**
 * Imports a P-256 public key from SPKI DER for ES256 verification. Returns null for anything that
 * is not a valid P-256 key in its canonical (uncompressed) DER form, so that one key can never be
 * registered under two ids.
 */
export async function importP256Spki(spkiDer: Uint8Array): Promise<CryptoKey | null> {
  let key: CryptoKey;
  try {
    key = await crypto.subtle.importKey("spki", spkiDer, P256, true, ["verify"]);
  } catch {
    return null;
  }
  const canonical = new Uint8Array((await crypto.subtle.exportKey("spki", key)) as ArrayBuffer);
  return constantTimeEqual(canonical, spkiDer) ? key : null;
}

export async function verifyEs256(publicKey: CryptoKey, signature: Uint8Array, data: Uint8Array): Promise<boolean> {
  if (signature.byteLength !== ES256_SIGNATURE_BYTES) return false;
  try {
    return await crypto.subtle.verify(ES256, publicKey, signature, data);
  } catch {
    return false;
  }
}

/** timingSafeEqual throws when lengths differ, so the (non-secret) length is compared first. */
export function constantTimeEqual(a: Uint8Array, b: Uint8Array): boolean {
  if (a.byteLength !== b.byteLength) return false;
  return crypto.subtle.timingSafeEqual(a, b);
}

export async function importHmacKey(raw: Uint8Array): Promise<CryptoKey> {
  return crypto.subtle.importKey("raw", raw, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
}

export async function hmacSha256(key: CryptoKey, data: Uint8Array): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.sign("HMAC", key, data));
}
