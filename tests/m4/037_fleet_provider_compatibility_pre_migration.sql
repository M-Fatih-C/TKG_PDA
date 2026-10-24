-- Historical RDS row created before migration 037.
INSERT INTO master.layouts (id, machine_id, code, name, status)
SELECT
    '02700000-0000-0000-0000-000000000001'::uuid,
    id,
    'M4-MIGRATION-TEST',
    'M4 migration test layout',
    'DRAFT'
FROM master.machines
WHERE code = 'SH-01-A'
ON CONFLICT (id) DO NOTHING;

INSERT INTO master.navigation_profiles (id, machine_id, code, name, status)
SELECT
    '02700000-0000-0000-0000-000000000002'::uuid,
    id,
    'M4-MIGRATION-RDS',
    'M4 migration legacy RDS profile',
    'DRAFT'
FROM master.machines
WHERE code = 'SH-01-A'
ON CONFLICT (id) DO NOTHING;

INSERT INTO ops.transport_requests (
    id,
    request_origin,
    machine_id,
    variant_id,
    layout_id,
    navigation_id,
    task_type_id,
    status
)
SELECT
    '02700000-0000-0000-0000-000000000003'::uuid,
    'API',
    machine.id,
    variant.id,
    '02700000-0000-0000-0000-000000000001'::uuid,
    '02700000-0000-0000-0000-000000000002'::uuid,
    task_type.id,
    'SENT_TO_RDS'
FROM master.machines machine
JOIN master.variants variant ON variant.machine_id = machine.id AND variant.code = '1'
JOIN master.task_types task_type ON task_type.code = 'BRING_EMPTY'
WHERE machine.code = 'SH-01-A'
ON CONFLICT (id) DO NOTHING;

INSERT INTO integration.rds_tasks (
    id,
    transport_request_id,
    rds_task_record_id,
    rds_task_label,
    rds_status,
    request_payload_json,
    response_payload_json,
    last_status_json,
    sent_at,
    last_sync_at
)
VALUES (
    '02700000-0000-0000-0000-000000000004'::uuid,
    '02700000-0000-0000-0000-000000000003'::uuid,
    '02700000-0000-0000-0000-000000000003',
    'M4_MIGRATION_RDS_TEST',
    1002,
    '{"source":"legacy-rds"}'::jsonb,
    '{}'::jsonb,
    '{"status":1002}'::jsonb,
    now(),
    now()
)
ON CONFLICT (id) DO NOTHING;
