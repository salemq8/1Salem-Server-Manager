import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import {
  BASE,
  call,
  expectGeneric404,
  expectJson,
  generateKey,
  newIdentity,
  nowSeconds,
  randomB64u,
  registerDevice,
  registerOwner,
  send,
  signedRequest,
} from "./helpers";

const UNAUTHORIZED = '{"error":"unauthorized"}';

async function expectUnauthorized(response: Response): Promise<void> {
  expect(response.status).toBe(401);
  expect(await response.text()).toBe(UNAUTHORIZED);
}

describe("registration (self-signed, proof of possession)", () => {
  it("derives own_/dev_ ids from the SPKI and is idempotent", async () => {
    const owner = await newIdentity("own");
    expect(owner.id).toMatch(/^own_[a-z2-7]{26}$/);
    const first = await expectJson(await call(owner, "POST", "/v1/owners", { spki: owner.spki }), 201);
    expect(first).toEqual({ ownerId: owner.id });
    const again = await expectJson(await call(owner, "POST", "/v1/owners", { spki: owner.spki }), 200);
    expect(again).toEqual({ ownerId: owner.id });

    const device = await newIdentity("dev");
    const registered = await expectJson(await call(device, "POST", "/v1/devices", { spki: device.spki }), 201);
    expect(registered).toEqual({ deviceId: device.id });
  });

  it("stores the public key only", async () => {
    const owner = await registerOwner();
    const row = await env.DB.prepare("SELECT * FROM owners WHERE id = ?1").bind(owner.id).first();
    expect(Object.keys(row!).sort()).toEqual(["created_at", "id", "spki"]);
    expect(row!.spki).toBe(owner.spki);
  });

  it("rejects a body SPKI that is not the signing key", async () => {
    const owner = await newIdentity("own");
    const other = await generateKey();
    // Header claims the owner's id and the owner signs, but the body carries someone else's key.
    await expectUnauthorized(await call(owner, "POST", "/v1/owners", { spki: other.spki }));
    // Header id matches the body key, but the signature is made with a different private key.
    await expectUnauthorized(
      await call(owner, "POST", "/v1/owners", { spki: owner.spki }, { signWith: other.privateKey }),
    );
    const row = await env.DB.prepare("SELECT id FROM owners WHERE id = ?1").bind(owner.id).first();
    expect(row).toBeNull();
  });

  it("rejects a device key used to register an owner", async () => {
    const device = await newIdentity("dev");
    await expectUnauthorized(await call(device, "POST", "/v1/owners", { spki: device.spki }));
  });

  it("rejects a malformed SPKI", async () => {
    const owner = await newIdentity("own");
    const response = await call(owner, "POST", "/v1/owners", { spki: randomB64u(91) });
    expect(response.status).toBe(400);
  });
});

describe("signed requests", () => {
  it("accepts a correctly signed request", async () => {
    const owner = await registerOwner();
    await expectJson(await call(owner, "GET", "/v1/owners/me/memberships"), 200);
  });

  it("rejects a replayed nonce", async () => {
    const owner = await registerOwner();
    const request = await signedRequest(owner, "GET", "/v1/owners/me/memberships");
    const replay = request.clone();
    await expectJson(await send(request), 200);
    await expectUnauthorized(await send(replay));

    // The same nonce under a fresh signature and timestamp is still a replay for this key.
    const nonce = randomB64u(16);
    await expectJson(await call(owner, "GET", "/v1/owners/me/memberships", undefined, { nonce }), 200);
    await expectUnauthorized(
      await call(owner, "GET", "/v1/owners/me/memberships", undefined, { nonce, time: nowSeconds() - 1 }),
    );
  });

  it("tracks nonces per key", async () => {
    const a = await registerOwner();
    const b = await registerOwner();
    const nonce = randomB64u(16);
    await expectJson(await call(a, "GET", "/v1/owners/me/memberships", undefined, { nonce }), 200);
    await expectJson(await call(b, "GET", "/v1/owners/me/memberships", undefined, { nonce }), 200);
  });

  it("rejects timestamps outside ±300 s", async () => {
    const owner = await registerOwner();
    await expectUnauthorized(
      await call(owner, "GET", "/v1/owners/me/memberships", undefined, { time: nowSeconds() - 301 }),
    );
    await expectUnauthorized(
      await call(owner, "GET", "/v1/owners/me/memberships", undefined, { time: nowSeconds() + 301 }),
    );
    await expectJson(
      await call(owner, "GET", "/v1/owners/me/memberships", undefined, { time: nowSeconds() - 290 }),
      200,
    );
  });

  it("rejects a signature by another key", async () => {
    const owner = await registerOwner();
    const other = await generateKey();
    await expectUnauthorized(
      await call(owner, "GET", "/v1/owners/me/memberships", undefined, { signWith: other.privateKey }),
    );
  });

  it("rejects a body that differs from the signed body", async () => {
    const owner = await registerOwner();
    const serverId = crypto.randomUUID();
    const body = { label: "A", protocol: "tcp", hostBridge: "100.64.0.1:7780" };
    await expectUnauthorized(
      await call(owner, "PUT", `/v1/servers/${serverId}`, body, {
        signedBody: JSON.stringify({ ...body, label: "B" }),
      }),
    );
  });

  it("signs the path without the query string", async () => {
    const owner = await registerOwner();
    await expectJson(
      await call(owner, "GET", "/v1/owners/me/revocations", undefined, { query: "?after=0" }),
      200,
    );
  });

  it("rejects malformed headers, unknown keys and the wrong key kind", async () => {
    const owner = await registerOwner();
    const device = await registerDevice();
    const stranger = await newIdentity("own");
    await expectUnauthorized(await call(stranger, "GET", "/v1/owners/me/memberships"));
    await expectUnauthorized(await call(device, "GET", "/v1/owners/me/memberships"));
    await expectUnauthorized(await call(owner, "GET", "/v1/devices/me/memberships"));
    await expectUnauthorized(await call(owner, "GET", "/v1/owners/me/memberships", undefined, { nonce: randomB64u(15) }));
    await expectUnauthorized(await call(owner, "GET", "/v1/owners/me/memberships", undefined, { keyId: "own_short" }));
    await expectUnauthorized(await send(new Request(`${BASE}/v1/owners/me/memberships`)));
  });

  it("rejects bodies over 16 KiB before doing any work", async () => {
    const owner = await registerOwner();
    const big = { ciphertext: "a".repeat(16 * 1024) };
    const response = await call(owner, "POST", "/v1/invites", big);
    expect(response.status).toBe(413);
    expect(await response.text()).toBe('{"error":"payload_too_large"}');
  });

  it("answers unknown paths and malformed ids with the generic 404", async () => {
    const owner = await registerOwner();
    await expectGeneric404(await send(new Request(`${BASE}/v1/nothing`)));
    await expectGeneric404(await call(owner, "POST", "/v1/memberships/not-an-id/approve"));
    await expectGeneric404(await call(owner, "PUT", "/v1/servers/NOT-A-GUID", {}));
  });
});
