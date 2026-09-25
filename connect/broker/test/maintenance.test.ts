import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { purgeExpired, SESSION_RETENTION_SECONDS } from "../src/maintenance";
import { call, expectJson, issueTicket, nowSeconds, randomB64u, readyFriend, revocationFeed } from "./helpers";

const count = async (sql: string, ...values: unknown[]) =>
  (await env.DB.prepare(sql).bind(...values).first<{ n: number }>())!.n;

describe("scheduled purge", () => {
  it("removes expired nonces, counters, enrollment blobs and old sessions, never revocation events", async () => {
    const now = nowSeconds();
    const friend = await readyFriend();
    await expectJson(
      await call(friend.owner, "POST", `/v1/memberships/${friend.membershipId}/enrollment`, { ciphertext: "opaque" }),
      201,
    );
    const old = await issueTicket(friend);
    const recent = await issueTicket(friend);
    await expectJson(await call(friend.owner, "POST", `/v1/sessions/${old.jti}/revoke`), 200);

    const nonce = randomB64u(16);
    const bucket = `test-purge:${randomB64u(8)}`;
    await env.DB.batch([
      env.DB.prepare(`UPDATE enrollments SET expires_at = ?1 WHERE membership_id = ?2`).bind(now, friend.membershipId),
      env.DB.prepare(`UPDATE sessions SET exp = ?1 WHERE jti = ?2`).bind(now - SESSION_RETENTION_SECONDS - 1, old.jti),
      env.DB.prepare(`INSERT INTO request_nonces (key_id, nonce, expires_at) VALUES (?1, ?2, ?3)`).bind(friend.device.id, nonce, now - 1),
      env.DB.prepare(`INSERT INTO rate_counters (bucket, window_start, hits, expires_at) VALUES (?1, 0, 1, ?2)`).bind(bucket, now),
    ]);

    const purged = await purgeExpired(env.DB, now);
    for (const n of Object.values(purged)) expect(n).toBeGreaterThanOrEqual(1);

    expect(await count(`SELECT COUNT(*) AS n FROM enrollments WHERE membership_id = ?1`, friend.membershipId)).toBe(0);
    expect(await count(`SELECT COUNT(*) AS n FROM sessions WHERE jti = ?1`, old.jti)).toBe(0);
    expect(await count(`SELECT COUNT(*) AS n FROM sessions WHERE jti = ?1`, recent.jti)).toBe(1);
    expect(await count(`SELECT COUNT(*) AS n FROM request_nonces WHERE nonce = ?1`, nonce)).toBe(0);
    expect(await count(`SELECT COUNT(*) AS n FROM rate_counters WHERE bucket = ?1`, bucket)).toBe(0);
    const feed = await revocationFeed(friend.owner);
    expect(feed.revocations.filter((r: any) => r.jti === old.jti)).toHaveLength(1);
  });
});
