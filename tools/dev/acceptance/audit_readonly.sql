-- Read-only commissioning and integrity evidence. Never repairs operational rows.
SELECT json_build_object(
 'active_machines', (SELECT count(*) FROM master.machines WHERE is_active),
 'variants', (SELECT count(*) FROM master.variants),
 'registry_profiles', (SELECT count(*) FROM master.navigation_profiles WHERE config_json->>'commissioningRegistry'='tkg_rds_variant_map_naming_v1'),
 'source_backed', (SELECT count(*) FROM master.navigation_profiles WHERE config_json->>'layoutBindingState'='SOURCE_BACKED' AND config_json->>'commissioningRegistry'='tkg_rds_variant_map_naming_v1'),
 'generic_templates', (SELECT count(*) FROM master.task_templates WHERE product_group_id IS NULL AND is_active),
 'fg_locations', (SELECT count(*) FROM ops.store_locations WHERE code ~ '^FG-[0-9]{2}$'),
 'worksite_plans', (SELECT count(*) FROM integration.rds_worksite_plans),
 'slot_plan_bindings', (SELECT count(*) FROM integration.layout_slot_rds_plan_bindings),
 'mirrored_worksites', (SELECT count(*) FROM integration.rds_worksites),
 'alt_missing_container', (SELECT count(*) FROM master.product_group_parts WHERE relation_type='ALT_PART' AND container_type_id IS NULL)
);

SELECT json_build_object(
 'orphan_dependencies', (SELECT count(*) FROM ops.transport_request_dependencies d LEFT JOIN ops.transport_requests p ON p.id=d.predecessor_request_id LEFT JOIN ops.transport_requests s ON s.id=d.successor_request_id WHERE p.id IS NULL OR s.id IS NULL),
 'invalid_request_task_machine', (SELECT count(*) FROM ops.transport_requests r LEFT JOIN master.task_types t ON t.id=r.task_type_id LEFT JOIN master.machines m ON m.id=r.machine_id WHERE t.id IS NULL OR m.id IS NULL),
 'invalid_session_machine', (SELECT count(*) FROM ops.production_sessions s LEFT JOIN master.machines m ON m.id=s.machine_id WHERE m.id IS NULL),
 'multiple_active_sessions', (SELECT count(*) FROM (SELECT machine_id FROM ops.production_sessions WHERE ended_at IS NULL GROUP BY machine_id HAVING count(*)>1) q),
 'orphan_slot_runtime', (SELECT count(*) FROM ops.slot_runtime_state s LEFT JOIN master.layout_slots l ON l.id=s.layout_slot_id WHERE l.id IS NULL),
 'cross_machine_slots', (SELECT count(*) FROM ops.transport_requests r JOIN master.layouts l ON l.id=r.layout_id LEFT JOIN master.layout_slots s ON s.id=r.source_slot_id LEFT JOIN master.layouts sl ON sl.id=s.layout_id LEFT JOIN master.layout_slots d ON d.id=r.destination_slot_id LEFT JOIN master.layouts dl ON dl.id=d.layout_id WHERE l.machine_id<>r.machine_id OR sl.machine_id<>r.machine_id OR dl.machine_id<>r.machine_id),
 'profile_binding_mismatch', (SELECT count(*) FROM integration.profile_shared_endpoint_bindings b JOIN integration.rds_worksites w ON w.id=b.rds_worksite_id WHERE b.navigation_profile_id IS DISTINCT FROM w.navigation_profile_id),
 'canonical_active_collisions', (SELECT count(*) FROM (SELECT destination_business_code FROM ops.transport_requests WHERE destination_business_code IS NOT NULL AND status NOT IN ('COMPLETED','FAILED','CANCELLED') GROUP BY destination_business_code HAVING count(*)>1) q),
 'invalid_runtime_variant_layout', (SELECT count(*) FROM ops.machine_runtime_state r LEFT JOIN master.variants v ON v.id=r.active_variant_id LEFT JOIN master.layouts l ON l.id=r.active_layout_id WHERE v.machine_id IS DISTINCT FROM r.machine_id OR l.machine_id IS DISTINCT FROM r.machine_id OR NOT EXISTS (SELECT 1 FROM master.variant_layout_assignments a WHERE a.variant_id=r.active_variant_id AND a.layout_id=r.active_layout_id AND a.machine_id=r.machine_id)),
 'unknown_destination_code', (SELECT count(*) FROM ops.transport_requests WHERE destination_business_code IS NOT NULL AND destination_business_code !~ '^(FG-(0[1-9]|1[0-9])|RETURN-0[1-8]|EMPTY-0[1-8])$'),
 'broken_group_part', (SELECT count(*) FROM master.product_group_parts g LEFT JOIN master.parts p ON p.id=g.part_id LEFT JOIN master.product_groups pg ON pg.id=g.product_group_id WHERE p.id IS NULL OR pg.id IS NULL),
 'duplicate_active_templates', (SELECT count(*) FROM (SELECT variant_id,product_group_id,task_type_id FROM master.task_templates WHERE is_active GROUP BY variant_id,product_group_id,task_type_id HAVING count(*)>1) q),
 'missing_source_variant_operations', (SELECT count(*) FROM master.variants v CROSS JOIN master.task_types tt WHERE tt.code IN ('BRING_FULL','BRING_EMPTY','REMOVE_FULL','REMOVE_EMPTY') AND EXISTS (SELECT 1 FROM master.variant_navigation_profile_assignments a JOIN master.navigation_profiles n ON n.id=a.navigation_profile_id WHERE a.variant_id=v.id AND n.config_json->>'layoutBindingState'='SOURCE_BACKED') AND NOT EXISTS (SELECT 1 FROM master.task_templates t WHERE t.variant_id=v.id AND t.task_type_id=tt.id AND t.product_group_id IS NULL AND t.is_active))
);

SELECT json_agg(q ORDER BY q.machine,q.variant) FROM (
 SELECT m.code AS machine, v.code AS variant, n.code AS navigation,
 n.status AS publish_state, n.config_json->>'layoutBindingState' AS source_state,
 n.config_json->>'geometryVerified' AS geometry_verified,
 (SELECT string_agg(l.code, ',' ORDER BY l.code) FROM master.variant_layout_assignments a JOIN master.layouts l ON l.id=a.layout_id WHERE a.variant_id=v.id) AS layouts,
 (SELECT count(*) FROM integration.rds_worksite_plans p WHERE p.navigation_profile_id=n.id) AS worksite_plans,
 (SELECT count(*) FROM integration.rds_worksites w WHERE w.navigation_profile_id=n.id) AS mirrored_worksites,
 (SELECT count(*) FROM integration.profile_shared_endpoint_bindings b WHERE b.navigation_profile_id=n.id AND b.endpoint_class='FINISHED_GOODS') AS fg_bindings,
 (SELECT count(*) FROM integration.profile_shared_endpoint_bindings b WHERE b.navigation_profile_id=n.id AND b.endpoint_class='NON_FINISHED_RETURN') AS return_bindings,
 (SELECT count(*) FROM integration.profile_shared_endpoint_bindings b WHERE b.navigation_profile_id=n.id AND b.endpoint_class IN ('EMPTY_SOURCE','EMPTY_RETURN')) AS empty_bindings,
 (SELECT count(*) FROM master.product_groups g JOIN master.product_group_parts p ON p.product_group_id=g.id WHERE g.variant_id=v.id AND p.relation_type='ALT_PART' AND p.container_type_id IS NULL) AS missing_alt_containers
 FROM master.machines m JOIN master.variants v ON v.machine_id=m.id
 JOIN master.variant_navigation_profile_assignments a ON a.variant_id=v.id
 JOIN master.navigation_profiles n ON n.id=a.navigation_profile_id
 WHERE m.is_active AND n.config_json->>'commissioningRegistry'='tkg_rds_variant_map_naming_v1'
) q;

SELECT json_agg(p ORDER BY operation_code) FROM master.production_end_priorities p;
SELECT json_agg(p ORDER BY product_group_id, layout_slot_id, part_id) FROM master.production_end_area_priorities p;
SELECT json_agg(s) FROM ops.production_sessions s WHERE ended_at IS NULL;
