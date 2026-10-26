-- Remove Part-to-Slot assignments that were introduced only by the former demo
-- fallback. Customer source data identifies these parts as ALT parts but does
-- not assign them to the listed physical positions.
BEGIN;

WITH legacy_demo_guesses(machine_code, variant_code, layout_code, slot_code, part_code) AS (
    VALUES
        ('412B', '2', 'CUSTOMER-PHYSICAL-V2', 'SLOT-003', '90174-12022'),
        ('412B', '2', 'CUSTOMER-PHYSICAL-V2', 'SLOT-004', '90174-12022'),
        ('412B', '3', 'CUSTOMER-PHYSICAL-V3', 'SLOT-003', '90179-10234'),
        ('412B', '3', 'CUSTOMER-PHYSICAL-V3', 'SLOT-004', '90179-10234'),
        ('GAR', '1', 'CUSTOMER-PHYSICAL-V1', 'SLOT-003', '90179-14075'),
        ('GAR', '1', 'CUSTOMER-PHYSICAL-V1', 'SLOT-004', '90179-14075')
), mappings AS (
    SELECT lsp.layout_slot_id, lsp.part_id
    FROM legacy_demo_guesses guess
    JOIN master.machines machine ON machine.code = guess.machine_code
    JOIN master.variants variant
      ON variant.machine_id = machine.id
     AND variant.code = guess.variant_code
    JOIN master.layouts layout
      ON layout.machine_id = machine.id
     AND layout.code = guess.layout_code
    JOIN master.variant_layout_assignments assignment
      ON assignment.variant_id = variant.id
     AND assignment.layout_id = layout.id
    JOIN master.layout_slots slot
      ON slot.layout_id = layout.id
     AND slot.code = guess.slot_code
    JOIN master.parts part ON part.part_code = guess.part_code
    JOIN master.layout_slot_parts lsp
      ON lsp.layout_slot_id = slot.id
     AND lsp.part_id = part.id
)
DELETE FROM master.layout_slot_parts mapping
USING mappings legacy
WHERE mapping.layout_slot_id = legacy.layout_slot_id
  AND mapping.part_id = legacy.part_id;

COMMIT;
