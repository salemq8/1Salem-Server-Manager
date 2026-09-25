#!/usr/bin/env node
// End-to-end proof of the broker control plane against a LOCAL broker (`wrangler dev --local`).
//
// Usage: node scripts/local-proof.mjs [--base-url http://127.0.0.1:8787]
//
// Runs one full friend lifecycle (docs/CONNECT_ARCHITECTURE.md §5-§14): published keys, owner and
// device registration, server, invite, redemption, approval, enrollment relay, node binding,
// session ticket, replay and tamper checks, revocation and the revocation feed. It prints one JSON
// summary with the published keyset and the single ticket it was issued (revoked before the proof
// ends), and exits 0 only when every check passed.
//
// Safety: the base URL must be a loopback IP literal (127.0.0.0/8 or ::1) and redirects are
// refused, so the proof cannot reach a deployed broker. Everything it registers is fake-mode data:
// a loopback host bridge, a "fake-proof-" node id and opaque test bytes standing in for the
// enrollment ciphertext. The invite secret and private keys stay in memory and are never printed.
//
// Exit codes: 0 all checks passed, 1 a check failed, 2 usage error or broker unreachable.

import { isIP } from "node:net";
import { parseArgs } from "node:util";

const { subtle } = globalThis.crypto;
const P256 = { name: "ECDSA", namedCurve: "P-256" };
const ES256 = { name: "ECDSA", hash: "SHA-256" };
const GENERIC_404 = '{"error":"not_found"}';
const FAKE_HOST_BRIDGE = "127.0.0.1:7780";
const REQUEST_TIMEOUT_MS = 10_000;

// ---- arguments ----------------------------------------------------------------------------------

function usageError(message) {
  console.error(`error: ${message}`);
  console.error("usage: node scripts/local-proof.mjs [--base-url http://127.0.0.1:8787]");
  process.exit(2);
}

/** Returns the origin when `text` is a plain http(s) URL whose host is a loopback IP literal. */
function loopbackOrigin(text) {
  let url;
  try {
    url = new URL(text);
  } catch {
    return null;
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") return null;
  if (url.username || url.password || url.search || url.hash || url.pathname !== "/") return null;
  // Names such as "localhost" are refused: a hosts file or resolver could point them elsewhere.
  const host = url.hostname.replace(/^\[(.*)\]$/, "$1");
  const loopback = (isIP(host) === 4 && host.startsWith("127.")) || (isIP(host) === 6 && host === "::1");
  return loopback ? url.origin : null;
}

let options;
try {
  options = parseArgs({
    options: { "base-url": { type: "string", default: "http://127.0.0.1:8787" }, help: { type: "boolean" } },
    strict: true,
  }).values;
} catch (error) {
  usageError(error.message);
}
if (options.help) usageError("no action requested");
const base = loopbackOrigin(options["base-url"]);
if (base === null) usageError("--base-url must be http(s)://<loopback IP literal>[:port] with no path");

// ---- encodings and keys -------------------------------------------------------------------------

const utf8 = (text) => new TextEncoder().encode(text);
const fromUtf8 = (bytes) => new TextDecoder("utf-8", { fatal: true }).decode(bytes);
const b64u = (bytes) => Buffer.from(bytes).toString("base64url");
const fromB64u = (text) => new Uint8Array(Buffer.from(text, "base64url"));
const random = (length) => crypto.getRandomValues(new Uint8Array(length));
const nowSeconds = () => Math.floor(Date.now() / 1000);
const sha256 = async (bytes) => new Uint8Array(await subtle.digest("SHA-256", bytes));

function base32Lower(bytes) {
  const alphabet = "abcdefghijklmnopqrstuvwxyz234567";
  let out = "";
  let bits = 0;
  let value = 0;
  for (const byte of bytes) {
    value = (value << 8) | byte;
    bits += 8;
    while (bits >= 5) {
      bits -= 5;
      out += alphabet[(value >> bits) & 31];
    }
    value &= (1 << bits) - 1;
  }
  if (bits > 0) out += alphabet[(value << (5 - bits)) & 31];
  return out;
}

/** P-256 key pair; the private key is non-extractable and lives only in this process. */
async function newKey() {
  const pair = await subtle.generateKey(P256, false, ["sign", "verify"]);
  const spkiDer = new Uint8Array(await subtle.exportKey("spki", pair.publicKey));
  return { spkiDer, spki: b64u(spkiDer), privateKey: pair.privateKey };
}

/** own_/dev_ + first 26 chars of lowercase base32(SHA-256(SPKI DER)) (§5). */
async function newIdentity(prefix) {
  const key = await newKey();
  return { ...key, id: `${prefix}_` + base32Lower(await sha256(key.spkiDer)).slice(0, 26) };
}

// ---- HTTP ---------------------------------------------------------------------------------------

class BrokerUnreachable extends Error {}

/** Builds a signed request (§5 canonical string). Build once and send twice to test replay. */
async function signedRequest(identity, method, path, body, { query = "", timeOffset = 0 } = {}) {
  const bodyText = body === undefined ? "" : JSON.stringify(body);
  const time = String(nowSeconds() + timeOffset);
  const nonce = b64u(random(16));
  const canonical = ["1SALEM-REQ-V1", method, path, time, nonce, b64u(await sha256(utf8(bodyText)))].join("\n");
  const signature = new Uint8Array(await subtle.sign(ES256, identity.privateKey, utf8(canonical)));
  const headers = { "X-1S-Key": identity.id, "X-1S-Time": time, "X-1S-Nonce": nonce, "X-1S-Sig": b64u(signature) };
  if (bodyText !== "") headers["Content-Type"] = "application/json";
  return { url: base + path + query, method, headers, body: bodyText === "" ? undefined : bodyText };
}

async function send(request) {
  let response;
  try {
    response = await fetch(request.url, {
      method: request.method,
      headers: request.headers,
      body: request.body,
      redirect: "error",
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });
  } catch (error) {
    throw new BrokerUnreachable(`${request.method} ${new URL(request.url).pathname}: ${error.cause?.code ?? error.name}`);
  }
  const text = await response.text();
  let json = null;
  try {
    json = JSON.parse(text);
  } catch {
    // Non-JSON bodies are reported by status only.
  }
  return { status: response.status, text, json };
}

const call = async (identity, method, path, body, options) => send(await signedRequest(identity, method, path, body, options));
const get = (path) => send({ url: base + path, method: "GET", headers: {} });

/** Broker errors are generic codes, so they are safe to show. */
const describe = (response) => `HTTP ${response.status}${response.json?.error ? ` ${response.json.error}` : ""}`;
const isGeneric404 = (response) => response.status === 404 && response.text === GENERIC_404;

// ---- ticket verification (as a host or friend transport would, §9) -----------------------------

async function verifyTicket(ticket, verifiers) {
  const parts = ticket.split(".");
  if (parts.length !== 3) return { valid: false, reason: "not a three-part compact JWS" };
  let header;
  let payload;
  try {
    header = JSON.parse(fromUtf8(fromB64u(parts[0])));
    payload = JSON.parse(fromUtf8(fromB64u(parts[1])));
  } catch {
    return { valid: false, reason: "header or payload is not base64url JSON" };
  }
  if (header.alg !== "ES256" || header.typ !== "1salem-ticket+jwt") return { valid: false, reason: "alg/typ" };
  const key = verifiers.get(header.kid);
  if (key === undefined) return { valid: false, reason: "kid is not published" };
  const signature = fromB64u(parts[2]);
  if (signature.byteLength !== 64) return { valid: false, reason: "signature is not 64-byte P1363" };
  const valid = await subtle.verify(ES256, key, signature, utf8(`${parts[0]}.${parts[1]}`));
  return { valid, reason: valid ? undefined : "signature does not verify", header, payload };
}

// ---- the proof ----------------------------------------------------------------------------------

class StepFailed extends Error {}

const checks = [];
function check(name, ok, detail) {
  checks.push(ok ? { name, ok: true } : { name, ok: false, detail });
  return Boolean(ok);
}
/** A check the rest of the lifecycle depends on. */
function must(name, ok, detail) {
  if (!check(name, ok, detail)) throw new StepFailed(name);
}

const summary = {
  proof: "1Salem Connect broker, local lifecycle",
  broker: base,
  mode: "fake: loopback host bridge, fake node id, opaque enrollment bytes; nothing deployed",
  startedAt: new Date().toISOString(),
  keys: null,
  ids: {},
  ticket: null,
  revocations: null,
};

async function run() {
  let r = await get("/v1/keys");
  must("GET /v1/keys publishes a keyset", r.status === 200 && Array.isArray(r.json?.keys) && r.json.keys.length > 0, describe(r));
  summary.keys = r.json;
  const verifiers = new Map();
  for (const key of r.json.keys) {
    const der = fromB64u(key.spki);
    const kidMatches = b64u(await sha256(der)).slice(0, 16) === key.kid;
    must(`published key ${key.kid} is ES256 with kid = SHA-256(SPKI) prefix`, key.alg === "ES256" && kidMatches);
    verifiers.set(key.kid, await subtle.importKey("spki", der, P256, false, ["verify"]));
  }

  const owner = await newIdentity("own");
  const device = await newIdentity("dev");
  r = await call(owner, "POST", "/v1/owners", { spki: owner.spki });
  must("owner registers with a self-signed request", [200, 201].includes(r.status) && r.json?.ownerId === owner.id, describe(r));
  r = await call(device, "POST", "/v1/devices", { spki: device.spki });
  must("device registers with a self-signed request", [200, 201].includes(r.status) && r.json?.deviceId === device.id, describe(r));
  summary.ids.ownerId = owner.id;
  summary.ids.deviceId = device.id;

  const serverId = crypto.randomUUID();
  r = await call(owner, "PUT", `/v1/servers/${serverId}`, { label: "Local proof (fake mode)", protocol: "tcp", hostBridge: FAKE_HOST_BRIDGE });
  must("owner registers a server", r.status === 200, describe(r));
  summary.ids.serverId = serverId;

  r = await call(owner, "POST", "/v1/invites", { serverId, ttlSeconds: 600 });
  const secretOk = typeof r.json?.secret === "string" && /^[A-Za-z0-9_-]{43}$/.test(r.json.secret);
  must("owner creates an invite with a 32-byte secret", r.status === 201 && secretOk, describe(r));
  const secret = r.json.secret;
  summary.ids.inviteId = r.json.inviteId;

  r = await call(device, "POST", "/v1/invites/redeem", { secret });
  must("device redeems the invite into a pending membership", r.status === 201 && r.json?.state === "pending", describe(r));
  const membershipId = r.json.membershipId;
  summary.ids.membershipId = membershipId;
  r = await call(device, "POST", "/v1/invites/redeem", { secret });
  check("the same invite cannot be redeemed twice (generic 404)", isGeneric404(r), describe(r));

  const session = await newKey();
  r = await call(device, "POST", "/v1/sessions", { membershipId, sessionSpki: session.spki });
  check("no ticket before approval (generic 404)", isGeneric404(r), describe(r));

  r = await call(owner, "GET", "/v1/owners/me/memberships");
  const listed = r.json?.memberships?.find((m) => m.membershipId === membershipId);
  must("owner sees the pending friend and its device key", listed?.state === "pending" && listed?.deviceSpki === device.spki, describe(r));
  r = await call(owner, "POST", `/v1/memberships/${membershipId}/approve`);
  must("owner approves the membership", r.status === 200 && r.json?.state === "approved", describe(r));

  const ciphertext = `proof-opaque.${b64u(random(96))}`;
  r = await call(owner, "POST", `/v1/memberships/${membershipId}/enrollment`, { ciphertext });
  const ttlOk = typeof r.json?.expiresAt === "number" && Math.abs(r.json.expiresAt - (nowSeconds() + 900)) <= 5;
  must("owner stores enrollment ciphertext for 15 minutes", r.status === 201 && ttlOk, describe(r));
  r = await call(device, "GET", `/v1/memberships/${membershipId}/enrollment`);
  must("device picks up exactly the stored ciphertext", r.status === 200 && r.json?.ciphertext === ciphertext && r.json?.ownerId === owner.id, describe(r));
  r = await call(device, "GET", `/v1/memberships/${membershipId}/enrollment`);
  check("enrollment pickup is one-time (generic 404 afterwards)", isGeneric404(r), describe(r));

  const nodeId = `fake-proof-${Buffer.from(random(6)).toString("hex")}`;
  r = await call(device, "POST", `/v1/memberships/${membershipId}/node`, { nodeId });
  must("device binds its fake node id", r.status === 200 && r.json?.nodeId === nodeId, describe(r));
  summary.ids.nodeId = nodeId;

  r = await call(device, "POST", "/v1/sessions", { membershipId, sessionSpki: session.spki });
  must("device receives a session ticket", r.status === 201 && typeof r.json?.ticket === "string", describe(r));
  const issued = r.json;
  const verdict = await verifyTicket(issued.ticket, verifiers);
  must("ticket verifies against the published key (ES256, 64-byte P1363)", verdict.valid, verdict.reason);
  const c = verdict.payload;
  summary.ticket = { compact: issued.ticket, header: verdict.header, claims: c, revokedDuringProof: true };
  check(
    "ticket claims bind owner, device, membership, server, node, session key, host bridge and av",
    c.iss === "1salem-connect-broker" && c.aud === owner.id && c.sub === device.id && c.mid === membershipId &&
      c.sid === serverId && c.proto === "tcp" && c.nid === nodeId && c.skp === session.spki &&
      c.hb === FAKE_HOST_BRIDGE && c.av === 1 && c.nbf === c.iat && c.exp - c.iat === 600 &&
      issued.expiresAt === c.exp && issued.hostBridge === FAKE_HOST_BRIDGE,
  );
  const [h, , s] = issued.ticket.split(".");
  const forged = b64u(utf8(JSON.stringify({ ...c, av: c.av + 1 })));
  check("a ticket with a tampered payload fails verification", !(await verifyTicket(`${h}.${forged}.${s}`, verifiers)).valid);

  const once = await signedRequest(device, "GET", "/v1/devices/me/memberships");
  const first = await send(once);
  const replay = await send(once);
  check("a replayed signed request is refused", first.status === 200 && replay.status === 401, `${describe(first)} then ${describe(replay)}`);
  r = await call(device, "GET", "/v1/devices/me/memberships", undefined, { timeOffset: -301 });
  check("a request signed more than 300 s ago is refused", r.status === 401, describe(r));

  // A host follows the feed with the seq cursor of its last poll, never with a time.
  r = await call(owner, "GET", "/v1/owners/me/revocations");
  must("owner reads the revocation feed cursor", r.status === 200 && Number.isSafeInteger(r.json?.cursor), describe(r));
  const feedCursor = r.json.cursor;

  r = await call(owner, "POST", `/v1/sessions/${c.jti}/revoke`);
  check("owner revokes the ticket by jti", r.status === 200 && r.json?.revoked === true, describe(r));
  r = await call(owner, "POST", `/v1/memberships/${membershipId}/revoke`);
  must("owner revokes the membership, raising av to 2", r.status === 200 && r.json?.state === "revoked" && r.json?.av === 2, describe(r));
  r = await call(device, "POST", "/v1/sessions", { membershipId, sessionSpki: session.spki });
  check("no ticket after revocation (generic 404)", isGeneric404(r), describe(r));

  r = await call(owner, "GET", "/v1/owners/me/revocations", undefined, { query: `?after=${feedCursor}` });
  const events = r.json?.revocations ?? [];
  summary.revocations = events;
  check(
    "revocation feed carries the session and membership events after the cursor",
    r.status === 200 &&
      events.some((e) => e.kind === "session" && e.jti === c.jti) &&
      events.some((e) => e.kind === "membership" && e.membershipId === membershipId && e.av === 2),
    describe(r),
  );
  const nextCursor = r.json?.cursor;
  check(
    "feed events are in seq order and the returned cursor is the last seq",
    events.length > 0 &&
      events.every((e, i) => e.seq > (i === 0 ? feedCursor : events[i - 1].seq)) &&
      nextCursor === events.at(-1).seq &&
      r.json?.more === false,
  );
  r = await call(owner, "GET", "/v1/owners/me/revocations", undefined, { query: `?after=${nextCursor}` });
  check(
    "polling again from the returned cursor yields nothing new",
    r.status === 200 && r.json?.revocations?.length === 0 && r.json?.cursor === nextCursor,
    describe(r),
  );
}

let exitCode = 0;
try {
  await run();
} catch (error) {
  if (error instanceof BrokerUnreachable) {
    summary.error = `broker unreachable at ${base} (${error.message}); start it with "npm run dev"`;
    exitCode = 2;
  } else if (!(error instanceof StepFailed)) {
    summary.error = `${error.name}: ${error.message}`;
  }
}
const failed = checks.filter((c) => !c.ok).length;
if (exitCode === 0 && (failed > 0 || summary.error !== undefined)) exitCode = 1;
Object.assign(summary, { checks, passed: checks.length - failed, failed, ok: exitCode === 0 });
console.log(JSON.stringify(summary, null, 2));
process.exit(exitCode);
