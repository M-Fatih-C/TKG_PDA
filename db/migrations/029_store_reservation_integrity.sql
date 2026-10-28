-- 029_store_reservation_integrity.sql
-- A PICKUP always identifies one physical pallet. A DROP reserves only an area.

BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_store_reservation_shape'
          AND conrelid = 'ops.store_reservations'::regclass
    ) THEN
        ALTER TABLE ops.store_reservations
            ADD CONSTRAINT ck_store_reservation_shape CHECK (
                (reservation_type = 'PICKUP' AND store_unit_id IS NOT NULL)
                OR (reservation_type = 'DROP' AND store_unit_id IS NULL)
            );
    END IF;
END;
$$;

COMMIT;
