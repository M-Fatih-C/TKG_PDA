-- Nineteen temporary finished-goods areas and an explicit empty-container
-- state. Display names are placeholders. A renamed name is left alone.
-- No DROP position is mapped to these areas.
BEGIN;

INSERT INTO ops.store_locations (code, display_name, capacity, is_enabled)
SELECT format('FG_AREA_%s', lpad(n::text, 2, '0')),
       format('Alan %s', n),
       NULL,
       true
FROM generate_series(1, 19) AS n
ON CONFLICT (code) DO NOTHING;

ALTER TABLE ops.store_units
    ADD COLUMN IF NOT EXISTS load_state varchar(20) NOT NULL DEFAULT 'FULL';

ALTER TABLE ops.store_units DROP CONSTRAINT IF EXISTS ck_store_unit_load;
ALTER TABLE ops.store_units
    ADD CONSTRAINT ck_store_unit_load CHECK (load_state IN ('EMPTY', 'FULL'));

ALTER TABLE ops.store_events DROP CONSTRAINT IF EXISTS ck_store_event_type;
ALTER TABLE ops.store_events
    ADD CONSTRAINT ck_store_event_type CHECK (event_type IN (
        'UNIT_DELIVERED', 'UNIT_REMOVED', 'LOCATION_CLEARED', 'EMPTY_CONTAINER_RECORDED'
    ));

COMMIT;
