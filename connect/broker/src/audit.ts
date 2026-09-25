import type { RequestContext } from "./context";

/**
 * Appends one audit row. `target` is an id (membership, invite, server, jti), never a secret,
 * ciphertext, ticket or signature. Written after the action succeeded, so the log records what
 * happened rather than what was attempted.
 */
export async function audit(ctx: RequestContext, actor: string, action: string, target: string | null): Promise<void> {
  await ctx.env.DB.prepare(`INSERT INTO audit (at, actor, action, target) VALUES (?1, ?2, ?3, ?4)`)
    .bind(ctx.now, actor, action, target)
    .run();
}
