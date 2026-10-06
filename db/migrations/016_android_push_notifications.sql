-- WP-09. Android push registration and a transactional outbox.
--
-- A finished-goods delivery notification is recorded in the same transaction
-- as the store unit. The FCM call happens later, from a worker, so a network
-- failure cannot roll the delivery back. Replaying this migration only
-- creates missing objects; it does not touch registrations or outbox rows.
BEGIN;

CREATE TABLE IF NOT EXISTS integration.device_push_registrations (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    device_id       uuid NOT NULL REFERENCES audit.client_devices(id) ON DELETE RESTRICT,
    user_id         uuid NOT NULL REFERENCES auth.users(id) ON DELETE RESTRICT,
    provider        varchar(32) NOT NULL,
    platform        varchar(32) NOT NULL,
    token           text NOT NULL,
    is_active       boolean NOT NULL DEFAULT true,
    registered_at   timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    revoked_at      timestamptz,
    CONSTRAINT ck_push_registration_provider CHECK (provider = 'FCM'),
    CONSTRAINT ck_push_registration_platform CHECK (platform = 'ANDROID'),
    CONSTRAINT ck_push_registration_token CHECK (length(btrim(token)) > 0),
    CONSTRAINT ck_push_registration_revoked CHECK (
        (is_active AND revoked_at IS NULL)
        OR (NOT is_active AND revoked_at IS NOT NULL)
    )
);

-- One current registration per device and provider. Token refresh updates it.
CREATE UNIQUE INDEX IF NOT EXISTS uq_push_registration_device_provider
    ON integration.device_push_registrations (device_id, provider);

-- One active device may hold a token. A rotated or moved token deactivates
-- the previous row before the new one becomes active.
CREATE UNIQUE INDEX IF NOT EXISTS uq_push_registration_active_token
    ON integration.device_push_registrations (provider, token)
    WHERE is_active;

CREATE TABLE IF NOT EXISTS integration.push_outbox (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    event_key           varchar(200) NOT NULL,
    notification_type   varchar(80) NOT NULL,
    registration_id     uuid NOT NULL REFERENCES integration.device_push_registrations(id) ON DELETE RESTRICT,
    title               text NOT NULL,
    body                text NOT NULL,
    data_json           jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    attempt_count       integer NOT NULL DEFAULT 0,
    next_attempt_at     timestamptz,
    sent_at             timestamptz,
    last_error          varchar(500),
    CONSTRAINT ck_push_outbox_attempts CHECK (attempt_count >= 0),
    CONSTRAINT uq_push_outbox_event_registration UNIQUE (event_key, registration_id)
);

CREATE INDEX IF NOT EXISTS ix_push_outbox_pending
    ON integration.push_outbox (created_at)
    WHERE sent_at IS NULL;

COMMIT;
