// Runs the tests inside workerd through @cloudflare/vitest-plugin, fully offline. Secrets are
// generated fresh for every run; no key material is ever read from or written to disk.
//
// wrangler.jsonc declares `secrets.required` so that a real deploy cannot forget them. Wrangler
// checks that list against .dev.vars / .env / process.env when the plugin reads the config, so the
// generated values are placed in this process's environment (never on disk) to satisfy the check,
// and passed again as explicit bindings so they also win over a developer's own .dev.vars.

import { fileURLToPath } from "node:url";
import { cloudflareTest, readD1Migrations } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

async function testSecrets(): Promise<{ TICKET_SIGNING_KEY: string; INVITE_PEPPER: string }> {
  const pair = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
  const pkcs8 = new Uint8Array(await crypto.subtle.exportKey("pkcs8", pair.privateKey));
  return {
    TICKET_SIGNING_KEY: Buffer.from(pkcs8).toString("base64"),
    INVITE_PEPPER: Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url"),
  };
}

export default defineConfig(async () => {
  const migrations = await readD1Migrations(fileURLToPath(new URL("./migrations", import.meta.url)));
  const secrets = await testSecrets();
  Object.assign(process.env, secrets);
  return {
    plugins: [
      cloudflareTest({
        wrangler: { configPath: "./wrangler.jsonc" },
        // Never proxy a binding to a real Cloudflare account.
        remoteBindings: false,
        miniflare: {
          bindings: { ...secrets, TEST_MIGRATIONS: migrations },
        },
      }),
    ],
    test: {
      setupFiles: ["./test/setup.ts"],
    },
  };
});
