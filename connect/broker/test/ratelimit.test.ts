import { env } from "cloudflare:workers";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { RequestContext } from "../src/context";
import { hit } from "../src/ratelimit";
import {
  BASE,
  call,
  createInvite,
  expectGeneric404,
  expectJson,
  freezeClock,
  generateKey,
  hourWindow,
  newIdentity,
  nowSeconds,
  putServer,
  randomB64u,
  readyFriend,
  redeem,
  registerDevice,
  registerOwner,
  requestSession,
  send,
  signedRequest,
  type Friend,
  type Identity,
} from "./helpers";

const RATE_LIMITED = '{"error":"rate_limited"}';

async function expectRateLimited(response: Response, retryAfter: number): Promise<void> {
  expect(response.status).toBe(429);
  expect(await response.text()).toBe(RATE_LIMITED);
  expect(response.headers.get("Retry-After")).toBe(String(retryAfter));
}

function registerFrom(ip: string): Promise<Response> {
  return newIdentity("dev").then((device) => call(device, "POST", "/v1/devices", { spki: device.spki }, { ip }));
}

/** Start of the minute-aligned window `offset` minutes after the current one. */
function minuteWindow(offset: number): number {
  const now = Math.floor(Date.now() / 1000);
  return now - (now % 60) + offset * 60;
}

async function counter(bucket: string, windowStart: number): Promise<number> {
  const row = await env.DB.prepare(`SELECT hits FROM rate_counters WHERE bucket = ?1 AND window_start = ?2`)
    .bind(bucket, windowStart)
    .first<{ hits: number }>();
  return row?.hits ?? 0;
}

/** Spending a whole budget with real requests would only slow the suite down; the counter row is
 *  the state the limiter keeps, so tests seed it. */
async function seedCounter(bucket: string, windowStart: number, windowSeconds: number, hits: number): Promise<void> {
  await env.DB.prepare(
    `INSERT INTO rate_counters (bucket, window_start, hits, expires_at) VALUES (?1, ?2, ?3, ?4)
     ON CONFLICT (bucket, window_start) DO UPDATE SET hits = excluded.hits`,
  )
    .bind(bucket, windowStart, hits, windowStart + windowSeconds)
    .run();
}

async function nonceStored(keyId: string, nonce: string): Promise<boolean> {
  const row = await env.DB.prepare(`SELECT 1 AS n FROM request_nonces WHERE key_id = ?1 AND nonce = ?2`)
    .bind(keyId, nonce)
    .first();
  return row !== null;
}

/** env.DB, except that first() goes through run() so every statement's rows_written is recorded. */
function recordingDb(written: number[]): D1Database {
  const wrap = (statement: D1PreparedStatement): D1PreparedStatement =>
    ({
      bind: (...values: unknown[]) => wrap(statement.bind(...values)),
      first: async () => {
        const result = await statement.run();
        written.push(result.meta.rows_written);
        return result.results[0] ?? null;
      },
    }) as unknown as D1PreparedStatement;
  return { prepare: (sql: string) => wrap(env.DB.prepare(sql)) } as unknown as D1Database;
}

afterEach(() => {
  vi.useRealTimers();
});

describe("fixed-window rate limits", () => {
  it("redeem per device: failures count, the 11th attempt is refused, the next window starts fresh", async () => {
    const window = hourWindow(1);
    freezeClock(window + 60);
    const owner = await registerOwner();
    const serverId = await putServer(owner);
    const invite = await createInvite(owner, serverId);
    const device = await registerDevice();

    for (let i = 0; i < 10; i++) await expectGeneric404(await redeem(device, randomB64u(32)));
    // Even a valid secret is refused once the budget is spent, and it is not consumed.
    await expectRateLimited(await redeem(device, invite.secret), 3600 - 60);

    freezeClock(window + 3600);
    await expectJson(await redeem(device, invite.secret), 201);
  });

  it("redeem per IP applies across devices", async () => {
    freezeClock(hourWindow(1) + 10);
    const ip = "10.200.0.1";
    for (let i = 0; i < 30; i++) {
      await expectGeneric404(await redeem(await registerDevice(), randomB64u(32), { ip }));
    }
    await expectRateLimited(await redeem(await registerDevice(), randomB64u(32), { ip }), 3600 - 10);
    await expectGeneric404(await redeem(await registerDevice(), randomB64u(32), { ip: "10.200.0.2" }));
  });

  it("registration per IP trips after 30 and resets in the next window", async () => {
    const window = hourWindow(1);
    freezeClock(window + 3599);
    const ip = "10.201.0.1";
    for (let i = 0; i < 30; i++) expect((await registerFrom(ip)).status).toBe(201);
    await expectRateLimited(await registerFrom(ip), 1);
    expect((await registerFrom("10.201.0.2")).status).toBe(201);

    freezeClock(window + 3600);
    expect((await registerFrom(ip)).status).toBe(201);
  });

  it("sessions per device trip after 120 tickets in a window", async () => {
    freezeClock(hourWindow(1) + 100);
    const friend = await readyFriend();
    for (let i = 0; i < 120; i++) expect((await requestSession(friend)).response.status).toBe(201);
    await expectRateLimited((await requestSession(friend)).response, 3600 - 100);
  }, 30_000);

  it("owner writes trip after 600 per window; owner reads have a budget of their own", async () => {
    const window = hourWindow(1);
    freezeClock(window + 5);
    const owner = await registerOwner();
    const serverId = await putServer(owner);
    // putServer above already used one of the 600.
    await seedCounter(`owner-write:${owner.id}`, window, 3600, 599);
    await createInvite(owner, serverId);
    await expectRateLimited(await call(owner, "POST", "/v1/invites", { serverId }), 3600 - 5);
    await expectJson(await call(owner, "GET", "/v1/owners/me/memberships"), 200);
  });

  it("keeps a pseudonym of the client IP, never the address", async () => {
    freezeClock(hourWindow(1) + 1);
    const ip = "10.202.3.4";
    expect((await registerFrom(ip)).status).toBe(201);
    const { results } = await env.DB.prepare(`SELECT bucket FROM rate_counters WHERE bucket LIKE 'register-ip:%'`).all<{
      bucket: string;
    }>();
    expect(results.length).toBeGreaterThan(0);
    for (const row of results) expect(row.bucket).not.toContain(ip);
  });
});

describe("a spent budget adds no hit, and nothing is charged or run twice", () => {
  it("a refused hit leaves the counter at its limit and writes no row", async () => {
    const written: number[] = [];
    const now = hourWindow(1) + 30;
    const ctx = { env: { DB: recordingDb(written) }, now } as unknown as RequestContext;
    const limit = { name: "test-hit", max: 3, windowSeconds: 3600 };
    const subject = randomB64u(8);

    for (let i = 0; i < 3; i++) await hit(ctx, limit, subject);
    for (let i = 0; i < 3; i++) {
      await expect(hit(ctx, limit, subject)).rejects.toMatchObject({
        status: 429,
        headers: { "Retry-After": String(3600 - 30) },
      });
    }
    expect(written.slice(0, 3).every((rows) => rows > 0)).toBe(true);
    expect(written.slice(3)).toEqual([0, 0, 0]);
    expect(await counter(`test-hit:${subject}`, hourWindow(1))).toBe(3);
  });

  it("an authenticated call over its budget adds no hit and is used up, so its bytes never run later", async () => {
    // Ten seconds before a window boundary, so the replay below lands in a fresh window while the
    // request is still well inside its 300 s signing window: only the spent nonce can refuse it.
    const next = hourWindow(1);
    freezeClock(next - 10);
    const owner = await registerOwner();
    const serverId = await putServer(owner);
    await seedCounter(`owner-write:${owner.id}`, next - 3600, 3600, 600);

    // A captured request refused for its budget ...
    const refused = await signedRequest(owner, "POST", "/v1/invites", { serverId });
    expect((await send(refused.clone())).status).toBe(429);
    expect(await counter(`owner-write:${owner.id}`, next - 3600)).toBe(600);
    expect(await nonceStored(owner.id, refused.headers.get("X-1S-Nonce")!)).toBe(true);

    // ... cannot be replayed into the next window.
    freezeClock(next + 1);
    expect((await send(refused.clone())).status).toBe(401);
    expect(await counter(`owner-write:${owner.id}`, next)).toBe(0);
    const { results } = await env.DB.prepare(`SELECT id FROM invites WHERE owner_id = ?1`).bind(owner.id).all();
    expect(results).toHaveLength(0);
  });

  it("a request whose signature fails is never charged and writes nothing", async () => {
    const window = minuteWindow(1);
    freezeClock(window + 1);
    const owner = await registerOwner();
    const { privateKey } = await generateKey();
    const nonce = randomB64u(16);
    const forged = await call(owner, "GET", "/v1/owners/me/memberships", undefined, { nonce, signWith: privateKey });
    expect(forged.status).toBe(401);
    expect(await counter(`owner-read:${owner.id}`, window)).toBe(0);
    expect(await nonceStored(owner.id, nonce)).toBe(false);
  });

  it("a replayed request is refused before it spends the key's budget", async () => {
    const window = minuteWindow(1);
    freezeClock(window + 1);
    const owner = await registerOwner();
    const request = await signedRequest(owner, "GET", "/v1/owners/me/memberships");
    await expectJson(await send(request.clone()), 200);
    for (let i = 0; i < 5; i++) expect((await send(request.clone())).status).toBe(401);
    expect(await counter(`owner-read:${owner.id}`, window)).toBe(1);
  });

  it("concurrent copies of one captured request run once and are charged once", async () => {
    const window = minuteWindow(1);
    freezeClock(window + 1);
    const owner = await registerOwner();
    const request = await signedRequest(owner, "GET", "/v1/owners/me/memberships");
    const statuses = (await Promise.all(Array.from({ length: 20 }, () => send(request.clone())))).map((r) => r.status);
    expect(statuses.filter((s) => s === 200)).toHaveLength(1);
    expect(statuses.filter((s) => s === 401)).toHaveLength(19);
    expect(await counter(`owner-read:${owner.id}`, window)).toBe(1);
  });

  it("malformed registrations are refused before the network is charged", async () => {
    freezeClock(hourWindow(1) + 20);
    const ip = "10.205.0.1";
    const device = await newIdentity("dev");
    const junk: (() => Promise<Request>)[] = [
      async () =>
        new Request(`${BASE}/v1/devices`, {
          method: "POST",
          headers: { "CF-Connecting-IP": ip, "Content-Type": "application/json" },
          body: "not json",
        }),
      () => signedRequest(device, "POST", "/v1/devices", { spki: 5 }, { ip }),
      () => signedRequest(device, "POST", "/v1/devices", { spki: device.spki }, { ip, keyId: "dev_short" }),
      () => signedRequest(device, "POST", "/v1/devices", { spki: device.spki }, { ip, time: nowSeconds() - 301 }),
    ];
    for (let i = 0; i < 32; i++) {
      const status = (await send(await junk[i % junk.length]!())).status;
      expect([400, 401]).toContain(status);
    }
    // More than the 30-per-window budget was refused above, yet this network may still register.
    await expectJson(await call(device, "POST", "/v1/devices", { spki: device.spki }, { ip }), 201);
  });
});

describe("per-caller budgets on reads, enrollment pickup and node binding", () => {
  const routes: {
    name: string;
    bucket: "owner-read" | "device-call";
    send: (friend: Friend, as?: Identity) => Promise<Response>;
  }[] = [
    { name: "GET /v1/owners/me/memberships", bucket: "owner-read", send: (f, as = f.owner) => call(as, "GET", "/v1/owners/me/memberships") },
    { name: "GET /v1/owners/me/revocations", bucket: "owner-read", send: (f, as = f.owner) => call(as, "GET", "/v1/owners/me/revocations") },
    { name: "GET /v1/devices/me/memberships", bucket: "device-call", send: (f, as = f.device) => call(as, "GET", "/v1/devices/me/memberships") },
    {
      name: "GET /v1/memberships/{id}/enrollment",
      bucket: "device-call",
      send: (f, as = f.device) => call(as, "GET", `/v1/memberships/${f.membershipId}/enrollment`),
    },
    {
      name: "POST /v1/memberships/{id}/node",
      bucket: "device-call",
      send: (f, as = f.device) => call(as, "POST", `/v1/memberships/${f.membershipId}/node`, { nodeId: f.nodeId }),
    },
  ];

  for (const route of routes) {
    it(`${route.name} is refused once its caller's ${route.bucket} budget is spent, and only for that caller`, async () => {
      const window = minuteWindow(1);
      freezeClock(window + 15);
      const friend = await readyFriend();
      const caller = route.bucket === "owner-read" ? friend.owner : friend.device;
      await seedCounter(`${route.bucket}:${caller.id}`, window, 60, 120);

      await expectRateLimited(await route.send(friend), 60 - 15);
      const other = route.bucket === "owner-read" ? await registerOwner() : await registerDevice();
      expect((await route.send(friend, other)).status).not.toBe(429);

      freezeClock(window + 60);
      expect((await route.send(friend)).status).not.toBe(429);
    });
  }

  it("an Agent polling both owner reads every 2 s stays well inside the budget, which is still finite", async () => {
    const window = minuteWindow(1);
    const owner = await registerOwner();
    for (let second = 0; second < 60; second += 2) {
      freezeClock(window + second);
      await expectJson(await call(owner, "GET", "/v1/owners/me/memberships"), 200);
      await expectJson(await call(owner, "GET", "/v1/owners/me/revocations"), 200);
    }
    freezeClock(window + 59);
    for (let i = 0; i < 60; i++) await expectJson(await call(owner, "GET", "/v1/owners/me/revocations"), 200);
    await expectRateLimited(await call(owner, "GET", "/v1/owners/me/revocations"), 1);
  }, 30_000);
});

describe("IP buckets count client networks", () => {
  it("rotating addresses inside one IPv6 /64 shares one registration bucket", async () => {
    freezeClock(hourWindow(1) + 40);
    const inside = () => `2001:db8:77:1:${[0, 0, 0, 0].map(() => (crypto.getRandomValues(new Uint16Array(1))[0]!).toString(16)).join(":")}`;
    for (let i = 0; i < 30; i++) expect((await registerFrom(inside())).status).toBe(201);
    await expectRateLimited(await registerFrom(inside()), 3600 - 40);
    expect((await registerFrom("2001:db8:77:2::1")).status).toBe(201);
  });

  it("an IPv4-mapped IPv6 address shares the bucket of the IPv4 address it carries", async () => {
    freezeClock(hourWindow(1) + 50);
    for (let i = 0; i < 15; i++) expect((await registerFrom("10.206.0.1")).status).toBe(201);
    for (let i = 0; i < 15; i++) expect((await registerFrom("::ffff:10.206.0.1")).status).toBe(201);
    await expectRateLimited(await registerFrom("10.206.0.1"), 3600 - 50);
    // 10.206.0.1 again, spelled as hex groups.
    await expectRateLimited(await registerFrom("::ffff:ace:1"), 3600 - 50);
  });

  it("unparseable client addresses share one bucket instead of getting one each", async () => {
    freezeClock(hourWindow(1) + 60);
    for (let i = 0; i < 30; i++) expect((await registerFrom(`not-an-address-${i}`)).status).toBe(201);
    await expectRateLimited(await registerFrom("unknown"), 3600 - 60);
  });
});

describe("global registration ceiling", () => {
  it("refuses registrations from every network once the window's ceiling is reached", async () => {
    // Storage is shared by the tests of one file, so this spends a window no other test uses.
    const window = hourWindow(5);
    freezeClock(window + 7);
    await seedCounter("register-all:all", window, 3600, 999);

    // Junk signatures from fresh networks never reach the shared ceiling.
    const device = await newIdentity("dev");
    const { privateKey } = await generateKey();
    for (let i = 0; i < 3; i++) {
      const junk = await call(device, "POST", "/v1/devices", { spki: device.spki }, { ip: `10.207.${i}.1`, signWith: privateKey });
      expect(junk.status).toBe(401);
    }
    expect(await counter("register-all:all", window)).toBe(999);

    expect((await registerFrom("10.207.10.1")).status).toBe(201);
    await expectRateLimited(await registerFrom("10.207.11.1"), 3600 - 7);
    await expectRateLimited(await registerFrom("2001:db8:88:1::1"), 3600 - 7);
    expect(await counter("register-all:all", window)).toBe(1000);

    freezeClock(window + 3600);
    expect((await registerFrom("10.207.11.1")).status).toBe(201);
  });

  it("a network over its own budget does not spend the global one", async () => {
    const window = hourWindow(1);
    freezeClock(window + 8);
    const ip = "10.208.0.1";
    for (let i = 0; i < 30; i++) expect((await registerFrom(ip)).status).toBe(201);
    const before = await counter("register-all:all", window);
    for (let i = 0; i < 5; i++) await expectRateLimited(await registerFrom(ip), 3600 - 8);
    expect(await counter("register-all:all", window)).toBe(before);
  });

  it("concurrent copies of one captured registration register once and spend the ceiling once", async () => {
    const window = hourWindow(11);
    freezeClock(window + 3);
    const device = await newIdentity("dev");
    const request = await signedRequest(device, "POST", "/v1/devices", { spki: device.spki }, { ip: "10.209.0.1" });
    const statuses = (await Promise.all(Array.from({ length: 20 }, () => send(request.clone())))).map((r) => r.status);
    expect(statuses.filter((s) => s === 201)).toHaveLength(1);
    expect(statuses.filter((s) => s === 401)).toHaveLength(19);
    expect(await counter("register-all:all", window)).toBe(1);
  });

  it("a registration refused by the ceiling is used up and cannot run after the window resets", async () => {
    // Ten seconds before a boundary, so the replay is in a fresh window but inside its 300 s.
    const next = hourWindow(9);
    freezeClock(next - 10);
    await seedCounter("register-all:all", next - 3600, 3600, 1000);
    const device = await newIdentity("dev");
    const refused = await signedRequest(device, "POST", "/v1/devices", { spki: device.spki }, { ip: "10.209.1.1" });
    await expectRateLimited(await send(refused.clone()), 10);
    expect(await nonceStored(device.id, refused.headers.get("X-1S-Nonce")!)).toBe(true);

    freezeClock(next + 1);
    expect((await send(refused.clone())).status).toBe(401);
    expect(await counter("register-all:all", next)).toBe(0);
    const row = await env.DB.prepare(`SELECT 1 AS n FROM devices WHERE id = ?1`).bind(device.id).first();
    expect(row).toBeNull();
  });
});
