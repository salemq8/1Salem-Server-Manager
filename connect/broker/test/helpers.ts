// Test client for the broker. Encodings, id derivation and the canonical request string are
// re-implemented here from the contract text rather than imported from src/, so the tests check
// the Worker against the specification instead of against itself.

import { exports } from "cloudflare:workers";
import { expect, vi } from "vitest";

export const BASE = "https://broker.test";

// ---- encodings (independent of src/encoding.ts) -------------------------------------------------

export function b64u(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function fromB64u(text: string): Uint8Array {
  const std = text.replace(/-/g, "+").replace(/_/g, "/");
  const binary = atob(std + "=".repeat((4 - (std.length % 4)) % 4));
  return Uint8Array.from(binary, (c) => c.charCodeAt(0));
}

export function base32Lower(bytes: Uint8Array): string {
  const alphabet = "abcdefghijklmnopqrstuvwxyz234567";
  let bitString = "";
  for (const byte of bytes) bitString += byte.toString(2).padStart(8, "0");
  let out = "";
  for (let i = 0; i < bitString.length; i += 5) out += alphabet[parseInt(bitString.slice(i, i + 5).padEnd(5, "0"), 2)];
  return out;
}

const text = (value: string) => new TextEncoder().encode(value);

export async function sha256(bytes: Uint8Array): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
}

export function randomB64u(length: number): string {
  return b64u(crypto.getRandomValues(new Uint8Array(length)));
}

export const nowSeconds = () => Math.floor(Date.now() / 1000);

/**
 * Freezes Date at `unixSeconds`. The Worker runs in the same isolate as the tests, so this moves
 * the broker's clock and the signing clock together. Undo with vi.useRealTimers() in afterEach.
 */
export function freezeClock(unixSeconds: number): void {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(unixSeconds * 1000);
}

/** Start of the hour-aligned rate-limit window `offset` hours after the current one. */
export function hourWindow(offset: number): number {
  const now = Math.floor(Date.now() / 1000);
  return now - (now % 3600) + offset * 3600;
}

export function randomIp(): string {
  const r = crypto.getRandomValues(new Uint8Array(3));
  return `10.${r[0]}.${r[1]}.${r[2]}`;
}

export function randomServerId(): string {
  return crypto.randomUUID().toLowerCase();
}

// ---- identities --------------------------------------------------------------------------------

const P256 = { name: "ECDSA", namedCurve: "P-256" } as const;
const ES256 = { name: "ECDSA", hash: "SHA-256" } as const;

export interface Identity {
  id: string;
  spki: string;
  privateKey: CryptoKey;
}

export async function generateKey(): Promise<{ spki: string; privateKey: CryptoKey }> {
  const pair = (await crypto.subtle.generateKey(P256, true, ["sign", "verify"])) as CryptoKeyPair;
  const spki = new Uint8Array((await crypto.subtle.exportKey("spki", pair.publicKey)) as ArrayBuffer);
  return { spki: b64u(spki), privateKey: pair.privateKey };
}

export async function newIdentity(kind: "own" | "dev"): Promise<Identity> {
  const { spki, privateKey } = await generateKey();
  const id = `${kind}_` + base32Lower(await sha256(fromB64u(spki))).slice(0, 26);
  return { id, spki, privateKey };
}

// ---- signed requests ---------------------------------------------------------------------------

export interface SignOptions {
  time?: number;
  nonce?: string;
  /** Sign with a different key than the identity's (wrong-signature tests). */
  signWith?: CryptoKey;
  /** Sign this body text instead of the one sent (body-tampering tests). */
  signedBody?: string;
  keyId?: string;
  ip?: string;
  query?: string;
}

export async function signedRequest(
  identity: Identity,
  method: string,
  path: string,
  body?: unknown,
  options: SignOptions = {},
): Promise<Request> {
  const bodyText = body === undefined ? "" : typeof body === "string" ? body : JSON.stringify(body);
  const time = String(options.time ?? nowSeconds());
  const nonce = options.nonce ?? randomB64u(16);
  const bodyHash = b64u(await sha256(text(options.signedBody ?? bodyText)));
  const canonical = `1SALEM-REQ-V1\n${method}\n${path}\n${time}\n${nonce}\n${bodyHash}`;
  const signature = new Uint8Array(
    await crypto.subtle.sign(ES256, options.signWith ?? identity.privateKey, text(canonical)),
  );
  const headers: Record<string, string> = {
    "X-1S-Key": options.keyId ?? identity.id,
    "X-1S-Time": time,
    "X-1S-Nonce": nonce,
    "X-1S-Sig": b64u(signature),
    "CF-Connecting-IP": options.ip ?? randomIp(),
  };
  if (bodyText !== "") headers["Content-Type"] = "application/json";
  return new Request(BASE + path + (options.query ?? ""), {
    method,
    headers,
    body: bodyText === "" ? undefined : bodyText,
  });
}

export function send(request: Request): Promise<Response> {
  return exports.default.fetch(request);
}

export async function call(
  identity: Identity,
  method: string,
  path: string,
  body?: unknown,
  options?: SignOptions,
): Promise<Response> {
  return send(await signedRequest(identity, method, path, body, options));
}

export async function expectJson(response: Response, status: number): Promise<any> {
  const body = await response.text();
  expect(response.status, body).toBe(status);
  return JSON.parse(body);
}

/** The exact body every "not found" must have. */
export const GENERIC_404 = '{"error":"not_found"}';

export async function expectGeneric404(response: Response): Promise<void> {
  expect(response.status).toBe(404);
  expect(await response.text()).toBe(GENERIC_404);
}

// ---- flows -------------------------------------------------------------------------------------

export async function registerOwner(): Promise<Identity> {
  const owner = await newIdentity("own");
  const body = await expectJson(await call(owner, "POST", "/v1/owners", { spki: owner.spki }), 201);
  expect(body.ownerId).toBe(owner.id);
  return owner;
}

export async function registerDevice(): Promise<Identity> {
  const device = await newIdentity("dev");
  const body = await expectJson(await call(device, "POST", "/v1/devices", { spki: device.spki }), 201);
  expect(body.deviceId).toBe(device.id);
  return device;
}

export async function putServer(owner: Identity, serverId = randomServerId(), label = "Survival"): Promise<string> {
  await expectJson(
    await call(owner, "PUT", `/v1/servers/${serverId}`, { label, protocol: "tcp", hostBridge: "100.101.102.103:7780" }),
    200,
  );
  return serverId;
}

export interface Invite {
  inviteId: string;
  secret: string;
  expiresAt: number;
}

export async function createInvite(owner: Identity, serverId: string, ttlSeconds?: number): Promise<Invite> {
  return expectJson(
    await call(owner, "POST", "/v1/invites", ttlSeconds === undefined ? { serverId } : { serverId, ttlSeconds }),
    201,
  );
}

export function redeem(device: Identity, secret: string, options?: SignOptions): Promise<Response> {
  return call(device, "POST", "/v1/invites/redeem", { secret }, options);
}

export interface Friend {
  owner: Identity;
  device: Identity;
  serverId: string;
  membershipId: string;
  nodeId: string;
}

export async function pendingFriend(owner?: Identity, device?: Identity, serverId?: string): Promise<Friend> {
  const o = owner ?? (await registerOwner());
  const d = device ?? (await registerDevice());
  const sid = serverId ?? (await putServer(o));
  const invite = await createInvite(o, sid);
  const redeemed = await expectJson(await redeem(d, invite.secret), 201);
  return { owner: o, device: d, serverId: sid, membershipId: redeemed.membershipId, nodeId: `fake-node-${randomB64u(6)}` };
}

export async function approve(friend: Friend): Promise<void> {
  await expectJson(await call(friend.owner, "POST", `/v1/memberships/${friend.membershipId}/approve`), 200);
}

export async function bindNode(friend: Friend): Promise<void> {
  await expectJson(
    await call(friend.device, "POST", `/v1/memberships/${friend.membershipId}/node`, { nodeId: friend.nodeId }),
    200,
  );
}

/** Registered owner and device, invite redeemed, approved and node bound: ready for sessions. */
export async function readyFriend(owner?: Identity, device?: Identity, serverId?: string): Promise<Friend> {
  const friend = await pendingFriend(owner, device, serverId);
  await approve(friend);
  await bindNode(friend);
  return friend;
}

export async function requestSession(friend: Friend): Promise<{ response: Response; sessionSpki: string }> {
  const { spki } = await generateKey();
  const response = await call(friend.device, "POST", "/v1/sessions", {
    membershipId: friend.membershipId,
    sessionSpki: spki,
  });
  return { response, sessionSpki: spki };
}

/** Issues a session for a ready friend and returns the ticket and its jti. */
export async function issueTicket(friend: Friend): Promise<{ ticket: string; jti: string }> {
  const body = await expectJson((await requestSession(friend)).response, 201);
  return { ticket: body.ticket, jti: ticketPayload(body.ticket).jti };
}

/** One page of the owner's revocation feed after the exclusive cursor `after` (0 = from the start). */
export function revocationFeed(owner: Identity, after: number | string = 0): Promise<any> {
  return call(owner, "GET", "/v1/owners/me/revocations", undefined, { query: `?after=${after}` }).then((r) =>
    expectJson(r, 200),
  );
}

// ---- tickets -----------------------------------------------------------------------------------

export function ticketPayload(ticket: string): any {
  return JSON.parse(new TextDecoder().decode(fromB64u(ticket.split(".")[1]!)));
}

export interface PublishedKey {
  kid: string;
  alg: string;
  spki: string;
}

export async function publishedKeys(): Promise<PublishedKey[]> {
  const body = await expectJson(await send(new Request(`${BASE}/v1/keys`)), 200);
  return body.keys;
}

/** Verifies a compact JWS the way a host does: exact shape, pinned alg/typ/kid, 64-byte P1363 sig. */
export async function verifyTicket(
  ticket: string,
  keys: PublishedKey[],
): Promise<{ valid: boolean; header: any; payload: any }> {
  const parts = ticket.split(".");
  if (parts.length !== 3) return { valid: false, header: null, payload: null };
  const [h, p, s] = parts as [string, string, string];
  const header = JSON.parse(new TextDecoder().decode(fromB64u(h)));
  const payload = JSON.parse(new TextDecoder().decode(fromB64u(p)));
  const key = keys.find((k) => k.kid === header.kid);
  if (key === undefined || header.alg !== "ES256" || header.typ !== "1salem-ticket+jwt") {
    return { valid: false, header, payload };
  }
  const signature = fromB64u(s);
  if (signature.byteLength !== 64) return { valid: false, header, payload };
  const publicKey = await crypto.subtle.importKey("spki", fromB64u(key.spki), P256, false, ["verify"]);
  const valid = await crypto.subtle.verify(ES256, publicKey, signature, text(`${h}.${p}`));
  return { valid, header, payload };
}
