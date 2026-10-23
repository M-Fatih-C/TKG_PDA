-- Production end keeps the session open until every planned pallet removal
-- has authoritatively completed. Additive and replay-safe.
BEGIN;

ALTER TABLE ops.production_sessions
    ADD COLUMN IF NOT EXISTS status varchar(20);

UPDATE ops.production_sessions
SET status = CASE WHEN ended_at IS NULL THEN 'ACTIVE' ELSE 'ENDED' END
WHERE status IS NULL;

ALTER TABLE ops.production_sessions
    ALTER COLUMN status SET DEFAULT 'ACTIVE';

ALTER TABLE ops.production_sessions
    ALTER COLUMN status SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_production_session_lifecycle'
          AND conrelid = 'ops.production_sessions'::regclass) THEN
        ALTER TABLE ops.production_sessions DROP CONSTRAINT IF EXISTS ck_production_end;
        ALTER TABLE ops.production_sessions
            ADD CONSTRAINT ck_production_session_lifecycle CHECK (
                status IN ('ACTIVE', 'ENDING', 'ENDED')
                AND (
                    (status IN ('ACTIVE', 'ENDING')
                        AND ended_at IS NULL AND ended_by IS NULL AND ended_device_id IS NULL)
                    OR (status = 'ENDED'
                        AND ended_at IS NOT NULL AND ended_by IS NOT NULL AND ended_device_id IS NOT NULL
                        AND ended_at >= started_at)
                ));
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS ops.production_end_runs (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    production_session_id   uuid NOT NULL,
    machine_id              uuid NOT NULL,
    idempotency_key         varchar(200) NOT NULL CHECK (length(btrim(idempotency_key)) > 0),
    requested_by            uuid NOT NULL REFERENCES auth.users(id) ON DELETE RESTRICT,
    device_id               uuid NOT NULL REFERENCES audit.client_devices(id) ON DELETE RESTRICT,
    variant_id              uuid NOT NULL,
    layout_id               uuid NOT NULL,
    product_group_id        uuid NOT NULL,
    main_part_id            uuid NOT NULL,
    status                  varchar(20) NOT NULL,
    failure_code            varchar(80),
    created_at              timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at            timestamptz,
    failed_at               timestamptz,
    CONSTRAINT uq_production_end_key UNIQUE (machine_id, idempotency_key),
    CONSTRAINT fk_production_end_session FOREIGN KEY (production_session_id, machine_id)
        REFERENCES ops.production_sessions(id, machine_id) ON DELETE RESTRICT,
    CONSTRAINT ck_production_end_run_status CHECK (status IN ('RUNNING', 'COMPLETED', 'FAILED'))
);

CREATE INDEX IF NOT EXISTS ix_production_end_session
    ON ops.production_end_runs (production_session_id, created_at);

CREATE TABLE IF NOT EXISTS ops.production_end_run_items (
    run_id                  uuid NOT NULL REFERENCES ops.production_end_runs(id) ON DELETE RESTRICT,
    sequence_no             integer NOT NULL CHECK (sequence_no > 0),
    layout_slot_id          uuid NOT NULL REFERENCES master.layout_slots(id) ON DELETE RESTRICT,
    slot_code               varchar(120) NOT NULL,
    occupancy_state         varchar(20) NOT NULL,
    load_state              varchar(20) NOT NULL,
    part_id                 uuid REFERENCES master.parts(id) ON DELETE RESTRICT,
    operation_code          varchar(40) NOT NULL,
    transport_request_id    uuid REFERENCES ops.transport_requests(id) ON DELETE RESTRICT,
    PRIMARY KEY (run_id, sequence_no),
    CONSTRAINT uq_production_end_item_slot UNIQUE (run_id, layout_slot_id),
    CONSTRAINT uq_production_end_item_request UNIQUE (transport_request_id)
);

COMMIT;
