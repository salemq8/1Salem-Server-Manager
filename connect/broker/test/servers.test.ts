import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import worker from "../src/index";
import {
  approve,
  bindNode,
  call,
  confirmNode,
  expectGeneric404,
  expectJson,
  generateKey,
  pendingFriend,
  randomServerId,
  registerOwner,
  signedRequest,
} from "./helpers";

// A loopback host bridge would point a friend's transport at its own machine. Only a local
// development broker (CONNECT_DEV_LOOPBACK_BRIDGE=allow, fake transport mode) may accept one.
const devEnv = { ...env, CONNECT_DEV_LOOPBACK_BRIDGE: "allow" };

describe("host bridge addresses", () => {
  it("accepts tailnet addresses and refuses loopback without the development setting", async () => {
    const owner = await registerOwner();
    for (const hostBridge of ["100.64.0.1:7780", "100.127.255.254:7780", "[fd7a:115c:a1e0::1]:7780"]) {
      await expectJson(await call(owner, "PUT", `/v1/servers/${randomServerId()}`, { label: "A", protocol: "tcp", hostBridge }), 200);
    }
    for (const hostBridge of ["127.0.0.1:7780", "127.9.9.9:7780", "[::1]:7780", "192.168.1.10:7780", "100.128.0.1:7780", "localhost:7780"]) {
      const body = await expectJson(
        await call(owner, "PUT", `/v1/servers/${randomServerId()}`, { label: "A", protocol: "tcp", hostBridge }),
        400,
      );
      expect(body.error).toBe("bad_request");
    }
  });

  it("accepts loopback only on a broker started with the development setting", async () => {
    const owner = await registerOwner();
    for (const hostBridge of ["127.0.0.1:7780", "[::1]:7780"]) {
      const request = await signedRequest(owner, "PUT", `/v1/servers/${randomServerId()}`, { label: "A", protocol: "tcp", hostBridge });
      await expectJson(await worker.fetch(request, devEnv), 200);
    }
    // Not every address becomes acceptable: LAN addresses stay refused.
    const lan = await signedRequest(owner, "PUT", `/v1/servers/${randomServerId()}`, { label: "A", protocol: "tcp", hostBridge: "192.168.1.10:7780" });
    await expectJson(await worker.fetch(lan, devEnv), 400);
  });

  it("never signs a ticket for a loopback bridge once the development setting is off", async () => {
    const friend = await pendingFriend();
    const put = await signedRequest(friend.owner, "PUT", `/v1/servers/${friend.serverId}`, {
      label: "Survival",
      protocol: "tcp",
      hostBridge: "127.0.0.1:7780",
    });
    await expectJson(await worker.fetch(put, devEnv), 200);
    await approve(friend);
    await bindNode(friend);
    await confirmNode(friend);
    const session = await generateKey();

    // The production-shaped broker (no setting) refuses to issue, with the generic 404 ...
    await expectGeneric404(await call(friend.device, "POST", "/v1/sessions", { membershipId: friend.membershipId, sessionSpki: session.spki }));
    // ... while the development broker still does, so the refusal really comes from the setting.
    const dev = await signedRequest(friend.device, "POST", "/v1/sessions", { membershipId: friend.membershipId, sessionSpki: session.spki });
    const issued = await expectJson(await worker.fetch(dev, devEnv), 201);
    expect(issued.hostBridge).toBe("127.0.0.1:7780");
  });
});
