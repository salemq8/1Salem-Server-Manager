import type { Env } from "./env";

/** Everything a route handler needs about the current request. `body` is already size-limited. */
export interface RequestContext {
  request: Request;
  env: Env;
  url: URL;
  /** Unix seconds, taken once per request. */
  now: number;
  /** Path parameters captured by the route pattern, already format-checked. */
  params: string[];
  body: Uint8Array;
  /** CF-Connecting-IP as sent, or "unknown" when absent (local dev). Rate limits key on
   *  clientNetwork() of it, never on the exact address. */
  clientIp: string;
}

export type CallerKind = "owner" | "device";

export interface Caller {
  kind: CallerKind;
  /** own_… or dev_… */
  id: string;
}
