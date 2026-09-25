// Revocation (§12) and the revocation feed hosts pull.
//
// Each revocation is one atomic D1 batch. The feed rows are inserted *first*, selecting only rows
// that are still live, and the state changes follow; inside the batch's transaction that means an
// event is recorded exactly when this request is the one that revoked something, and its seq is
// taken in the same transaction that makes the revocation visible.

import { audit } from "../audit";
import type { Caller, RequestContext } from "../context";
import { badRequest, conflict, json, notFound } from "../http";

const LIVE = `state IN ('pending', 'approved')`;

/** POST /v1/memberships/{id}/revoke: state → revoked, av + 1, its tickets revoked, blob dropped. */
export async function revokeMembership(ctx: RequestContext, owner: Caller): Promise<Response> {
  const membershipId = ctx.params[0]!;
  const db = ctx.env.DB;
  const results = await db.batch([
    db
      .prepare(
        `INSERT INTO revocations (owner_id, kind, membership_id, device_id, server_id, av, at)
         SELECT owner_id, 'membership', id, device_id, server_id, av + 1, ?1
         FROM memberships WHERE id = ?2 AND owner_id = ?3 AND ${LIVE}`,
      )
      .bind(ctx.now, membershipId, owner.id),
    db
      .prepare(
        `UPDATE sessions SET revoked_at = ?1
         WHERE membership_id = ?2 AND owner_id = ?3 AND revoked_at IS NULL`,
      )
      .bind(ctx.now, membershipId, owner.id),
    db.prepare(`DELETE FROM enrollments WHERE membership_id = ?1 AND owner_id = ?2`).bind(membershipId, owner.id),
    db
      .prepare(
        `UPDATE memberships SET state = 'revoked', av = av + 1, revoked_at = ?1, updated_at = ?1
         WHERE id = ?2 AND owner_id = ?3 AND ${LIVE}`,
      )
      .bind(ctx.now, membershipId, owner.id),
  ]);

  const row = await db
    .prepare(`SELECT state, av FROM memberships WHERE id = ?1 AND owner_id = ?2`)
    .bind(membershipId, owner.id)
    .first<{ state: string; av: number }>();
  if (row === null) throw notFound("membership not found for owner");
  if (results[3]!.meta.changes === 1) await audit(ctx, owner.id, "membership.revoke", membershipId);
  // Revoking again is harmless; a rejected membership never had access and is reported as is.
  return json(200, { membershipId, state: row.state, av: row.av });
}

/**
 * POST /v1/devices/{deviceId}/revoke: every live membership of that device on the *caller's*
 * servers. Memberships the device holds with other owners are untouched.
 */
export async function revokeDevice(ctx: RequestContext, owner: Caller): Promise<Response> {
  const deviceId = ctx.params[0]!;
  const db = ctx.env.DB;
  const results = await db.batch([
    db
      .prepare(
        `INSERT INTO revocations (owner_id, kind, device_id, at)
         SELECT ?1, 'device', ?2, ?3
         WHERE EXISTS (SELECT 1 FROM memberships WHERE device_id = ?2 AND owner_id = ?1 AND ${LIVE})`,
      )
      .bind(owner.id, deviceId, ctx.now),
    db
      .prepare(
        `INSERT INTO revocations (owner_id, kind, membership_id, device_id, server_id, av, at)
         SELECT owner_id, 'membership', id, device_id, server_id, av + 1, ?3
         FROM memberships WHERE device_id = ?2 AND owner_id = ?1 AND ${LIVE}`,
      )
      .bind(owner.id, deviceId, ctx.now),
    db
      .prepare(
        `UPDATE sessions SET revoked_at = ?3
         WHERE owner_id = ?1 AND device_id = ?2 AND revoked_at IS NULL`,
      )
      .bind(owner.id, deviceId, ctx.now),
    db.prepare(`DELETE FROM enrollments WHERE owner_id = ?1 AND device_id = ?2`).bind(owner.id, deviceId),
    db
      .prepare(
        `UPDATE memberships SET state = 'revoked', av = av + 1, revoked_at = ?3, updated_at = ?3
         WHERE device_id = ?2 AND owner_id = ?1 AND ${LIVE}`,
      )
      .bind(owner.id, deviceId, ctx.now),
  ]);

  const revoked = results[4]!.meta.changes;
  if (revoked === 0) {
    // Only a device that has (or had) a membership with this owner is known to this owner.
    const known = await db
      .prepare(`SELECT 1 AS known FROM memberships WHERE device_id = ?1 AND owner_id = ?2 LIMIT 1`)
      .bind(deviceId, owner.id)
      .first();
    if (known === null) throw notFound("device unknown to owner");
  } else {
    await audit(ctx, owner.id, "device.revoke", deviceId);
  }
  return json(200, { deviceId, revokedMemberships: revoked });
}

/** POST /v1/sessions/{jti}/revoke: one ticket of one of the caller's memberships. */
export async function revokeSession(ctx: RequestContext, owner: Caller): Promise<Response> {
  const jti = ctx.params[0]!;
  const db = ctx.env.DB;
  const results = await db.batch([
    db
      .prepare(
        `INSERT INTO revocations (owner_id, kind, jti, membership_id, device_id, server_id, av, at)
         SELECT owner_id, 'session', jti, membership_id, device_id, server_id, av, ?1
         FROM sessions WHERE jti = ?2 AND owner_id = ?3 AND revoked_at IS NULL`,
      )
      .bind(ctx.now, jti, owner.id),
    db
      .prepare(`UPDATE sessions SET revoked_at = ?1 WHERE jti = ?2 AND owner_id = ?3 AND revoked_at IS NULL`)
      .bind(ctx.now, jti, owner.id),
  ]);
  if (results[1]!.meta.changes !== 1) {
    const known = await db
      .prepare(`SELECT 1 AS known FROM sessions WHERE jti = ?1 AND owner_id = ?2`)
      .bind(jti, owner.id)
      .first();
    if (known === null) throw notFound("session not found for owner");
  } else {
    await audit(ctx, owner.id, "session.revoke", jti);
  }
  return json(200, { jti, revoked: true });
}

export const FEED_PAGE = 1000;
const CURSOR = /^[0-9]{1,16}$/;

/**
 * GET /v1/owners/me/revocations?after=<seq> → {revocations: [{seq, kind, …, at}], cursor, more}
 *
 * Pages on `seq`, never on time. D1 runs one write transaction at a time and seq (AUTOINCREMENT,
 * never reused while the database is not restored; see below) is assigned inside it, so seq order
 * is commit order: an event that commits after a page was read always gets a seq above everything
 * that page could show. A wall-clock cursor skipped an event whose request began in the second
 * before a poll but committed after it.
 *
 * `after` is exclusive and defaults to 0 (the whole history). A host stores `cursor` from each
 * response and passes it back as `after`; `more` means the next page is already waiting. `cursor`
 * is the last event's seq, or `after` itself when the page is empty. Seqs are shared by all
 * owners, so they have gaps; only their order carries meaning. `at` is informational.
 *
 * A cursor that is not one of this owner's events (for example one from another broker) gets 409
 * {"error":"cursor_ahead"} and the host starts again from after=0; events are idempotent, so
 * replaying history is safe. Every legitimate non-zero cursor is the seq of one of the owner's own
 * events, and events are never deleted, so the check is "that row exists". Merely comparing with the
 * table's highest seq is not enough: other owners' events can pass a stale cursor.
 *
 * A database restore also rolls the seq counter back, so a new event can later receive the very seq
 * a host kept as its cursor, and this check would then pass. That is why restoring the database
 * requires the operator step in README ("Requirements outside the broker"): raise the revocations
 * sequence past every seq issued before the restore, so that no kept cursor can match again.
 */
export async function revocationFeed(ctx: RequestContext, owner: Caller): Promise<Response> {
  const afterText = ctx.url.searchParams.get("after") ?? "0";
  const after = CURSOR.test(afterText) ? Number(afterText) : Number.NaN;
  if (!Number.isSafeInteger(after)) throw badRequest("after must be a revocation seq");
  // `seq >= after` also returns the cursor's own row (when after > 0), which proves the cursor is
  // one of this owner's events; it is dropped from the page below.
  const { results: rows } = await ctx.env.DB.prepare(
    `SELECT seq, kind, jti, membership_id, device_id, server_id, av, at
     FROM revocations WHERE owner_id = ?1 AND seq >= ?2
     ORDER BY seq
     LIMIT ?3`,
  )
    .bind(owner.id, after, FEED_PAGE + 2)
    .all<{
      seq: number;
      kind: "session" | "membership" | "device";
      jti: string | null;
      membership_id: string | null;
      device_id: string | null;
      server_id: string | null;
      av: number | null;
      at: number;
    }>();

  let results = rows;
  if (after > 0) {
    if (rows[0]?.seq !== after) throw conflict("cursor_ahead");
    results = rows.slice(1);
  }

  const page = results.slice(0, FEED_PAGE);
  return json(200, {
    revocations: page.map((r) => ({
      seq: r.seq,
      kind: r.kind,
      ...(r.jti !== null && { jti: r.jti }),
      ...(r.membership_id !== null && { membershipId: r.membership_id }),
      ...(r.device_id !== null && { deviceId: r.device_id }),
      ...(r.server_id !== null && { serverId: r.server_id }),
      ...(r.av !== null && { av: r.av }),
      at: r.at,
    })),
    cursor: page.at(-1)?.seq ?? after,
    more: results.length > FEED_PAGE,
  });
}
