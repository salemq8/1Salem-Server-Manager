import { audit } from "../audit";
import type { Caller, RequestContext } from "../context";
import { badRequest, json, parseJsonObject, stringField } from "../http";
import { isHostBridge, isLabel, loopbackBridgeAllowed } from "../validate";

/** PUT /v1/servers/{serverId} {label, protocol, hostBridge}: register or update the caller's server. */
export async function putServer(ctx: RequestContext, owner: Caller): Promise<Response> {
  const serverId = ctx.params[0]!;
  const body = parseJsonObject(ctx.body);
  const label = stringField(body, "label");
  const protocol = stringField(body, "protocol");
  const hostBridge = stringField(body, "hostBridge");
  if (!isLabel(label)) throw badRequest("label must be 1-64 printable characters");
  // "udp" is reserved in the ticket format but has no bridge in Build 8 (§18).
  if (protocol !== "tcp") throw badRequest("protocol must be tcp");
  if (!isHostBridge(hostBridge, loopbackBridgeAllowed(ctx.env))) throw badRequest("hostBridge must be a tailnet ip:port");

  await ctx.env.DB.prepare(
    `INSERT INTO servers (owner_id, server_id, label, protocol, host_bridge, created_at, updated_at)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?6)
     ON CONFLICT (owner_id, server_id) DO UPDATE SET
       label = excluded.label, protocol = excluded.protocol,
       host_bridge = excluded.host_bridge, updated_at = excluded.updated_at`,
  )
    .bind(owner.id, serverId, label, protocol, hostBridge, ctx.now)
    .run();
  await audit(ctx, owner.id, "server.put", serverId);
  return json(200, { serverId, label, protocol, hostBridge });
}
