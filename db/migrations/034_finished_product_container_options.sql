-- Customer-confirmed primary and alternative empty-container types for each
-- finished product. Customer source codes are retained even when the current
-- canonical part master has not yet been reconciled to that spelling.

CREATE TABLE IF NOT EXISTS master.finished_product_container_options (
    id                              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_part_code              varchar(120) NOT NULL UNIQUE,
    part_id                         uuid REFERENCES master.parts(id) ON DELETE RESTRICT,
    primary_container_type_id       uuid NOT NULL REFERENCES master.container_types(id) ON DELETE RESTRICT,
    alternative_container_type_id   uuid NOT NULL REFERENCES master.container_types(id) ON DELETE RESTRICT,
    source_reference                text NOT NULL,
    is_active                       boolean NOT NULL DEFAULT true,
    created_at                      timestamptz NOT NULL DEFAULT now(),
    updated_at                      timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_finished_product_container_options_part
    ON master.finished_product_container_options (part_id)
    WHERE part_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_finished_product_container_options_active
    ON master.finished_product_container_options (is_active, part_id);
