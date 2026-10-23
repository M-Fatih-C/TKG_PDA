-- Production-end removal order can be overridden for an exact, authoritative
-- Product Group + Part + physical Layout Slot mapping. The existing operation
-- priorities remain as the safe fallback for mappings that have not yet been
-- configured through the Admin panel.
BEGIN;

CREATE TABLE IF NOT EXISTS master.production_end_area_priorities (
    product_group_id    uuid NOT NULL REFERENCES master.product_groups(id) ON DELETE CASCADE,
    layout_slot_id      uuid NOT NULL,
    part_id             uuid NOT NULL,
    priority            integer NOT NULL,
    updated_at          timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_by          uuid NULL REFERENCES auth.users(id) ON DELETE SET NULL,
    PRIMARY KEY (product_group_id, layout_slot_id, part_id),
    CONSTRAINT fk_production_end_area_slot_part
        FOREIGN KEY (layout_slot_id, part_id)
        REFERENCES master.layout_slot_parts(layout_slot_id, part_id) ON DELETE CASCADE,
    CONSTRAINT ck_production_end_area_priority_positive CHECK (priority > 0)
);

CREATE INDEX IF NOT EXISTS ix_production_end_area_priorities_slot
    ON master.production_end_area_priorities(layout_slot_id);

CREATE INDEX IF NOT EXISTS ix_production_end_area_priorities_part
    ON master.production_end_area_priorities(part_id);

COMMIT;
