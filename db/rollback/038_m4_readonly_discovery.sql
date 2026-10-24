-- Explicit rollback for migration 038. Discovery snapshots are observation
-- evidence and can be removed without changing transport or M4 order state.
BEGIN;

DROP TABLE IF EXISTS integration.m4_discovery_snapshots;

COMMIT;
