// Scheduled cleanup (cron in wrangler.jsonc). Every statement is a range delete on an expiry
// index, so each run scans only rows that are actually removed.

/** Expired session rows linger a day so an owner revoking a just-expired jti gets a normal answer
 *  instead of a 404. Revocation events live in their own table and are not affected. */
export const SESSION_RETENTION_SECONDS = 24 * 3600;

export interface PurgeCounts {
  nonces: number;
  rateCounters: number;
  enrollments: number;
  sessions: number;
}

export async function purgeExpired(db: D1Database, now: number): Promise<PurgeCounts> {
  const [nonces, rateCounters, enrollments, sessions] = await db.batch([
    db.prepare(`DELETE FROM request_nonces WHERE expires_at < ?1`).bind(now),
    db.prepare(`DELETE FROM rate_counters WHERE expires_at <= ?1`).bind(now),
    db.prepare(`DELETE FROM enrollments WHERE expires_at <= ?1`).bind(now),
    db.prepare(`DELETE FROM sessions WHERE exp < ?1`).bind(now - SESSION_RETENTION_SECONDS),
  ]);
  return {
    nonces: nonces!.meta.changes,
    rateCounters: rateCounters!.meta.changes,
    enrollments: enrollments!.meta.changes,
    sessions: sessions!.meta.changes,
  };
}
