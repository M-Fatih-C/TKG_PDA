-- Stable internal finished-goods locations FG-01..FG-19.
-- Migration 016 inserted FG_AREA_01..FG_AREA_19 with placeholder display
-- names. This seed renames those rows in place, so the id does not change.
-- Display text becomes the internal code only while it is still that
-- placeholder. A later customer display name is left alone.
-- Capacity stays unknown. No RDS WorkSite is created.
-- Replaying 016 can recreate unused FG_AREA rows. Replaying this seed
-- removes those unused duplicates and does not insert a second FG row.

BEGIN;

UPDATE ops.store_locations AS location
SET code = 'FG-' || substring(location.code FROM 9),
    display_name = CASE
        WHEN location.display_name = 'Alan ' || ltrim(substring(location.code FROM 9), '0')
          OR location.display_name = location.code
        THEN 'FG-' || substring(location.code FROM 9)
        ELSE location.display_name
    END,
    updated_at = now()
WHERE location.code ~ '^FG_AREA_[0-9]{2}$'
  AND NOT EXISTS (
      SELECT 1
      FROM ops.store_locations AS taken
      WHERE taken.code = 'FG-' || substring(location.code FROM 9)
  );

INSERT INTO ops.store_locations (code, display_name, capacity, is_enabled)
SELECT format('FG-%s', lpad(n::text, 2, '0')),
       format('FG-%s', lpad(n::text, 2, '0')),
       NULL,
       true
FROM generate_series(1, 19) AS n
ON CONFLICT (code) DO NOTHING;

UPDATE ops.store_locations
SET display_name = code,
    updated_at = now()
WHERE code ~ '^FG-[0-9]{2}$'
  AND (
      display_name ~ '^Alan [0-9]+$'
      OR display_name ~ '^FG_AREA_[0-9]{2}$'
  );

DELETE FROM ops.store_locations AS legacy
WHERE legacy.code ~ '^FG_AREA_[0-9]{2}$'
  AND EXISTS (
      SELECT 1
      FROM ops.store_locations AS current
      WHERE current.code = 'FG-' || substring(legacy.code FROM 9))
  AND NOT EXISTS (
      SELECT 1 FROM ops.store_units u WHERE u.store_location_id = legacy.id)
  AND NOT EXISTS (
      SELECT 1 FROM ops.store_events e WHERE e.store_location_id = legacy.id)
  AND NOT EXISTS (
      SELECT 1 FROM ops.store_location_delivery_slots d WHERE d.store_location_id = legacy.id);

COMMIT;
