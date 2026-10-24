-- Explicit rollback for migration 037.
--
-- This script intentionally refuses rollback after any M4-owned data exists.
-- Existing RDS operational tables and their records are never removed.
BEGIN;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ops.transport_requests WHERE execution_provider <> 'RDS')
       OR EXISTS (SELECT 1 FROM master.navigation_profiles WHERE execution_provider <> 'RDS')
       OR EXISTS (SELECT 1 FROM integration.fleet_orders WHERE provider <> 'RDS')
       OR EXISTS (SELECT 1 FROM integration.external_endpoints WHERE provider <> 'RDS')
       OR EXISTS (SELECT 1 FROM integration.fleet_callback_inbox WHERE provider <> 'RDS') THEN
        RAISE EXCEPTION 'M4_DATA_PRESENT: migration 037 cannot be rolled back while M4-owned data exists';
    END IF;
END $$;

DROP VIEW IF EXISTS integration.fleet_order_compatibility;
DROP TRIGGER IF EXISTS trg_rds_task_fleet_order_sync ON integration.rds_tasks;
DROP FUNCTION IF EXISTS integration.sync_rds_task_to_fleet_order();
DROP FUNCTION IF EXISTS integration.normalize_rds_fleet_status(integer, timestamptz, varchar, boolean);

DROP TABLE IF EXISTS integration.physical_endpoint_evidence;
DROP TABLE IF EXISTS integration.fleet_callback_inbox;
DROP TABLE IF EXISTS integration.layout_slot_external_mapping;
DROP TABLE IF EXISTS integration.external_endpoints;
DROP TABLE IF EXISTS integration.fleet_orders;

ALTER TABLE ops.transport_requests
    DROP CONSTRAINT IF EXISTS fk_transport_navigation_provider,
    DROP CONSTRAINT IF EXISTS uq_transport_request_id_provider,
    DROP CONSTRAINT IF EXISTS ck_transport_execution_provider,
    DROP COLUMN IF EXISTS execution_provider;

ALTER TABLE master.navigation_profiles
    DROP CONSTRAINT IF EXISTS uq_navigation_id_provider,
    DROP CONSTRAINT IF EXISTS ck_navigation_execution_provider,
    DROP COLUMN IF EXISTS external_scene_name,
    DROP COLUMN IF EXISTS external_scene_id,
    DROP COLUMN IF EXISTS execution_provider;

COMMIT;
