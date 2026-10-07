-- Generated production task templates for source-backed TKG variants.
-- This is not the simulation seed. It does not insert live RDS WorkSites,
-- does not publish a navigation profile, and does not confirm geometry.
--
-- rds_task_label is a deterministic technical identifier (TKG_NATIVE_<OPERATION>).
-- RDSCORE_NATIVE_ORDER does not execute by that Wind Task label.
-- Replay inserts a missing generic template only. An existing row keeps its
-- priority and config_json, including a customer change.
-- SEALER V3 stays out until its navigation profile is SOURCE_BACKED.
BEGIN;

INSERT INTO master.task_templates (
    id, variant_id, task_type_id, rds_task_label, priority, config_json, is_active)
SELECT (
        substr(md5('tkg-task-v1:' || m.code || ':' || v.code || ':' || tt.code), 1, 8) || '-' ||
        substr(md5('tkg-task-v1:' || m.code || ':' || v.code || ':' || tt.code), 9, 4) || '-' ||
        '4' || substr(md5('tkg-task-v1:' || m.code || ':' || v.code || ':' || tt.code), 13, 3) || '-' ||
        'a' || substr(md5('tkg-task-v1:' || m.code || ':' || v.code || ':' || tt.code), 17, 3) || '-' ||
        substr(md5('tkg-task-v1:' || m.code || ':' || v.code || ':' || tt.code), 21, 12)
    )::uuid,
    v.id,
    tt.id,
    'TKG_NATIVE_' || tt.code,
    100,
    jsonb_build_object(
        'executionMode', 'RDSCORE_NATIVE_ORDER',
        'templateRegistry', 'tkg_variant_task_templates_v1',
        'labelRole', 'TECHNICAL_ONLY'),
    true
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.is_active
JOIN master.variant_navigation_profile_assignments a ON a.variant_id = v.id AND a.machine_id = m.id
JOIN master.navigation_profiles n ON n.id = a.navigation_profile_id AND n.machine_id = m.id
JOIN master.task_types tt ON tt.code IN ('BRING_FULL', 'BRING_EMPTY', 'REMOVE_FULL', 'REMOVE_EMPTY')
WHERE n.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
  AND n.config_json ->> 'layoutBindingState' = 'SOURCE_BACKED'
  AND NOT EXISTS (
      SELECT 1
      FROM master.task_templates existing
      WHERE existing.variant_id = v.id
        AND existing.task_type_id = tt.id
        AND existing.product_group_id IS NULL)
ON CONFLICT (id) DO NOTHING;

COMMIT;
