-- ============================================================
-- Migration 012: Field Terminal to Machine Binding (WP-03)
-- Allows audit.client_devices (field PDA terminals) to be bound
-- to a specific physical production machine in master.machines.
--
-- Forward-only, additive, replay-safe.
-- machine_id is deliberately non-unique to allow backup/service terminals.
-- Existing devices remain machine_id = NULL (unbound/mobile/warehouse).
-- ============================================================

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'audit'
          AND table_name = 'client_devices'
          AND column_name = 'machine_id'
    ) THEN
        ALTER TABLE audit.client_devices
            ADD COLUMN machine_id uuid NULL REFERENCES master.machines(id) ON DELETE RESTRICT;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_client_devices_machine_id
    ON audit.client_devices(machine_id)
    WHERE machine_id IS NOT NULL;
