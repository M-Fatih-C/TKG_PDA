BEGIN;

-- A preparation pallet normally leaves an area through a physical warehouse
-- action and therefore records released_by.  Cancelling a request before
-- robot execution is different: the server closes the request-bound staging
-- claim atomically, so there may be no separate warehouse actor.
ALTER TABLE ops.preparation_pallets
    DROP CONSTRAINT IF EXISTS ck_preparation_pallet_released;

ALTER TABLE ops.preparation_pallets
    ADD CONSTRAINT ck_preparation_pallet_released CHECK (
        (released_at IS NULL AND released_by IS NULL AND released_reason IS NULL)
        OR (released_at IS NOT NULL
            AND released_reason IS NOT NULL
            AND released_at >= placed_at
            AND (released_by IS NOT NULL OR released_reason = 'REQUEST_CANCELLED'))
    );

-- Repair rows left open by older application versions.  A row that has ever
-- reached the robot executor is intentionally excluded because cancellation
-- no longer proves the physical pallet location in that case.
WITH released AS (
    UPDATE ops.preparation_pallets pallet
    SET released_at = GREATEST(clock_timestamp(), pallet.placed_at),
        released_by = NULL,
        released_reason = 'REQUEST_CANCELLED',
        row_version = pallet.row_version + 1
    FROM ops.transport_requests request
    WHERE pallet.transport_request_id = request.id
      AND pallet.released_at IS NULL
      AND request.status = 'CANCELLED'
      AND request.started_at IS NULL
      AND NOT EXISTS (
          SELECT 1
          FROM integration.rds_tasks task
          WHERE task.transport_request_id = request.id)
    RETURNING pallet.id, pallet.preparation_area_id, pallet.part_id,
              pallet.container_type_id, pallet.transport_request_id
)
INSERT INTO ops.preparation_events (
    preparation_area_id, preparation_pallet_id, event_type, part_id,
    container_type_id, transport_request_id, actor_user_id, device_id, data_json)
SELECT preparation_area_id, id, 'PALLET_RELEASED', part_id,
       container_type_id, transport_request_id, NULL, NULL,
       jsonb_build_object(
           'reason', 'REQUEST_CANCELLED',
           'source', 'MIGRATION_RECONCILIATION')
FROM released;

COMMIT;
