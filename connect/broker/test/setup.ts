import { applyD1Migrations } from "cloudflare:test";
import { env } from "cloudflare:workers";

// Idempotent: already-applied migrations are skipped, so every test file can run it.
await applyD1Migrations(env.DB, env.TEST_MIGRATIONS);
