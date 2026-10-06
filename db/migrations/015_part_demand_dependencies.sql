-- Alt-part repeat requests may remove the pallet that is already standing
-- before a new BRING_FULL may be dispatched. The successor row exists in the
-- same transaction; dispatch waits for the predecessor to complete.
BEGIN;

CREATE TABLE IF NOT EXISTS ops.transport_request_dependencies (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    predecessor_request_id  uuid NOT NULL REFERENCES ops.transport_requests(id) ON DELETE RESTRICT,
    successor_request_id    uuid NOT NULL REFERENCES ops.transport_requests(id) ON DELETE RESTRICT,
    created_at              timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_transport_dependency_distinct CHECK (predecessor_request_id <> successor_request_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_transport_dependency_predecessor
    ON ops.transport_request_dependencies (predecessor_request_id);

CREATE UNIQUE INDEX IF NOT EXISTS uq_transport_dependency_successor
    ON ops.transport_request_dependencies (successor_request_id);

COMMIT;
