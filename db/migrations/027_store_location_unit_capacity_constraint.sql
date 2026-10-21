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

-- 2. Clean up any invalid legacy duplicate active units per location (keep the newest, remove older)
WITH duplicates AS (
    SELECT id,
           ROW_NUMBER() OVER (
               PARTITION BY store_location_id
               ORDER BY delivered_at DESC, id DESC
           ) AS rn
    FROM ops.store_units
    WHERE removed_at IS NULL
)
UPDATE ops.store_units
SET removed_at = clock_timestamp(),
    removed_by = COALESCE(
        (SELECT id FROM auth.users WHERE username = 'operator-1001' LIMIT 1),
        (SELECT id FROM auth.users LIMIT 1)
    ),
    removed_device_id = COALESCE(
        (SELECT id FROM audit.client_devices WHERE device_code = 'TAB-BITMIS-URUN' LIMIT 1),
        (SELECT id FROM audit.client_devices LIMIT 1)
    ),
    row_version = row_version + 1
WHERE id IN (
    SELECT id FROM duplicates WHERE rn > 1
);

-- 3. Enforce that each store location holds at most one active (unremoved) unit at any time
CREATE UNIQUE INDEX IF NOT EXISTS uq_store_active_unit_per_location
    ON ops.store_units (store_location_id)
    WHERE removed_at IS NULL;

COMMIT;
