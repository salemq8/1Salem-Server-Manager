import { env } from "cloudflare:workers";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  approve,
  call,
  expectGeneric404,
  expectJson,
  freezeClock,
  nowSeconds,
  pendingFriend,
  randomB64u,
  registerOwner,
  type Friend,
} from "./helpers";

const enrollmentPath = (friend: Friend) => `/v1/memberships/${friend.membershipId}/enrollment`;

/** Opaque stand-in for the Agent's ECDH-ES ciphertext; the broker never looks inside. */
const fakeCiphertext = () => `v1.${randomB64u(65)}.${randomB64u(12)}.${randomB64u(120)}`;

function store(friend: Friend, ciphertext: string): Promise<Response> {
  return call(friend.owner, "POST", enrollmentPath(friend), { ciphertext });
}

function take(friend: Friend, device = friend.device): Promise<Response> {
  return call(device, "GET", enrollmentPath(friend));
}

async function approvedFriend(owner?: Friend["owner"], serverId?: string): Promise<Friend> {
  const friend = await pendingFriend(owner, undefined, serverId);
  await approve(friend);
  return friend;
}

afterEach(() => {
  vi.useRealTimers();
});

describe("enrollment relay: owner stores", () => {
  it("accepts ciphertext only for the caller's approved membership", async () => {
    const friend = await pendingFriend();
    const stranger = await registerOwner();
    const ciphertext = fakeCiphertext();

    expect((await store(friend, ciphertext)).status).toBe(409);
    await approve(friend);
    await expectGeneric404(await call(stranger, "POST", enrollmentPath(friend), { ciphertext }));
    await expectGeneric404(
      await call(stranger, "POST", "/v1/memberships/mem_aaaaaaaaaaaaaaaaaaaaaaaaaa/enrollment", { ciphertext }),
    );

    const before = nowSeconds();
    const stored = await expectJson(await store(friend, ciphertext), 201);
    expect(stored.membershipId).toBe(friend.membershipId);
    expect(stored.expiresAt - before).toBeGreaterThanOrEqual(900);
    expect(stored.expiresAt - before).toBeLessThanOrEqual(901);

    const row = await env.DB.prepare(`SELECT * FROM enrollments WHERE membership_id = ?1`)
      .bind(friend.membershipId)
      .first<any>();
    expect(row).toMatchObject({ owner_id: friend.owner.id, device_id: friend.device.id, ciphertext });
  });

  it("refuses ciphertext that is empty, not printable ASCII or too long", async () => {
    const friend = await approvedFriend();
    for (const ciphertext of ["", "has spaces", "tab\there", "é", "a".repeat(12289)]) {
      expect((await store(friend, ciphertext)).status, JSON.stringify(ciphertext.slice(0, 12))).toBe(400);
    }
    expect((await call(friend.owner, "POST", enrollmentPath(friend), { ciphertext: 42 })).status).toBe(400);
    expect((await store(friend, "a".repeat(12288))).status).toBe(201);
  });
});

describe("enrollment relay: device picks up", () => {
  it("hands the blob to the membership's own device exactly once", async () => {
    const friend = await approvedFriend();
    const neighbour = await approvedFriend(friend.owner, friend.serverId);
    const ciphertext = fakeCiphertext();
    await expectJson(await store(friend, ciphertext), 201);

    // Another approved friend of the same owner and server cannot read it, and trying does not
    // consume it.
    await expectGeneric404(await take(friend, neighbour.device));

    const taken = await expectJson(await take(friend), 200);
    expect(taken).toEqual({ membershipId: friend.membershipId, ownerId: friend.owner.id, ciphertext });
    await expectGeneric404(await take(friend));
    const left = await env.DB.prepare(`SELECT COUNT(*) AS n FROM enrollments WHERE membership_id = ?1`)
      .bind(friend.membershipId)
      .first<{ n: number }>();
    expect(left!.n).toBe(0);
  });

  it("gives the device the latest blob when the owner stores again", async () => {
    const friend = await approvedFriend();
    await expectJson(await store(friend, fakeCiphertext()), 201);
    const replacement = fakeCiphertext();
    await expectJson(await store(friend, replacement), 201);
    expect((await expectJson(await take(friend), 200)).ciphertext).toBe(replacement);
  });

  it("expires 15 minutes after it was stored", async () => {
    const start = nowSeconds();
    freezeClock(start);
    const late = await approvedFriend();
    const onTime = await approvedFriend(late.owner, late.serverId);
    await expectJson(await store(late, fakeCiphertext()), 201);
    await expectJson(await store(onTime, fakeCiphertext()), 201);

    freezeClock(start + 899);
    await expectJson(await take(onTime), 200);
    freezeClock(start + 900);
    await expectGeneric404(await take(late));
  });

  it("is the same generic 404 for a missing, foreign, pending or never-stored membership", async () => {
    const pending = await pendingFriend();
    const empty = await approvedFriend();
    const answers = await Promise.all([
      take(pending),
      take(empty),
      call(empty.device, "GET", "/v1/memberships/mem_aaaaaaaaaaaaaaaaaaaaaaaaaa/enrollment"),
      take(empty, pending.device),
    ]);
    for (const answer of answers) await expectGeneric404(answer);
  });

  it("is dropped when the membership is revoked", async () => {
    const friend = await approvedFriend();
    await expectJson(await store(friend, fakeCiphertext()), 201);
    await expectJson(await call(friend.owner, "POST", `/v1/memberships/${friend.membershipId}/revoke`), 200);
    await expectGeneric404(await take(friend));
    expect((await store(friend, fakeCiphertext())).status).toBe(409);
  });
});
