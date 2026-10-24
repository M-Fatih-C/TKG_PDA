-- Migration 037: Provider-neutral fleet compatibility foundation
--
-- Additive and replay-safe. Existing RDS tables remain the operational source
-- while M4 is commissioned. No live provider is enabled by this migration.
BEGIN;

ALTER TABLE master.navigation_profiles
    ADD COLUMN IF NOT EXISTS execution_provider varchar(10),
    ADD COLUMN IF NOT EXISTS external_scene_id varchar(200),
    ADD COLUMN IF NOT EXISTS external_scene_name varchar(200);

UPDATE master.navigation_profiles
SET execution_provider = 'RDS'
WHERE execution_provider IS NULL;

ALTER TABLE master.navigation_profiles
    ALTER COLUMN execution_provider SET DEFAULT 'RDS',
    ALTER COLUMN execution_provider SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_navigation_execution_provider'
          AND conrelid = 'master.navigation_profiles'::regclass
    ) THEN
        ALTER TABLE master.navigation_profiles
            ADD CONSTRAINT ck_navigation_execution_provider
            CHECK (execution_provider IN ('RDS', 'M4'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'uq_navigation_id_provider'
          AND conrelid = 'master.navigation_profiles'::regclass
    ) THEN
        ALTER TABLE master.navigation_profiles
            ADD CONSTRAINT uq_navigation_id_provider
            UNIQUE (id, execution_provider);
    END IF;
END $$;

ALTER TABLE ops.transport_requests
    ADD COLUMN IF NOT EXISTS execution_provider varchar(10);

UPDATE ops.transport_requests
SET execution_provider = 'RDS'
WHERE execution_provider IS NULL;

ALTER TABLE ops.transport_requests
    ALTER COLUMN execution_provider SET DEFAULT 'RDS',
    ALTER COLUMN execution_provider SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_transport_execution_provider'
          AND conrelid = 'ops.transport_requests'::regclass
    ) THEN
        ALTER TABLE ops.transport_requests
            ADD CONSTRAINT ck_transport_execution_provider
            CHECK (execution_provider IN ('RDS', 'M4'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'uq_transport_request_id_provider'
          AND conrelid = 'ops.transport_requests'::regclass
    ) THEN
        ALTER TABLE ops.transport_requests
            ADD CONSTRAINT uq_transport_request_id_provider
            UNIQUE (id, execution_provider);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_transport_navigation_provider'
          AND conrelid = 'ops.transport_requests'::regclass
    ) THEN
        ALTER TABLE ops.transport_requests
            ADD CONSTRAINT fk_transport_navigation_provider
            FOREIGN KEY (navigation_id, execution_provider)
            REFERENCES master.navigation_profiles (id, execution_provider)
            ON DELETE RESTRICT;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS integration.fleet_orders (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    transport_request_id    uuid NOT NULL,
    provider                varchar(10) NOT NULL,
    external_order_id       varchar(200),
    upstream_external_id    varchar(200) NOT NULL,
    normalized_status       varchar(30) NOT NULL DEFAULT 'PENDING',
    provider_status         varchar(100),
    actual_robot_name       varchar(200),
    cancellation_requested  boolean NOT NULL DEFAULT false,
    result_uncertain        boolean NOT NULL DEFAULT false,
    request_payload_json    jsonb NOT NULL DEFAULT '{}'::jsonb,
    response_payload_json   jsonb NOT NULL DEFAULT '{}'::jsonb,
    last_status_json        jsonb NOT NULL DEFAULT '{}'::jsonb,
    sent_at                 timestamptz,
    started_at              timestamptz,
    ended_at                timestamptz,
    last_sync_at            timestamptz,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_fleet_order_request_provider
        FOREIGN KEY (transport_request_id, provider)
        REFERENCES ops.transport_requests (id, execution_provider) ON DELETE CASCADE,
    CONSTRAINT uq_fleet_order_request UNIQUE (transport_request_id),
    CONSTRAINT ck_fleet_order_provider CHECK (provider IN ('RDS', 'M4')),
    CONSTRAINT ck_fleet_order_status CHECK (normalized_status IN (
        'UNKNOWN', 'QUEUED', 'ALLOCATED', 'PENDING', 'EXECUTING',
        'COMPLETED', 'CANCELLING', 'CANCELLED', 'FAILED'
    ))
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_fleet_order_provider_external_order
    ON integration.fleet_orders (provider, external_order_id)
    WHERE external_order_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_fleet_order_provider_upstream_external
    ON integration.fleet_orders (provider, upstream_external_id);

CREATE INDEX IF NOT EXISTS ix_fleet_orders_status_sync
    ON integration.fleet_orders (provider, normalized_status, last_sync_at);

CREATE TABLE IF NOT EXISTS integration.external_endpoints (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    provider                varchar(10) NOT NULL,
    navigation_profile_id   uuid NOT NULL,
    external_scene_id       varchar(200),
    external_location_id    varchar(200) NOT NULL,
    external_location_name  varchar(200),
    endpoint_kind           varchar(30) NOT NULL DEFAULT 'UNKNOWN',
    raw_metadata_json       jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_external_endpoint_provider CHECK (provider IN ('RDS', 'M4')),
    CONSTRAINT ck_external_endpoint_kind CHECK (endpoint_kind IN (
        'POINT', 'STORAGE_BIN', 'WORKSITE', 'UNKNOWN'
    )),
    CONSTRAINT fk_external_endpoint_profile_provider
        FOREIGN KEY (navigation_profile_id, provider)
        REFERENCES master.navigation_profiles (id, execution_provider)
        ON DELETE CASCADE,
    CONSTRAINT uq_external_endpoint_location
        UNIQUE (provider, navigation_profile_id, external_location_id),
    CONSTRAINT uq_external_endpoint_identity_profile_provider
        UNIQUE (id, navigation_profile_id, provider)
);

CREATE INDEX IF NOT EXISTS ix_external_endpoints_scene
    ON integration.external_endpoints (provider, external_scene_id)
    WHERE external_scene_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS integration.layout_slot_external_mapping (
    layout_slot_id          uuid NOT NULL REFERENCES master.layout_slots(id) ON DELETE CASCADE,
    navigation_profile_id   uuid NOT NULL REFERENCES master.navigation_profiles(id) ON DELETE CASCADE,
    provider                varchar(10) NOT NULL,
    external_endpoint_id    uuid NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (layout_slot_id, navigation_profile_id, provider),
    CONSTRAINT ck_layout_slot_external_provider CHECK (provider IN ('RDS', 'M4')),
    CONSTRAINT fk_layout_slot_external_same_profile_provider
        FOREIGN KEY (external_endpoint_id, navigation_profile_id, provider)
        REFERENCES integration.external_endpoints (id, navigation_profile_id, provider)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_layout_slot_external_endpoint
    ON integration.layout_slot_external_mapping (external_endpoint_id);

CREATE TABLE IF NOT EXISTS integration.fleet_callback_inbox (
    id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    provider                varchar(10) NOT NULL,
    event_name              varchar(120) NOT NULL,
    event_hash              varchar(128) NOT NULL,
    external_order_id       varchar(200),
    upstream_external_id    varchar(200),
    received_at             timestamptz NOT NULL DEFAULT now(),
    processed_at            timestamptz,
    processing_status       varchar(20) NOT NULL DEFAULT 'RECEIVED',
    processing_error        text,
    raw_payload_json        jsonb NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT ck_fleet_callback_provider CHECK (provider IN ('RDS', 'M4')),
    CONSTRAINT ck_fleet_callback_status CHECK (processing_status IN (
        'RECEIVED', 'PROCESSING', 'PROCESSED', 'FAILED', 'IGNORED'
    )),
    CONSTRAINT uq_fleet_callback_provider_hash UNIQUE (provider, event_hash)
);

CREATE INDEX IF NOT EXISTS ix_fleet_callback_pending
    ON integration.fleet_callback_inbox (provider, received_at)
    WHERE processing_status IN ('RECEIVED', 'FAILED');

CREATE TABLE IF NOT EXISTS integration.physical_endpoint_evidence (
    external_endpoint_id    uuid PRIMARY KEY REFERENCES integration.external_endpoints(id) ON DELETE CASCADE,
    occupancy               varchar(20) NOT NULL DEFAULT 'UNKNOWN',
    load_state              varchar(20) NOT NULL DEFAULT 'UNKNOWN',
    disabled                boolean,
    locked                  boolean,
    read_failed             boolean NOT NULL DEFAULT false,
    blocking_reason         varchar(200),
    observed_at             timestamptz NOT NULL,
    source_timestamp        timestamptz,
    freshness_deadline      timestamptz,
    evidence_source         varchar(100),
    raw_state_json          jsonb NOT NULL DEFAULT '{}'::jsonb,
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_physical_endpoint_occupancy CHECK (occupancy IN ('VACANT', 'OCCUPIED', 'UNKNOWN')),
    CONSTRAINT ck_physical_endpoint_load CHECK (load_state IN ('EMPTY', 'FULL', 'UNKNOWN')),
    CONSTRAINT ck_physical_endpoint_load_requires_occupancy CHECK (
        occupancy = 'OCCUPIED' OR load_state = 'UNKNOWN'
    )
);

CREATE INDEX IF NOT EXISTS ix_physical_endpoint_freshness
    ON integration.physical_endpoint_evidence (freshness_deadline, observed_at);

CREATE OR REPLACE FUNCTION integration.normalize_rds_fleet_status(
    status_value integer,
    started_value timestamptz,
    robot_name_value varchar,
    cancellation_requested_value boolean
)
RETURNS varchar(30)
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT CASE
        WHEN status_value = 1001 THEN 'COMPLETED'
        WHEN status_value = 1003 THEN 'CANCELLED'
        WHEN status_value IN (1004, 1005, 1006) THEN 'FAILED'
        WHEN cancellation_requested_value THEN 'CANCELLING'
        WHEN status_value IS NULL THEN 'PENDING'
        WHEN status_value = 1000 AND started_value IS NOT NULL THEN 'EXECUTING'
        WHEN status_value = 1000 AND NULLIF(BTRIM(robot_name_value), '') IS NOT NULL THEN 'ALLOCATED'
        WHEN status_value IN (1000, 1002) THEN 'PENDING'
        ELSE 'UNKNOWN'
    END::varchar(30)
$$;

CREATE OR REPLACE FUNCTION integration.sync_rds_task_to_fleet_order()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO integration.fleet_orders (
        transport_request_id,
        provider,
        external_order_id,
        upstream_external_id,
        normalized_status,
        provider_status,
        actual_robot_name,
        cancellation_requested,
        request_payload_json,
        response_payload_json,
        last_status_json,
        sent_at,
        started_at,
        ended_at,
        last_sync_at,
        created_at,
        updated_at
    )
    VALUES (
        NEW.transport_request_id,
        'RDS',
        NEW.rds_task_record_id,
        COALESCE(NEW.rds_task_record_id, NEW.transport_request_id::text),
        integration.normalize_rds_fleet_status(
            NEW.rds_status,
            NEW.started_at,
            NEW.agv_id,
            NEW.response_payload_json ? 'cancellationRequestedAt'),
        NEW.rds_status::text,
        NEW.agv_id,
        NEW.response_payload_json ? 'cancellationRequestedAt',
        NEW.request_payload_json,
        NEW.response_payload_json,
        NEW.last_status_json,
        NEW.sent_at,
        NEW.started_at,
        NEW.ended_at,
        NEW.last_sync_at,
        NEW.created_at,
        NEW.updated_at
    )
    ON CONFLICT (transport_request_id) DO UPDATE
    SET external_order_id = COALESCE(EXCLUDED.external_order_id, integration.fleet_orders.external_order_id),
        upstream_external_id = EXCLUDED.upstream_external_id,
        normalized_status = EXCLUDED.normalized_status,
        provider_status = EXCLUDED.provider_status,
        actual_robot_name = EXCLUDED.actual_robot_name,
        cancellation_requested = integration.fleet_orders.cancellation_requested OR EXCLUDED.cancellation_requested,
        request_payload_json = EXCLUDED.request_payload_json,
        response_payload_json = EXCLUDED.response_payload_json,
        last_status_json = EXCLUDED.last_status_json,
        sent_at = EXCLUDED.sent_at,
        started_at = EXCLUDED.started_at,
        ended_at = EXCLUDED.ended_at,
        last_sync_at = EXCLUDED.last_sync_at,
        updated_at = EXCLUDED.updated_at;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_rds_task_fleet_order_sync ON integration.rds_tasks;
CREATE TRIGGER trg_rds_task_fleet_order_sync
    AFTER INSERT OR UPDATE ON integration.rds_tasks
    FOR EACH ROW
    EXECUTE FUNCTION integration.sync_rds_task_to_fleet_order();

INSERT INTO integration.fleet_orders (
    transport_request_id,
    provider,
    external_order_id,
    upstream_external_id,
    normalized_status,
    provider_status,
    actual_robot_name,
    cancellation_requested,
    request_payload_json,
    response_payload_json,
    last_status_json,
    sent_at,
    started_at,
    ended_at,
    last_sync_at,
    created_at,
    updated_at
)
SELECT
    task.transport_request_id,
    'RDS',
    task.rds_task_record_id,
    COALESCE(task.rds_task_record_id, task.transport_request_id::text),
    integration.normalize_rds_fleet_status(
        task.rds_status,
        task.started_at,
        task.agv_id,
        task.response_payload_json ? 'cancellationRequestedAt'),
    task.rds_status::text,
    task.agv_id,
    task.response_payload_json ? 'cancellationRequestedAt',
    task.request_payload_json,
    task.response_payload_json,
    task.last_status_json,
    task.sent_at,
    task.started_at,
    task.ended_at,
    task.last_sync_at,
    task.created_at,
    task.updated_at
FROM integration.rds_tasks task
ON CONFLICT (transport_request_id) DO UPDATE
SET external_order_id = COALESCE(EXCLUDED.external_order_id, integration.fleet_orders.external_order_id),
    upstream_external_id = EXCLUDED.upstream_external_id,
    normalized_status = EXCLUDED.normalized_status,
    provider_status = EXCLUDED.provider_status,
    actual_robot_name = EXCLUDED.actual_robot_name,
    cancellation_requested = integration.fleet_orders.cancellation_requested OR EXCLUDED.cancellation_requested,
    request_payload_json = EXCLUDED.request_payload_json,
    response_payload_json = EXCLUDED.response_payload_json,
    last_status_json = EXCLUDED.last_status_json,
    sent_at = EXCLUDED.sent_at,
    started_at = EXCLUDED.started_at,
    ended_at = EXCLUDED.ended_at,
    last_sync_at = EXCLUDED.last_sync_at,
    updated_at = EXCLUDED.updated_at;

CREATE OR REPLACE VIEW integration.fleet_order_compatibility AS
SELECT
    fleet.id,
    fleet.transport_request_id,
    fleet.provider,
    fleet.external_order_id,
    fleet.upstream_external_id,
    fleet.normalized_status,
    fleet.provider_status,
    fleet.actual_robot_name,
    fleet.cancellation_requested,
    fleet.result_uncertain,
    fleet.request_payload_json,
    fleet.response_payload_json,
    fleet.last_status_json,
    fleet.sent_at,
    fleet.started_at,
    fleet.ended_at,
    fleet.last_sync_at,
    fleet.created_at,
    fleet.updated_at,
    rds.rds_task_record_id AS legacy_rds_task_record_id,
    rds.rds_task_label AS legacy_rds_task_label,
    rds.out_order_no AS legacy_rds_out_order_no
FROM integration.fleet_orders fleet
LEFT JOIN integration.rds_tasks rds
  ON fleet.provider = 'RDS'
 AND rds.transport_request_id = fleet.transport_request_id;

COMMENT ON TABLE integration.fleet_orders IS
    'Provider-neutral fleet order projection. RDS rows remain synchronized from integration.rds_tasks during migration.';
COMMENT ON TABLE integration.external_endpoints IS
    'Discovered external endpoints only. Rows do not constitute customer approval or enable dispatch.';
COMMENT ON TABLE integration.physical_endpoint_evidence IS
    'Latest provider observation. UNKNOWN and stale evidence must fail closed at dispatch.';

COMMIT;
