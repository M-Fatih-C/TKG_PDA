-- Customer-approved operational fallback (2026-10-27):
-- blank ALT-part container fields are handled as EURO_PALET.
-- Explicit customer container mappings are never overwritten.

BEGIN;

INSERT INTO master.container_types (
    code,
    name,
    category,
    length_mm,
    width_mm,
    height_mm
)
VALUES ('EURO_PALET', 'Euro Palet', 'PALLET', 1200, 800, 144)
ON CONFLICT (code) DO UPDATE
SET name = EXCLUDED.name,
    category = EXCLUDED.category,
    length_mm = EXCLUDED.length_mm,
    width_mm = EXCLUDED.width_mm,
    height_mm = EXCLUDED.height_mm,
    updated_at = now()
WHERE master.container_types.name IS DISTINCT FROM EXCLUDED.name
   OR master.container_types.category IS DISTINCT FROM EXCLUDED.category
   OR master.container_types.length_mm IS DISTINCT FROM EXCLUDED.length_mm
   OR master.container_types.width_mm IS DISTINCT FROM EXCLUDED.width_mm
   OR master.container_types.height_mm IS DISTINCT FROM EXCLUDED.height_mm;

UPDATE master.product_group_parts AS group_part
SET container_type_id = euro.id,
    updated_at = now()
FROM master.container_types AS euro
WHERE euro.code = 'EURO_PALET'
  AND group_part.relation_type = 'ALT_PART'
  AND group_part.container_type_id IS NULL;

UPDATE master.part_planning_metrics AS metric
SET container_type_id = group_part.container_type_id,
    updated_at = now()
FROM master.product_group_parts AS group_part
WHERE group_part.product_group_id = metric.product_group_id
  AND group_part.part_id = metric.part_id
  AND group_part.relation_type = 'ALT_PART'
  AND metric.container_type_id IS NULL
  AND group_part.container_type_id IS NOT NULL;

COMMIT;
