BEGIN;

UPDATE auth.user_machine_permissions
SET can_view = true
WHERE can_view = false
  AND (can_operate OR can_change_variant OR can_manage_slots);

ALTER TABLE auth.user_machine_permissions
    DROP CONSTRAINT IF EXISTS ck_user_machine_permissions_view_required;

ALTER TABLE auth.user_machine_permissions
    ADD CONSTRAINT ck_user_machine_permissions_view_required
    CHECK (can_view OR NOT (can_operate OR can_change_variant OR can_manage_slots));

COMMIT;
