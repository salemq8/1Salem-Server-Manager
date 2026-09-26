import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import {
  approve,
  bindNode,
  call,
  confirmNode,
  expectGeneric404,
  expectJson,
  pendingFriend,
  putServer,
  randomB64u,
  readyFriend,
  registerOwner,
  requestSession,
  type Identity,
} from "./helpers";

const bindNodeAs = (device: Identity, membershipId: string, nodeId: string) =>
  call(device, "POST", `/v1/memberships/${membershipId}/node`, { nodeId });

async function nodeOf(membershipId: string): Promise<string | null> {
  const row = await env.DB.prepare(`SELECT node_id FROM memberships WHERE id = ?1`)
    .bind(membershipId)
    .first<{ node_id: string | null }>();
  return row!.node_id;
}

async function nodeStateOf(membershipId: string): Promise<string | null> {
  const row = await env.DB.prepare(`SELECT node_state FROM memberships WHERE id = ?1`)
    .bind(membershipId)
    .first<{ node_state: string | null }>();
  return row!.node_state;
}

describe("approval", () => {
  it("only the owning owner can approve; anyone else gets the generic 404", async () => {
    const friend = await pendingFriend();
    const stranger = await registerOwner();
    const path = `/v1/memberships/${friend.membershipId}/approve`;

    const foreign = await call(stranger, "POST", path);
    const missing = await call(stranger, "POST", "/v1/memberships/mem_aaaaaaaaaaaaaaaaaaaaaaaaaa/approve");
    await expectGeneric404(foreign);
    await expectGeneric404(missing);

    const approved = await expectJson(await call(friend.owner, "POST", path), 200);
    expect(approved).toEqual({ membershipId: friend.membershipId, state: "approved" });
    // Repeating the decision is a no-op.
    await expectJson(await call(friend.owner, "POST", path), 200);
  });

  it("a rejected membership cannot be approved afterwards", async () => {
    const friend = await pendingFriend();
    const rejected = await expectJson(
      await call(friend.owner, "POST", `/v1/memberships/${friend.membershipId}/reject`),
      200,
    );
    expect(rejected.state).toBe("rejected");
    const response = await call(friend.owner, "POST", `/v1/memberships/${friend.membershipId}/approve`);
    expect(response.status).toBe(409);
  });

  it("lists pending and approved friends with the device key the Agent encrypts to", async () => {
    const friend = await pendingFriend();
    const listed = await expectJson(await call(friend.owner, "GET", "/v1/owners/me/memberships"), 200);
    expect(listed.memberships).toEqual([
      expect.objectContaining({
        membershipId: friend.membershipId,
        serverId: friend.serverId,
        deviceId: friend.device.id,
        deviceSpki: friend.device.spki,
        state: "pending",
        av: 1,
        nodeId: null,
        nodeState: null,
        nodeBoundAt: null,
        nodeConfirmedAt: null,
      }),
    ]);
    const stranger = await registerOwner();
    const empty = await expectJson(await call(stranger, "GET", "/v1/owners/me/memberships"), 200);
    expect(empty.memberships).toEqual([]);
  });

  it("shows the friend its own memberships", async () => {
    const friend = await pendingFriend();
    await approve(friend);
    const listed = await expectJson(await call(friend.device, "GET", "/v1/devices/me/memberships"), 200);
    expect(listed.memberships).toEqual([
      expect.objectContaining({
        membershipId: friend.membershipId,
        ownerId: friend.owner.id,
        serverId: friend.serverId,
        state: "approved",
        protocol: "tcp",
      }),
    ]);
  });
});

describe("node binding", () => {
  it("requires approval, binds once, and is limited to the membership's device", async () => {
    const friend = await pendingFriend();
    const other = await pendingFriend(friend.owner, undefined, friend.serverId);
    const path = `/v1/memberships/${friend.membershipId}/node`;

    const early = await call(friend.device, "POST", path, { nodeId: friend.nodeId });
    expect(early.status).toBe(409);

    await approve(friend);
    await expectGeneric404(await call(other.device, "POST", path, { nodeId: "attacker-node" }));
    await expectJson(await call(friend.device, "POST", path, { nodeId: friend.nodeId }), 200);
    // Same id again is idempotent; a different id is refused.
    await expectJson(await call(friend.device, "POST", path, { nodeId: friend.nodeId }), 200);
    const rebind = await call(friend.device, "POST", path, { nodeId: "another-node" });
    expect(rebind.status).toBe(409);
    const malformed = await call(friend.device, "POST", path, { nodeId: "bad id with spaces" });
    expect(malformed.status).toBe(400);
  });

  it("lets one device reuse its node for every server of the same owner", async () => {
    const first = await readyFriend();
    const second = await pendingFriend(first.owner, first.device, await putServer(first.owner));
    await approve(second);
    const reused = await bindNodeAs(second.device, second.membershipId, first.nodeId);
    expect(await expectJson(reused, 200)).toEqual({ membershipId: second.membershipId, nodeId: first.nodeId });
    await confirmNode({ ...second, nodeId: first.nodeId });
    expect((await requestSession(second)).response.status).toBe(201);
  });

  it("refuses a node another device of the same owner holds, with a bare 409 that names nobody", async () => {
    const holder = await readyFriend();
    const intruder = await pendingFriend(holder.owner, undefined, holder.serverId);
    await approve(intruder);

    const refused = await bindNodeAs(intruder.device, intruder.membershipId, holder.nodeId);
    expect(refused.status).toBe(409);
    expect(await refused.text()).toBe('{"error":"node_in_use"}');
    expect(await nodeOf(intruder.membershipId)).toBeNull();
    // The refusal consumed nothing: the intruder can still bind a node of its own.
    await expectJson(await bindNodeAs(intruder.device, intruder.membershipId, intruder.nodeId), 200);
  });

  it("refuses the node on another server of the same owner too", async () => {
    const holder = await readyFriend();
    const intruder = await pendingFriend(holder.owner, undefined, await putServer(holder.owner));
    await approve(intruder);
    expect((await bindNodeAs(intruder.device, intruder.membershipId, holder.nodeId)).status).toBe(409);
  });

  it("never consults other owners' bindings", async () => {
    const elsewhere = await readyFriend();
    const friend = await pendingFriend();
    await approve(friend);
    await expectJson(await bindNodeAs(friend.device, friend.membershipId, elsewhere.nodeId), 200);
  });

  it("frees the node once the holder's membership is no longer live", async () => {
    const holder = await readyFriend();
    const successor = await pendingFriend(holder.owner, undefined, holder.serverId);
    await approve(successor);
    await expectJson(await call(holder.owner, "POST", `/v1/memberships/${holder.membershipId}/revoke`), 200);
    await expectJson(await bindNodeAs(successor.device, successor.membershipId, holder.nodeId), 200);
  });

  it("lets exactly one of two racing devices take a free node", async () => {
    const a = await pendingFriend();
    const b = await pendingFriend(a.owner, undefined, a.serverId);
    await approve(a);
    await approve(b);
    const contested = `fake-node-contested-${randomB64u(6)}`;
    const responses = await Promise.all([
      bindNodeAs(a.device, a.membershipId, contested),
      bindNodeAs(b.device, b.membershipId, contested),
    ]);
    expect(responses.map((r) => r.status).sort()).toEqual([200, 409]);
    const holders = [await nodeOf(a.membershipId), await nodeOf(b.membershipId)].filter((n) => n !== null);
    expect(holders).toEqual([contested]);
  });
});

describe("owner-confirmed node binding", () => {
  it("lists a candidate to both sides and issues no ticket until its owner confirms it", async () => {
    const friend = await pendingFriend();
    await approve(friend);
    await bindNode(friend);

    const ownerList = await expectJson(await call(friend.owner, "GET", "/v1/owners/me/memberships"), 200);
    expect(ownerList.memberships[0]).toEqual(expect.objectContaining({
      membershipId: friend.membershipId,
      nodeId: friend.nodeId,
      nodeState: "candidate",
      nodeBoundAt: expect.any(Number),
      nodeConfirmedAt: null,
    }));
    const deviceList = await expectJson(await call(friend.device, "GET", "/v1/devices/me/memberships"), 200);
    expect(deviceList.memberships[0]).toEqual(expect.objectContaining({ nodeState: "candidate" }));
    expect((await requestSession(friend)).response.status).toBe(404);

    await confirmNode(friend);
    await confirmNode(friend); // idempotent
    expect(await nodeStateOf(friend.membershipId)).toBe("confirmed");
    expect((await requestSession(friend)).response.status).toBe(201);
  });

  it("scopes confirmation to the owner and distinguishes missing, mismatched and invalid state", async () => {
    const friend = await pendingFriend();
    await approve(friend);
    await bindNode(friend);
    const stranger = await registerOwner();
    const path = `/v1/memberships/${friend.membershipId}/node/confirm`;

    await expectGeneric404(await call(stranger, "POST", path, { nodeId: friend.nodeId }));
    await expectGeneric404(
      await call(friend.owner, "POST", "/v1/memberships/mem_aaaaaaaaaaaaaaaaaaaaaaaaaa/node/confirm", { nodeId: friend.nodeId }),
    );
    expect(await expectJson(await call(friend.owner, "POST", path, { nodeId: "different-node" }), 409))
      .toEqual({ error: "node_mismatch" });
    await confirmNode(friend);
    expect(await expectJson(
      await call(friend.owner, "POST", `/v1/memberships/${friend.membershipId}/node/reject`, { nodeId: friend.nodeId }),
      409,
    )).toEqual({ error: "invalid_state" });
  });

  it("keeps a rejected candidate unusable and frees its node for another device", async () => {
    const rejected = await pendingFriend();
    const successor = await pendingFriend(rejected.owner, undefined, rejected.serverId);
    await approve(rejected);
    await approve(successor);
    await bindNode(rejected);

    const path = `/v1/memberships/${rejected.membershipId}/node/reject`;
    const decision = await expectJson(await call(rejected.owner, "POST", path, { nodeId: rejected.nodeId }), 200);
    expect(decision).toEqual({ membershipId: rejected.membershipId, nodeId: rejected.nodeId, nodeState: "rejected" });
    await expectJson(await call(rejected.owner, "POST", path, { nodeId: rejected.nodeId }), 200); // idempotent
    expect((await requestSession(rejected)).response.status).toBe(404);
    expect(await expectJson(await bindNodeAs(rejected.device, rejected.membershipId, rejected.nodeId), 409))
      .toEqual({ error: "node_rejected" });

    await expectJson(await bindNodeAs(successor.device, successor.membershipId, rejected.nodeId), 200);
    expect(await nodeStateOf(successor.membershipId)).toBe("candidate");
  });
});
