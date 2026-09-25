import { env } from "cloudflare:workers";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  approve,
  call,
  expectGeneric404,
  expectJson,
  freezeClock,
  issueTicket,
  nowSeconds,
  pendingFriend,
  putServer,
  readyFriend,
  registerDevice,
  registerOwner,
  requestSession,
  revocationFeed,
  type Friend,
} from "./helpers";

const revokeMembership = (friend: Friend, as = friend.owner) =>
  call(as, "POST", `/v1/memberships/${friend.membershipId}/revoke`);

async function sessionRow(jti: string): Promise<{ revoked_at: number | null }> {
  const row = await env.DB.prepare(`SELECT revoked_at FROM sessions WHERE jti = ?1`).bind(jti).first<any>();
  return row;
}

async function membershipRow(id: string): Promise<{ state: string; av: number }> {
  return (await env.DB.prepare(`SELECT state, av FROM memberships WHERE id = ?1`).bind(id).first<any>())!;
}

afterEach(() => {
  vi.useRealTimers();
});

describe("single session revocation", () => {
  it("revokes one ticket and leaves the membership and its other tickets alone", async () => {
    const friend = await readyFriend();
    const target = await issueTicket(friend);
    const other = await issueTicket(friend);

    await expectJson(await call(friend.owner, "POST", `/v1/sessions/${target.jti}/revoke`), 200);
    expect((await sessionRow(target.jti)).revoked_at).not.toBeNull();
    expect((await sessionRow(other.jti)).revoked_at).toBeNull();
    expect(await membershipRow(friend.membershipId)).toEqual({ state: "approved", av: 1 });
    expect((await requestSession(friend)).response.status).toBe(201);
  });

  it("answers a missing jti and a foreign jti with the same generic 404", async () => {
    const friend = await readyFriend();
    const { jti } = await issueTicket(friend);
    const stranger = await registerOwner();
    await expectGeneric404(await call(stranger, "POST", `/v1/sessions/${jti}/revoke`));
    await expectGeneric404(await call(friend.owner, "POST", "/v1/sessions/AAAAAAAAAAAAAAAAAAAAAA/revoke"));
    expect((await sessionRow(jti)).revoked_at).toBeNull();
  });
});

describe("membership revocation", () => {
  it("sets revoked, bumps av once, revokes every ticket of that membership only, and feeds the host", async () => {
    const friend = await readyFriend();
    const secondServer = await readyFriend(friend.owner, friend.device);
    const tickets = [await issueTicket(friend), await issueTicket(friend)];
    const unrelated = await issueTicket(secondServer);

    const stranger = await registerOwner();
    await expectGeneric404(await revokeMembership(friend, stranger));
    await expectGeneric404(await call(friend.owner, "POST", "/v1/memberships/mem_aaaaaaaaaaaaaaaaaaaaaaaaaa/revoke"));
    expect(await membershipRow(friend.membershipId)).toEqual({ state: "approved", av: 1 });

    const revoked = await expectJson(await revokeMembership(friend), 200);
    expect(revoked).toEqual({ membershipId: friend.membershipId, state: "revoked", av: 2 });
    for (const { jti } of tickets) expect((await sessionRow(jti)).revoked_at).not.toBeNull();
    expect((await sessionRow(unrelated.jti)).revoked_at).toBeNull();
    await expectGeneric404((await requestSession(friend)).response);
    expect((await requestSession(secondServer)).response.status).toBe(201);

    // Repeating is harmless: no second bump, no second event.
    expect(await expectJson(await revokeMembership(friend), 200)).toEqual(revoked);
    const feed = await revocationFeed(friend.owner);
    expect(feed.revocations.filter((r: any) => r.membershipId === friend.membershipId)).toEqual([
      {
        seq: expect.any(Number),
        kind: "membership",
        membershipId: friend.membershipId,
        deviceId: friend.device.id,
        serverId: friend.serverId,
        av: 2,
        at: expect.any(Number),
      },
    ]);

    const ownerView = await expectJson(await call(friend.owner, "GET", "/v1/owners/me/memberships"), 200);
    expect(ownerView.memberships.map((m: any) => m.membershipId)).toEqual([secondServer.membershipId]);
    const deviceView = await expectJson(await call(friend.device, "GET", "/v1/devices/me/memberships"), 200);
    expect(deviceView.memberships.find((m: any) => m.membershipId === friend.membershipId).state).toBe("revoked");
  });

  it("revoking a pending membership closes it for good", async () => {
    const friend = await pendingFriend();
    expect((await expectJson(await revokeMembership(friend), 200)).state).toBe("revoked");
    expect((await call(friend.owner, "POST", `/v1/memberships/${friend.membershipId}/approve`)).status).toBe(409);
  });
});

describe("device-wide revocation", () => {
  it("revokes the device on every server of the calling owner and nowhere else", async () => {
    const device = await registerDevice();
    const ownerA = await registerOwner();
    const ownerB = await registerOwner();
    const onA1 = await readyFriend(ownerA, device);
    const onA2 = await pendingFriend(ownerA, device, await putServer(ownerA));
    const onB = await readyFriend(ownerB, device);
    const bystander = await readyFriend(ownerA, undefined, onA1.serverId);
    const ticketA = await issueTicket(onA1);
    const ticketB = await issueTicket(onB);
    const ticketBystander = await issueTicket(bystander);

    const result = await expectJson(await call(ownerA, "POST", `/v1/devices/${device.id}/revoke`), 200);
    expect(result).toEqual({ deviceId: device.id, revokedMemberships: 2 });

    expect(await membershipRow(onA1.membershipId)).toEqual({ state: "revoked", av: 2 });
    expect(await membershipRow(onA2.membershipId)).toEqual({ state: "revoked", av: 2 });
    expect((await sessionRow(ticketA.jti)).revoked_at).not.toBeNull();
    await expectGeneric404((await requestSession(onA1)).response);

    // Owner B's membership of the same device, and owner A's other friends, are untouched.
    expect(await membershipRow(onB.membershipId)).toEqual({ state: "approved", av: 1 });
    expect((await sessionRow(ticketB.jti)).revoked_at).toBeNull();
    expect((await sessionRow(ticketBystander.jti)).revoked_at).toBeNull();
    expect((await requestSession(onB)).response.status).toBe(201);

    const feedA = await revocationFeed(ownerA);
    const aboutDevice = feedA.revocations.filter((r: any) => r.deviceId === device.id);
    expect(aboutDevice).toEqual(
      expect.arrayContaining([
        { seq: expect.any(Number), kind: "device", deviceId: device.id, at: expect.any(Number) },
        expect.objectContaining({ kind: "membership", membershipId: onA1.membershipId, av: 2 }),
        expect.objectContaining({ kind: "membership", membershipId: onA2.membershipId, av: 2 }),
      ]),
    );
    expect(aboutDevice).toHaveLength(3);
    const feedB = await revocationFeed(ownerB);
    expect(feedB.revocations).toEqual([]);

    // Again: the device is known to owner A, nothing is left to revoke, no new events.
    expect(await expectJson(await call(ownerA, "POST", `/v1/devices/${device.id}/revoke`), 200)).toEqual({
      deviceId: device.id,
      revokedMemberships: 0,
    });
    expect((await revocationFeed(ownerA)).revocations).toHaveLength(3);
  });

  it("does not reveal whether a device exists to an owner it never joined", async () => {
    const friend = await readyFriend();
    const stranger = await registerOwner();
    const unregistered = "dev_aaaaaaaaaaaaaaaaaaaaaaaaaa";
    await expectGeneric404(await call(stranger, "POST", `/v1/devices/${friend.device.id}/revoke`));
    await expectGeneric404(await call(stranger, "POST", `/v1/devices/${unregistered}/revoke`));
    expect(await membershipRow(friend.membershipId)).toEqual({ state: "approved", av: 1 });
  });
});

describe("revocation feed", () => {
  /** The contract's page size. */
  const PAGE = 1000;

  it("lists only the caller's events in seq order, with an exclusive after= cursor", async () => {
    const t0 = nowSeconds();
    freezeClock(t0);
    const owner = await registerOwner();
    const first = await readyFriend(owner);
    const second = await readyFriend(owner);
    const bystander = await readyFriend();
    const { jti } = await issueTicket(first);
    await expectJson(await call(owner, "POST", `/v1/sessions/${jti}/revoke`), 200);
    // Another owner's event lands between this owner's two: seqs are shared, so they have gaps.
    await expectJson(await revokeMembership(bystander), 200);

    freezeClock(t0 + 10);
    await expectJson(await revokeMembership(second), 200);

    const all = await revocationFeed(owner, 0);
    const [s1, s2] = all.revocations.map((r: any) => r.seq);
    expect(s2).toBeGreaterThan(s1 + 1);
    expect(all).toEqual({
      revocations: [
        {
          seq: s1,
          kind: "session",
          jti,
          membershipId: first.membershipId,
          deviceId: first.device.id,
          serverId: first.serverId,
          av: 1,
          at: t0,
        },
        {
          seq: s2,
          kind: "membership",
          membershipId: second.membershipId,
          deviceId: second.device.id,
          serverId: second.serverId,
          av: 2,
          at: t0 + 10,
        },
      ],
      cursor: s2,
      more: false,
    });
    // No query is the same as after=0.
    expect(await expectJson(await call(owner, "GET", "/v1/owners/me/revocations"), 200)).toEqual(all);
    expect(await revocationFeed(owner, s1)).toEqual({ revocations: [all.revocations[1]], cursor: s2, more: false });
    // An empty page hands back the cursor it was given.
    expect(await revocationFeed(owner, s2)).toEqual({ revocations: [], cursor: s2, more: false });

    const stranger = await registerOwner();
    expect(await revocationFeed(stranger)).toEqual({ revocations: [], cursor: 0, more: false });
  });

  it("delivers an event that commits after a poll even though its time is earlier than the poll", async () => {
    const t = nowSeconds();
    const friend = await readyFriend();

    // The host polls in second t + 1 and keeps the cursor.
    freezeClock(t + 1);
    const polled = await revocationFeed(friend.owner);
    expect(polled.revocations).toEqual([]);

    // A revocation stamped with second t commits only after that poll. A wall-clock cursor taken
    // at t + 1 would skip it forever; the seq cursor cannot.
    freezeClock(t);
    await expectJson(await revokeMembership(friend), 200);

    freezeClock(t + 2);
    const next = await revocationFeed(friend.owner, polled.cursor);
    expect(next.revocations).toEqual([
      expect.objectContaining({ kind: "membership", membershipId: friend.membershipId, av: 2, at: t }),
    ]);
    expect(next.cursor).toBe(next.revocations[0].seq);
  });

  it(`pages ${PAGE} events at a time and says when more are waiting`, async () => {
    const owner = await registerOwner();
    const total = PAGE + 1;
    await env.DB.prepare(
      `WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < ?2)
       INSERT INTO revocations (owner_id, kind, device_id, at)
       SELECT ?1, 'device', 'dev_aaaaaaaaaaaaaaaaaaaaaaaaaa', ?3 FROM n`,
    )
      .bind(owner.id, total, nowSeconds())
      .run();

    const page1 = await revocationFeed(owner);
    expect(page1.revocations).toHaveLength(PAGE);
    expect(page1.more).toBe(true);
    expect(page1.cursor).toBe(page1.revocations.at(-1).seq);

    const page2 = await revocationFeed(owner, page1.cursor);
    expect(page2.revocations).toHaveLength(total - PAGE);
    expect(page2.more).toBe(false);

    const seqs = [...page1.revocations, ...page2.revocations].map((r: any) => r.seq);
    for (let i = 1; i < seqs.length; i++) expect(seqs[i]).toBeGreaterThan(seqs[i - 1]);
    expect(await revocationFeed(owner, page2.cursor)).toEqual({ revocations: [], cursor: page2.cursor, more: false });
  });

  it(`pages correctly from a non-zero cursor, with exactly ${PAGE} and ${PAGE} + 1 events after it`, async () => {
    const owner = await registerOwner();
    const seed = (count: number) =>
      env.DB.prepare(
        `WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < ?2)
         INSERT INTO revocations (owner_id, kind, device_id, at)
         SELECT ?1, 'device', 'dev_aaaaaaaaaaaaaaaaaaaaaaaaaa', ?3 FROM n`,
      )
        .bind(owner.id, count, nowSeconds())
        .run();

    // One event to stand on, then exactly one full page after it: nothing more is waiting.
    await seed(1);
    const start = (await revocationFeed(owner)).cursor;
    await seed(PAGE);
    const full = await revocationFeed(owner, start);
    expect(full.revocations).toHaveLength(PAGE);
    expect(full.revocations[0].seq).toBeGreaterThan(start);
    expect(full.more).toBe(false);

    // One more event: the same cursor now sees a full page and is told the next one is waiting.
    await seed(1);
    const again = await revocationFeed(owner, start);
    expect(again.revocations).toHaveLength(PAGE);
    expect(again.more).toBe(true);
    const rest = await revocationFeed(owner, again.cursor);
    expect(rest.revocations).toHaveLength(1);
    expect(rest.more).toBe(false);
  });

  it("rejects an after that is not a non-negative safe integer", async () => {
    const owner = await registerOwner();
    for (const after of ["", "-1", "abc", "1.5", "1e3", "9007199254740992", "12345678901234567"]) {
      const response = await call(owner, "GET", "/v1/owners/me/revocations", undefined, { query: `?after=${after}` });
      expect(response.status, after).toBe(400);
      expect(await response.text()).toBe('{"error":"bad_request"}');
    }
  });

  it("refuses a cursor this broker never gave the owner instead of silently skipping events", async () => {
    const friend = await readyFriend();
    await expectJson(await revokeMembership(friend), 200);
    const { cursor } = await revocationFeed(friend.owner);
    const feed = (after: number) =>
      call(friend.owner, "GET", "/v1/owners/me/revocations", undefined, { query: `?after=${after}` });

    // A cursor ahead of the table (kept across a restore, or from another broker).
    const ahead = await feed(cursor + 1000);
    expect(ahead.status).toBe(409);
    expect(await ahead.text()).toBe('{"error":"cursor_ahead"}');

    // What a comparison with the table's highest seq misses: after a restore rewinds seq, other
    // owners' new events push the maximum past this owner's kept cursor K, and this owner's next
    // events land below K and would be skipped. A test cannot rewind seq, but it can pin the rule
    // that prevents it: a K that is not one of this owner's own events is refused, even once the
    // maximum has passed it.
    const kept = cursor + 3;
    for (let i = 0; i < 6; i++) await expectJson(await revokeMembership(await readyFriend()), 200);
    const second = await readyFriend(friend.owner);
    await expectJson(await revokeMembership(second), 200);
    const stale = await feed(kept);
    expect(stale.status).toBe(409);
    expect(await stale.text()).toBe('{"error":"cursor_ahead"}');

    // Real cursors keep working, including one below other owners' newer events.
    expect(await revocationFeed(friend.owner, cursor)).toEqual({
      revocations: [expect.objectContaining({ kind: "membership", membershipId: second.membershipId })],
      cursor: expect.any(Number),
      more: false,
    });
    const latest = (await revocationFeed(friend.owner, cursor)).cursor;
    expect(await revocationFeed(friend.owner, latest)).toEqual({ revocations: [], cursor: latest, more: false });
  });

  it("needs the documented post-restore step, which makes a cursor kept across a restore refused", async () => {
    // A restore rolls the rows and the seq counter back together; simulate one that loses the
    // owner's latest event, which a host had already read and kept as its cursor.
    const restore = async (keptSeq: number, restoredTop: number) => {
      await env.DB.prepare(`DELETE FROM revocations WHERE seq > ?1`).bind(restoredTop).run();
      await env.DB.prepare(`UPDATE sqlite_sequence SET seq = ?1 WHERE name = 'revocations'`).bind(restoredTop).run();
      expect(keptSeq).toBeGreaterThan(restoredTop);
    };
    const kept = async () => {
      const friend = await readyFriend();
      await expectJson(await revokeMembership(friend), 200);
      const top = (await revocationFeed(friend.owner)).cursor;
      await expectJson(await revokeMembership(await readyFriend(friend.owner)), 200);
      return { friend, restoredTop: top, cursor: (await revocationFeed(friend.owner, top)).cursor };
    };

    // Without the step: the owner's next event reuses the kept seq and the host skips it silently.
    const a = await kept();
    await restore(a.cursor, a.restoredTop);
    await expectJson(await revokeMembership(await readyFriend(a.friend.owner)), 200);
    expect(await revocationFeed(a.friend.owner, a.cursor)).toEqual({ revocations: [], cursor: a.cursor, more: false });

    // With the step from the README: the kept cursor is refused and the host starts over.
    const b = await kept();
    await restore(b.cursor, b.restoredTop);
    await env.DB.prepare(`UPDATE sqlite_sequence SET seq = seq + 1000000000 WHERE name = 'revocations'`).run();
    await expectJson(await revokeMembership(await readyFriend(b.friend.owner)), 200);
    const stale = await call(b.friend.owner, "GET", "/v1/owners/me/revocations", undefined, { query: `?after=${b.cursor}` });
    expect(stale.status).toBe(409);
    expect(await stale.text()).toBe('{"error":"cursor_ahead"}');
    expect((await revocationFeed(b.friend.owner)).revocations.at(-1).seq).toBeGreaterThan(1000000000);
  });

  it("gives an empty page for a real cursor that sits below other owners' newer events", async () => {
    const friend = await readyFriend();
    await expectJson(await revokeMembership(friend), 200);
    const { cursor } = await revocationFeed(friend.owner);
    await expectJson(await revokeMembership(await readyFriend()), 200);
    expect(await revocationFeed(friend.owner, cursor)).toEqual({ revocations: [], cursor, more: false });
  });

  it("is only for owners", async () => {
    const device = await registerDevice();
    const response = await call(device, "GET", "/v1/owners/me/revocations");
    expect(response.status).toBe(401);
  });
});

describe("approval after re-invite", () => {
  it("a revoked friend can be invited again and starts over at pending", async () => {
    const friend = await readyFriend();
    await expectJson(await revokeMembership(friend), 200);
    const again = await pendingFriend(friend.owner, friend.device, friend.serverId);
    expect(again.membershipId).not.toBe(friend.membershipId);
    expect(await membershipRow(again.membershipId)).toEqual({ state: "pending", av: 1 });
    await approve(again);
  });
});
