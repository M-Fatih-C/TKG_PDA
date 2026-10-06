-- Variant commissioning hardening.
-- A layout slot and its navigation profile must belong to the same machine.
-- Existing same-machine rows are left in place. Replay only replaces the guard.

CREATE OR REPLACE FUNCTION integration.enforce_slot_navigation_same_machine()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM master.layout_slots s
        JOIN master.layouts l ON l.id = s.layout_id
        JOIN master.navigation_profiles n ON n.id = NEW.navigation_profile_id
        WHERE s.id = NEW.layout_slot_id
          AND l.machine_id = n.machine_id
    ) THEN
        RAISE EXCEPTION 'layout slot machine must equal navigation profile machine'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_profile_mapping_same_machine ON integration.layout_slot_rds_profile_mapping;
CREATE TRIGGER trg_profile_mapping_same_machine
    BEFORE INSERT OR UPDATE ON integration.layout_slot_rds_profile_mapping
    FOR EACH ROW
    EXECUTE FUNCTION integration.enforce_slot_navigation_same_machine();

DROP TRIGGER IF EXISTS trg_plan_binding_same_machine ON integration.layout_slot_rds_plan_bindings;
CREATE TRIGGER trg_plan_binding_same_machine
    BEFORE INSERT OR UPDATE ON integration.layout_slot_rds_plan_bindings
    FOR EACH ROW
    EXECUTE FUNCTION integration.enforce_slot_navigation_same_machine();
