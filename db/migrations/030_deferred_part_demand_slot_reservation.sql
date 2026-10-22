BEGIN;

-- Alt-part orders are warehouse preparation demands before they are physical
-- robot moves.  A prepared pallet may only claim its machine position when
-- that position can be used safely.  Existing transport requests keep their
-- historical behaviour through the TRUE default.
ALTER TABLE ops.transport_requests
    ADD COLUMN IF NOT EXISTS machine_slot_reservation_active boolean NOT NULL DEFAULT true;

CREATE INDEX IF NOT EXISTS ix_transport_requests_deferred_part_demand
    ON ops.transport_requests (ready_at, request_no)
    WHERE status = 'QUEUED'
      AND readiness_state IN ('WAITING_PREPARATION', 'READY')
      AND NOT machine_slot_reservation_active;

COMMIT;
