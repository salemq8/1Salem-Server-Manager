// Route table for API v1 (§13). Path parameters are matched with their exact id formats, so a
// malformed id falls through to the same generic 404 as an unknown path. Every signed route names
// the per-caller budget it is charged to (§14); the type makes an unmetered signed route impossible.

import { authenticate } from "./auth";
import type { Caller, CallerKind, RequestContext } from "./context";
import { LIMITS, type Limit } from "./ratelimit";
import { putEnrollment, takeEnrollment } from "./routes/enrollment";
import { registerDevice, registerOwner } from "./routes/identities";
import { invitePage } from "./routes/invitePage";
import { createInvite, redeemInvite, revokeInvite } from "./routes/invites";
import { getKeys } from "./routes/keys";
import {
  approveMembership,
  bindNode,
  confirmNode,
  listDeviceMemberships,
  listOwnerMemberships,
  rejectMembership,
  rejectNode,
} from "./routes/memberships";
import { revocationFeed, revokeDevice, revokeMembership, revokeSession } from "./routes/revocation";
import { putServer } from "./routes/servers";
import { createSession } from "./routes/sessions";
import { DEVICE_ID, INVITE_ID, JTI, MEMBERSHIP_ID, SERVER_ID } from "./validate";

type Route =
  | { method: string; path: RegExp; auth: null; handler: (ctx: RequestContext) => Promise<Response> }
  | {
      method: string;
      path: RegExp;
      auth: CallerKind;
      limit: Limit;
      handler: (ctx: RequestContext, caller: Caller) => Promise<Response>;
    };

const path = (pattern: string) => new RegExp(`^${pattern}$`);

const { ownerReads, ownerWrites, deviceCalls, redeemPerDevice, sessionsPerDevice } = LIMITS;

const ROUTES: Route[] = [
  { method: "GET", path: path("/v1/keys"), auth: null, handler: getKeys },
  // The invite landing page. The secret stays in the link's fragment, which never reaches here.
  { method: "GET", path: path("/i"), auth: null, handler: invitePage },
  // Registration authenticates itself: the signing key arrives in the body (proof of possession).
  // It meters itself too, per client network and globally, since no key is known yet.
  { method: "POST", path: path("/v1/owners"), auth: null, handler: registerOwner },
  { method: "POST", path: path("/v1/devices"), auth: null, handler: registerDevice },

  { method: "PUT", path: path(`/v1/servers/(${SERVER_ID})`), auth: "owner", limit: ownerWrites, handler: putServer },
  { method: "POST", path: path("/v1/invites"), auth: "owner", limit: ownerWrites, handler: createInvite },
  { method: "POST", path: path("/v1/invites/redeem"), auth: "device", limit: redeemPerDevice, handler: redeemInvite },
  { method: "POST", path: path(`/v1/invites/(${INVITE_ID})/revoke`), auth: "owner", limit: ownerWrites, handler: revokeInvite },

  { method: "GET", path: path("/v1/owners/me/memberships"), auth: "owner", limit: ownerReads, handler: listOwnerMemberships },
  { method: "GET", path: path("/v1/owners/me/revocations"), auth: "owner", limit: ownerReads, handler: revocationFeed },
  { method: "GET", path: path("/v1/devices/me/memberships"), auth: "device", limit: deviceCalls, handler: listDeviceMemberships },

  { method: "POST", path: path(`/v1/memberships/(${MEMBERSHIP_ID})/approve`), auth: "owner", limit: ownerWrites, handler: approveMembership },
  { method: "POST", path: path(`/v1/memberships/(${MEMBERSHIP_ID})/reject`), auth: "owner", limit: ownerWrites, handler: rejectMembership },
  { method: "POST", path: path(`/v1/memberships/(${MEMBERSHIP_ID})/revoke`), auth: "owner", limit: ownerWrites, handler: revokeMembership },
  { method: "POST", path: path(`/v1/memberships/(${MEMBERSHIP_ID})/enrollment`), auth: "owner", limit: ownerWrites, handler: putEnrollment },
  { method: "GET", path: path(`/v1/memberships/(${MEMBERSHIP_ID})/enrollment`), auth: "device", limit: deviceCalls, handler: takeEnrollment },
  { method: "POST", path: path(`/v1/memberships/(${MEMBERSHIP_ID})/node`), auth: "device", limit: deviceCalls, handler: bindNode },
  // The owner's decision on the device-reported candidate node (§21 D-1).
  { method: "POST", path: path(`/v1/memberships/(${MEMBERSHIP_ID})/node/confirm`), auth: "owner", limit: ownerWrites, handler: confirmNode },
  { method: "POST", path: path(`/v1/memberships/(${MEMBERSHIP_ID})/node/reject`), auth: "owner", limit: ownerWrites, handler: rejectNode },

  { method: "POST", path: path("/v1/sessions"), auth: "device", limit: sessionsPerDevice, handler: createSession },
  { method: "POST", path: path(`/v1/sessions/(${JTI})/revoke`), auth: "owner", limit: ownerWrites, handler: revokeSession },
  { method: "POST", path: path(`/v1/devices/(${DEVICE_ID})/revoke`), auth: "owner", limit: ownerWrites, handler: revokeDevice },
];

export interface MatchedRoute {
  /** Method and pattern, safe to log (contains no ids). */
  name: string;
  params: string[];
  run(ctx: RequestContext): Promise<Response>;
}

export function matchRoute(method: string, pathname: string): MatchedRoute | null {
  for (const route of ROUTES) {
    if (route.method !== method) continue;
    const match = route.path.exec(pathname);
    if (match === null) continue;
    return {
      name: `${route.method} ${route.path.source}`,
      params: match.slice(1),
      run: (ctx) => dispatch(route, ctx),
    };
  }
  return null;
}

async function dispatch(route: Route, ctx: RequestContext): Promise<Response> {
  if (route.auth === null) return route.handler(ctx);
  return route.handler(ctx, await authenticate(ctx, route.auth, route.limit));
}
