-- One successful-snapshot watermark. Empty snapshots must advance it too.
-- A failed RDS read must not insert or update this row.
BEGIN;

CREATE TABLE IF NOT EXISTS integration.robot_alarm_observation_state (
    singleton_key                 boolean PRIMARY KEY DEFAULT true,
    last_successful_snapshot_at   timestamptz NOT NULL,
    last_correlation_id           uuid NOT NULL,
    updated_at                    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_robot_alarm_observation_state_singleton CHECK (singleton_key = true)
);

COMMIT;
