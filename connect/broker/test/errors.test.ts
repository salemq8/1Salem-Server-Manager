import { env } from "cloudflare:workers";
import { afterEach, describe, expect, it, vi } from "vitest";
import { redact } from "../src/http";
import {
  approve,
  BASE,
  call,
  createInvite,
  expectGeneric404,
  expectJson,
  freezeClock,
  hourWindow,
  issueTicket,
  newIdentity,
  pendingFriend,
  randomB64u,
  randomServerId,
  readyFriend,
  redeem,
  registerDevice,
  registerOwner,
  send,
  signedRequest,
} from "./helpers";

const ERROR_BODY = /^\{"error":"[a-z_]+"\}$/;

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("request body limit (16 KiB)", () => {
  async function inviteWithBodyOfSize(size: number): Promise<Response> {
    const owner = await registerOwner();
    const serverId = randomServerId();
    const prefix = `{"serverId":"${serverId}","pad":"`;
    const body = prefix + "a".repeat(size - prefix.length - 2) + '"}';
    expect(new TextEncoder().encode(body).byteLength).toBe(size);
    return call(owner, "POST", "/v1/invites", body);
  }

  it("accepts exactly 16384 bytes and refuses 16385", async () => {
    // 16384 bytes reach the handler (an unknown server is the generic 404); one more byte does not.
    await expectGeneric404(await inviteWithBodyOfSize(16 * 1024));
    const over = await inviteWithBodyOfSize(16 * 1024 + 1);
    expect(over.status).toBe(413);
    expect(await over.text()).toBe('{"error":"payload_too_large"}');
  });

  it("refuses an oversized streamed body that declares no length", async () => {
    const device = await registerDevice();
    const chunk = new TextEncoder().encode("a".repeat(4096));
    let sent = 0;
    const stream = new ReadableStream<Uint8Array>({
      pull(controller) {
        if (sent >= 5) controller.close();
        else {
          sent++;
          controller.enqueue(chunk);
        }
      },
    });
    const signed = await signedRequest(device, "POST", "/v1/invites/redeem", "x");
    const headers = new Headers(signed.headers);
    headers.delete("Content-Length");
    const response = await send(new Request(signed.url, { method: "POST", headers, body: stream }));
    expect(response.status).toBe(413);
    expect(await response.text()).toBe('{"error":"payload_too_large"}');
  });

  it("refuses an oversized registration before counting it against the IP", async () => {
    freezeClock(hourWindow(1) + 1);
    const ip = "10.203.0.1";
    for (let i = 0; i < 31; i++) {
      const response = await send(
        new Request(`${BASE}/v1/devices`, {
          method: "POST",
          headers: { "CF-Connecting-IP": ip, "Content-Type": "application/json" },
          body: "x".repeat(16 * 1024 + 1),
        }),
      );
      expect(response.status).toBe(413);
    }
    // More than the 30-per-window budget was refused above, yet this IP may still register.
    const device = await newIdentity("dev");
    await expectJson(await call(device, "POST", "/v1/devices", { spki: device.spki }, { ip }), 201);
  });
});

describe("error bodies and logs carry no secrets", () => {
  it("every failure is a bare error code, whatever secret the request carried", async () => {
    const logged: string[] = [];
    for (const method of ["log", "info", "warn", "error"] as const) {
      vi.spyOn(console, method).mockImplementation((...args: unknown[]) => {
        logged.push(args.map(String).join(" "));
      });
    }

    const friend = await readyFriend();
    const { ticket } = await issueTicket(friend);
    const invite = await createInvite(friend.owner, friend.serverId);
    await expectJson(await redeem(await registerDevice(), invite.secret), 201);
    const secondInvite = await createInvite(friend.owner, friend.serverId);
    const replayed = await signedRequest(friend.owner, "GET", "/v1/owners/me/memberships");
    const replayedSignature = replayed.headers.get("X-1S-Sig")!;
    await send(replayed.clone());

    const failures: Response[] = [
      // Used invite secret, and a live membership that makes a second redeem conflict.
      await redeem(await registerDevice(), invite.secret),
      await redeem(friend.device, secondInvite.secret),
      // Replayed signature.
      await send(replayed),
      // Ticket and secrets echoed back in bodies the broker refuses.
      await call(friend.device, "POST", `/v1/memberships/${friend.membershipId}/node`, { nodeId: ticket }),
      await call(friend.owner, "POST", "/v1/invites", { serverId: friend.serverId, ttlSeconds: ticket }),
      await call(friend.device, "POST", "/v1/sessions", { membershipId: friend.membershipId, sessionSpki: ticket }),
      await call(friend.owner, "PUT", `/v1/servers/${friend.serverId}`, {
        label: invite.secret,
        protocol: "udp",
        hostBridge: env.INVITE_PEPPER,
      }),
      await call(friend.owner, "POST", `/v1/memberships/${friend.membershipId}/enrollment`, {
        ciphertext: `${ticket} ${env.TICKET_SIGNING_KEY}`,
      }),
      await call(await registerOwner(), "POST", `/v1/memberships/${friend.membershipId}/approve`),
      await send(new Request(`${BASE}/v1/sessions`, { method: "POST", body: JSON.stringify({ ticket }) })),
    ];

    const sensitive = [
      invite.secret,
      secondInvite.secret,
      ticket,
      ticket.split(".")[2]!,
      replayedSignature,
      env.INVITE_PEPPER,
      env.TICKET_SIGNING_KEY.slice(0, 40),
    ];
    const statuses = new Set<number>();
    for (const response of failures) {
      const text = await response.text();
      statuses.add(response.status);
      expect(text).toMatch(ERROR_BODY);
      for (const value of sensitive) expect(text).not.toContain(value);
    }
    expect([...statuses].sort()).toEqual([400, 401, 404, 409]);

    expect(logged.length).toBeGreaterThan(0);
    for (const line of logged) for (const value of sensitive) expect(line).not.toContain(value);
  });

  it("the 404 for a foreign membership is byte-identical to the 404 for an unknown path", async () => {
    const friend = await pendingFriend();
    await approve(friend);
    const stranger = await registerOwner();
    const foreign = await call(stranger, "POST", `/v1/memberships/${friend.membershipId}/reject`);
    const unknown = await send(new Request(`${BASE}/v1/memberships/${friend.membershipId}/unknown`));
    const pair = await Promise.all(
      [foreign, unknown].map(async (r) => `${r.status} ${[...r.headers].sort().join(";")} ${await r.text()}`),
    );
    expect(pair[0]).toBe(pair[1]);
  });

  it("redacts long base64/hex runs from log details", () => {
    const secret = randomB64u(32);
    const line = redact(`failed for ${secret} and deadbeefdeadbeefdeadbeefdeadbeef, short ok`);
    expect(line).toBe("failed for [redacted] and [redacted], short ok");
  });
});
