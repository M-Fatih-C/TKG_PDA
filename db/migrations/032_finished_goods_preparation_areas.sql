-- Separate finished-goods warehouse preparation from the four shared
-- semi-finished preparation positions. The new identities are logical TKG
-- areas only; this migration does not invent RDS WorkSite mappings.
BEGIN;

ALTER TABLE ops.preparation_areas
    ADD COLUMN IF NOT EXISTS warehouse_scope varchar(30) NOT NULL DEFAULT 'SEMI_FINISHED';

ALTER TABLE ops.preparation_areas
    DROP CONSTRAINT IF EXISTS ck_preparation_area_number;
ALTER TABLE ops.preparation_areas
    DROP CONSTRAINT IF EXISTS ck_preparation_area_scope;

ALTER TABLE ops.preparation_areas
    ADD CONSTRAINT ck_preparation_area_scope
        CHECK (warehouse_scope IN ('SEMI_FINISHED', 'FINISHED_GOODS')),
    ADD CONSTRAINT ck_preparation_area_number CHECK (
        (warehouse_scope = 'SEMI_FINISHED' AND area_number BETWEEN 1 AND 4)
        OR (warehouse_scope = 'FINISHED_GOODS' AND area_number BETWEEN 5 AND 10)
    );

CREATE UNIQUE INDEX IF NOT EXISTS uq_preparation_area_scope_number
    ON ops.preparation_areas (warehouse_scope, area_number);

INSERT INTO ops.preparation_areas (area_number, display_name, warehouse_scope)
SELECT n + 4, format('Boş Kasa Hazırlık Alanı %s', n), 'FINISHED_GOODS'
FROM generate_series(1, 6) AS n
ON CONFLICT (warehouse_scope, area_number) DO NOTHING;

-- The customer-approved finished-goods screen has two single-pallet delivery
-- positions. FG-01/FG-02 are stable internal identities already present in the
-- system; this role does not assert a physical WorkSite or geometry mapping.
ALTER TABLE ops.store_locations
    ADD COLUMN IF NOT EXISTS area_purpose varchar(30) NOT NULL DEFAULT 'LEGACY';
ALTER TABLE ops.store_locations
    ADD COLUMN IF NOT EXISTS is_operator_visible boolean NOT NULL DEFAULT false;

ALTER TABLE ops.store_locations
    DROP CONSTRAINT IF EXISTS ck_store_location_area_purpose;
ALTER TABLE ops.store_locations
    ADD CONSTRAINT ck_store_location_area_purpose
        CHECK (area_purpose IN ('DELIVERY', 'LEGACY'));

UPDATE ops.store_locations
SET area_purpose = CASE WHEN code IN ('FG-01', 'FG-02') THEN 'DELIVERY' ELSE 'LEGACY' END,
    is_operator_visible = code IN ('FG-01', 'FG-02'),
    display_name = CASE code
        WHEN 'FG-01' THEN 'Mamul Teslim Alanı 1'
        WHEN 'FG-02' THEN 'Mamul Teslim Alanı 2'
        ELSE display_name
    END,
    capacity = CASE WHEN code IN ('FG-01', 'FG-02') THEN 1 ELSE capacity END,
    updated_at = now()
WHERE code ~ '^FG-[0-9]{2}$';

CREATE INDEX IF NOT EXISTS ix_store_locations_area_purpose
    ON ops.store_locations (area_purpose, code);

-- Remove only legacy placeholders that have never participated in any
-- operation or commissioning. Rows with audit/history/mapping evidence stay
-- available to administrators as LEGACY records and are hidden from the new
-- warehouse operator surface.
DELETE FROM ops.store_locations legacy
WHERE legacy.area_purpose = 'LEGACY'
  AND legacy.code ~ '^FG-[0-9]{2}$'
  AND NOT EXISTS (SELECT 1 FROM ops.store_units u WHERE u.store_location_id = legacy.id)
  AND NOT EXISTS (SELECT 1 FROM ops.store_events e WHERE e.store_location_id = legacy.id)
  AND NOT EXISTS (SELECT 1 FROM ops.store_location_delivery_slots d WHERE d.store_location_id = legacy.id)
  AND NOT EXISTS (SELECT 1 FROM ops.store_reservations r WHERE r.store_location_id = legacy.id)
  AND NOT EXISTS (
      SELECT 1 FROM integration.profile_shared_endpoint_bindings b
      WHERE b.store_location_id = legacy.id);

COMMIT;
