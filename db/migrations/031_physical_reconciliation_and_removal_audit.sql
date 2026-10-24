-- Preserve uncertain physical reservations after robot execution failures and
-- distinguish operator removals from robot removals without inventing users or
-- devices for automated movements.
BEGIN;

ALTER TABLE ops.store_units
    ADD COLUMN IF NOT EXISTS removal_source varchar(20);

-- Rows created before this migration satisfied the old constraint only when a
-- real user and device were present, so OPERATOR is the faithful backfill.
UPDATE ops.store_units
SET removal_source = 'OPERATOR'
WHERE removed_at IS NOT NULL AND removal_source IS NULL;

ALTER TABLE ops.store_units
    DROP CONSTRAINT IF EXISTS ck_store_unit_removed;

ALTER TABLE ops.store_units
    ADD CONSTRAINT ck_store_unit_removed CHECK (
        (removed_at IS NULL AND removed_by IS NULL AND removed_device_id IS NULL AND removal_source IS NULL)
        OR
        (removed_at IS NOT NULL AND removed_at >= delivered_at AND (
            (removal_source = 'OPERATOR' AND removed_by IS NOT NULL AND removed_device_id IS NOT NULL)
            OR
            (removal_source = 'ROBOT' AND removed_by IS NULL AND removed_device_id IS NULL)
        ))
    );

ALTER TABLE ops.store_reservations
    ADD COLUMN IF NOT EXISTS reconciliation_required boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS reconciled_at timestamptz,
    ADD COLUMN IF NOT EXISTS reconciled_by uuid REFERENCES auth.users(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS reconciled_device_id uuid REFERENCES audit.client_devices(id) ON DELETE RESTRICT;

ALTER TABLE ops.store_reservations
    DROP CONSTRAINT IF EXISTS ck_store_reservation_reconciliation;

ALTER TABLE ops.store_reservations
    ADD CONSTRAINT ck_store_reservation_reconciliation CHECK (
        (reconciled_at IS NULL AND reconciled_by IS NULL AND reconciled_device_id IS NULL)
        OR
        (reconciliation_required AND released_at IS NOT NULL
         AND reconciled_at IS NOT NULL AND reconciled_by IS NOT NULL AND reconciled_device_id IS NOT NULL)
    );

CREATE INDEX IF NOT EXISTS ix_store_reservations_reconciliation
    ON ops.store_reservations (created_at, transport_request_id)
    WHERE released_at IS NULL AND reconciliation_required;

COMMIT;
