import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import {
  approve,
  b64u,
  BASE,
  bindNode,
  call,
  confirmNode,
  expectGeneric404,
  expectJson,
  fromB64u,
  nowSeconds,
  pendingFriend,
  publishedKeys,
  readyFriend,
  registerOwner,
  requestSession,
  send,
  sha256,
  verifyTicket,
} from "./helpers";

describe("GET /v1/keys", () => {
  it("is public and has exactly the {keys:[{kid, alg, spki}]} shape", async () => {
    const response = await send(new Request(`${BASE}/v1/keys`));
    const body = await expectJson(response, 200);
    expect(Object.keys(body)).toEqual(["keys"]);
    expect(body.keys).toHaveLength(1);
    expect(Object.keys(body.keys[0]).sort()).toEqual(["alg", "kid", "spki"]);
    expect(body.keys[0].kid).toMatch(/^[A-Za-z0-9_-]{16}$/);
    // A P-256 SubjectPublicKeyInfo is 91 bytes of DER; nothing private is published.
    expect(fromB64u(body.keys[0].spki).byteLength).toBe(91);
    expect(response.headers.get("Content-Type")).toBe("application/json; charset=utf-8");
    expect(response.headers.get("Cache-Control")).toBe("public, max-age=300");
  });

  it("publishes the ticket key with kid = first 16 chars of base64url(SHA-256(SPKI))", async () => {
    const keys = await publishedKeys();
    expect(keys).toHaveLength(1);
    const [key] = keys;
    expect(key!.alg).toBe("ES256");
    expect(key!.kid).toBe(b64u(await sha256(fromB64u(key!.spki))).slice(0, 16));
    // The published key is a P-256 SPKI.
    await crypto.subtle.importKey("spki", fromB64u(key!.spki), { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
  });
});

describe("session issuance", () => {
  it("is refused before approval", async () => {
    const friend = await pendingFriend();
    await expectGeneric404((await requestSession(friend)).response);
  });

  it("is refused before the node is bound and while the binding is only a candidate", async () => {
    const friend = await pendingFriend();
    await approve(friend);
    await expectGeneric404((await requestSession(friend)).response);
    await bindNode(friend);
    await expectGeneric404((await requestSession(friend)).response);
    await confirmNode(friend);
    expect((await requestSession(friend)).response.status).toBe(201);
  });

  it("is refused after revocation", async () => {
    const friend = await readyFriend();
    expect((await requestSession(friend)).response.status).toBe(201);
    await expectJson(await call(friend.owner, "POST", `/v1/memberships/${friend.membershipId}/revoke`), 200);
    await expectGeneric404((await requestSession(friend)).response);
  });

  it("is refused for another device's membership", async () => {
    const friend = await readyFriend();
    const thief = await readyFriend(friend.owner, undefined, friend.serverId);
    await expectGeneric404((await requestSession({ ...friend, device: thief.device })).response);
  });

  it("issues a ticket that verifies with the published key and carries exactly the §9 claims", async () => {
    const friend = await readyFriend();
    const before = nowSeconds();
    const { response, sessionSpki } = await requestSession(friend);
    const body = await expectJson(response, 201);
    expect(Object.keys(body).sort()).toEqual(["expiresAt", "hostBridge", "ticket"]);
    expect(body.hostBridge).toBe("100.101.102.103:7780");

    const keys = await publishedKeys();
    const { valid, header, payload } = await verifyTicket(body.ticket, keys);
    expect(valid).toBe(true);
    // Header bytes are exactly the contract's layout.
    const encodedHeader = new TextDecoder().decode(fromB64u(body.ticket.split(".")[0]));
    expect(encodedHeader).toBe(`{"alg":"ES256","typ":"1salem-ticket+jwt","kid":"${keys[0]!.kid}"}`);
    expect(header).toEqual({ alg: "ES256", typ: "1salem-ticket+jwt", kid: keys[0]!.kid });

    expect(Object.keys(payload)).toEqual([
      "iss", "aud", "jti", "sub", "mid", "sid", "proto", "nid", "skp", "hb", "av", "iat", "nbf", "exp",
    ]);
    expect(payload).toMatchObject({
      iss: "1salem-connect-broker",
      aud: friend.owner.id,
      sub: friend.device.id,
      mid: friend.membershipId,
      sid: friend.serverId,
      proto: "tcp",
      nid: friend.nodeId,
      skp: sessionSpki,
      hb: "100.101.102.103:7780",
      av: 1,
    });
    expect(payload.jti).toMatch(/^[A-Za-z0-9_-]{22}$/);
    expect(fromB64u(payload.jti).byteLength).toBe(16);
    expect(payload.iat).toBeGreaterThanOrEqual(before);
    expect(payload.nbf).toBe(payload.iat);
    expect(payload.exp - payload.iat).toBe(600);
    expect(body.expiresAt).toBe(payload.exp);

    const row = await env.DB.prepare("SELECT membership_id, exp, revoked_at FROM sessions WHERE jti = ?1")
      .bind(payload.jti)
      .first();
    expect(row).toEqual({ membership_id: friend.membershipId, exp: payload.exp, revoked_at: null });
  });

  it("produces tickets whose tampering is detected", async () => {
    const friend = await readyFriend();
    const { ticket } = await expectJson((await requestSession(friend)).response, 201);
    const keys = await publishedKeys();
    const [h, p, s] = ticket.split(".");

    const claims = JSON.parse(new TextDecoder().decode(fromB64u(p)));
    const forged = b64u(new TextEncoder().encode(JSON.stringify({ ...claims, av: claims.av + 5 })));
    expect((await verifyTicket(`${h}.${forged}.${s}`, keys)).valid).toBe(false);

    const sig = fromB64u(s);
    sig[10] = sig[10]! ^ 0x01;
    expect((await verifyTicket(`${h}.${p}.${b64u(sig)}`, keys)).valid).toBe(false);

    const otherKid = b64u(new TextEncoder().encode(JSON.stringify({ alg: "ES256", typ: "1salem-ticket+jwt", kid: "AAAAAAAAAAAAAAAA" })));
    expect((await verifyTicket(`${otherKid}.${p}.${s}`, keys)).valid).toBe(false);
    expect((await verifyTicket(ticket, keys)).valid).toBe(true);
  });

  it("rejects a session key that is not a P-256 SPKI", async () => {
    const friend = await readyFriend();
    const response = await call(friend.device, "POST", "/v1/sessions", {
      membershipId: friend.membershipId,
      sessionSpki: b64u(crypto.getRandomValues(new Uint8Array(91))),
    });
    expect(response.status).toBe(400);
  });
});

describe("session revocation", () => {
  it("only the owning owner can revoke a ticket, and it appears in the feed", async () => {
    const friend = await readyFriend();
    const { ticket } = await expectJson((await requestSession(friend)).response, 201);
    const jti = JSON.parse(new TextDecoder().decode(fromB64u(ticket.split(".")[1]))).jti;

    const stranger = await registerOwner();
    await expectGeneric404(await call(stranger, "POST", `/v1/sessions/${jti}/revoke`));
    const revoked = await expectJson(await call(friend.owner, "POST", `/v1/sessions/${jti}/revoke`), 200);
    expect(revoked).toEqual({ jti, revoked: true });
    await expectJson(await call(friend.owner, "POST", `/v1/sessions/${jti}/revoke`), 200);

    const feed = await expectJson(await call(friend.owner, "GET", "/v1/owners/me/revocations", undefined, { query: "?after=0" }), 200);
    const events = feed.revocations.filter((r: any) => r.jti === jti);
    expect(events).toEqual([
      { seq: expect.any(Number), kind: "session", jti, membershipId: friend.membershipId, deviceId: friend.device.id, serverId: friend.serverId, av: 1, at: expect.any(Number) },
    ]);
  });
});
