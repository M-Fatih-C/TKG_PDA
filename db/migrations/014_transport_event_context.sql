-- ============================================================
-- Migration 014: Operational Event Context (WP-06)
-- Adds device_id and correlation_id to ops.transport_request_events
-- to establish a reliable, attributable operational event timeline.
--
-- Forward-only, additive, replay-safe.
-- ============================================================

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'ops'
          AND table_name = 'transport_request_events'
          AND column_name = 'device_id'
    ) THEN
        ALTER TABLE ops.transport_request_events
            ADD COLUMN device_id uuid NULL REFERENCES audit.client_devices(id) ON DELETE SET NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'ops'
          AND table_name = 'transport_request_events'
          AND column_name = 'correlation_id'
    ) THEN
        ALTER TABLE ops.transport_request_events
            ADD COLUMN correlation_id uuid NULL;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_transport_events_type_created
    ON ops.transport_request_events (event_type, created_at);

CREATE INDEX IF NOT EXISTS ix_transport_events_correlation_id
    ON ops.transport_request_events (correlation_id)
    WHERE correlation_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_transport_events_device_created
    ON ops.transport_request_events (device_id, created_at)
    WHERE device_id IS NOT NULL;
