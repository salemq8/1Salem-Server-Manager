#!/usr/bin/env node
// Creates the broker's two PRODUCTION secrets and stores them with `wrangler secret put`, for the
// separately approved deployment step only (README, "Production deployment runbook").
//
//   TICKET_SIGNING_KEY  PKCS#8 DER of a fresh ECDSA P-256 key, standard base64
//   INVITE_PEPPER       32 CSPRNG bytes, base64url
//
// Usage: node scripts/put-production-secrets.mjs --confirm-production
//
// The values exist only in this process and are piped to Wrangler's stdin; they are never printed,
// written to disk or passed on a command line. The script refuses to run if either secret already
// exists, because replacing the ticket key would invalidate the key Agents have pinned. Only the
// public half (kid and SPKI, what GET /v1/keys will publish) is printed.

import { spawnSync } from "node:child_process";
import { parseArgs } from "node:util";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const { values } = parseArgs({ options: { "confirm-production": { type: "boolean", default: false } }, strict: true });
if (!values["confirm-production"]) {
  console.error("error: this writes production secrets; pass --confirm-production to proceed.");
  process.exit(2);
}

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const wrangler = join(root, "node_modules", "wrangler", "bin", "wrangler.js");
const run = (args, input) =>
  spawnSync(process.execPath, [wrangler, ...args], { cwd: root, input, encoding: "utf8", stdio: ["pipe", "pipe", "inherit"] });

const listed = run(["secret", "list", "--env", "production", "--format", "json"]);
if (listed.status !== 0) {
  console.error("error: could not list the production secrets (is Wrangler logged in to the right account?).");
  process.exit(1);
}
const existing = new Set(JSON.parse(listed.stdout).map((secret) => secret.name));
for (const name of ["TICKET_SIGNING_KEY", "INVITE_PEPPER"]) {
  if (existing.has(name)) {
    console.error(`error: ${name} already exists in production; refusing to replace it.`);
    process.exit(1);
  }
}

const { subtle } = globalThis.crypto;
const b64url = (bytes) => Buffer.from(bytes).toString("base64url");
const pair = await subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
const pkcs8 = new Uint8Array(await subtle.exportKey("pkcs8", pair.privateKey));
const spki = new Uint8Array(await subtle.exportKey("spki", pair.publicKey));
const secrets = {
  TICKET_SIGNING_KEY: Buffer.from(pkcs8).toString("base64"),
  INVITE_PEPPER: b64url(crypto.getRandomValues(new Uint8Array(32))),
};
pkcs8.fill(0);

for (const [name, value] of Object.entries(secrets)) {
  const stored = run(["secret", "put", name, "--env", "production"], value);
  if (stored.status !== 0) {
    console.error(`error: storing ${name} failed; nothing else was changed after it.`);
    process.exit(1);
  }
  console.log(`${name} stored.`);
}

// Same derivation as src/secrets.ts: first 16 chars of base64url(SHA-256(SPKI DER)).
const kid = b64url(new Uint8Array(await subtle.digest("SHA-256", spki))).slice(0, 16);
console.log(JSON.stringify({ kid, alg: "ES256", spki: b64url(spki) }, null, 2));
