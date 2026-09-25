// Invitations (§6). The secret is 32 CSPRNG bytes, returned once and stored only as
// HMAC-SHA256(INVITE_PEPPER, secret). Redemption is a single conditional UPDATE, so an invite can
// be used exactly once, and it creates a *pending* membership: an invite never grants access.

import { audit } from "../audit";
import type { Caller, RequestContext } from "../context";
import { randomBytes, randomId } from "../crypto";
import { base64UrlDecode, base64UrlEncode } from "../encoding";
import { badRequest, conflict, json, notFound, parseJsonObject, stringField } from "../http";
import { hitClientIp, LIMITS } from "../ratelimit";
import { inviteSecretHmac } from "../secrets";
import { isServerId } from "../validate";

export const INVITE_SECRET_BYTES = 32;
export const INVITE_TTL_DEFAULT_SECONDS = 24 * 3600;
export const INVITE_TTL_MAX_SECONDS = 7 * 24 * 3600;
export const INVITE_TTL_MIN_SECONDS = 60;

/** POST /v1/invites {serverId, ttlSeconds?} → {inviteId, secret, expiresAt} */
export async function createInvite(ctx: RequestContext, owner: Caller): Promise<Response> {
  const body = parseJsonObject(ctx.body);
  const serverId = stringField(body, "serverId");
  const ttl = body.ttlSeconds ?? INVITE_TTL_DEFAULT_SECONDS;
  if (
    typeof ttl !== "number" ||
    !Number.isInteger(ttl) ||
    ttl < INVITE_TTL_MIN_SECONDS ||
    ttl > INVITE_TTL_MAX_SECONDS
  ) {
    throw badRequest(`ttlSeconds must be an integer in ${INVITE_TTL_MIN_SECONDS}..${INVITE_TTL_MAX_SECONDS}`);
  }
  if (!isServerId(serverId)) throw notFound("malformed server id");

  const secret = randomBytes(INVITE_SECRET_BYTES);
  const secretHmac = await inviteSecretHmac(ctx.env, secret);
  const inviteId = randomId("inv_");
  const expiresAt = ctx.now + ttl;
  // Inserting through a SELECT on the caller's own server row makes "not your server" and "no such
  // server" the same zero-row outcome.
  const result = await ctx.env.DB.prepare(
    `INSERT INTO invites (id, owner_id, server_id, secret_hmac, state, created_at, expires_at)
     SELECT ?1, owner_id, server_id, ?2, 'active', ?3, ?4
     FROM servers WHERE owner_id = ?5 AND server_id = ?6`,
  )
    .bind(inviteId, secretHmac, ctx.now, expiresAt, owner.id, serverId)
    .run();
  if (result.meta.changes !== 1) throw notFound("server not found for owner");

  await audit(ctx, owner.id, "invite.create", inviteId);
  return json(201, { inviteId, secret: base64UrlEncode(secret), expiresAt });
}

/** POST /v1/invites/{inviteId}/revoke: only an unused, unrevoked invite of the caller. */
export async function revokeInvite(ctx: RequestContext, owner: Caller): Promise<Response> {
  const inviteId = ctx.params[0]!;
  const result = await ctx.env.DB.prepare(
    `UPDATE invites SET state = 'revoked', revoked_at = ?1
     WHERE id = ?2 AND owner_id = ?3 AND state = 'active'`,
  )
    .bind(ctx.now, inviteId, owner.id)
    .run();
  if (result.meta.changes !== 1) throw notFound("invite not active for owner");
  await audit(ctx, owner.id, "invite.revoke", inviteId);
  return json(200, { inviteId, state: "revoked" });
}

/**
 * POST /v1/invites/redeem {secret} → {membershipId, state: "pending", serverLabel}
 *
 * The per-device budget is charged by the route table; the per-network one is charged here.
 */
export async function redeemInvite(ctx: RequestContext, device: Caller): Promise<Response> {
  await hitClientIp(ctx, LIMITS.redeemPerIp);

  const secretText = stringField(parseJsonObject(ctx.body), "secret");
  // A malformed secret gets exactly the same answer as a wrong, expired, used or revoked one.
  const secret = base64UrlDecode(secretText);
  if (secret === null || secret.byteLength !== INVITE_SECRET_BYTES) throw notFound("invite not valid");
  const secretHmac = await inviteSecretHmac(ctx.env, secret);
  const membershipId = randomId("mem_");

  const db = ctx.env.DB;
  let results: D1Result[];
  try {
    // One atomic batch: consume the invite, then create the membership from the row this request
    // consumed (matched by the fresh membership id), then read the label for the response.
    results = await db.batch([
      db
        .prepare(
          `UPDATE invites SET state = 'used', used_at = ?1, used_by_device = ?2, membership_id = ?3
           WHERE secret_hmac = ?4 AND state = 'active' AND expires_at > ?1`,
        )
        .bind(ctx.now, device.id, membershipId, secretHmac),
      db
        .prepare(
          `INSERT INTO memberships (id, owner_id, server_id, device_id, invite_id, state, av, created_at, updated_at)
           SELECT ?1, owner_id, server_id, ?2, id, 'pending', 1, ?3, ?3
           FROM invites WHERE secret_hmac = ?4 AND membership_id = ?1`,
        )
        .bind(membershipId, device.id, ctx.now, secretHmac),
      db
        .prepare(
          `SELECT s.label AS label FROM memberships m
           JOIN servers s ON s.owner_id = m.owner_id AND s.server_id = m.server_id
           WHERE m.id = ?1`,
        )
        .bind(membershipId),
    ]);
  } catch (error) {
    // The partial unique index allows one live membership per (server, device). The batch rolled
    // back, so the invite stays unused for whoever it was really meant for.
    if (error instanceof Error && error.message.includes("UNIQUE constraint failed: memberships")) {
      throw conflict("already_member");
    }
    throw error;
  }

  const consumed = results[0]!.meta.changes === 1;
  const label = (results[2]!.results[0] as { label: string } | undefined)?.label;
  if (!consumed || label === undefined) throw notFound("invite not valid");

  await audit(ctx, device.id, "invite.redeem", membershipId);
  return json(201, { membershipId, state: "pending", serverLabel: label });
}
