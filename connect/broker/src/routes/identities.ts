// Self-signed registration of owner and device keys (§5). Idempotent: the id is derived from the
// key, so registering the same key again returns the same id.

import { audit } from "../audit";
import { authenticateRegistration, readSignedHeaders } from "../auth";
import type { CallerKind, RequestContext } from "../context";
import { json, parseJsonObject, stringField } from "../http";
import { hitClientIp, LIMITS } from "../ratelimit";

async function register(ctx: RequestContext, kind: CallerKind): Promise<{ id: string; created: boolean }> {
  // Checks that need neither D1 nor crypto come first, so malformed floods never reach the database.
  const spki = stringField(parseJsonObject(ctx.body), "spki");
  const signed = readSignedHeaders(ctx, kind);
  // The network's own budget is counted before any signature work, so a flood of bogus
  // registrations stays cheap to refuse and only ever spends the flooding network's budget. The
  // global ceiling is charged inside authenticateRegistration, after the proof of possession, so a
  // network over its own budget, or one sending junk signatures, never uses it up.
  await hitClientIp(ctx, LIMITS.registrationPerIp);
  const { id } = await authenticateRegistration(ctx, kind, signed, spki, LIMITS.registrationGlobal);
  const table = kind === "owner" ? "owners" : "devices";
  const result = await ctx.env.DB.prepare(
    `INSERT INTO ${table} (id, spki, created_at) VALUES (?1, ?2, ?3) ON CONFLICT (id) DO NOTHING`,
  )
    .bind(id, spki, ctx.now)
    .run();
  const created = result.meta.changes === 1;
  if (created) await audit(ctx, id, `${kind}.register`, null);
  return { id, created };
}

/** POST /v1/owners {spki} → {ownerId} */
export async function registerOwner(ctx: RequestContext): Promise<Response> {
  const { id, created } = await register(ctx, "owner");
  return json(created ? 201 : 200, { ownerId: id });
}

/** POST /v1/devices {spki} → {deviceId} */
export async function registerDevice(ctx: RequestContext): Promise<Response> {
  const { id, created } = await register(ctx, "device");
  return json(created ? 201 : 200, { deviceId: id });
}
