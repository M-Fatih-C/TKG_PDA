-- 027_store_location_unit_capacity_constraint.sql
-- Enforce capacity = 1 for all Finished Goods pallet locations (FG-01..FG-19)
-- and guarantee at the database level that no store location can hold more than 1 active unit.

BEGIN;

-- 1. Ensure all finished-goods locations (FG-01..FG-19 and FG_AREA_*) have strictly capacity = 1
UPDATE ops.store_locations
SET capacity = 1,
    updated_at = now()
WHERE (code ~ '^FG-[0-9]{2}$' OR code ~ '^FG_AREA_[0-9]{2}$')
  AND (capacity IS NULL OR capacity != 1);

-- 2. Never discard warehouse records automatically. Duplicate active units mean
-- physical stock and software disagree, so deployment must stop for reconciliation.
DO $$
DECLARE
    conflicts text;
BEGIN
    SELECT string_agg(format('%s (%s)', sl.code, grouped.unit_count), ', ' ORDER BY sl.code)
    INTO conflicts
    FROM (
        SELECT store_location_id, count(*) AS unit_count
        FROM ops.store_units
        WHERE removed_at IS NULL
        GROUP BY store_location_id
        HAVING count(*) > 1
    ) grouped
    JOIN ops.store_locations sl ON sl.id = grouped.store_location_id;

    IF conflicts IS NOT NULL THEN
        RAISE EXCEPTION 'STORE_LOCATION_INTEGRITY_CONFLICT: %', conflicts
            USING ERRCODE = '23514';
    END IF;
END;
$$;

-- 3. Enforce that each store location holds at most one active (unremoved) unit at any time
CREATE UNIQUE INDEX IF NOT EXISTS uq_store_active_unit_per_location
    ON ops.store_units (store_location_id)
    WHERE removed_at IS NULL;

COMMIT;
