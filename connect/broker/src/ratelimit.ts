// Exact fixed-window counters in D1 (§14). The Workers Rate Limiting binding is per-location and
// eventually consistent, so it could only ever be a coarse outer layer; these counters are the
// authoritative limit. Every attempt that reaches a counter counts up to the limit, successful or
// not, so failures cannot be retried for free. (Forged and replayed signed requests never reach
// one, see auth.ts.) A counter never goes past its limit: an attempt over it adds no hit, so an
// unauthenticated flood against a spent budget costs no counter write. (A signed request over its
// key's budget has already stored its nonce, see auth.ts; only the key's holder can cause that.)

import { clientNetwork } from "./clientip";
import type { RequestContext } from "./context";
import { rateLimited } from "./http";
import { ipPseudonym } from "./secrets";

export interface Limit {
  name: string;
  max: number;
  windowSeconds: number;
}

export const LIMITS = {
  /** POST /v1/owners and POST /v1/devices share one bucket per client network (IPv4 or IPv6 /64). */
  registrationPerIp: { name: "register-ip", max: 30, windowSeconds: 3600 },
  /**
   * Every registration from anywhere whose signature verifies: rotating networks cannot create keys
   * without bound. Reaching it refuses registrations only (repeats of an existing key included);
   * already registered keys keep working.
   */
  registrationGlobal: { name: "register-all", max: 1000, windowSeconds: 3600 },
  redeemPerDevice: { name: "redeem-device", max: 10, windowSeconds: 3600 },
  redeemPerIp: { name: "redeem-ip", max: 30, windowSeconds: 3600 },
  /** Tickets live 10 minutes and are refreshed before expiry, per server a friend has open. */
  sessionsPerDevice: { name: "session-device", max: 120, windowSeconds: 3600 },
  /** Every owner-signed non-GET call. */
  ownerWrites: { name: "owner-write", max: 600, windowSeconds: 3600 },
  /**
   * Owner GETs (friend list, revocation feed). 2 per second sustained: an Agent polling both every
   * 2 s uses half. A one-minute window lets a runaway loop recover within a minute.
   */
  ownerReads: { name: "owner-read", max: 120, windowSeconds: 60 },
  /** Device calls with no budget of their own: membership list, enrollment pickup, node binding. */
  deviceCalls: { name: "device-call", max: 120, windowSeconds: 60 },
} as const satisfies Record<string, Limit>;

/** The subject for the per-window total of a limit that is not per caller. */
export const GLOBAL_SUBJECT = "all";

export async function hit(ctx: RequestContext, limit: Limit, subject: string): Promise<void> {
  const windowStart = ctx.now - (ctx.now % limit.windowSeconds);
  const windowEnd = windowStart + limit.windowSeconds;
  // One statement, so concurrent requests cannot both take the last slot. Once `hits` reaches
  // `max` the DO UPDATE's WHERE is false: the row is left as it is and RETURNING yields nothing.
  const row = await ctx.env.DB.prepare(
    `INSERT INTO rate_counters (bucket, window_start, hits, expires_at) VALUES (?1, ?2, 1, ?3)
     ON CONFLICT (bucket, window_start) DO UPDATE SET hits = hits + 1 WHERE hits < ?4
     RETURNING hits`,
  )
    .bind(`${limit.name}:${subject}`, windowStart, windowEnd, limit.max)
    .first<{ hits: number }>();
  if (row === null) {
    throw rateLimited(Math.max(1, windowEnd - ctx.now), `${limit.name} exceeded`);
  }
}

export async function hitClientIp(ctx: RequestContext, limit: Limit): Promise<void> {
  await hit(ctx, limit, await ipPseudonym(ctx.env, clientNetwork(ctx.clientIp)));
}
