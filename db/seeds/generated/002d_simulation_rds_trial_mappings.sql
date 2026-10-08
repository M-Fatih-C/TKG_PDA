-- 002d_simulation_rds_trial_mappings.sql
-- Simulation Profile: OPTIMAK_SIM_2026 (Environment: SIMULATION)
-- Configures Roboshop / SEER simulation maps:
--   Map A: 26.7.2026_map_optimak (bound to SH-01-A Variant 1 via DEV-RDS-SIM-MAP-A)
--   Map B: 26.7.2026_map_optimak_1 (bound to SH-01-A Variant 2 via DEV-RDS-SIM-MAP-B)
-- Transition site: SM11
-- Endpoint roles:
--   EMPTY_STORAGE -> BUFFER-001 -> AP21
--   DROP          -> DROP-001   -> AP4
--   Machine slots   -> SLOT-001..SLOT-004 -> AP7, AP81, AP82, AP83
-- NO HARDCODED AP4 or AP21 in task templates.

DO $$
DECLARE
    v_machine_id uuid;
    v_variant_1_id uuid;
    v_variant_2_id uuid;
    v_layout_1_id uuid;
    v_layout_2_id uuid;
    v_container_type_id uuid;
    v_nav_a_id uuid := 'fba61025-221d-4556-9945-865fa8d04ab3';
    v_nav_b_id uuid := 'fba61025-221d-4556-9945-865fa8d04ab4';
    v_task_bring_empty uuid;
    v_task_remove_full uuid;
    v_task_remove_empty uuid;
BEGIN
    SELECT id INTO v_machine_id FROM master.machines WHERE code = 'SH-01-A';
    IF v_machine_id IS NULL THEN
        RAISE EXCEPTION 'Machine SH-01-A not found';
    END IF;

    SELECT id INTO v_variant_1_id FROM master.variants WHERE machine_id = v_machine_id AND code = '1';
    SELECT id INTO v_variant_2_id FROM master.variants WHERE machine_id = v_machine_id AND code = '2';
    SELECT id INTO v_layout_1_id FROM master.layouts WHERE machine_id = v_machine_id AND code = 'CUSTOMER-PHYSICAL-V1';
    SELECT id INTO v_layout_2_id FROM master.layouts WHERE machine_id = v_machine_id AND code = 'CUSTOMER-PHYSICAL-V2';
    SELECT id INTO v_container_type_id FROM master.container_types LIMIT 1;
    SELECT id INTO v_task_bring_empty FROM master.task_types WHERE code = 'BRING_EMPTY';
    SELECT id INTO v_task_remove_full FROM master.task_types WHERE code = 'REMOVE_FULL';
    SELECT id INTO v_task_remove_empty FROM master.task_types WHERE code = 'REMOVE_EMPTY';

    -- 1. Navigation Profiles for Simulation
    INSERT INTO master.navigation_profiles (
        id, machine_id, code, name, version, status, robot_map_name, config_json
    )
    VALUES
        (
            v_nav_a_id,
            v_machine_id,
            'DEV-RDS-SIM-MAP-A',
            'DEV-RDS-SIM (Map A - 26.7.2026_map_optimak)',
            1,
            'PUBLISHED',
            '26.7.2026_map_optimak',
            '{"environment":"SIMULATION","scope":"OPTIMAK_SIM_2026","switchTransitionStation":"SM11","vehicle":"Sfl-Cbd15","allowedVehicles":["Sfl-Cbd15"]}'::jsonb
        ),
        (
            v_nav_b_id,
            v_machine_id,
            'DEV-RDS-SIM-MAP-B',
            'DEV-RDS-SIM (Map B - 26.7.2026_map_optimak_1)',
            1,
            'PUBLISHED',
            '26.7.2026_map_optimak_1',
            '{"environment":"SIMULATION","scope":"OPTIMAK_SIM_2026","switchTransitionStation":"SM11","vehicle":"Sfl-Cbd15","allowedVehicles":["Sfl-Cbd15"]}'::jsonb
        )
    ON CONFLICT (id) DO UPDATE SET
        code = EXCLUDED.code,
        name = EXCLUDED.name,
        status = EXCLUDED.status,
        robot_map_name = EXCLUDED.robot_map_name,
        config_json = EXCLUDED.config_json;

    -- 2. Bind Navigation Profiles to Variants (Variant 1 -> Map A, Variant 2 -> Map B)
    INSERT INTO master.variant_navigation_profile_assignments (
        variant_id, machine_id, navigation_profile_id, is_default, assigned_at
    )
    VALUES
        (v_variant_1_id, v_machine_id, v_nav_a_id, true, now()),
        (v_variant_2_id, v_machine_id, v_nav_b_id, true, now())
    ON CONFLICT (variant_id, navigation_profile_id) DO UPDATE SET
        is_default = EXCLUDED.is_default,
        assigned_at = now();

    -- 3. Register ActionPoints as Mirrored WorkSites under Map A Navigation Profile
    INSERT INTO integration.rds_worksites (
        id, navigation_profile_id, rds_site_id, area, group_name, rds_type,
        last_filled, last_locked, last_disabled, last_content, last_agv_id, last_sync_failed, last_sync_at, raw_state_json
    )
    VALUES
        ('c1000000-0000-0000-0000-000000000001', v_nav_a_id, 'AP21', 'DEPOT', 'BUFFER', 1, 0, 0, 0, null, null, 0, now(), '{}'),
        ('c1000000-0000-0000-0000-000000000002', v_nav_a_id, 'AP7', 'LINE', 'FEED', 1, 0, 0, 0, null, null, 0, now(), '{}'),
        ('c1000000-0000-0000-0000-000000000003', v_nav_a_id, 'AP81', 'LINE', 'FEED', 1, 0, 0, 0, null, null, 0, now(), '{}'),
        ('c1000000-0000-0000-0000-000000000004', v_nav_a_id, 'AP82', 'LINE', 'FEED', 1, 0, 0, 0, null, null, 0, now(), '{}'),
        ('c1000000-0000-0000-0000-000000000005', v_nav_a_id, 'AP83', 'LINE', 'FEED', 1, 0, 0, 0, null, null, 0, now(), '{}'),
        ('c1000000-0000-0000-0000-000000000006', v_nav_a_id, 'AP4', 'WAREHOUSE', 'DROP', 1, 0, 0, 0, null, null, 0, now(), '{}'),
        ('c1000000-0000-0000-0000-000000000007', v_nav_a_id, 'AP1', 'WAREHOUSE', 'STORAGE', 1, 0, 0, 0, null, null, 0, now(), '{}'),
        ('c1000000-0000-0000-0000-000000000008', v_nav_a_id, 'PP80', 'PARK', 'PARK', 1, 0, 0, 0, null, null, 0, now(), '{}'),
        ('c1000000-0000-0000-0000-000000000009', v_nav_a_id, 'AP84', 'LINE', 'FEED', 1, 0, 0, 0, null, null, 0, now(), '{}'),
        ('c1000000-0000-0000-0000-00000000000a', v_nav_a_id, 'AP85', 'LINE', 'FEED', 1, 0, 0, 0, null, null, 0, now(), '{}')
    ON CONFLICT (id) DO UPDATE SET 
        rds_site_id = EXCLUDED.rds_site_id,
        area = EXCLUDED.area,
        group_name = EXCLUDED.group_name,
        last_sync_at = now();

    -- 4. Ensure BUFFER-001 (EMPTY_STORAGE) and DROP-001 (DROP) slots exist on Variant 1 and Variant 2 layouts
    INSERT INTO master.layout_slots (
        id, layout_id, code, display_name, slot_role, default_container_type_id,
        ui_x, ui_y, ui_width, ui_height, display_order, allows_empty_load, allows_full_load, is_enabled, metadata_json
    )
    VALUES
        ('c2000000-0000-0000-0000-000000000001', v_layout_1_id, 'BUFFER-001', 'Depo Boş Kasa Buffer (AP21)', 'EMPTY_STORAGE', v_container_type_id, 10, 10, 50, 50, 90, true, false, true, '{"scope":"OPTIMAK_SIM_2026"}'),
        ('c2000000-0000-0000-0000-000000000002', v_layout_1_id, 'DROP-001', 'Dolu Kasa Teslim (AP4)', 'DROP', v_container_type_id, 70, 10, 50, 50, 91, false, true, true, '{"scope":"OPTIMAK_SIM_2026"}'),
        ('c2000000-0000-0000-0000-000000000003', v_layout_2_id, 'BUFFER-001', 'Depo Boş Kasa Buffer (AP21)', 'EMPTY_STORAGE', v_container_type_id, 10, 10, 50, 50, 90, true, false, true, '{"scope":"OPTIMAK_SIM_2026"}'),
        ('c2000000-0000-0000-0000-000000000004', v_layout_2_id, 'DROP-001', 'Dolu Kasa Teslim (AP4)', 'DROP', v_container_type_id, 70, 10, 50, 50, 91, false, true, true, '{"scope":"OPTIMAK_SIM_2026"}')
    ON CONFLICT (layout_id, code) DO UPDATE SET
        display_name = EXCLUDED.display_name,
        slot_role = EXCLUDED.slot_role;

    -- 5. Runtime state for buffer and drop slots
    INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state, row_version, updated_at)
    VALUES
        ('c2000000-0000-0000-0000-000000000001', 'OCCUPIED', 'EMPTY', 0, now()),
        ('c2000000-0000-0000-0000-000000000002', 'VACANT', 'UNKNOWN', 0, now()),
        ('c2000000-0000-0000-0000-000000000003', 'OCCUPIED', 'EMPTY', 0, now()),
        ('c2000000-0000-0000-0000-000000000004', 'VACANT', 'UNKNOWN', 0, now())
    ON CONFLICT (layout_slot_id) DO UPDATE SET
        occupancy_state = EXCLUDED.occupancy_state,
        load_state = EXCLUDED.load_state;

    -- Insert/Reset all layout slots for machine SH-01-A to VACANT / UNKNOWN
    INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state, row_version, updated_at)
    SELECT id, 'VACANT', 'UNKNOWN', 0, now()
    FROM master.layout_slots
    WHERE layout_id IN (v_layout_1_id, v_layout_2_id)
      AND slot_role NOT IN ('EMPTY_STORAGE', 'DROP')
    ON CONFLICT (layout_slot_id) DO UPDATE SET
        occupancy_state = 'VACANT',
        load_state = 'UNKNOWN',
        updated_at = now();

    -- 6. Map Layout Slots to RDS WorkSites
    INSERT INTO integration.layout_slot_rds_mapping (layout_slot_id, rds_worksite_id)
    SELECT ls.id, rw.id
    FROM master.layout_slots ls
    JOIN master.layouts l ON l.id = ls.layout_id
    CROSS JOIN integration.rds_worksites rw
    WHERE l.machine_id = v_machine_id
      -- The two maps no longer share point names for the buffer. AP21 exists
      -- only in Map B, so Variant 1 draws its empty containers from AP1, which
      -- is a WAREHOUSE/STORAGE point of Map A and is claimed by nothing else.
      -- Mapping both variants to AP21 sent Variant 1 to a point its own map
      -- does not contain.
      AND ((ls.code = 'BUFFER-001' AND l.id = v_layout_1_id AND rw.rds_site_id = 'AP1')
        OR (ls.code = 'BUFFER-001' AND l.id = v_layout_2_id AND rw.rds_site_id = 'AP21')
        OR (ls.code = 'SLOT-001' AND rw.rds_site_id = 'AP7')
        OR (ls.code = 'SLOT-002' AND rw.rds_site_id = 'AP81')
        OR (ls.code = 'SLOT-003' AND rw.rds_site_id = 'AP82')
        OR (ls.code = 'SLOT-004' AND rw.rds_site_id = 'AP83')
        OR (ls.code = 'SLOT-005' AND rw.rds_site_id = 'AP84')
        OR (ls.code = 'SLOT-006' AND rw.rds_site_id = 'AP85')
        OR (ls.code = 'DROP-001' AND rw.rds_site_id = 'AP4'))
    ON CONFLICT (layout_slot_id) DO UPDATE SET
        rds_worksite_id = EXCLUDED.rds_worksite_id;

    -- 7. Task Templates for SH-01-A Variant 1 & Variant 2 (Zero hardcoded AP4/locations)
    INSERT INTO master.task_templates (id, variant_id, task_type_id, rds_task_label, priority, config_json, is_active)
    VALUES
        -- Variant 1 templates
        ('c3000000-0000-0000-0000-000000000001', v_variant_1_id, v_task_bring_empty, 'TKG_BRING_EMPTY', 1, '{"executionMode":"RDSCORE_NATIVE_ORDER","templateRegistry":"tkg_variant_task_templates_v1"}'::jsonb, true),
        ('c3000000-0000-0000-0000-000000000002', v_variant_1_id, v_task_remove_full, 'TKG_REMOVE_FULL', 1, '{"executionMode":"RDSCORE_NATIVE_ORDER","templateRegistry":"tkg_variant_task_templates_v1"}'::jsonb, true),
        ('c3000000-0000-0000-0000-000000000003', v_variant_1_id, v_task_remove_empty, 'TKG_REMOVE_EMPTY', 1, '{"executionMode":"RDSCORE_NATIVE_ORDER","templateRegistry":"tkg_variant_task_templates_v1"}'::jsonb, true),
        -- Variant 2 templates
        ('c3000000-0000-0000-0000-000000000004', v_variant_2_id, v_task_bring_empty, 'TKG_BRING_EMPTY', 1, '{"executionMode":"RDSCORE_NATIVE_ORDER","templateRegistry":"tkg_variant_task_templates_v1"}'::jsonb, true),
        ('c3000000-0000-0000-0000-000000000005', v_variant_2_id, v_task_remove_full, 'TKG_REMOVE_FULL', 1, '{"executionMode":"RDSCORE_NATIVE_ORDER","templateRegistry":"tkg_variant_task_templates_v1"}'::jsonb, true),
        ('c3000000-0000-0000-0000-000000000006', v_variant_2_id, v_task_remove_empty, 'TKG_REMOVE_EMPTY', 1, '{"executionMode":"RDSCORE_NATIVE_ORDER","templateRegistry":"tkg_variant_task_templates_v1"}'::jsonb, true)
    ON CONFLICT (id) DO UPDATE SET
        rds_task_label = EXCLUDED.rds_task_label,
        config_json = EXCLUDED.config_json,
        is_active = true;

    -- 8. Initial active Navigation Profile on SH-01-A runtime state
    INSERT INTO ops.machine_runtime_state (
        machine_id, active_variant_id, active_layout_id, active_navigation_id, row_version, updated_at
    )
    VALUES (
        v_machine_id, v_variant_1_id, v_layout_1_id, v_nav_a_id, 0, now()
    )
    ON CONFLICT (machine_id) DO UPDATE SET
        active_variant_id = EXCLUDED.active_variant_id,
        active_layout_id = EXCLUDED.active_layout_id,
        active_navigation_id = EXCLUDED.active_navigation_id,
        updated_at = now()
    WHERE ops.machine_runtime_state.active_variant_id IS NULL
       OR ops.machine_runtime_state.active_layout_id IS NULL;

END $$;
