import type { D1Migration } from "cloudflare:test";
import type { Env as BrokerEnv } from "../src/env";

declare global {
  namespace Cloudflare {
    interface Env extends BrokerEnv {
      /** Supplied by vitest.config.ts; always set in tests. */
      TICKET_SIGNING_KEY: string;
      INVITE_PEPPER: string;
      TEST_MIGRATIONS: D1Migration[];
    }
    interface GlobalProps {
      mainModule: typeof import("../src/index");
    }
  }
}
