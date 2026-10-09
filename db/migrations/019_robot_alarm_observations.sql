-- TKG-observed history of successful RDS alarm snapshots.
-- A failed or missing read must not mark an open observation cleared.
BEGIN;

CREATE TABLE IF NOT EXISTS integration.robot_alarm_observations (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    fingerprint         text NOT NULL,
    robot_id            text NOT NULL,
    severity            text NOT NULL,
    alarm_code          text NOT NULL,
    description         text NULL,
    occurred_at_raw     text NULL,
    occurrence_count    integer NULL,
    first_seen_at       timestamptz NOT NULL,
    last_seen_at        timestamptz NOT NULL,
    cleared_at          timestamptz NULL,
    last_correlation_id uuid NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_robot_alarm_open_fingerprint
    ON integration.robot_alarm_observations (fingerprint)
    WHERE cleared_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_robot_alarm_observations_last_seen
    ON integration.robot_alarm_observations (last_seen_at DESC);

CREATE TABLE IF NOT EXISTS integration.robot_alarm_read_failures (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    correlation_id  uuid NOT NULL,
    observed_at     timestamptz NOT NULL,
    error_code      text NOT NULL,
    message         text NOT NULL
);

COMMIT;
