// Membership lifecycle: pending → approved | rejected; on an approved membership the friend's
// device binds a candidate node, which the owner then confirms or rejects (§21 D-1). Revocation
// lives in revocation.ts. Every state change is one conditional UPDATE scoped to the caller, so a
// foreign id and a missing id both change zero rows and both end in the same 404.

import { audit } from "../audit";
import type { Caller, RequestContext } from "../context";
import { badRequest, conflict, json, notFound, parseJsonObject, stringField } from "../http";
import { isNodeId } from "../validate";

type MembershipState = "pending" | "approved" | "rejected" | "revoked";

/** NULL until the device binds a node; only "confirmed" gets session tickets (sessions.ts). */
type NodeState = "candidate" | "confirmed" | "rejected";

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
 * the Agent encrypts the enrollment blob to it (§7), and the node binding's state and times
 * because the Agent finds candidates here and checks them against the key it minted (§21 D-1).
 */
export async function listOwnerMemberships(ctx: RequestContext, owner: Caller): Promise<Response> {
  const { results } = await ctx.env.DB.prepare(
    `SELECT m.id, m.server_id, s.label, m.device_id, d.spki AS device_spki, m.state, m.av, m.node_id,
            m.node_state, m.node_bound_at, m.node_confirmed_at, m.created_at, m.approved_at
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
      node_state: NodeState | null;
      node_bound_at: number | null;
      node_confirmed_at: number | null;
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
      nodeState: m.node_state,
      nodeBoundAt: m.node_bound_at,
      nodeConfirmedAt: m.node_confirmed_at,
      createdAt: m.created_at,
      approvedAt: m.approved_at,
    })),
  });
}

/**
 * GET /v1/devices/me/memberships: the friend's servers and their state, including ended ones.
 * nodeState lets the app tell "waiting for the owner to confirm this PC" and "setting up this PC
 * failed" apart from "ready to connect".
 */
export async function listDeviceMemberships(ctx: RequestContext, device: Caller): Promise<Response> {
  const { results } = await ctx.env.DB.prepare(
    `SELECT m.id, m.owner_id, m.server_id, s.label, s.protocol, m.state, m.node_id, m.node_state, m.created_at
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
      node_state: NodeState | null;
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
      nodeState: m.node_state,
      createdAt: m.created_at,
    })),
  });
}

/**
 * SQL condition: a *different* device of the same owner holds node `node` on a live membership,
 * as a candidate or a confirmed binding. A candidate the owner rejected holds nothing, so a node
 * someone claimed falsely is free again for the friend it really belongs to.
 */
function heldByAnotherDevice(membership: string, node: string, device: string): string {
  return `EXISTS (
    SELECT 1 FROM memberships AS holder
    WHERE holder.owner_id = ${membership}.owner_id AND holder.node_id = ${node}
      AND holder.device_id <> ${device} AND holder.state IN ('pending', 'approved')
      AND holder.node_state IN ('candidate', 'confirmed'))`;
}

/**
 * POST /v1/memberships/{id}/node {nodeId}: the friend binds its tailnet node, once, as a
 * *candidate* the owner still has to confirm (§21 D-1).
 *
 * One device uses one node for every server of one owner (one node per owner tailnet), so reusing
 * it across that owner's memberships is expected. A node id that a *different* device already
 * holds on a live membership of the same owner is refused with a bare 409; otherwise any approved
 * friend could claim another friend's node, first write wins. The check sits inside the UPDATE, so
 * two racing binds cannot both pass it. Other owners' nodes are never consulted, so this reveals
 * nothing across owners.
 *
 * The id is self-reported, so it proves nothing about which machine the friend runs until the
 * owner's Agent has checked the node through the Tailscale API and confirmed it (confirmNode).
 * Sessions are issued only for a confirmed binding. Once the owner has rejected the candidate,
 * the membership refuses every further binding (node_rejected): the friend needs a new invitation.
 */
export async function bindNode(ctx: RequestContext, device: Caller): Promise<Response> {
  const membershipId = ctx.params[0]!;
  const nodeId = stringField(parseJsonObject(ctx.body), "nodeId");
  if (!isNodeId(nodeId)) throw badRequest("nodeId has an unexpected format");

  const result = await ctx.env.DB.prepare(
    `UPDATE memberships SET node_id = ?1, node_state = 'candidate', node_bound_at = ?2, updated_at = ?2
     WHERE id = ?3 AND device_id = ?4 AND state = 'approved' AND node_id IS NULL
       AND NOT ${heldByAnotherDevice("memberships", "?1", "?4")}`,
  )
    .bind(nodeId, ctx.now, membershipId, device.id)
    .run();
  if (result.meta.changes !== 1) {
    // One statement, so the membership and the holder check are read from the same state.
    const row = await ctx.env.DB.prepare(
      `SELECT m.state, m.node_id, m.node_state, ${heldByAnotherDevice("m", "?3", "?2")} AS held
       FROM memberships AS m WHERE m.id = ?1 AND m.device_id = ?2`,
    )
      .bind(membershipId, device.id, nodeId)
      .first<{ state: MembershipState; node_id: string | null; node_state: NodeState | null; held: number }>();
    if (row === null) throw notFound("membership not found for device");
    if (row.state !== "approved") throw conflict("invalid_state");
    if (row.node_state === "rejected") throw conflict("node_rejected");
    if (row.node_id === null) {
      // Approved and still unbound. Another device holding the node is why the UPDATE failed.
      // Otherwise the membership changed between the UPDATE and this read (the approval, or the
      // holder's end, committed in between): nothing was bound and a retry binds, which
      // invalid_state says without blaming a holder that no longer exists.
      throw conflict(row.held === 1 ? "node_in_use" : "invalid_state");
    }
    if (row.node_id !== nodeId) throw conflict("already_bound");
    return json(200, { membershipId, nodeId });
  }
  await audit(ctx, device.id, "membership.bind_node", membershipId);
  return json(200, { membershipId, nodeId });
}

/**
 * The owner's decision on a candidate node (§21 D-1). One conditional UPDATE scoped to the owner
 * decides a candidate only when it is exactly the node the owner names: the Agent echoes the id
 * it verified through the Tailscale API, so a decision can never land on a node nobody checked.
 * When nothing changed, a follow-up read scoped to the owner explains why; repeating the same
 * decision is a no-op success, as for approve and reject.
 */
async function decideNode(ctx: RequestContext, owner: Caller, target: "confirmed" | "rejected"): Promise<Response> {
  const membershipId = ctx.params[0]!;
  const nodeId = stringField(parseJsonObject(ctx.body), "nodeId");
  if (!isNodeId(nodeId)) throw badRequest("nodeId has an unexpected format");

  const result = await ctx.env.DB.prepare(
    `UPDATE memberships SET node_state = ?1, updated_at = ?2,
       node_confirmed_at = CASE WHEN ?1 = 'confirmed' THEN ?2 ELSE node_confirmed_at END
     WHERE id = ?3 AND owner_id = ?4 AND state = 'approved' AND node_id = ?5 AND node_state = 'candidate'`,
  )
    .bind(target, ctx.now, membershipId, owner.id, nodeId)
    .run();
  if (result.meta.changes !== 1) {
    const row = await ctx.env.DB.prepare(`SELECT state, node_id, node_state FROM memberships WHERE id = ?1 AND owner_id = ?2`)
      .bind(membershipId, owner.id)
      .first<{ state: MembershipState; node_id: string | null; node_state: NodeState | null }>();
    if (row === null) throw notFound("membership not found for owner");
    // Only an approved membership with a bound node has a binding to decide on.
    if (row.state !== "approved" || row.node_id === null) throw conflict("invalid_state");
    if (row.node_id !== nodeId) throw conflict("node_mismatch");
    // The opposite decision is refused: a confirmed binding ends only with its membership
    // (revoke), and a rejected one stays rejected until the friend is invited again.
    if (row.node_state !== target) throw conflict("invalid_state");
    return json(200, { membershipId, nodeId, nodeState: target });
  }
  await audit(ctx, owner.id, target === "confirmed" ? "membership.confirm_node" : "membership.reject_node", membershipId);
  return json(200, { membershipId, nodeId, nodeState: target });
}

/**
 * POST /v1/memberships/{id}/node/confirm {nodeId}: the owner's Agent found the candidate node in
 * its tailnet with tag:onesalem-client, created after the key it sealed for this friend and bound
 * nowhere else (plan §5). From now on the membership gets session tickets for that node.
 */
export const confirmNode = (ctx: RequestContext, owner: Caller) => decideNode(ctx, owner, "confirmed");

/**
 * POST /v1/memberships/{id}/node/reject {nodeId}: the candidate failed the Agent's check. The node
 * id is kept for the record, the binding no longer holds the node against other devices, and the
 * friend's next binding attempt gets node_rejected. No revocation event is written: a candidate
 * never had a ticket, and a "membership" event would end the membership permanently at the host
 * (§12). The owner revokes the membership if the friend should lose it too.
 */
export const rejectNode = (ctx: RequestContext, owner: Caller) => decideNode(ctx, owner, "rejected");
