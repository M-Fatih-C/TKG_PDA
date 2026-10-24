DO $$
BEGIN
    IF (SELECT execution_provider FROM ops.transport_requests
        WHERE id = '02700000-0000-0000-0000-000000000003') <> 'RDS' THEN
        RAISE EXCEPTION 'Existing request was not backfilled to RDS';
    END IF;

    IF (SELECT execution_provider FROM master.navigation_profiles
        WHERE id = '02700000-0000-0000-0000-000000000002') <> 'RDS' THEN
        RAISE EXCEPTION 'Existing navigation profile was not backfilled to RDS';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM integration.fleet_orders
        WHERE transport_request_id = '02700000-0000-0000-0000-000000000003'
          AND provider = 'RDS'
          AND upstream_external_id = '02700000-0000-0000-0000-000000000003'
          AND normalized_status = 'PENDING'
    ) THEN
        RAISE EXCEPTION 'Legacy RDS order was not backfilled';
    END IF;
END $$;

UPDATE integration.rds_tasks
SET response_payload_json = '{"cancellationRequestedAt":"2026-10-19T00:00:00Z"}'::jsonb,
    updated_at = now()
WHERE transport_request_id = '02700000-0000-0000-0000-000000000003';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM integration.fleet_orders
        WHERE transport_request_id = '02700000-0000-0000-0000-000000000003'
          AND normalized_status = 'CANCELLING'
          AND cancellation_requested
    ) THEN
        RAISE EXCEPTION 'RDS cancellation was not normalized';
    END IF;
END $$;

UPDATE integration.rds_tasks
SET rds_status = 1001,
    agv_id = 'SIM-ROBOT-01',
    started_at = now() - interval '1 minute',
    ended_at = now(),
    last_status_json = '{"status":1001}'::jsonb,
    last_sync_at = now(),
    updated_at = now()
WHERE transport_request_id = '02700000-0000-0000-0000-000000000003';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM integration.fleet_orders
        WHERE transport_request_id = '02700000-0000-0000-0000-000000000003'
          AND normalized_status = 'COMPLETED'
          AND actual_robot_name = 'SIM-ROBOT-01'
    ) THEN
        RAISE EXCEPTION 'RDS compatibility trigger did not update the fleet order';
    END IF;
END $$;

INSERT INTO master.navigation_profiles (
    id, machine_id, code, name, status, execution_provider, external_scene_id, external_scene_name
)
SELECT
    '02700000-0000-0000-0000-000000000010'::uuid,
    id,
    'M4-MIGRATION-M4',
    'M4 migration M4 profile',
    'DRAFT',
    'M4',
    'scene-test',
    'Scene Test'
FROM master.machines
WHERE code = 'SH-01-A';

INSERT INTO ops.transport_requests (
    id,
    request_origin,
    machine_id,
    variant_id,
    layout_id,
    navigation_id,
    task_type_id,
    status,
    execution_provider
)
SELECT
    '02700000-0000-0000-0000-000000000011'::uuid,
    'API',
    machine.id,
    variant.id,
    '02700000-0000-0000-0000-000000000001'::uuid,
    '02700000-0000-0000-0000-000000000010'::uuid,
    task_type.id,
    'QUEUED',
    'M4'
FROM master.machines machine
JOIN master.variants variant ON variant.machine_id = machine.id AND variant.code = '1'
JOIN master.task_types task_type ON task_type.code = 'BRING_EMPTY'
WHERE machine.code = 'SH-01-A';

INSERT INTO integration.fleet_orders (
    transport_request_id,
    provider,
    external_order_id,
    upstream_external_id,
    normalized_status,
    result_uncertain
)
VALUES (
    '02700000-0000-0000-0000-000000000011'::uuid,
    'M4',
    'm4-order-test',
    '02700000-0000-0000-0000-000000000011',
    'QUEUED',
    false
);

INSERT INTO master.layout_slots (
    id, layout_id, code, display_name, slot_role
)
VALUES (
    '02700000-0000-0000-0000-000000000012'::uuid,
    '02700000-0000-0000-0000-000000000001'::uuid,
    'M4-MIGRATION-SLOT',
    'M4 migration test slot',
    'OTHER'
);

INSERT INTO integration.external_endpoints (
    id,
    provider,
    navigation_profile_id,
    external_scene_id,
    external_location_id,
    external_location_name,
    endpoint_kind
)
VALUES (
    '02700000-0000-0000-0000-000000000013'::uuid,
    'M4',
    '02700000-0000-0000-0000-000000000010'::uuid,
    'scene-test',
    'location-test',
    'Location Test',
    'POINT'
);

INSERT INTO integration.layout_slot_external_mapping (
    layout_slot_id,
    navigation_profile_id,
    provider,
    external_endpoint_id
)
VALUES (
    '02700000-0000-0000-0000-000000000012'::uuid,
    '02700000-0000-0000-0000-000000000010'::uuid,
    'M4',
    '02700000-0000-0000-0000-000000000013'::uuid
);

INSERT INTO integration.physical_endpoint_evidence (
    external_endpoint_id,
    occupancy,
    load_state,
    disabled,
    locked,
    read_failed,
    observed_at,
    freshness_deadline,
    evidence_source
)
VALUES (
    '02700000-0000-0000-0000-000000000013'::uuid,
    'UNKNOWN',
    'UNKNOWN',
    NULL,
    NULL,
    false,
    now(),
    now() + interval '5 minutes',
    'M4_TEST'
);

INSERT INTO integration.fleet_callback_inbox (
    provider, event_name, event_hash, external_order_id, raw_payload_json
)
VALUES (
    'M4', 'ORDER_CHANGED', 'm4-test-event-hash', 'm4-order-test', '{"status":"QUEUED"}'::jsonb
);

DO $$
BEGIN
    BEGIN
        INSERT INTO integration.external_endpoints (
            provider, navigation_profile_id, external_location_id, endpoint_kind
        ) VALUES (
            'M4',
            '02700000-0000-0000-0000-000000000002'::uuid,
            'wrong-provider-profile',
            'POINT'
        );
        RAISE EXCEPTION 'Provider/profile mismatch was accepted';
    EXCEPTION
        WHEN foreign_key_violation THEN NULL;
    END;

    BEGIN
        INSERT INTO integration.layout_slot_external_mapping (
            layout_slot_id, navigation_profile_id, provider, external_endpoint_id
        ) VALUES (
            '02700000-0000-0000-0000-000000000012'::uuid,
            '02700000-0000-0000-0000-000000000002'::uuid,
            'M4',
            '02700000-0000-0000-0000-000000000013'::uuid
        );
        RAISE EXCEPTION 'Cross-profile endpoint mapping was accepted';
    EXCEPTION
        WHEN foreign_key_violation THEN NULL;
    END;

    BEGIN
        INSERT INTO integration.fleet_callback_inbox (
            provider, event_name, event_hash, raw_payload_json
        ) VALUES (
            'M4', 'ORDER_CHANGED', 'm4-test-event-hash', '{}'::jsonb
        );
        RAISE EXCEPTION 'Duplicate callback hash was accepted';
    EXCEPTION
        WHEN unique_violation THEN NULL;
    END;

    BEGIN
        UPDATE integration.physical_endpoint_evidence
        SET occupancy = 'VACANT', load_state = 'FULL'
        WHERE external_endpoint_id = '02700000-0000-0000-0000-000000000013';
        RAISE EXCEPTION 'Invalid physical occupancy/load combination was accepted';
    EXCEPTION
        WHEN check_violation THEN NULL;
    END;
END $$;
