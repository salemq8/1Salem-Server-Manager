-- 1Salem Connect broker, initial schema (docs/CONNECT_ARCHITECTURE.md §13).
--
-- Invariants:
--  * No private key, Tailscale OAuth secret, auth key or plaintext invite secret is ever stored.
--    Invites keep only HMAC-SHA256(INVITE_PEPPER, secret); enrollments keep opaque ciphertext.
--  * Times are unix seconds (INTEGER).
--  * D1 bills rows scanned, so every lookup path below has an index that matches its WHERE clause.

-- Owners and devices are identified by their public key. The id is derived from the SPKI, so a
-- row can only be created by whoever holds the matching private key (proof of possession).
CREATE TABLE owners (
  id          TEXT PRIMARY KEY,            -- own_ + base32(SHA-256(SPKI))[0..26]
  spki        TEXT NOT NULL,               -- base64url SubjectPublicKeyInfo DER, P-256
  created_at  INTEGER NOT NULL
);

CREATE TABLE devices (
  id          TEXT PRIMARY KEY,            -- dev_ + base32(SHA-256(SPKI))[0..26]
  spki        TEXT NOT NULL,
  created_at  INTEGER NOT NULL
);

-- ServerIds are chosen by each owner's Server Manager, so they are only unique per owner. A
-- global key would let one owner detect another owner's ServerId through a conflict.
CREATE TABLE servers (
  owner_id     TEXT NOT NULL REFERENCES owners(id),
  server_id    TEXT NOT NULL,              -- lowercase GUID
  label        TEXT NOT NULL,
  protocol     TEXT NOT NULL CHECK (protocol IN ('tcp', 'udp')),
  host_bridge  TEXT NOT NULL,              -- ip:port of the host bridge on the tailnet
  created_at   INTEGER NOT NULL,
  updated_at   INTEGER NOT NULL,
  PRIMARY KEY (owner_id, server_id)
);

CREATE TABLE invites (
  id              TEXT PRIMARY KEY,        -- inv_ + base32(16 random bytes)
  owner_id        TEXT NOT NULL,
  server_id       TEXT NOT NULL,
  secret_hmac     TEXT NOT NULL UNIQUE,    -- hex HMAC-SHA256(INVITE_PEPPER, secret); index serves redeem
  state           TEXT NOT NULL CHECK (state IN ('active', 'used', 'revoked')),
  created_at      INTEGER NOT NULL,
  expires_at      INTEGER NOT NULL,
  used_at         INTEGER,
  used_by_device  TEXT,
  membership_id   TEXT,
  revoked_at      INTEGER,
  FOREIGN KEY (owner_id, server_id) REFERENCES servers(owner_id, server_id)
);

-- One membership per (device, server). av is the authorization version: revocation increments it
-- and hosts reject tickets whose av is below the published floor.
CREATE TABLE memberships (
  id             TEXT PRIMARY KEY,         -- mem_ + base32(16 random bytes)
  owner_id       TEXT NOT NULL,
  server_id      TEXT NOT NULL,
  device_id      TEXT NOT NULL REFERENCES devices(id),
  invite_id      TEXT NOT NULL REFERENCES invites(id),
  state          TEXT NOT NULL CHECK (state IN ('pending', 'approved', 'rejected', 'revoked')),
  av             INTEGER NOT NULL DEFAULT 1,
  node_id        TEXT,                     -- Tailscale StableNodeID, bound once by the device
  created_at     INTEGER NOT NULL,
  updated_at     INTEGER NOT NULL,
  approved_at    INTEGER,
  node_bound_at  INTEGER,
  revoked_at     INTEGER,
  FOREIGN KEY (owner_id, server_id) REFERENCES servers(owner_id, server_id)
);
-- A device holds at most one live membership per server; a revoked or rejected friend can be
-- invited again.
CREATE UNIQUE INDEX memberships_live
  ON memberships(owner_id, server_id, device_id) WHERE state IN ('pending', 'approved');
CREATE INDEX memberships_by_owner ON memberships(owner_id, state, created_at);
CREATE INDEX memberships_by_device ON memberships(device_id, owner_id);

-- Enrollment relay: opaque ciphertext for exactly one device, 15 minutes, deleted on first read.
CREATE TABLE enrollments (
  membership_id  TEXT PRIMARY KEY REFERENCES memberships(id),
  owner_id       TEXT NOT NULL,
  device_id      TEXT NOT NULL,
  ciphertext     TEXT NOT NULL,
  created_at     INTEGER NOT NULL,
  expires_at     INTEGER NOT NULL
);
CREATE INDEX enrollments_by_expiry ON enrollments(expires_at);
CREATE INDEX enrollments_by_owner_device ON enrollments(owner_id, device_id);

-- Issued tickets. The ticket itself is never stored; only its id and binding.
CREATE TABLE sessions (
  jti            TEXT PRIMARY KEY,
  membership_id  TEXT NOT NULL REFERENCES memberships(id),
  owner_id       TEXT NOT NULL,
  device_id      TEXT NOT NULL,
  server_id      TEXT NOT NULL,
  av             INTEGER NOT NULL,
  issued_at      INTEGER NOT NULL,
  exp            INTEGER NOT NULL,
  revoked_at     INTEGER
);
CREATE INDEX sessions_by_membership ON sessions(membership_id);
CREATE INDEX sessions_by_owner_device ON sessions(owner_id, device_id);
CREATE INDEX sessions_by_exp ON sessions(exp);

-- Revocation feed for host enforcement (GET /v1/owners/me/revocations). Kept separately from
-- sessions so that purging expired sessions never drops a revocation a host has not seen.
CREATE TABLE revocations (
  seq            INTEGER PRIMARY KEY AUTOINCREMENT,
  owner_id       TEXT NOT NULL,
  kind           TEXT NOT NULL CHECK (kind IN ('session', 'membership', 'device')),
  jti            TEXT,
  membership_id  TEXT,
  device_id      TEXT,
  server_id      TEXT,
  av             INTEGER,
  at             INTEGER NOT NULL
);
CREATE INDEX revocations_by_owner ON revocations(owner_id, at, seq);

-- Signed-request nonces, remembered per key until the request timestamp leaves the ±300 s window.
CREATE TABLE request_nonces (
  key_id      TEXT NOT NULL,
  nonce       TEXT NOT NULL,
  expires_at  INTEGER NOT NULL,
  PRIMARY KEY (key_id, nonce)
);
CREATE INDEX request_nonces_by_expiry ON request_nonces(expires_at);

-- Exact fixed-window counters. IP buckets hold an HMAC of the address, not the address.
CREATE TABLE rate_counters (
  bucket        TEXT NOT NULL,
  window_start  INTEGER NOT NULL,
  hits          INTEGER NOT NULL,
  expires_at    INTEGER NOT NULL,
  PRIMARY KEY (bucket, window_start)
);
CREATE INDEX rate_counters_by_expiry ON rate_counters(expires_at);

-- Who did what. Never secrets, invite secrets, tickets, signatures or IP addresses. Append-only;
-- no route reads it, so it carries no secondary index (each index costs a write per row).
CREATE TABLE audit (
  seq     INTEGER PRIMARY KEY AUTOINCREMENT,
  at      INTEGER NOT NULL,
  actor   TEXT NOT NULL,
  action  TEXT NOT NULL,
  target  TEXT
);
