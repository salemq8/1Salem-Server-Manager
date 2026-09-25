import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import {
  call,
  createInvite,
  expectGeneric404,
  expectJson,
  fromB64u,
  type Invite,
  nowSeconds,
  putServer,
  randomB64u,
  randomServerId,
  redeem,
  registerDevice,
  registerOwner,
  requestSession,
} from "./helpers";

async function hmacHex(secret: Uint8Array): Promise<string> {
  const key = await crypto.subtle.importKey(
    "raw",
    fromB64u(env.INVITE_PEPPER),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const mac = new Uint8Array(await crypto.subtle.sign("HMAC", key, secret));
  return [...mac].map((b) => b.toString(16).padStart(2, "0")).join("");
}

describe("invite creation", () => {
  it("issues 1000 unique 32-byte base64url secrets with the byte spread of a CSPRNG", async () => {
    // Two owners of 500 each stay under the 600-per-hour owner write limit.
    const owners = [await registerOwner(), await registerOwner()];
    const invites: Invite[] = [];
    for (const owner of owners) {
      const serverId = await putServer(owner);
      for (let batch = 0; batch < 10; batch++) {
        invites.push(...(await Promise.all(Array.from({ length: 50 }, () => createInvite(owner, serverId)))));
      }
    }
    expect(invites).toHaveLength(1000);
    for (const invite of invites) {
      expect(invite.secret).toMatch(/^[A-Za-z0-9_-]{43}$/);
      expect(invite.inviteId).toMatch(/^inv_[a-z2-7]{26}$/);
      expect(fromB64u(invite.secret).byteLength).toBe(32);
    }
    expect(new Set(invites.map((i) => i.secret)).size).toBe(1000);
    expect(new Set(invites.map((i) => i.inviteId)).size).toBe(1000);

    // 32000 uniform bytes: every value appears, each close to 125 times (chi-square with 255
    // degrees of freedom; 400 is far beyond the 99.99th percentile of about 350).
    const counts = new Array<number>(256).fill(0);
    for (const invite of invites) for (const byte of fromB64u(invite.secret)) counts[byte]!++;
    const expected = 32000 / 256;
    const chiSquare = counts.reduce((sum, c) => sum + (c - expected) ** 2 / expected, 0);
    expect(Math.min(...counts)).toBeGreaterThan(0);
    expect(chiSquare).toBeLessThan(400);
  }, 60_000);

  it("stores only the HMAC of the secret", async () => {
    const owner = await registerOwner();
    const serverId = await putServer(owner);
    const invite = await createInvite(owner, serverId);
    const row = await env.DB.prepare("SELECT * FROM invites WHERE id = ?1").bind(invite.inviteId).first<any>();
    expect(row.secret_hmac).toBe(await hmacHex(fromB64u(invite.secret)));
    expect(JSON.stringify(row)).not.toContain(invite.secret);
  });

  it("defaults to 24 h, allows up to 7 days and refuses more", async () => {
    const owner = await registerOwner();
    const serverId = await putServer(owner);
    const before = nowSeconds();
    const standard = await createInvite(owner, serverId);
    expect(standard.expiresAt - before).toBeGreaterThanOrEqual(86400 - 1);
    expect(standard.expiresAt - before).toBeLessThanOrEqual(86400 + 1);
    const week = await createInvite(owner, serverId, 7 * 86400);
    expect(week.expiresAt - nowSeconds()).toBeLessThanOrEqual(7 * 86400);
    const tooLong = await call(owner, "POST", "/v1/invites", { serverId, ttlSeconds: 7 * 86400 + 1 });
    expect(tooLong.status).toBe(400);
    const fractional = await call(owner, "POST", "/v1/invites", { serverId, ttlSeconds: 3600.5 });
    expect(fractional.status).toBe(400);
  });

  it("refuses to invite to another owner's or an unknown server with the generic 404", async () => {
    const owner = await registerOwner();
    const other = await registerOwner();
    const serverId = await putServer(owner);
    await expectGeneric404(await call(other, "POST", "/v1/invites", { serverId }));
    await expectGeneric404(await call(owner, "POST", "/v1/invites", { serverId: randomServerId() }));
  });
});

describe("invite redemption", () => {
  it("creates a pending membership only", async () => {
    const owner = await registerOwner();
    const device = await registerDevice();
    const serverId = await putServer(owner, undefined, "Friends SMP");
    const invite = await createInvite(owner, serverId);
    const body = await expectJson(await redeem(device, invite.secret), 201);
    expect(body).toEqual({ membershipId: expect.stringMatching(/^mem_[a-z2-7]{26}$/), state: "pending", serverLabel: "Friends SMP" });

    const row = await env.DB.prepare("SELECT state, node_id FROM memberships WHERE id = ?1")
      .bind(body.membershipId)
      .first<any>();
    expect(row).toEqual({ state: "pending", node_id: null });
    // Redeeming grants nothing: a pending membership cannot open a session.
    const friend = { owner, device, serverId, membershipId: body.membershipId, nodeId: "unused" };
    await expectGeneric404((await requestSession(friend)).response);
  });

  it("is single use", async () => {
    const owner = await registerOwner();
    const serverId = await putServer(owner);
    const invite = await createInvite(owner, serverId);
    await expectJson(await redeem(await registerDevice(), invite.secret), 201);
    await expectGeneric404(await redeem(await registerDevice(), invite.secret));
  });

  it("refuses an expired invite", async () => {
    const owner = await registerOwner();
    const serverId = await putServer(owner);
    const invite = await createInvite(owner, serverId);
    await env.DB.prepare("UPDATE invites SET expires_at = ?1 WHERE id = ?2").bind(nowSeconds(), invite.inviteId).run();
    await expectGeneric404(await redeem(await registerDevice(), invite.secret));
  });

  it("refuses a revoked invite, and only its owner can revoke it", async () => {
    const owner = await registerOwner();
    const other = await registerOwner();
    const serverId = await putServer(owner);
    const invite = await createInvite(owner, serverId);
    await expectGeneric404(await call(other, "POST", `/v1/invites/${invite.inviteId}/revoke`));
    const revoked = await expectJson(await call(owner, "POST", `/v1/invites/${invite.inviteId}/revoke`), 200);
    expect(revoked).toEqual({ inviteId: invite.inviteId, state: "revoked" });
    await expectGeneric404(await redeem(await registerDevice(), invite.secret));
  });

  it("gives byte-identical answers for wrong, malformed, expired, used and revoked secrets", async () => {
    const owner = await registerOwner();
    const serverId = await putServer(owner);
    const expired = await createInvite(owner, serverId);
    await env.DB.prepare("UPDATE invites SET expires_at = ?1 WHERE id = ?2").bind(nowSeconds() - 5, expired.inviteId).run();
    const used = await createInvite(owner, serverId);
    await expectJson(await redeem(await registerDevice(), used.secret), 201);
    const revoked = await createInvite(owner, serverId);
    await expectJson(await call(owner, "POST", `/v1/invites/${revoked.inviteId}/revoke`), 200);

    const device = await registerDevice();
    const attempts = [randomB64u(32), randomB64u(31), "not a secret", expired.secret, used.secret, revoked.secret];
    const answers = await Promise.all(
      attempts.map(async (secret) => {
        const response = await redeem(device, secret);
        return `${response.status} ${response.headers.get("Content-Type")} ${await response.text()}`;
      }),
    );
    expect(new Set(answers)).toEqual(new Set(['404 application/json; charset=utf-8 {"error":"not_found"}']));
  });

  it("does not let a device hold two live memberships on one server", async () => {
    const owner = await registerOwner();
    const device = await registerDevice();
    const serverId = await putServer(owner);
    await expectJson(await redeem(device, (await createInvite(owner, serverId)).secret), 201);
    const second = await createInvite(owner, serverId);
    const response = await redeem(device, second.secret);
    expect(response.status).toBe(409);
    // The batch rolled back: the second invite is still usable by someone else.
    await expectJson(await redeem(await registerDevice(), second.secret), 201);
  });
});
