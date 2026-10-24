INSERT INTO integration.m4_discovery_snapshots (
    id,
    correlation_id,
    status,
    requested_scene_id,
    requested_scene_name,
    captured_at,
    fresh_until,
    snapshot_json
)
VALUES (
    '03800000-0000-0000-0000-000000000001'::uuid,
    '03800000-0000-0000-0000-000000000002'::uuid,
    'SUCCESS',
    'sim-scene-id',
    'OneDemo',
    now(),
    now() + interval '5 minutes',
    '{"status":"SUCCESS","resources":[{"kind":"SCENE_LIST","success":true}]}'::jsonb
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM integration.m4_discovery_snapshots
        WHERE id = '03800000-0000-0000-0000-000000000001'
          AND status = 'SUCCESS'
          AND snapshot_json #>> '{resources,0,kind}' = 'SCENE_LIST'
    ) THEN
        RAISE EXCEPTION 'M4 discovery raw evidence was not persisted';
    END IF;

    BEGIN
        INSERT INTO integration.m4_discovery_snapshots (
            id, correlation_id, status, captured_at, fresh_until, snapshot_json
        ) VALUES (
            gen_random_uuid(), gen_random_uuid(), 'SUCCESS', now(), now(), '{}'::jsonb
        );
        RAISE EXCEPTION 'Invalid M4 discovery freshness was accepted';
    EXCEPTION
        WHEN check_violation THEN NULL;
    END;
END $$;
