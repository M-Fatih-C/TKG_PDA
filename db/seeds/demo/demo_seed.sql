DO $$
DECLARE
    v_admin_role uuid;
    v_sup_role uuid;
    v_line_role uuid;
    v_wh_role uuid;
    v_sf_wh_role uuid;
    v_fg_wh_role uuid;
    m_rec RECORD;
    v_nav_id uuid;
    v_default_var_id uuid;
    v_default_layout_id uuid;
BEGIN
    SELECT id INTO v_admin_role FROM auth.roles WHERE code = 'ADMIN';
    SELECT id INTO v_sup_role FROM auth.roles WHERE code = 'SUPERVISOR';
    SELECT id INTO v_line_role FROM auth.roles WHERE code = 'LINE_OPERATOR';
    SELECT id INTO v_wh_role FROM auth.roles WHERE code = 'WAREHOUSE_OPERATOR';
    SELECT id INTO v_sf_wh_role FROM auth.roles WHERE code = 'SEMI_FINISHED_WAREHOUSE_OPERATOR';
    SELECT id INTO v_fg_wh_role FROM auth.roles WHERE code = 'FINISHED_GOODS_WAREHOUSE_OPERATOR';

    -- 1. Demo users. Password hashes are provisioned without exposing plaintext credentials.
    -- Free up any conflicting employee numbers assigned to legacy or other test users
    UPDATE auth.users SET employee_no = NULL
    WHERE employee_no IN ('DEV-ADM', 'DEV-SUP', 'DEV-LINE', '1001', 'DEV-WH', '2001', '2002')
      AND username NOT IN ('dev-admin', 'dev-supervisor', 'dev-line', 'operator-1001', 'dev-warehouse', 'dev-wh-semi', 'dev-wh-finished');

    INSERT INTO auth.users (username, password_hash, full_name, employee_no, is_active)
    VALUES
      ('dev-admin', '$tkg-pbkdf2-sha512$v=1$i=210000$gDFwUtCCloIXM7g+r5cw8w==$dC/4mbXQS6ztQYKdc0ejY9Mt85OPZsU1Ua3CP35BaK8=', 'Demo Yönetici (Admin)', 'DEV-ADM', true),
      ('dev-supervisor', '$tkg-pbkdf2-sha512$v=1$i=210000$gDFwUtCCloIXM7g+r5cw8w==$dC/4mbXQS6ztQYKdc0ejY9Mt85OPZsU1Ua3CP35BaK8=', 'Demo Postabaşı / Süpervizör', 'DEV-SUP', true),
      ('dev-line', '$tkg-pbkdf2-sha512$v=1$i=210000$gDFwUtCCloIXM7g+r5cw8w==$dC/4mbXQS6ztQYKdc0ejY9Mt85OPZsU1Ua3CP35BaK8=', 'Demo Hat Operatörü', 'DEV-LINE', true),
      ('operator-1001', '$tkg-pbkdf2-sha512$v=1$i=210000$gDFwUtCCloIXM7g+r5cw8w==$dC/4mbXQS6ztQYKdc0ejY9Mt85OPZsU1Ua3CP35BaK8=', 'Demo Operatör 1001', '1001', true),
      ('dev-warehouse', '$tkg-pbkdf2-sha512$v=1$i=210000$gDFwUtCCloIXM7g+r5cw8w==$dC/4mbXQS6ztQYKdc0ejY9Mt85OPZsU1Ua3CP35BaK8=', 'Demo Depo Sorumlusu', 'DEV-WH', true),
      ('dev-wh-semi', '$tkg-pbkdf2-sha512$v=1$i=210000$gDFwUtCCloIXM7g+r5cw8w==$dC/4mbXQS6ztQYKdc0ejY9Mt85OPZsU1Ua3CP35BaK8=', 'Yarı Mamul Depo Operatörü', '2001', true),
      ('dev-wh-finished', '$tkg-pbkdf2-sha512$v=1$i=210000$gDFwUtCCloIXM7g+r5cw8w==$dC/4mbXQS6ztQYKdc0ejY9Mt85OPZsU1Ua3CP35BaK8=', 'Bitmiş Ürün Depo Operatörü', '2002', true)
    ON CONFLICT (username) DO UPDATE
    SET full_name = EXCLUDED.full_name, employee_no = EXCLUDED.employee_no, password_hash = EXCLUDED.password_hash, is_active = true;

    UPDATE auth.users SET employee_no = 'DEV-LINE' WHERE username = 'dev-line';
    UPDATE auth.users SET employee_no = '1001' WHERE username = 'operator-1001';
    UPDATE auth.users SET employee_no = '2001' WHERE username = 'dev-wh-semi';
    UPDATE auth.users SET employee_no = '2002' WHERE username = 'dev-wh-finished';

    -- 2. Demo roles are replaced on every seed replay so removed privileges do not linger.
    DELETE FROM auth.user_roles ur
    USING auth.users u
    WHERE ur.user_id = u.id
      AND u.username IN ('dev-admin', 'dev-supervisor', 'dev-line', 'operator-1001', 'dev-warehouse', 'dev-wh-semi', 'dev-wh-finished');

    -- The two cross-machine demo operator accounts retain SUPERVISOR deliberately;
    -- production LINE_OPERATOR accounts remain bound to one physical machine device.
    INSERT INTO auth.user_roles (user_id, role_id)
    SELECT u.id, v_admin_role FROM auth.users u WHERE u.username IN ('dev-admin', 'dev-supervisor') ON CONFLICT DO NOTHING;
    INSERT INTO auth.user_roles (user_id, role_id)
    SELECT u.id, v_sup_role FROM auth.users u WHERE u.username IN ('dev-supervisor', 'dev-line', 'operator-1001') ON CONFLICT DO NOTHING;
    INSERT INTO auth.user_roles (user_id, role_id)
    SELECT u.id, v_line_role FROM auth.users u WHERE u.username IN ('dev-line', 'operator-1001') ON CONFLICT DO NOTHING;
    INSERT INTO auth.user_roles (user_id, role_id)
    SELECT u.id, v_wh_role FROM auth.users u WHERE u.username = 'dev-warehouse' ON CONFLICT DO NOTHING;
    IF v_sf_wh_role IS NOT NULL THEN
        INSERT INTO auth.user_roles (user_id, role_id)
        SELECT u.id, v_sf_wh_role FROM auth.users u WHERE u.username IN ('dev-warehouse', 'dev-wh-semi') ON CONFLICT DO NOTHING;
    END IF;
    IF v_fg_wh_role IS NOT NULL THEN
        INSERT INTO auth.user_roles (user_id, role_id)
        SELECT u.id, v_fg_wh_role FROM auth.users u WHERE u.username IN ('dev-warehouse', 'dev-wh-finished') ON CONFLICT DO NOTHING;
    END IF;

    -- 3. Dedicated and Universal Client Devices
    INSERT INTO audit.client_devices (device_code, name, device_type, machine_id, is_active)
    VALUES
      ('DEV-SUPERVISOR', 'Demo Süpervizör Terminali (Tüm Makineler)', 'PDA', NULL, true),
      ('DEV-LINE', 'Demo Operatör Terminali (Tüm Makineler)', 'PDA', NULL, true),
      ('DEV-WAREHOUSE', 'Demo Depo Terminali (Genel)', 'PDA', NULL, true),
      ('TAB-YARI-MAMUL', 'Tablet 1 — Yarı Mamul Hazırlık Deposu', 'PDA', NULL, true),
      ('TAB-BITMIS-URUN', 'Tablet 2 — Bitmiş Ürün Deposu', 'PDA', NULL, true)
    ON CONFLICT (device_code) DO UPDATE SET name = EXCLUDED.name, machine_id = NULL, is_active = true;

    -- Machine-bound terminals for each of the 9 machines
    INSERT INTO audit.client_devices (device_code, name, device_type, machine_id, is_active)
    SELECT 'DEV-' || REPLACE(m.code, ' ', ''), 'Demo ' || m.code || ' Terminali', 'PDA', m.id, true
    FROM master.machines m WHERE m.is_active
    ON CONFLICT (device_code) DO UPDATE SET name = EXCLUDED.name, machine_id = EXCLUDED.machine_id, is_active = true;

    -- 4. Replace demo machine permissions with the minimum cross-machine demo scope.
    -- ADMIN has a server-side global bypass; warehouse roles do not need machine permissions.
    DELETE FROM auth.user_machine_permissions permission
    USING auth.users u
    WHERE permission.user_id = u.id
      AND u.username IN ('dev-admin', 'dev-supervisor', 'dev-line', 'operator-1001', 'dev-warehouse', 'dev-wh-semi', 'dev-wh-finished');

    INSERT INTO auth.user_machine_permissions (user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
    SELECT u.id, m.id, true, true, true, false
    FROM auth.users u CROSS JOIN master.machines m
    WHERE u.username IN ('dev-supervisor', 'dev-line', 'operator-1001') AND m.is_active
    ON CONFLICT (user_id, machine_id) DO UPDATE
    SET can_view = true, can_operate = true, can_change_variant = true, can_manage_slots = false;

    -- 5. Publish All Navigation Profiles so robot route checking is valid
    UPDATE master.navigation_profiles SET status = 'PUBLISHED' WHERE status <> 'PUBLISHED';

    -- 6. Ensure Every Machine has an Active Runtime State with published profile
    FOR m_rec IN SELECT m.id, m.code FROM master.machines m WHERE m.is_active LOOP
        SELECT id INTO v_nav_id FROM master.navigation_profiles
        WHERE machine_id = m_rec.id AND status = 'PUBLISHED'
        ORDER BY version DESC LIMIT 1;

        SELECT id INTO v_default_var_id FROM master.variants
        WHERE machine_id = m_rec.id AND code = '1';

        IF v_default_var_id IS NULL THEN
            SELECT id INTO v_default_var_id FROM master.variants
            WHERE machine_id = m_rec.id ORDER BY display_order, code LIMIT 1;
        END IF;

        SELECT vla.layout_id INTO v_default_layout_id
        FROM master.variant_layout_assignments vla
        WHERE vla.variant_id = v_default_var_id AND vla.is_default LIMIT 1;

        IF v_default_layout_id IS NULL THEN
            SELECT id INTO v_default_layout_id FROM master.layouts
            WHERE machine_id = m_rec.id AND status = 'PUBLISHED'
            ORDER BY version DESC LIMIT 1;
        END IF;

        IF v_default_var_id IS NOT NULL AND v_default_layout_id IS NOT NULL THEN
            INSERT INTO ops.machine_runtime_state (
                machine_id, active_variant_id, active_layout_id, active_navigation_id
            ) VALUES (
                m_rec.id, v_default_var_id, v_default_layout_id, v_nav_id
            ) ON CONFLICT (machine_id) DO UPDATE
            SET active_variant_id = EXCLUDED.active_variant_id,
                active_layout_id = EXCLUDED.active_layout_id,
                active_navigation_id = COALESCE(EXCLUDED.active_navigation_id, ops.machine_runtime_state.active_navigation_id);
        END IF;
    END LOOP;

    -- 7. Complete Layout Slot Part Mappings for 100% Machine and Variant Parity
    -- SH-01-P1--SH-01-P2 (Layout: CUSTOMER-PHYSICAL-SHARED-V1-V2)
    -- Variant 1 Mamül (71550BN000-10-HAOS) -> SLOT-013, SLOT-014
    -- Variant 2 Mamül (71560BN000-10-HAOS) -> SLOT-006, SLOT-007
    INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
    SELECT ls.id, p.id
    FROM master.machines m
    JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-SHARED-V1-V2'
    JOIN master.layout_slots ls ON ls.layout_id = l.id
    JOIN master.parts p ON (
        (ls.code IN ('SLOT-013', 'SLOT-014') AND p.part_code = '71550BN000-10-HAOS') OR
        (ls.code IN ('SLOT-006', 'SLOT-007') AND p.part_code = '71560BN000-10-HAOS') OR
        (ls.code = 'SLOT-001' AND p.part_code IN ('71576BN000-20', '71586BN000-20')) OR
        (ls.code = 'SLOT-002' AND p.part_code IN ('71556BN000-20', '71566BN000-20')) OR
        (ls.code IN ('SLOT-003', 'SLOT-010') AND p.part_code = '71515BN000-40') OR
        (ls.code IN ('SLOT-004', 'SLOT-011') AND p.part_code IN ('71574BN000-50', '71584BN000-50')) OR
        (ls.code IN ('SLOT-005', 'SLOT-009') AND p.part_code IN ('71514BN000-50', '71524BN000-50')) OR
        (ls.code = 'SLOT-012' AND p.part_code IN ('71517BN000-20', '71527BN000-20'))
    )
    WHERE m.code = 'SH-01-P1--SH-01-P2'
    ON CONFLICT (layout_slot_id, part_id) DO NOTHING;

    -- SH-02-P1 -SH-02-P2 (Layout: CUSTOMER-PHYSICAL-SHARED-V1-V2, slots 001-008)
    -- Variant 1 Mamül (71550BN400-10-HMTR) -> SLOT-001, SLOT-002
    -- Variant 2 Mamül (71560BN400-10-HMTR) -> SLOT-006, SLOT-007
    INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
    SELECT ls.id, p.id
    FROM master.machines m
    JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-SHARED-V1-V2'
    JOIN master.layout_slots ls ON ls.layout_id = l.id
    JOIN master.parts p ON (
        (ls.code IN ('SLOT-001', 'SLOT-002') AND p.part_code = '71550BN400-10-HMTR') OR
        (ls.code IN ('SLOT-006', 'SLOT-007') AND p.part_code = '71560BN400-10-HMTR') OR
        (ls.code IN ('SLOT-003', 'SLOT-004') AND p.part_code IN ('71576BN400-20', '71586BN400-20', '71515BN400-40')) OR
        (ls.code IN ('SLOT-005', 'SLOT-008') AND p.part_code IN ('71556BN400-20', '71566BN400-20', '71574BN400-50', '71514BN400-50'))
    )
    WHERE m.code = 'SH-02-P1 -SH-02-P2'
    ON CONFLICT (layout_slot_id, part_id) DO NOTHING;

    -- SH-01-A (Layout: CUSTOMER-PHYSICAL-V4) -> Variant 4 Mamül (67131BN000-10-HAOS)
    INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
    SELECT ls.id, p.id
    FROM master.machines m
    JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-V4'
    JOIN master.layout_slots ls ON ls.layout_id = l.id
    JOIN master.parts p ON p.part_code = '67131BN000-10-HAOS'
    WHERE m.code = 'SH-01-A' AND ls.code IN ('SLOT-004', 'SLOT-005')
    ON CONFLICT (layout_slot_id, part_id) DO NOTHING;

    -- SH-01-B (Layout: CUSTOMER-PHYSICAL-V2) -> Variant 2 Mamül (66424BN000-10-HAOS)
    INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
    SELECT ls.id, p.id
    FROM master.machines m
    JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-V2'
    JOIN master.layout_slots ls ON ls.layout_id = l.id
    JOIN master.parts p ON p.part_code = '66424BN000-10-HAOS'
    WHERE m.code = 'SH-01-B' AND ls.code IN ('SLOT-001', 'SLOT-007')
    ON CONFLICT (layout_slot_id, part_id) DO NOTHING;

    -- SEALER: Assign layout to Variant 3 if missing
    INSERT INTO master.variant_layout_assignments (machine_id, variant_id, layout_id, is_default)
    SELECT m.id, v.id, l.id, true
    FROM master.machines m
    JOIN master.variants v ON v.machine_id = m.id AND v.code = '3'
    JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-SHARED-V1-V2'
    WHERE m.code = 'SEALER'
      AND NOT EXISTS (
          SELECT 1 FROM master.variant_layout_assignments vla WHERE vla.variant_id = v.id AND vla.is_default
      )
    ON CONFLICT (variant_id, layout_id) DO NOTHING;

    -- SEALER (Layout: CUSTOMER-PHYSICAL-SHARED-V1-V2) -> Variant 1 & 2 BN000 parts and Variant 3 parts
    INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
    SELECT ls.id, p.id
    FROM master.machines m
    JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-SHARED-V1-V2'
    JOIN master.layout_slots ls ON ls.layout_id = l.id
    JOIN master.parts p ON (
        (ls.code IN ('SLOT-001', 'SLOT-009') AND p.part_code IN ('71517BN000-20', '71527BN000-20', '67134BN400-20')) OR
        (ls.code IN ('SLOT-002', 'SLOT-010') AND p.part_code IN ('71556BN000-20', '71566BN000-20', '67134GJ000-20')) OR
        (ls.code IN ('SLOT-003', 'SLOT-011') AND p.part_code IN ('71576BN000-20', '71586BN000-20'))
    )
    WHERE m.code = 'SEALER'
    ON CONFLICT (layout_slot_id, part_id) DO NOTHING;

    -- 7. Ensure All Subparts across all 9 machines & all variants are mapped to layout slots
    -- First, specific high-priority subpart assignments matching customer layout labels:
    -- SH-01-B V2 Subparts
    INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
    SELECT ls.id, p.id
    FROM master.machines m
    JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-V2'
    JOIN master.layout_slots ls ON ls.layout_id = l.id
    JOIN master.parts p ON (
        (ls.code = 'SLOT-006' AND p.part_code IN ('81110BN000-40', '81110BN400-40')) OR
        (ls.code = 'SLOT-008' AND p.part_code IN ('66425BN000-50', '66425BN400-50'))
    )
    WHERE m.code = 'SH-01-B'
    ON CONFLICT (layout_slot_id, part_id) DO NOTHING;

    -- SH-01-P1--SH-01-P2 V1 & V2 Subparts
    INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
    SELECT ls.id, p.id
    FROM master.machines m
    JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-SHARED-V1-V2'
    JOIN master.layout_slots ls ON ls.layout_id = l.id
    JOIN master.parts p ON (
        (ls.code = 'SLOT-008' AND p.part_code IN ('81163-C7000', '71538BN400-50')) OR
        (ls.code = 'SLOT-005' AND p.part_code IN ('71525BN000-40'))
    )
    WHERE m.code = 'SH-01-P1--SH-01-P2'
    ON CONFLICT (layout_slot_id, part_id) DO NOTHING;

    -- SH-02-P1 -SH-02-P2 V1 & V2 Subparts
    INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
    SELECT ls.id, p.id
    FROM master.machines m
    JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-SHARED-V1-V2'
    JOIN master.layout_slots ls ON ls.layout_id = l.id
    JOIN master.parts p ON (
        (ls.code = 'SLOT-003' AND p.part_code IN ('71517BN400-20')) OR
        (ls.code = 'SLOT-004' AND p.part_code IN ('71538BN400-50', '71548BN400-50')) OR
        (ls.code = 'SLOT-005' AND p.part_code IN ('715A2BN400-50')) OR
        (ls.code = 'SLOT-008' AND p.part_code IN ('715B2BN400-50'))
    )
    WHERE m.code = 'SH-02-P1 -SH-02-P2'
    ON CONFLICT (layout_slot_id, part_id) DO NOTHING;

    -- SEALER Subparts (Variants 1, 2, 3)
    INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
    SELECT ls.id, p.id
    FROM master.machines m
    JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-SHARED-V1-V2'
    JOIN master.layout_slots ls ON ls.layout_id = l.id
    JOIN master.parts p ON (
        (ls.code IN ('SLOT-004', 'SLOT-012') AND p.part_code IN ('71517BN000-40', '71517BN400-40', '71527BN000-40')) OR
        (ls.code IN ('SLOT-005', 'SLOT-013') AND p.part_code IN ('71556BN000-50', '71556BN400-50', '67134BN400-50', '67134GJ000-50')) OR
        (ls.code IN ('SLOT-006', 'SLOT-014') AND p.part_code IN ('71576BN000-50', '71576BN400-50')) OR
        (ls.code IN ('SLOT-007', 'SLOT-014') AND p.part_code IN ('71586BN000-50', '71586BN400-50')) OR
        (ls.code IN ('SLOT-008', 'SLOT-013') AND p.part_code IN ('71566BN000-50', '71566BN400-50'))
    )
    WHERE m.code = 'SEALER'
    ON CONFLICT (layout_slot_id, part_id) DO NOTHING;

    -- 412B and GAR parts without a customer-confirmed physical slot remain
    -- deliberately unmapped. A Mamul slot must never be reused as an ALT area.

    -- PAH-01-A Subparts (Variants 1, 2)
    INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
    SELECT ls.id, p.id
    FROM master.machines m
    JOIN master.layouts l ON l.machine_id = m.id
    JOIN master.layout_slots ls ON ls.layout_id = l.id
    JOIN master.parts p ON (
        (l.code = 'CUSTOMER-PHYSICAL-V1' AND ls.code IN ('SLOT-003', 'SLOT-004') AND p.part_code IN ('76173-A7000', '77151-BN400')) OR
        (l.code = 'CUSTOMER-PHYSICAL-V2' AND ls.code IN ('SLOT-003', 'SLOT-004') AND p.part_code = '76151GJ000')
    )
    WHERE m.code = 'PAH-01-A'
    ON CONFLICT (layout_slot_id, part_id) DO NOTHING;

    -- PAH-02-A Subparts (Variants 1, 2)
    INSERT INTO master.layout_slot_parts (layout_slot_id, part_id)
    SELECT ls.id, p.id
    FROM master.machines m
    JOIN master.layouts l ON l.machine_id = m.id
    JOIN master.layout_slots ls ON ls.layout_id = l.id
    JOIN master.parts p ON (
        (l.code = 'CUSTOMER-PHYSICAL-V1' AND ls.code IN ('SLOT-003', 'SLOT-004') AND p.part_code IN ('76151-BN400', '76173-A7000')) OR
        (l.code = 'CUSTOMER-PHYSICAL-V2' AND ls.code IN ('SLOT-003', 'SLOT-004') AND p.part_code IN ('76151-BN000', '76173-A7000'))
    )
    WHERE m.code = 'PAH-02-A'
    ON CONFLICT (layout_slot_id, part_id) DO NOTHING;

    -- Unmapped parts stay unresolved until a customer-confirmed assignment is
    -- supplied. Do not manufacture a fallback Part-to-Slot mapping.

    -- 8. Discover and Seed All RDS Worksites & Shared Endpoint Bindings
    -- Populates integration.rds_worksites from planned worksites so all 25 profiles
    -- have valid destinations for FINISHED_GOODS (Kasa Doldu) and EMPTY_SOURCE/RETURN (Boş Kasa Getir)
    INSERT INTO integration.rds_worksites (
        navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, last_filled, last_locked, raw_state_json
    )
    SELECT DISTINCT p.navigation_profile_id, p.planned_rds_site_id, 1, now(), 0, 0, 0, 0, '{}'::jsonb
    FROM integration.rds_worksite_plans p
    ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE SET
        last_sync_at = now(), last_disabled = 0, last_sync_failed = 0, last_filled = 0, last_locked = 0;

    INSERT INTO integration.profile_shared_endpoint_bindings (
        navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id
    )
    SELECT DISTINCT ON (w.navigation_profile_id, p.ordinal)
        w.navigation_profile_id,
        'FINISHED_GOODS',
        'FG-' || LPAD(p.ordinal::text, 2, '0'),
        (SELECT id FROM ops.store_locations WHERE code = 'FG-' || LPAD(p.ordinal::text, 2, '0') LIMIT 1),
        w.id
    FROM integration.rds_worksites w
    JOIN integration.rds_worksite_plans p
      ON p.navigation_profile_id = w.navigation_profile_id
     AND p.planned_rds_site_id = w.rds_site_id
    WHERE p.object_family = 'FINISHED_GOODS'
    ORDER BY w.navigation_profile_id, p.ordinal, w.id
    ON CONFLICT (navigation_profile_id, endpoint_class, business_code)
    DO UPDATE SET rds_worksite_id = EXCLUDED.rds_worksite_id,
                  store_location_id = EXCLUDED.store_location_id;

    -- Link Layout DROP Slots to Finished Goods Store Locations for direct drop resolution
    INSERT INTO ops.store_location_delivery_slots (store_location_id, layout_slot_id)
    SELECT sl.id, ls.id
    FROM master.layout_slots ls
    CROSS JOIN LATERAL (
        SELECT id FROM ops.store_locations WHERE is_enabled ORDER BY code LIMIT 1
    ) sl
    WHERE ls.slot_role = 'DROP'
    ON CONFLICT (layout_slot_id) DO NOTHING;

    INSERT INTO integration.profile_shared_endpoint_bindings (
        navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id
    )
    SELECT DISTINCT ON (w.navigation_profile_id, p.ordinal)
        w.navigation_profile_id,
        'EMPTY_SOURCE',
        'EMPTY-' || LPAD(p.ordinal::text, 2, '0'),
        (SELECT id FROM ops.store_locations WHERE code = 'FG-' || LPAD(p.ordinal::text, 2, '0') LIMIT 1),
        w.id
    FROM integration.rds_worksites w
    JOIN integration.rds_worksite_plans p
      ON p.navigation_profile_id = w.navigation_profile_id
     AND p.planned_rds_site_id = w.rds_site_id
    WHERE p.object_family = 'EMPTY'
    ORDER BY w.navigation_profile_id, p.ordinal, w.id
    ON CONFLICT (navigation_profile_id, endpoint_class, business_code)
    DO UPDATE SET rds_worksite_id = EXCLUDED.rds_worksite_id,
                  store_location_id = EXCLUDED.store_location_id;

    INSERT INTO integration.profile_shared_endpoint_bindings (
        navigation_profile_id, endpoint_class, business_code, rds_worksite_id
    )
    SELECT DISTINCT ON (w.navigation_profile_id, p.ordinal)
        w.navigation_profile_id,
        'EMPTY_RETURN',
        'EMPTY-' || LPAD(p.ordinal::text, 2, '0'),
        w.id
    FROM integration.rds_worksites w
    JOIN integration.rds_worksite_plans p
      ON p.navigation_profile_id = w.navigation_profile_id
     AND p.planned_rds_site_id = w.rds_site_id
    WHERE p.object_family = 'EMPTY'
    ORDER BY w.navigation_profile_id, p.ordinal, w.id
    ON CONFLICT (navigation_profile_id, endpoint_class, business_code)
    DO UPDATE SET rds_worksite_id = EXCLUDED.rds_worksite_id;

    INSERT INTO integration.profile_shared_endpoint_bindings (
        navigation_profile_id, endpoint_class, business_code, rds_worksite_id
    )
    SELECT DISTINCT ON (w.navigation_profile_id, p.ordinal)
        w.navigation_profile_id,
        'NON_FINISHED_RETURN',
        'RETURN-' || LPAD(p.ordinal::text, 2, '0'),
        w.id
    FROM integration.rds_worksites w
    JOIN integration.rds_worksite_plans p
      ON p.navigation_profile_id = w.navigation_profile_id
     AND p.planned_rds_site_id = w.rds_site_id
    WHERE p.object_family = 'RETURN'
    ORDER BY w.navigation_profile_id, p.ordinal, w.id
    ON CONFLICT (navigation_profile_id, endpoint_class, business_code)
    DO UPDATE SET rds_worksite_id = EXCLUDED.rds_worksite_id;

    -- 9. Initialize Slot Runtime States for Interactive Demo Testing
    -- Mamul slots: Alternating OCCUPIED/FULL (KASA DOLDU) and VACANT/UNKNOWN (BOŞ KASA GETİR)
    -- Alt parça (feed) slots: VACANT/UNKNOWN so 'TALEP ET' immediately requests BringFull from warehouse
    -- Empty Storage buffer: OCCUPIED/EMPTY
    -- current_part_id is populated for OCCUPIED slots to prevent PART_AREA_CURRENT_PART_UNKNOWN
    INSERT INTO ops.slot_runtime_state (
        layout_slot_id, occupancy_state, load_state, current_part_id, updated_at
    )
    SELECT ls.id,
           CASE 
               WHEN ls.slot_role = 'EMPTY_STORAGE' THEN 'OCCUPIED'
               WHEN act_part.part_id IS NOT NULL AND ls.display_order % 2 = 1 THEN 'OCCUPIED'
               ELSE 'VACANT'
           END,
           CASE 
               WHEN ls.slot_role = 'EMPTY_STORAGE' THEN 'EMPTY'
               WHEN act_part.part_id IS NOT NULL AND ls.display_order % 2 = 1 THEN 'FULL'
               ELSE 'UNKNOWN'
           END,
           CASE
               WHEN ls.slot_role = 'EMPTY_STORAGE' THEN NULL
               WHEN act_part.part_id IS NOT NULL AND ls.display_order % 2 = 1 THEN act_part.part_id
               ELSE NULL
           END,
           now()
    FROM master.layout_slots ls
    LEFT JOIN LATERAL (
        SELECT lsp.part_id
        FROM master.layout_slot_parts lsp
        JOIN master.product_groups pg ON pg.main_part_id = lsp.part_id
        JOIN ops.machine_runtime_state mrs ON mrs.active_variant_id = pg.variant_id
        JOIN master.layouts l ON l.id = ls.layout_id AND l.machine_id = mrs.machine_id
        WHERE lsp.layout_slot_id = ls.id
        LIMIT 1
    ) act_part ON true
    WHERE ls.is_enabled
    ON CONFLICT (layout_slot_id) DO UPDATE
    SET occupancy_state = EXCLUDED.occupancy_state,
        load_state = EXCLUDED.load_state,
        current_part_id = EXCLUDED.current_part_id,
        updated_at = now();

    -- 10. Production starts only after the operator selects the main part and
    -- explicitly presses "Üretime Başla". Demo seeding never opens or closes a
    -- production session.

    -- 11. Seed Production End Priorities
    INSERT INTO master.production_end_priorities (operation_code, priority, updated_at)
    VALUES
        ('REMOVE_FULL', 40, now()),
        ('REMOVE_EMPTY', 70, now()),
        ('BRING_EMPTY', 30, now()),
        ('BRING_FULL', 20, now())
    ON CONFLICT (operation_code) DO UPDATE
    SET priority = EXCLUDED.priority, updated_at = now();

    -- 12. Seed EURO_PALET and apply the customer-approved fallback for every
    -- active ALT_PART whose source container field is blank. Explicit source
    -- values such as 4311, 4322 and KCK are preserved unchanged.
    INSERT INTO master.container_types (code, name, category, length_mm, width_mm, height_mm)
    VALUES ('EURO_PALET', 'Euro Palet', 'PALLET', 1200, 800, 144)
    ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, length_mm = EXCLUDED.length_mm, width_mm = EXCLUDED.width_mm, height_mm = EXCLUDED.height_mm;

    UPDATE master.product_group_parts
    SET container_type_id = (SELECT id FROM master.container_types WHERE code = 'EURO_PALET' LIMIT 1),
        updated_at = now()
    WHERE relation_type = 'ALT_PART'
      AND container_type_id IS NULL;

    -- 13. Clean any unremoved store units so the two finished-goods delivery
    -- positions start empty. Empty-container demand is now prepared against
    -- the six request-bound FINISHED_GOODS preparation areas.
    -- Respect store_events ON DELETE RESTRICT by updating instead of deleting.
    -- Respect ck_store_unit_removed by providing valid non-null removed_by, removed_device_id and removed_at >= delivered_at.
    UPDATE ops.store_units
    SET removed_at = GREATEST(clock_timestamp(), delivered_at),
        removed_by = COALESCE(
            (SELECT id FROM auth.users WHERE username = 'operator-1001' LIMIT 1),
            (SELECT id FROM auth.users LIMIT 1)
        ),
        removed_device_id = COALESCE(
            (SELECT id FROM audit.client_devices WHERE device_code = 'TAB-BITMIS-URUN' LIMIT 1),
            (SELECT id FROM audit.client_devices LIMIT 1)
        ),
        removal_source = 'OPERATOR'
    WHERE removed_at IS NULL;

    -- 14. Keep only the two approved operator-visible delivery positions.
    -- Historical or commissioned legacy rows remain hidden and are never
    -- deleted while another table still references them.
    UPDATE ops.store_locations
    SET area_purpose = CASE WHEN code IN ('FG-01', 'FG-02') THEN 'DELIVERY' ELSE 'LEGACY' END,
        is_operator_visible = code IN ('FG-01', 'FG-02'),
        display_name = CASE code
            WHEN 'FG-01' THEN 'Mamul Teslim Alanı 1'
            WHEN 'FG-02' THEN 'Mamul Teslim Alanı 2'
            ELSE display_name
        END,
        capacity = CASE WHEN code IN ('FG-01', 'FG-02') THEN 1 ELSE capacity END,
        updated_at = now()
    WHERE code ~ '^FG-[0-9]{2}$';

    DELETE FROM ops.store_locations AS legacy
    WHERE legacy.code ~ '^FG_AREA_[0-9]{2}$'
      AND NOT EXISTS (SELECT 1 FROM ops.store_units u WHERE u.store_location_id = legacy.id)
      AND NOT EXISTS (SELECT 1 FROM ops.store_events e WHERE e.store_location_id = legacy.id)
      AND NOT EXISTS (SELECT 1 FROM ops.store_location_delivery_slots d WHERE d.store_location_id = legacy.id)
      AND NOT EXISTS (SELECT 1 FROM ops.store_reservations r WHERE r.store_location_id = legacy.id)
      AND NOT EXISTS (
          SELECT 1 FROM integration.profile_shared_endpoint_bindings b
          WHERE b.store_location_id = legacy.id);

    DELETE FROM ops.store_locations AS legacy
    WHERE legacy.area_purpose = 'LEGACY'
      AND legacy.code ~ '^FG-[0-9]{2}$'
      AND NOT EXISTS (SELECT 1 FROM ops.store_units u WHERE u.store_location_id = legacy.id)
      AND NOT EXISTS (SELECT 1 FROM ops.store_events e WHERE e.store_location_id = legacy.id)
      AND NOT EXISTS (SELECT 1 FROM ops.store_location_delivery_slots d WHERE d.store_location_id = legacy.id)
      AND NOT EXISTS (SELECT 1 FROM ops.store_reservations r WHERE r.store_location_id = legacy.id)
      AND NOT EXISTS (
          SELECT 1 FROM integration.profile_shared_endpoint_bindings b
          WHERE b.store_location_id = legacy.id);
END $$;

