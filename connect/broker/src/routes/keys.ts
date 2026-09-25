import type { RequestContext } from "../context";
import { json } from "../http";
import { ticketSigner } from "../secrets";

/** GET /v1/keys: the public ticket keys hosts and friend transports pin. Public and cacheable. */
export async function getKeys(ctx: RequestContext): Promise<Response> {
  const signer = await ticketSigner(ctx.env);
  return json(200, { keys: [{ kid: signer.kid, alg: "ES256", spki: signer.spki }] }, {
    "Cache-Control": "public, max-age=300",
  });
}
