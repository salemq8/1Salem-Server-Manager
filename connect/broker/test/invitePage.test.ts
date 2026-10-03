import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import worker from "../src/index";
import { BASE, send } from "./helpers";

async function page(query = "", overrides: Record<string, string> = {}): Promise<Response> {
  return worker.fetch(new Request(`${BASE}/i${query}`), { ...env, ...overrides } as typeof env);
}

describe("invite landing page", () => {
  it("serves one static page with a locked-down policy and no external loads", async () => {
    const response = await send(new Request(`${BASE}/i`));
    expect(response.status).toBe(200);
    expect(response.headers.get("Content-Type")).toBe("text/html; charset=utf-8");
    const csp = response.headers.get("Content-Security-Policy") ?? "";
    expect(csp).toMatch(/^default-src 'none'; script-src 'sha256-[A-Za-z0-9+/]+=*';/);
    expect(csp).toContain("frame-ancestors 'none'");
    expect(response.headers.get("Referrer-Policy")).toBe("no-referrer");
    expect(response.headers.get("X-Content-Type-Options")).toBe("nosniff");
    const html = await response.text();
    expect(html).not.toMatch(/<(img|link|iframe|form)\b|src=/i);
    expect(html).toContain('dir="rtl"');
  });

  it("is the same document whatever the request carries", async () => {
    const plain = await (await page()).text();
    expect(await (await page("?secret=abc<script>")).text()).toBe(plain);
  });

  it("links a download page only when an https one is configured", async () => {
    expect(await (await page()).text()).not.toContain("<a ");
    expect(await (await page("", { CONNECT_DOWNLOAD_URL: "http://example.test/" })).text()).not.toContain("<a ");
    const setup = "https://github.com/salemq8/1Salem-Server-Manager/releases/latest/download/1SalemConnect-Setup.exe";
    const linked = await (await page("", { CONNECT_DOWNLOAD_URL: setup })).text();
    expect(linked).toContain(`<a class="download" href="${setup}">Download 1Salem Connect</a>`);
    expect(linked).toContain(`<a class="download" href="${setup}">تنزيل 1Salem Connect</a>`);
  });

  it("answers only GET /i; neighbours stay the generic 404", async () => {
    for (const path of ["/i/", "/i/x", "/index.html", "/"]) {
      const response = await send(new Request(BASE + path));
      expect(response.status, path).toBe(404);
    }
    expect((await send(new Request(`${BASE}/i`, { method: "POST" }))).status).toBe(404);
  });
});
