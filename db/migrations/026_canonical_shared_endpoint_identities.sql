-- Migration 026: Canonicalize shared endpoint business identities
-- Ensures integration.profile_shared_endpoint_bindings and ops.transport_requests
-- record canonical global business codes (RETURN-01..08, EMPTY-01..08) instead of
-- profile-specific planned WorkSite names.
-- Finished Goods (FG-01..19) and Preparation (1..4) remain unaffected.
BEGIN;

-- Update NON_FINISHED_RETURN bindings whose business_code is a planned site name or matches _WS_RETURN(\d+)
UPDATE integration.profile_shared_endpoint_bindings b
SET business_code = 'RETURN-' || LPAD(p.ordinal::text, 2, '0')
FROM integration.rds_worksites w
JOIN integration.rds_worksite_plans p
  ON p.navigation_profile_id = w.navigation_profile_id
 AND p.planned_rds_site_id = w.rds_site_id
WHERE b.rds_worksite_id = w.id
  AND b.endpoint_class = 'NON_FINISHED_RETURN'
  AND p.object_family = 'RETURN'
  AND b.business_code <> ('RETURN-' || LPAD(p.ordinal::text, 2, '0'));

-- Update EMPTY_SOURCE / EMPTY_RETURN bindings whose business_code is a planned site name or matches _WS_EMPTY(\d+)
UPDATE integration.profile_shared_endpoint_bindings b
SET business_code = 'EMPTY-' || LPAD(p.ordinal::text, 2, '0')
FROM integration.rds_worksites w
JOIN integration.rds_worksite_plans p
  ON p.navigation_profile_id = w.navigation_profile_id
 AND p.planned_rds_site_id = w.rds_site_id
WHERE b.rds_worksite_id = w.id
  AND b.endpoint_class IN ('EMPTY_SOURCE', 'EMPTY_RETURN')
  AND p.object_family = 'EMPTY'
  AND b.business_code <> ('EMPTY-' || LPAD(p.ordinal::text, 2, '0'));

-- Update any historical transport requests that recorded destination_business_code as planned site name
UPDATE ops.transport_requests r
SET destination_business_code = 'RETURN-' || SUBSTRING(r.destination_business_code FROM '_WS_RETURN(\d{2})$')
WHERE r.destination_business_code ~ '_WS_RETURN\d{2}$';

UPDATE ops.transport_requests r
SET destination_business_code = 'EMPTY-' || SUBSTRING(r.destination_business_code FROM '_WS_EMPTY(\d{2})$')
WHERE r.destination_business_code ~ '_WS_EMPTY\d{2}$';

COMMIT;
