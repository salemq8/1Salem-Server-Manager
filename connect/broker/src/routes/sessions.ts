// Session tickets (§9, §10). A ticket is issued only for an approved membership of the calling
// device whose tailnet node binding the owner has confirmed (§21 D-1); it binds that node id, the
// session public key and the membership's current authorization version.

import type { Caller, RequestContext } from "../context";
import { importP256Spki, randomToken128 } from "../crypto";
import { base64UrlDecode } from "../encoding";
import { badRequest, json, notFound, parseJsonObject, stringField } from "../http";
import { ticketSigner } from "../secrets";
import { signTicket, TICKET_ISSUER, TICKET_LIFETIME_SECONDS } from "../tickets";
import { isHostBridge, isMembershipId, loopbackBridgeAllowed } from "../validate";

interface Issuable {
  owner_id: string;
  server_id: string;
  node_id: string;
  av: number;
  protocol: string;
  host_bridge: string;
}

/**
 * POST /v1/sessions {membershipId, sessionSpki} → {ticket, expiresAt, hostBridge}
 *
 * Charged to the device's session budget by the route table.
 */
export async function createSession(ctx: RequestContext, device: Caller): Promise<Response> {
  const body = parseJsonObject(ctx.body);
  const membershipId = stringField(body, "membershipId");
  const sessionSpki = stringField(body, "sessionSpki");
  if (!isMembershipId(membershipId)) throw notFound("malformed membership id");
  const sessionDer = base64UrlDecode(sessionSpki);
  if (sessionDer === null || (await importP256Spki(sessionDer)) === null) {
    throw badRequest("sessionSpki is not a canonical P-256 SubjectPublicKeyInfo");
  }

  const db = ctx.env.DB;
  const membership = await db
    .prepare(
      `SELECT m.owner_id, m.server_id, m.node_id, m.av, s.protocol, s.host_bridge
       FROM memberships m
       JOIN servers s ON s.owner_id = m.owner_id AND s.server_id = m.server_id
       WHERE m.id = ?1 AND m.device_id = ?2 AND m.state = 'approved'
         AND m.node_id IS NOT NULL AND m.node_state = 'confirmed'`,
    )
    .bind(membershipId, device.id)
    .first<Issuable>();
  // Missing, foreign, pending, rejected, revoked, not yet bound and not yet confirmed (or
  // rejected) by the owner all look the same from outside. The bridge is checked again so a
  // loopback address stored by a development broker is never signed into a ticket once that
  // setting is off.
  if (
    membership === null ||
    membership.protocol !== "tcp" ||
    !isHostBridge(membership.host_bridge, loopbackBridgeAllowed(ctx.env))
  ) {
    throw notFound("membership not issuable");
  }

  // Resolve the signer before recording anything, so a misconfigured broker writes no session.
  const signer = await ticketSigner(ctx.env);
  const jti = randomToken128();
  const iat = ctx.now;
  const exp = iat + TICKET_LIFETIME_SECONDS;

  // Re-checks state, av and the confirmed node inside the INSERT: a revocation that landed after
  // the SELECT bumps av or changes state, and then no session row (and no ticket) is produced.
  const inserted = await db
    .prepare(
      `INSERT INTO sessions (jti, membership_id, owner_id, device_id, server_id, av, issued_at, exp)
       SELECT ?1, id, owner_id, device_id, server_id, av, ?2, ?3
       FROM memberships
       WHERE id = ?4 AND device_id = ?5 AND state = 'approved' AND av = ?6
         AND node_id = ?7 AND node_state = 'confirmed'`,
    )
    .bind(jti, iat, exp, membershipId, device.id, membership.av, membership.node_id)
    .run();
  if (inserted.meta.changes !== 1) throw notFound("membership changed during issue");

  const ticket = await signTicket(signer, {
    iss: TICKET_ISSUER,
    aud: membership.owner_id,
    jti,
    sub: device.id,
    mid: membershipId,
    sid: membership.server_id,
    proto: "tcp",
    nid: membership.node_id,
    skp: sessionSpki,
    hb: membership.host_bridge,
    av: membership.av,
    iat,
    nbf: iat,
    exp,
  });
  // No audit row here: sessions are refreshed every few minutes and the sessions table already
  // records every issued jti.
  return json(201, { ticket, expiresAt: exp, hostBridge: membership.host_bridge });
}
