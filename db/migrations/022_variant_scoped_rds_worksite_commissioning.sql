-- Variant-scoped RDS commissioning.
-- One logical layout slot may map to a different live WorkSite on each
-- navigation profile. Planned names are not live RDS WorkSites.
-- Additive and replay-safe. Existing layout_slot_rds_mapping rows stay.
BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'uq_rds_worksite_id_profile'
          AND conrelid = 'integration.rds_worksites'::regclass) THEN
        ALTER TABLE integration.rds_worksites
            ADD CONSTRAINT uq_rds_worksite_id_profile UNIQUE (id, navigation_profile_id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS integration.layout_slot_rds_profile_mapping (
    layout_slot_id          uuid NOT NULL REFERENCES master.layout_slots(id) ON DELETE CASCADE,
    navigation_profile_id   uuid NOT NULL REFERENCES master.navigation_profiles(id) ON DELETE RESTRICT,
    rds_worksite_id         uuid NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (layout_slot_id, navigation_profile_id),
    CONSTRAINT fk_slot_rds_profile_worksite_same_profile
        FOREIGN KEY (rds_worksite_id, navigation_profile_id)
        REFERENCES integration.rds_worksites (id, navigation_profile_id)
);

CREATE TABLE IF NOT EXISTS integration.rds_worksite_plans (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    navigation_profile_id   uuid NOT NULL REFERENCES master.navigation_profiles(id) ON DELETE CASCADE,
    planned_rds_site_id     varchar(200) NOT NULL,
    object_family           varchar(40) NOT NULL,
    ordinal                 integer NOT NULL,
    commissioning_state     varchar(20) NOT NULL DEFAULT 'PLANNED',
    geometry_verified       boolean NOT NULL DEFAULT false,
    metadata_json           jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_rds_worksite_plan_profile_site UNIQUE (navigation_profile_id, planned_rds_site_id),
    CONSTRAINT uq_rds_worksite_plan_id_profile UNIQUE (id, navigation_profile_id),
    CONSTRAINT ck_rds_worksite_plan_state CHECK (
        commissioning_state IN ('PLANNED', 'MAPPED', 'VERIFIED', 'COMMISSIONED')),
    CONSTRAINT ck_rds_worksite_plan_family CHECK (
        object_family IN (
            'MACHINE_POSITION', 'PREPARATION', 'EMPTY', 'RETURN',
            'FINISHED_GOODS', 'PARK', 'CHARGE')),
    CONSTRAINT ck_rds_worksite_plan_ordinal CHECK (ordinal > 0)
);

-- A matching planned name does not create a live WorkSite and does not
-- advance commissioning_state. Promotion is an explicit later step, and
-- geometry verification stays independent of that state.

CREATE TABLE IF NOT EXISTS integration.layout_slot_rds_plan_bindings (
    layout_slot_id          uuid NOT NULL REFERENCES master.layout_slots(id) ON DELETE CASCADE,
    navigation_profile_id   uuid NOT NULL REFERENCES master.navigation_profiles(id) ON DELETE CASCADE,
    worksite_plan_id        uuid NOT NULL,
    logical_slot_code       varchar(100) NOT NULL,
    business_role           varchar(40),
    source_layout_code      varchar(100),
    metadata_json           jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (layout_slot_id, navigation_profile_id),
    CONSTRAINT fk_slot_rds_plan_same_profile
        FOREIGN KEY (worksite_plan_id, navigation_profile_id)
        REFERENCES integration.rds_worksite_plans (id, navigation_profile_id)
);

CREATE OR REPLACE FUNCTION integration.resolve_slot_worksite(
    p_layout_slot_id uuid,
    p_navigation_profile_id uuid
) RETURNS uuid
LANGUAGE sql
STABLE
AS $$
    SELECT CASE
        WHEN p_navigation_profile_id IS NULL THEN (
            SELECT map.rds_worksite_id
            FROM integration.layout_slot_rds_mapping map
            WHERE map.layout_slot_id = p_layout_slot_id
        )
        ELSE COALESCE(
            (
                SELECT profile_map.rds_worksite_id
                FROM integration.layout_slot_rds_profile_mapping profile_map
                JOIN integration.rds_worksites rw ON rw.id = profile_map.rds_worksite_id
                WHERE profile_map.layout_slot_id = p_layout_slot_id
                  AND profile_map.navigation_profile_id = p_navigation_profile_id
                  AND rw.navigation_profile_id = p_navigation_profile_id
            ),
            (
                SELECT legacy.rds_worksite_id
                FROM integration.layout_slot_rds_mapping legacy
                JOIN integration.rds_worksites rw ON rw.id = legacy.rds_worksite_id
                WHERE legacy.layout_slot_id = p_layout_slot_id
                  AND rw.navigation_profile_id = p_navigation_profile_id
            )
        )
    END
$$;

COMMENT ON FUNCTION integration.resolve_slot_worksite(uuid, uuid) IS
    'Live WorkSite for a slot. An explicit navigation profile never falls back to another profile or to a null-profile legacy WorkSite. A null profile keeps the legacy slot mapping.';

COMMIT;
