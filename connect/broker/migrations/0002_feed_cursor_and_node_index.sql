-- 1Salem Connect broker: revocation feed cursor and node binding lookup.
--
-- Only indexes change, so this applies to an existing database without touching its rows.

-- The revocation feed pages on seq (commit order), no longer on the wall-clock `at`:
-- WHERE owner_id = ? AND seq > ? ORDER BY seq. The old (owner_id, at, seq) index served only the
-- time cursor, and every index costs a write per inserted row.
DROP INDEX IF EXISTS revocations_by_owner;
CREATE INDEX revocations_by_owner_seq ON revocations(owner_id, seq);

-- Node binding refuses a node id that another device of the same owner holds on a live membership.
-- Only bound rows are indexed; a membership is created unbound and bound at most once.
CREATE INDEX memberships_by_owner_node ON memberships(owner_id, node_id) WHERE node_id IS NOT NULL;
