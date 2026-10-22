-- 028_store_reservations_and_source_endpoint.sql
-- Introduce physical store location reservations (PICKUP / DROP) to prevent race conditions,
-- ensure deterministic binding between physical store pallets and robot worksites,
-- and add source_business_code to transport requests.

BEGIN;

-- 1. Add source_business_code to ops.transport_requests
ALTER TABLE ops.transport_requests
    ADD COLUMN IF NOT EXISTS source_business_code varchar(80);

-- 2. Create physical store reservations table
CREATE TABLE IF NOT EXISTS ops.store_reservations (
    id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    store_location_id    uuid NOT NULL REFERENCES ops.store_locations(id) ON DELETE CASCADE,
    store_unit_id        uuid REFERENCES ops.store_units(id) ON DELETE CASCADE,
    transport_request_id uuid NOT NULL REFERENCES ops.transport_requests(id) ON DELETE CASCADE,
    reservation_type     varchar(20) NOT NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    released_at          timestamptz,
    CONSTRAINT ck_store_reservation_type CHECK (reservation_type IN ('PICKUP', 'DROP'))
);

-- Exactly one active reservation per store location
CREATE UNIQUE INDEX IF NOT EXISTS uq_store_active_location_reservation
    ON ops.store_reservations (store_location_id)
    WHERE released_at IS NULL;

-- Exactly one active pickup reservation per store unit
CREATE UNIQUE INDEX IF NOT EXISTS uq_store_active_unit_reservation
    ON ops.store_reservations (store_unit_id)
    WHERE released_at IS NULL AND store_unit_id IS NOT NULL;

-- Exactly one active store reservation per transport request
CREATE UNIQUE INDEX IF NOT EXISTS uq_store_active_request_reservation
    ON ops.store_reservations (transport_request_id)
    WHERE released_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_store_reservations_request
    ON ops.store_reservations (transport_request_id);

COMMIT;
