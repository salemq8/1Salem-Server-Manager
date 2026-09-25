// Signed requests (docs/CONNECT_ARCHITECTURE.md §5).
//
// Every owner/device call carries X-1S-Key, X-1S-Time, X-1S-Nonce and X-1S-Sig. The signature is
// ES256 (P1363) over the canonical string below. Checks run cheapest first; the nonce is recorded
// only after the signature verifies, so nobody can burn another key's nonces, and before the key's
// budget (or the global registration ceiling) is charged, so a replay is refused without spending
// it. Registration is the exception for its per-network budget, which identities.ts charges before
// any cryptography so junk floods stay cheap. Every auth failure is the same 401.

import type { Caller, CallerKind, RequestContext } from "./context";
import { constantTimeEqual, identityId, importP256Spki, sha256, verifyEs256, type IdentityKind } from "./crypto";
import { base64UrlDecode, base64UrlEncode, utf8 } from "./encoding";
import { badRequest, unauthorized } from "./http";
import { GLOBAL_SUBJECT, hit, type Limit } from "./ratelimit";

export const CLOCK_WINDOW_SECONDS = 300;
const NONCE_BYTES = 16;

const KEY_ID = /^(own|dev)_[a-z2-7]{26}$/;
const UNIX_SECONDS = /^[0-9]{1,12}$/;

const PREFIX: Record<CallerKind, IdentityKind> = { owner: "own", device: "dev" };
const TABLE: Record<CallerKind, "owners" | "devices"> = { owner: "owners", device: "devices" };

export interface SignedHeaders {
  keyId: string;
  timeText: string;
  time: number;
  nonce: string;
  signature: Uint8Array;
}

/**
 * The exact string that is signed. UTF-8, "\n"-separated, no trailing newline. PATH is the
 * request path without the query string.
 */
export function canonicalRequest(
  method: string,
  path: string,
  timeText: string,
  nonce: string,
  bodySha256B64Url: string,
): string {
  return ["1SALEM-REQ-V1", method, path, timeText, nonce, bodySha256B64Url].join("\n");
}

/** Format and clock checks only: no database access and no cryptography. */
export function readSignedHeaders(ctx: RequestContext, kind: CallerKind): SignedHeaders {
  const headers = ctx.request.headers;
  const keyId = headers.get("X-1S-Key") ?? "";
  const timeText = headers.get("X-1S-Time") ?? "";
  const nonce = headers.get("X-1S-Nonce") ?? "";
  const sigText = headers.get("X-1S-Sig") ?? "";

  if (!KEY_ID.test(keyId) || !keyId.startsWith(`${PREFIX[kind]}_`)) throw unauthorized("bad or wrong-kind key id");
  if (!UNIX_SECONDS.test(timeText)) throw unauthorized("bad time header");
  const nonceBytes = base64UrlDecode(nonce);
  if (nonceBytes === null || nonceBytes.byteLength !== NONCE_BYTES) throw unauthorized("bad nonce header");
  const signature = base64UrlDecode(sigText);
  if (signature === null) throw unauthorized("bad signature header");

  const time = Number(timeText);
  if (Math.abs(ctx.now - time) > CLOCK_WINDOW_SECONDS) throw unauthorized("time outside window");
  return { keyId, timeText, time, nonce, signature };
}

async function verifySignature(ctx: RequestContext, key: CryptoKey, signed: SignedHeaders): Promise<void> {
  const bodyHash = base64UrlEncode(await sha256(ctx.body));
  const canonical = canonicalRequest(ctx.request.method, ctx.url.pathname, signed.timeText, signed.nonce, bodyHash);
  if (!(await verifyEs256(key, signed.signature, utf8(canonical)))) throw unauthorized("signature invalid");
}

/**
 * Remembers the nonce until the request's timestamp leaves the window; a second use is a replay.
 * The INSERT is the only replay check: it is atomic, so of any number of concurrent copies of one
 * request exactly one gets past it.
 */
async function consumeNonce(ctx: RequestContext, signed: SignedHeaders): Promise<void> {
  const result = await ctx.env.DB.prepare(
    `INSERT INTO request_nonces (key_id, nonce, expires_at) VALUES (?1, ?2, ?3)
     ON CONFLICT (key_id, nonce) DO NOTHING`,
  )
    .bind(signed.keyId, signed.nonce, signed.time + CLOCK_WINDOW_SECONDS)
    .run();
  if (result.meta.changes !== 1) throw unauthorized("nonce replay");
}

/**
 * Authenticates a request signed by an already-registered owner or device key and charges `limit`
 * to that key. The order is deliberate:
 * - signature first, so a forger costs no write and spends nobody's budget;
 * - then the nonce, so a replay (even many concurrent copies) is refused before anything is
 *   charged, and a request refused below for its budget is used up: its captured bytes can never
 *   be executed later, after the window resets;
 * - then the budget. Over budget, the only write is that nonce, which only the key's holder can
 *   cause; the outer deployment limiter bounds that (README, "Requirements outside the broker").
 */
export async function authenticate(ctx: RequestContext, kind: CallerKind, limit: Limit): Promise<Caller> {
  const signed = readSignedHeaders(ctx, kind);
  const row = await ctx.env.DB.prepare(`SELECT spki FROM ${TABLE[kind]} WHERE id = ?1`)
    .bind(signed.keyId)
    .first<{ spki: string }>();
  if (row === null) throw unauthorized("unknown key");
  const der = base64UrlDecode(row.spki);
  const key = der === null ? null : await importP256Spki(der);
  if (key === null) throw unauthorized("stored key unusable");
  await verifySignature(ctx, key, signed);
  await consumeNonce(ctx, signed);
  await hit(ctx, limit, signed.keyId);
  return { kind, id: signed.keyId };
}

/**
 * Authenticates a self-signed registration: the body's SPKI must derive the X-1S-Key id and must
 * verify the request's own signature (proof of possession of the private key). `signed` comes
 * from readSignedHeaders, which the caller runs before charging its per-network limit.
 *
 * `sharedLimit` is one budget for every registration from anywhere. It is charged only once the
 * proof of possession holds and the nonce is fresh, so junk signatures from rotating networks and
 * replays of a captured registration cannot use it up.
 */
export async function authenticateRegistration(
  ctx: RequestContext,
  kind: CallerKind,
  signed: SignedHeaders,
  spkiText: string,
  sharedLimit: Limit,
): Promise<{ id: string; spki: string }> {
  const der = base64UrlDecode(spkiText);
  const key = der === null ? null : await importP256Spki(der);
  if (der === null || key === null) throw badRequest("spki is not a canonical P-256 SubjectPublicKeyInfo");
  const id = await identityId(PREFIX[kind], der);
  if (!constantTimeEqual(utf8(id), utf8(signed.keyId))) throw unauthorized("key id does not match spki");
  await verifySignature(ctx, key, signed);
  await consumeNonce(ctx, signed);
  await hit(ctx, sharedLimit, GLOBAL_SUBJECT);
  return { id, spki: spkiText };
}
