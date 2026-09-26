// Membership lifecycle: pending → approved | rejected, approved → node bound. Revocation lives in
// revocation.ts. Every state change is one conditional UPDATE scoped to the caller, so a foreign
// id and a missing id both change zero rows and both end in the same 404.

import { audit } from "../audit";
import type { Caller, RequestContext } from "../context";
import { badRequest, conflict, json, notFound, parseJsonObject, stringField } from "../http";
import { isNodeId } from "../validate";

type MembershipState = "pending" | "approved" | "rejected" | "revoked";

async function ownedState(ctx: RequestContext, membershipId: string, ownerId: string): Promise<MembershipState | null> {
  const row = await ctx.env.DB.prepare(`SELECT state FROM memberships WHERE id = ?1 AND owner_id = ?2`)
    .bind(membershipId, ownerId)
    .first<{ state: MembershipState }>();
  return row?.state ?? null;
}

/** Moves a pending membership to `target`; repeating the same decision is a no-op success. */
async function decide(ctx: RequestContext, owner: Caller, target: "approved" | "rejected"): Promise<Response> {
  const membershipId = ctx.params[0]!;
  const result = await ctx.env.DB.prepare(
    `UPDATE memberships SET state = ?1, updated_at = ?2,
       approved_at = CASE WHEN ?1 = 'approved' THEN ?2 ELSE approved_at END
     WHERE id = ?3 AND owner_id = ?4 AND state = 'pending'`,
  )
    .bind(target, ctx.now, membershipId, owner.id)
    .run();
  if (result.meta.changes !== 1) {
    const state = await ownedState(ctx, membershipId, owner.id);
    if (state === null) throw notFound("membership not found for owner");
    if (state !== target) throw conflict("invalid_state");
    return json(200, { membershipId, state });
  }
  await audit(ctx, owner.id, target === "approved" ? "membership.approve" : "membership.reject", membershipId);
  return json(200, { membershipId, state: target });
}

/** POST /v1/memberships/{id}/approve */
export const approveMembership = (ctx: RequestContext, owner: Caller) => decide(ctx, owner, "approved");

/** POST /v1/memberships/{id}/reject */
export const rejectMembership = (ctx: RequestContext, owner: Caller) => decide(ctx, owner, "rejected");

/**
 * GET /v1/owners/me/memberships: pending and approved friends. Includes each device's SPKI because
 * the Agent encrypts the enrollment blob to it (§7).
 */
export async function listOwnerMemberships(ctx: RequestContext, owner: Caller): Promise<Response> {
  const { results } = await ctx.env.DB.prepare(
    `SELECT m.id, m.server_id, s.label, m.device_id, d.spki AS device_spki, m.state, m.av, m.node_id,
            m.created_at, m.approved_at
     FROM memberships m
     JOIN servers s ON s.owner_id = m.owner_id AND s.server_id = m.server_id
     JOIN devices d ON d.id = m.device_id
     WHERE m.owner_id = ?1 AND m.state IN ('pending', 'approved')
     ORDER BY m.created_at
     LIMIT 1000`,
  )
    .bind(owner.id)
    .all<{
      id: string;
      server_id: string;
      label: string;
      device_id: string;
      device_spki: string;
      state: MembershipState;
      av: number;
      node_id: string | null;
      created_at: number;
      approved_at: number | null;
    }>();
  return json(200, {
    memberships: results.map((m) => ({
      membershipId: m.id,
      serverId: m.server_id,
      serverLabel: m.label,
      deviceId: m.device_id,
      deviceSpki: m.device_spki,
      state: m.state,
      av: m.av,
      nodeId: m.node_id,
      createdAt: m.created_at,
      approvedAt: m.approved_at,
    })),
  });
}

/** GET /v1/devices/me/memberships: the friend's servers and their state, including ended ones. */
export async function listDeviceMemberships(ctx: RequestContext, device: Caller): Promise<Response> {
  const { results } = await ctx.env.DB.prepare(
    `SELECT m.id, m.owner_id, m.server_id, s.label, s.protocol, m.state, m.node_id, m.created_at
     FROM memberships m
     JOIN servers s ON s.owner_id = m.owner_id AND s.server_id = m.server_id
     WHERE m.device_id = ?1
     ORDER BY m.created_at
     LIMIT 1000`,
  )
    .bind(device.id)
    .all<{
      id: string;
      owner_id: string;
      server_id: string;
      label: string;
      protocol: string;
      state: MembershipState;
      node_id: string | null;
      created_at: number;
    }>();
  return json(200, {
    memberships: results.map((m) => ({
      membershipId: m.id,
      ownerId: m.owner_id,
      serverId: m.server_id,
      serverLabel: m.label,
      protocol: m.protocol,
      state: m.state,
      nodeId: m.node_id,
      createdAt: m.created_at,
    })),
  });
}

/**
 * POST /v1/memberships/{id}/node {nodeId}: the friend binds its tailnet node, once.
 *
 * One device uses one node for every server of one owner (one node per owner tailnet), so reusing
 * it across that owner's memberships is expected. A node id that a *different* device already
 * holds on a live membership of the same owner is refused with a bare 409; otherwise any approved
 * friend could claim another friend's node, first write wins. The check sits inside the UPDATE, so
 * two racing binds cannot both pass it. Other owners' nodes are never consulted, so this reveals
 * nothing across owners.
 *
 * The id is still self-reported. Until the Agent has confirmed through the Tailscale API that the
 * node carries tag:onesalem-client (§7), it proves nothing about which machine the friend runs.
 */
export async function bindNode(ctx: RequestContext, device: Caller): Promise<Response> {
  const membershipId = ctx.params[0]!;
  const nodeId = stringField(parseJsonObject(ctx.body), "nodeId");
  if (!isNodeId(nodeId)) throw badRequest("nodeId has an unexpected format");

  const result = await ctx.env.DB.prepare(
    `UPDATE memberships SET node_id = ?1, node_bound_at = ?2, updated_at = ?2
     WHERE id = ?3 AND device_id = ?4 AND state = 'approved' AND node_id IS NULL
       AND NOT EXISTS (
         SELECT 1 FROM memberships AS holder
         WHERE holder.owner_id = memberships.owner_id AND holder.node_id = ?1
           AND holder.device_id <> ?4 AND holder.state IN ('pending', 'approved'))`,
  )
    .bind(nodeId, ctx.now, membershipId, device.id)
    .run();
  if (result.meta.changes !== 1) {
    const row = await ctx.env.DB.prepare(`SELECT state, node_id FROM memberships WHERE id = ?1 AND device_id = ?2`)
      .bind(membershipId, device.id)
      .first<{ state: MembershipState; node_id: string | null }>();
    if (row === null) throw notFound("membership not found for device");
    if (row.state !== "approved") throw conflict("invalid_state");
    // Approved and still unbound: the only condition left is another device holding the node.
    if (row.node_id === null) throw conflict("node_in_use");
    if (row.node_id !== nodeId) throw conflict("already_bound");
    return json(200, { membershipId, nodeId });
  }
  await audit(ctx, device.id, "membership.bind_node", membershipId);
  return json(200, { membershipId, nodeId });
}
