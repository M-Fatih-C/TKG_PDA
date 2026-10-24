-- Migration 038: Persist read-only M4 discovery evidence.
--
-- Discovery data is observation evidence only. It does not create approved
-- navigation mappings, enable dispatch, or change physical occupancy/load.
BEGIN;

CREATE TABLE IF NOT EXISTS integration.m4_discovery_snapshots (
    id                      uuid PRIMARY KEY,
    correlation_id          uuid NOT NULL,
    status                  varchar(20) NOT NULL,
    requested_scene_id      varchar(200),
    requested_scene_name    varchar(200),
    captured_at             timestamptz NOT NULL,
    fresh_until             timestamptz NOT NULL,
    snapshot_json           jsonb NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_m4_discovery_status CHECK (status IN ('SUCCESS', 'PARTIAL', 'FAILED')),
    CONSTRAINT ck_m4_discovery_freshness CHECK (fresh_until > captured_at),
    CONSTRAINT ck_m4_discovery_scene_id_length CHECK (
        requested_scene_id IS NULL OR length(requested_scene_id) BETWEEN 1 AND 200),
    CONSTRAINT ck_m4_discovery_scene_name_length CHECK (
        requested_scene_name IS NULL OR length(requested_scene_name) BETWEEN 1 AND 200)
);

CREATE INDEX IF NOT EXISTS ix_m4_discovery_snapshots_latest
    ON integration.m4_discovery_snapshots (captured_at DESC, id DESC);

COMMENT ON TABLE integration.m4_discovery_snapshots IS
    'Read-only M4 discovery evidence. Rows are not approved runtime endpoint mappings.';

COMMIT;
