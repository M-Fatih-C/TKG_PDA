BEGIN;

-- Operator intent is persisted even when a shared source/destination has no
-- current capacity.  The request remains QUEUED and the workflow worker
-- allocates the endpoint later.  Existing rows keep their historical,
-- dispatchable behaviour through the TRUE default.
ALTER TABLE ops.transport_requests
    ADD COLUMN IF NOT EXISTS store_endpoint_reservation_active boolean NOT NULL DEFAULT true;

CREATE INDEX IF NOT EXISTS ix_transport_requests_deferred_store_endpoint
    ON ops.transport_requests (priority, queued_at, request_no)
    WHERE status = 'QUEUED'
      AND NOT store_endpoint_reservation_active;

COMMIT;
