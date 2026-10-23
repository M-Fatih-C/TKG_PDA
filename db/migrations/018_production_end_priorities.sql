-- Configurable priorities for tasks created by ending production.
-- No customer numbers are seeded. An unconfigured operation stays unset.
BEGIN;

CREATE TABLE IF NOT EXISTS master.production_end_priorities (
    operation_code  varchar(40) PRIMARY KEY,
    priority        integer NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_by      uuid NULL REFERENCES auth.users(id) ON DELETE SET NULL,
    CONSTRAINT ck_production_end_priority_positive CHECK (priority > 0),
    CONSTRAINT ck_production_end_priority_operation CHECK (
        operation_code IN ('BRING_EMPTY', 'BRING_FULL', 'REMOVE_FULL', 'REMOVE_EMPTY')
    )
);

COMMIT;
