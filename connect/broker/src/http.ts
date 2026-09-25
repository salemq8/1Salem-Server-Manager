// Response helpers, bounded body reading and the broker's only logger.
//
// External errors are deliberately coarse: a caller learns "not found", "unauthorized" or "rate
// limited", never which check failed. The precise reason stays in the (redacted) log.

import { fromUtf8 } from "./encoding";

export const MAX_BODY_BYTES = 16 * 1024;

export class HttpError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    /** Internal reason for the log only; never sent to the client. */
    readonly reason?: string,
    readonly headers?: Record<string, string>,
  ) {
    super(code);
  }
}

/** The one 404. Missing, foreign, malformed-id and invalid-invite cases all produce this exact
 *  response so that nothing reveals whether an id exists or belongs to someone else. */
export const notFound = (reason?: string) => new HttpError(404, "not_found", reason);
export const unauthorized = (reason: string) => new HttpError(401, "unauthorized", reason);
export const badRequest = (reason: string) => new HttpError(400, "bad_request", reason);
export const conflict = (code: string) => new HttpError(409, code);
export const notConfigured = (reason: string) => new HttpError(503, "not_configured", reason);
export const rateLimited = (retryAfterSeconds: number, reason: string) =>
  new HttpError(429, "rate_limited", reason, { "Retry-After": String(retryAfterSeconds) });

const BASE_HEADERS: Record<string, string> = {
  "Content-Type": "application/json; charset=utf-8",
  "Cache-Control": "no-store",
  "X-Content-Type-Options": "nosniff",
};

export function json(status: number, body: unknown, headers?: Record<string, string>): Response {
  return new Response(JSON.stringify(body), { status, headers: { ...BASE_HEADERS, ...headers } });
}

export function errorResponse(error: HttpError): Response {
  return json(error.status, { error: error.code }, error.headers);
}

/** Reads the whole body, refusing more than MAX_BODY_BYTES whether or not Content-Length is honest. */
export async function readBody(request: Request): Promise<Uint8Array> {
  const declared = request.headers.get("Content-Length");
  if (declared !== null && Number(declared) > MAX_BODY_BYTES) {
    throw new HttpError(413, "payload_too_large", "declared length over limit");
  }
  if (request.body === null) return new Uint8Array(0);

  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > MAX_BODY_BYTES) {
      await reader.cancel();
      throw new HttpError(413, "payload_too_large", "streamed length over limit");
    }
    chunks.push(value);
  }
  const body = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    body.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return body;
}

export type JsonObject = Record<string, unknown>;

export function parseJsonObject(body: Uint8Array): JsonObject {
  const text = fromUtf8(body);
  if (text === null) throw badRequest("body is not UTF-8");
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    throw badRequest("body is not JSON");
  }
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw badRequest("body is not a JSON object");
  }
  return value as JsonObject;
}

export function stringField(body: JsonObject, name: string): string {
  const value = body[name];
  if (typeof value !== "string") throw badRequest(`${name} must be a string`);
  return value;
}

// Anything that looks like key material, a secret, a ticket or a signature (long base64/hex runs)
// is replaced before a line is written. Ids are long enough to be caught too; logs do not need them.
const SECRET_LIKE = /[A-Za-z0-9+/_\-.=]{20,}/g;

export function redact(text: string): string {
  return text.replace(SECRET_LIKE, "[redacted]");
}

export function log(level: "info" | "warn" | "error", event: string, detail?: string): void {
  const line = detail === undefined ? event : `${event}: ${redact(detail)}`;
  if (level === "error") console.error(line);
  else if (level === "warn") console.warn(line);
  else console.log(line);
}
