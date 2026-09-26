-- 1Salem Connect broker: owner-confirmed node binding (docs/CONNECT_ARCHITECTURE.md §21 D-1).
--
-- Additive only: two nullable columns and a backfill. memberships is not rebuilt (enrollments,
-- sessions and invites reference it), and revocations is not touched (its AUTOINCREMENT seq is the
-- feed cursor, and a rebuild would put that continuity at risk).

-- NULL while no node is bound. 'candidate' once the friend's device reports its node,
-- 'confirmed' once the owner's Agent has verified that node through the Tailscale API, and
-- 'rejected' when that verification failed. Only a confirmed binding gets session tickets.
ALTER TABLE memberships ADD COLUMN node_state TEXT NULL
  CHECK (node_state IS NULL OR node_state IN ('candidate', 'confirmed', 'rejected'));
ALTER TABLE memberships ADD COLUMN node_confirmed_at INTEGER NULL;

-- Bindings made before this migration were never verified by the owner, so they fail closed:
-- they become candidates and get no ticket until the owner confirms them.
UPDATE memberships SET node_state = 'candidate' WHERE node_id IS NOT NULL;
