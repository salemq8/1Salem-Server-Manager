// 1Salem Connect broker: control plane only (docs/CONNECT_ARCHITECTURE.md §13). It registers keys,
// relays invites, approvals and enrollment ciphertext, and signs short-lived session tickets. It
// never carries game traffic and holds no Tailscale credential.

import type { RequestContext } from "./context";
import type { Env } from "./env";
import { clientNetwork } from "./clientip";
import { errorResponse, HttpError, json, log, notConfigured, notFound, rateLimited, readBody } from "./http";
import { purgeExpired } from "./maintenance";
import { matchRoute } from "./router";

const FLOOD_WINDOW_SECONDS = 60;

/**
 * The deployment-level limiter is deliberately checked before route lookup, body reads and D1.
 * Local development has no binding and remains usable unless it explicitly requires one. A
 * production deployment declares CONNECT_REQUIRE_FLOOD_LIMIT=true, so a missing or broken binding
 * fails closed instead of quietly making D1 the first line of flood protection (§21 D-2).
 */
export async function enforceOuterFloodLimit(request: Request, env: Env): Promise<Response | null> {
  if (env.FLOOD === undefined) {
    return env.CONNECT_REQUIRE_FLOOD_LIMIT === "true"
      ? errorResponse(notConfigured("required outer flood limiter binding is missing"))
      : null;
  }

  try {
    const network = clientNetwork(request.headers.get("CF-Connecting-IP") ?? "unknown");
    const outcome = await env.FLOOD.limit({ key: network });
    return outcome.success
      ? null
      : errorResponse(rateLimited(FLOOD_WINDOW_SECONDS, "outer flood limit exceeded"));
  } catch (error) {
    const detail = error instanceof Error ? `${error.name}: ${error.message}` : "non-Error thrown";
    log("error", "outer flood limiter unavailable", detail);
    return errorResponse(notConfigured("required outer flood limiter is unavailable"));
  }
}

export async function handle(request: Request, env: Env): Promise<Response> {
  const floodResponse = await enforceOuterFloodLimit(request, env);
  if (floodResponse !== null) return floodResponse;

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
      ).catch((error) => {
        const detail = error instanceof Error ? `${error.name}: ${error.message}` : "non-Error thrown";
        log("error", "purge failed", detail);
      }),
    );
  },
} satisfies ExportedHandler<Env>;
