-- Some source-backed finished-product pallets have dimensions but no customer
-- container code.  An exact FINISHED_GOODS BRING_EMPTY demand may still be
-- prepared without inventing a master.container_types row.  FULL pallets and
-- unbound empty stock continue to require a concrete container type in the
-- application/domain rules.

ALTER TABLE ops.preparation_pallets
    ALTER COLUMN container_type_id DROP NOT NULL;

ALTER TABLE ops.preparation_pallets
    DROP CONSTRAINT IF EXISTS ck_preparation_untyped_request_bound_empty;

ALTER TABLE ops.preparation_pallets
    ADD CONSTRAINT ck_preparation_untyped_request_bound_empty
    CHECK (
        container_type_id IS NOT NULL
        OR (load_state = 'EMPTY' AND transport_request_id IS NOT NULL)
    );
