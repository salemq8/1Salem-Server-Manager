// 1Salem Connect broker: control plane only (docs/CONNECT_ARCHITECTURE.md §13). It registers keys,
// relays invites, approvals and enrollment ciphertext, and signs short-lived session tickets. It
// never carries game traffic and holds no Tailscale credential.

import type { RequestContext } from "./context";
import type { Env } from "./env";
import { errorResponse, HttpError, json, log, notFound, readBody } from "./http";
import { purgeExpired } from "./maintenance";
import { matchRoute } from "./router";

async function handle(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const route = matchRoute(request.method, url.pathname);
  if (route === null) {
    // Unknown paths and methods get the same body as every other "not found".
    return errorResponse(notFound());
  }
  try {
    const ctx: RequestContext = {
      request,
      env,
      url,
      now: Math.floor(Date.now() / 1000),
      params: route.params,
      body: await readBody(request),
      clientIp: request.headers.get("CF-Connecting-IP") ?? "unknown",
    };
    return await route.run(ctx);
  } catch (error) {
    if (error instanceof HttpError) {
      log(error.status >= 500 ? "error" : "info", `${route.name} -> ${error.status}`, error.reason);
      return errorResponse(error);
    }
    const detail = error instanceof Error ? `${error.name}: ${error.message}` : "non-Error thrown";
    log("error", `${route.name} -> 500`, detail);
    return json(500, { error: "internal" });
  }
}

export default {
  fetch: handle,

  async scheduled(_controller, env, ctx) {
    ctx.waitUntil(
      purgeExpired(env.DB, Math.floor(Date.now() / 1000)).then((counts) =>
        log("info", "purge", JSON.stringify(counts)),
      ),
    );
  },
} satisfies ExportedHandler<Env>;
