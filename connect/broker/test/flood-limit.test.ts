import { describe, expect, it, vi } from "vitest";
import type { Env } from "../src/env";
import { handle } from "../src/index";
import { BASE } from "./helpers";

function testEnv(overrides: Partial<Env> = {}): Env {
  return {
    DB: null as unknown as D1Database,
    TICKET_SIGNING_KEY: "unused",
    INVITE_PEPPER: "unused",
    ...overrides,
  };
}

describe("outer flood limiter", () => {
  it("is optional in local development", async () => {
    const response = await handle(new Request(`${BASE}/missing`), testEnv());
    expect(response.status).toBe(404);
  });

  it("fails closed before route handling when production requires a missing binding", async () => {
    const response = await handle(
      new Request(`${BASE}/missing`),
      testEnv({ CONNECT_REQUIRE_FLOOD_LIMIT: "true" }),
    );
    expect(response.status).toBe(503);
    expect(await response.text()).toBe('{"error":"not_configured"}');
  });

  it("continues into normal routing when the configured binding allows the request", async () => {
    const limit = vi.fn(async () => ({ success: true }));
    const response = await handle(
      new Request(`${BASE}/missing`, { headers: { "CF-Connecting-IP": "203.0.113.42" } }),
      testEnv({ FLOOD: { limit }, CONNECT_REQUIRE_FLOOD_LIMIT: "true" }),
    );
    expect(response.status).toBe(404);
    expect(limit).toHaveBeenCalledWith({ key: "203.0.113.42" });
  });

  it("keys the binding on the normalized client network and stops a refused body before it is read", async () => {
    const limit = vi.fn(async () => ({ success: false }));
    const body = new ReadableStream<Uint8Array>({
      pull() {
        throw new Error("body must not be read before the outer limiter");
      },
    });
    const response = await handle(
      new Request(`${BASE}/v1/keys`, {
        method: "POST",
        headers: { "CF-Connecting-IP": "2001:0db8:1234:5678:aaaa:bbbb:cccc:dddd" },
        body,
      }),
      testEnv({ FLOOD: { limit } }),
    );
    expect(response.status).toBe(429);
    expect(response.headers.get("Retry-After")).toBe("60");
    expect(limit).toHaveBeenCalledWith({ key: "2001:db8:1234:5678::/64" });
  });

  it("fails closed when the configured binding is unavailable", async () => {
    const response = await handle(
      new Request(`${BASE}/missing`),
      testEnv({ FLOOD: { limit: async () => { throw new Error("binding unavailable"); } } }),
    );
    expect(response.status).toBe(503);
    expect(await response.text()).toBe('{"error":"not_configured"}');
  });
});
