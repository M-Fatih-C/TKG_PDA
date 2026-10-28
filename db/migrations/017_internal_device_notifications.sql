-- Self-hosted warehouse notifications. The tablet polls this API.
-- The raw notification credential is never stored; only its hash is.
-- A field-session lease is not required to read a pending notification.
BEGIN;

CREATE TABLE IF NOT EXISTS integration.device_notification_registrations (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    device_id           uuid NOT NULL REFERENCES audit.client_devices(id) ON DELETE RESTRICT,
    user_id             uuid NOT NULL REFERENCES auth.users(id) ON DELETE RESTRICT,
    credential_hash     bytea NOT NULL,
    is_active           boolean NOT NULL,
    registered_at       timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    revoked_at          timestamptz,
    last_seen_at        timestamptz,
    CONSTRAINT ck_device_notification_registration_revoked CHECK (
        (is_active AND revoked_at IS NULL) OR (NOT is_active AND revoked_at IS NOT NULL)
    )
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_device_notification_registration_device
    ON integration.device_notification_registrations (device_id);

CREATE UNIQUE INDEX IF NOT EXISTS uq_device_notification_credential_hash
    ON integration.device_notification_registrations (credential_hash);

CREATE TABLE IF NOT EXISTS integration.notification_outbox (
    id                          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    event_key                   varchar(200) NOT NULL,
    registration_id             uuid NOT NULL REFERENCES integration.device_notification_registrations(id) ON DELETE RESTRICT,
    notification_type           varchar(80) NOT NULL,
    title                       text NOT NULL,
    body                        text NOT NULL,
    data_json                   jsonb NOT NULL,
    created_at                  timestamptz NOT NULL DEFAULT clock_timestamp(),
    lease_until                 timestamptz,
    delivery_attempt_count      integer NOT NULL DEFAULT 0,
    last_delivery_at            timestamptz,
    acknowledged_at             timestamptz,
    CONSTRAINT ck_notification_outbox_attempts CHECK (delivery_attempt_count >= 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_notification_outbox_event_registration
    ON integration.notification_outbox (event_key, registration_id);

CREATE INDEX IF NOT EXISTS ix_notification_outbox_pending
    ON integration.notification_outbox (registration_id, created_at)
    WHERE acknowledged_at IS NULL;

COMMIT;
