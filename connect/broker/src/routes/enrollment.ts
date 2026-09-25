// Enrollment relay (§7). The Agent encrypts the friend's one-off auth key to the friend's device
// key; the broker only ever holds that ciphertext, for at most 15 minutes, and hands it out once.

import { audit } from "../audit";
import type { Caller, RequestContext } from "../context";
import { badRequest, conflict, json, notFound, parseJsonObject, stringField } from "../http";
import { isCiphertext } from "../validate";

export const ENROLLMENT_TTL_SECONDS = 15 * 60;

/** POST /v1/memberships/{id}/enrollment {ciphertext}: owner stores (or replaces) the blob. */
export async function putEnrollment(ctx: RequestContext, owner: Caller): Promise<Response> {
  const membershipId = ctx.params[0]!;
  const ciphertext = stringField(parseJsonObject(ctx.body), "ciphertext");
  if (!isCiphertext(ciphertext)) throw badRequest("ciphertext must be 1-12288 printable ASCII characters");

  const expiresAt = ctx.now + ENROLLMENT_TTL_SECONDS;
  const result = await ctx.env.DB.prepare(
    `INSERT INTO enrollments (membership_id, owner_id, device_id, ciphertext, created_at, expires_at)
     SELECT id, owner_id, device_id, ?1, ?2, ?3
     FROM memberships WHERE id = ?4 AND owner_id = ?5 AND state = 'approved'
     ON CONFLICT (membership_id) DO UPDATE SET
       ciphertext = excluded.ciphertext, created_at = excluded.created_at, expires_at = excluded.expires_at`,
  )
    .bind(ciphertext, ctx.now, expiresAt, membershipId, owner.id)
    .run();
  if (result.meta.changes !== 1) {
    const owned = await ctx.env.DB.prepare(`SELECT 1 AS owned FROM memberships WHERE id = ?1 AND owner_id = ?2`)
      .bind(membershipId, owner.id)
      .first();
    if (owned === null) throw notFound("membership not found for owner");
    throw conflict("invalid_state");
  }
  await audit(ctx, owner.id, "enrollment.put", membershipId);
  return json(201, { membershipId, expiresAt });
}

/** GET /v1/memberships/{id}/enrollment: the friend's device fetches the blob; the read deletes it. */
export async function takeEnrollment(ctx: RequestContext, device: Caller): Promise<Response> {
  const membershipId = ctx.params[0]!;
  // DELETE … RETURNING is the atomic "read once": two concurrent reads cannot both get the blob.
  const row = await ctx.env.DB.prepare(
    `DELETE FROM enrollments
     WHERE membership_id = ?1 AND device_id = ?2 AND expires_at > ?3
       AND EXISTS (SELECT 1 FROM memberships WHERE id = ?1 AND device_id = ?2 AND state = 'approved')
     RETURNING ciphertext, owner_id`,
  )
    .bind(membershipId, device.id, ctx.now)
    .first<{ ciphertext: string; owner_id: string }>();
  if (row === null) throw notFound("no enrollment for device");
  await audit(ctx, device.id, "enrollment.take", membershipId);
  return json(200, { membershipId, ownerId: row.owner_id, ciphertext: row.ciphertext });
}
