-- Nineteen temporary finished-goods areas. Forward-only.
-- Technical codes stay stable. Display names are placeholders until TKG
-- logistics supplies the real names. Replay must not overwrite a renamed row.
-- No delivery-slot mapping is seeded: the physical DROP assignment is unknown.
BEGIN;

INSERT INTO ops.store_locations (code, display_name, capacity, is_enabled)
VALUES
    ('FG_AREA_01', 'Alan 1', NULL, true),
    ('FG_AREA_02', 'Alan 2', NULL, true),
    ('FG_AREA_03', 'Alan 3', NULL, true),
    ('FG_AREA_04', 'Alan 4', NULL, true),
    ('FG_AREA_05', 'Alan 5', NULL, true),
    ('FG_AREA_06', 'Alan 6', NULL, true),
    ('FG_AREA_07', 'Alan 7', NULL, true),
    ('FG_AREA_08', 'Alan 8', NULL, true),
    ('FG_AREA_09', 'Alan 9', NULL, true),
    ('FG_AREA_10', 'Alan 10', NULL, true),
    ('FG_AREA_11', 'Alan 11', NULL, true),
    ('FG_AREA_12', 'Alan 12', NULL, true),
    ('FG_AREA_13', 'Alan 13', NULL, true),
    ('FG_AREA_14', 'Alan 14', NULL, true),
    ('FG_AREA_15', 'Alan 15', NULL, true),
    ('FG_AREA_16', 'Alan 16', NULL, true),
    ('FG_AREA_17', 'Alan 17', NULL, true),
    ('FG_AREA_18', 'Alan 18', NULL, true),
    ('FG_AREA_19', 'Alan 19', NULL, true)
ON CONFLICT (code) DO NOTHING;

COMMIT;
