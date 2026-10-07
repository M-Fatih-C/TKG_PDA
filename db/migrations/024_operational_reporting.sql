-- Reporting indexes for the read-only operations history.
-- No new source of truth. Replay-safe.
BEGIN;

CREATE INDEX IF NOT EXISTS ix_transport_report_created
    ON ops.transport_requests (created_at DESC, id DESC);

CREATE INDEX IF NOT EXISTS ix_transport_report_machine_created
    ON ops.transport_requests (machine_id, created_at DESC, id DESC);

CREATE INDEX IF NOT EXISTS ix_transport_report_status_created
    ON ops.transport_requests (status, created_at DESC, id DESC);

DO $$
BEGIN
    IF to_regclass('auth.user_sessions') IS NOT NULL THEN
        CREATE INDEX IF NOT EXISTS ix_user_sessions_user_created
            ON auth.user_sessions (user_id, created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_user_sessions_created
            ON auth.user_sessions (created_at DESC);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_audit_device_created
    ON audit.audit_logs (device_id, created_at DESC)
    WHERE device_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_audit_action_created
    ON audit.audit_logs (action, created_at DESC);

COMMIT;
