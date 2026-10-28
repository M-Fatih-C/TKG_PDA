-- ============================================================
-- Migration 013: Server-Authoritative Field Sessions (WP-04)
-- Enforces single active session per employee across field devices.
-- Tracks session lease keepalives (heartbeat) and token lifespans.
--
-- Forward-only, additive, replay-safe.
-- ============================================================

CREATE TABLE IF NOT EXISTS auth.user_sessions (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             uuid NOT NULL REFERENCES auth.users(id) ON DELETE RESTRICT,
    device_id           uuid NOT NULL REFERENCES audit.client_devices(id) ON DELETE RESTRICT,
    client_type         varchar(32) NOT NULL DEFAULT 'PDA',
    created_at          timestamptz NOT NULL DEFAULT now(),
    last_seen_at        timestamptz NOT NULL DEFAULT now(),
    lease_expires_at    timestamptz NOT NULL,
    token_expires_at    timestamptz NOT NULL,
    revoked_at          timestamptz NULL,
    revocation_reason   varchar(64) NULL
);

-- Invariant: At most one unrevoked session per user at any time.
CREATE UNIQUE INDEX IF NOT EXISTS uq_user_sessions_active_user
    ON auth.user_sessions (user_id)
    WHERE revoked_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_user_sessions_lookup
    ON auth.user_sessions (user_id, device_id);

CREATE INDEX IF NOT EXISTS ix_user_sessions_lease
    ON auth.user_sessions (lease_expires_at);
