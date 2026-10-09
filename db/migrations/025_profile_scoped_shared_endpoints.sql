-- Profile-scoped shared endpoints for customer commissioning.
-- Additive. Does not publish a profile, confirm geometry, or insert a live WorkSite.
-- Migration 024 is operational reporting and is left unchanged.
BEGIN;

ALTER TABLE ops.transport_requests
    ADD COLUMN IF NOT EXISTS destination_business_code varchar(80);

ALTER TABLE integration.preparation_area_rds_mapping
    ADD COLUMN IF NOT EXISTS navigation_profile_id uuid;

UPDATE integration.preparation_area_rds_mapping AS mapping
SET navigation_profile_id = site.navigation_profile_id
FROM integration.rds_worksites AS site
WHERE site.id = mapping.rds_worksite_id
  AND mapping.navigation_profile_id IS DISTINCT FROM site.navigation_profile_id;

CREATE OR REPLACE FUNCTION integration.sync_preparation_mapping_profile()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    site_profile uuid;
BEGIN
    SELECT navigation_profile_id INTO site_profile
    FROM integration.rds_worksites
    WHERE id = NEW.rds_worksite_id;

    NEW.navigation_profile_id := site_profile;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_preparation_mapping_profile ON integration.preparation_area_rds_mapping;
CREATE TRIGGER trg_preparation_mapping_profile
    BEFORE INSERT OR UPDATE OF rds_worksite_id, navigation_profile_id
    ON integration.preparation_area_rds_mapping
    FOR EACH ROW
    EXECUTE FUNCTION integration.sync_preparation_mapping_profile();

-- Legacy rows whose WorkSite has no profile stay unconstrained.
-- One business preparation area has at most one live WorkSite on a profile.
CREATE UNIQUE INDEX IF NOT EXISTS uq_preparation_area_navigation_profile
    ON integration.preparation_area_rds_mapping (preparation_area_id, navigation_profile_id)
    WHERE navigation_profile_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS integration.profile_shared_endpoint_bindings (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    navigation_profile_id   uuid NOT NULL REFERENCES master.navigation_profiles(id) ON DELETE CASCADE,
    endpoint_class          varchar(40) NOT NULL,
    business_code           varchar(80) NOT NULL,
    store_location_id       uuid REFERENCES ops.store_locations(id) ON DELETE RESTRICT,
    rds_worksite_id         uuid NOT NULL REFERENCES integration.rds_worksites(id) ON DELETE CASCADE,
    created_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_profile_shared_endpoint_class CHECK (endpoint_class IN (
        'EMPTY_SOURCE', 'EMPTY_RETURN', 'FINISHED_GOODS', 'NON_FINISHED_RETURN')),
    CONSTRAINT uq_profile_shared_endpoint_business
        UNIQUE (navigation_profile_id, endpoint_class, business_code)
);

CREATE INDEX IF NOT EXISTS ix_profile_shared_endpoint_worksite
    ON integration.profile_shared_endpoint_bindings (rds_worksite_id);

CREATE OR REPLACE FUNCTION integration.reject_cross_profile_shared_endpoint()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    site_profile uuid;
BEGIN
    SELECT navigation_profile_id INTO site_profile
    FROM integration.rds_worksites
    WHERE id = NEW.rds_worksite_id;

    IF site_profile IS DISTINCT FROM NEW.navigation_profile_id THEN
        RAISE EXCEPTION 'PROFILE_MISMATCH'
            USING ERRCODE = '23514';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_profile_shared_endpoint_profile ON integration.profile_shared_endpoint_bindings;
CREATE TRIGGER trg_profile_shared_endpoint_profile
    BEFORE INSERT OR UPDATE OF navigation_profile_id, rds_worksite_id
    ON integration.profile_shared_endpoint_bindings
    FOR EACH ROW
    EXECUTE FUNCTION integration.reject_cross_profile_shared_endpoint();

COMMIT;
