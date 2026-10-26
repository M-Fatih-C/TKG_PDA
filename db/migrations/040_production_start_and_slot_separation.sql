-- Preserve the explicit production-start gate and keep Mamul positions separate
-- from ALT-part positions. Forward-only cleanup of demo-generated state.
BEGIN;

-- Earlier demo data opened one production session on every machine. Close only
-- those recognizable seed sessions; operator-created production is untouched.
UPDATE ops.production_sessions
SET status = 'ENDED',
    ended_at = GREATEST(clock_timestamp(), started_at),
    ended_by = started_by,
    ended_device_id = started_device_id
WHERE ended_at IS NULL
  AND start_key LIKE 'demo-prod-%';

-- A physical position cannot be both a Mamul position and an ALT-part position
-- in the same variant. Preserve the customer-derived main-product assignment and
-- remove only the conflicting ALT relation introduced by demo fallback seeding.
WITH conflicting_alt_assignments AS (
    SELECT DISTINCT alt_lsp.layout_slot_id, alt_lsp.part_id
    FROM master.layout_slot_parts alt_lsp
    JOIN master.layout_slots slot ON slot.id = alt_lsp.layout_slot_id
    JOIN master.variant_layout_assignments assignment ON assignment.layout_id = slot.layout_id
    JOIN master.product_groups alt_group
      ON alt_group.variant_id = assignment.variant_id
     AND alt_group.is_active
    JOIN master.product_group_parts alt_relation
      ON alt_relation.product_group_id = alt_group.id
     AND alt_relation.part_id = alt_lsp.part_id
     AND alt_relation.relation_type = 'ALT_PART'
     AND alt_relation.is_active
    JOIN master.layout_slot_parts main_lsp
      ON main_lsp.layout_slot_id = alt_lsp.layout_slot_id
     AND main_lsp.part_id <> alt_lsp.part_id
    JOIN master.product_groups main_group
      ON main_group.variant_id = assignment.variant_id
     AND main_group.main_part_id = main_lsp.part_id
     AND main_group.is_active
)
DELETE FROM master.layout_slot_parts mapping
USING conflicting_alt_assignments conflict
WHERE mapping.layout_slot_id = conflict.layout_slot_id
  AND mapping.part_id = conflict.part_id;

COMMIT;
