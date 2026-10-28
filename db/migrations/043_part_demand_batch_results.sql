BEGIN;

-- A material batch contains independent pallet demands. Keep its complete
-- submitted part set and per-item outcome so a lost response/restart cannot
-- retry accepted items or silently change a previously rejected item.
ALTER TABLE ops.transport_batches
    ADD COLUMN IF NOT EXISTS part_demand_result_json jsonb;

COMMIT;
