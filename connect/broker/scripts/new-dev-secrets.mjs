#!/usr/bin/env node
// Generates LOCAL DEVELOPMENT secrets for the broker (docs/CONNECT_ARCHITECTURE.md §13):
//
//   TICKET_SIGNING_KEY  PKCS#8 DER of a fresh ECDSA P-256 key, standard base64
//   INVITE_PEPPER       32 CSPRNG bytes, base64url
//
// Usage: node scripts/new-dev-secrets.mjs --out <file> [--force]
//
// The file is written only to the path given with --out; there is no default location, so the
// script can never drop key material somewhere unexpected. An existing file is left alone unless
// --force is given. The secrets themselves are never printed: stdout carries only the public half
// (the SPKI that GET /v1/keys will publish, and its kid) so it can be pinned by a local host.
//
// Use the output with `wrangler dev --env-file <file>`, or name it `.dev.vars` in this directory
// (git-ignored). These values are for a local broker only and must never be used in Cloudflare.

import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { parseArgs } from "node:util";

const { subtle } = globalThis.crypto;

function usage(message) {
  if (message) console.error(`error: ${message}`);
  console.error("usage: node scripts/new-dev-secrets.mjs --out <file> [--force]");
  process.exit(2);
}

let args;
try {
  args = parseArgs({
    options: { out: { type: "string" }, force: { type: "boolean", default: false }, help: { type: "boolean" } },
    strict: true,
  }).values;
} catch (error) {
  usage(error.message);
}
if (args.help) usage();
if (!args.out) usage("--out <file> is required");

const b64url = (bytes) => Buffer.from(bytes).toString("base64url");

const pair = await subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
const pkcs8 = new Uint8Array(await subtle.exportKey("pkcs8", pair.privateKey));
const spki = new Uint8Array(await subtle.exportKey("spki", pair.publicKey));
const pepper = crypto.getRandomValues(new Uint8Array(32));
// Same derivation as src/secrets.ts: first 16 chars of base64url(SHA-256(SPKI DER)).
const kid = b64url(new Uint8Array(await subtle.digest("SHA-256", spki))).slice(0, 16);

const content = [
  "# 1Salem Connect broker: LOCAL DEVELOPMENT secrets only. Never commit, never deploy.",
  `# Generated ${new Date().toISOString()} by scripts/new-dev-secrets.mjs. Ticket key kid: ${kid}`,
  `TICKET_SIGNING_KEY="${Buffer.from(pkcs8).toString("base64")}"`,
  `INVITE_PEPPER="${b64url(pepper)}"`,
  "",
].join("\n");

const out = resolve(args.out);
try {
  // "wx" refuses to replace an existing file; mode 0600 applies where the OS honours it (on
  // Windows the file inherits the directory ACL, so keep it under your own profile or the repo).
  writeFileSync(out, content, { encoding: "utf8", mode: 0o600, flag: args.force ? "w" : "wx" });
} catch (error) {
  if (error.code === "EEXIST") usage(`${out} already exists; pass --force to replace it`);
  console.error(`error: could not write ${out}: ${error.code ?? error.message}`);
  process.exit(1);
}

console.log(JSON.stringify({ out, kid, alg: "ES256", spki: b64url(spki) }, null, 2));
