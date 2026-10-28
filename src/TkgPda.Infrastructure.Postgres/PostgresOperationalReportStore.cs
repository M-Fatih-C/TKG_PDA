using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed class PostgresOperationalReportStore(NpgsqlDataSource dataSource) : IOperationalReportStore
{
    private const string TaskFrom = """
        FROM ops.transport_requests r
        JOIN master.machines m ON m.id = r.machine_id
        JOIN master.task_types tt ON tt.id = r.task_type_id
        LEFT JOIN master.variants v ON v.id = r.variant_id
        LEFT JOIN master.navigation_profiles n ON n.id = r.navigation_id
        LEFT JOIN auth.users u ON u.id = r.requested_by
        LEFT JOIN master.parts p ON p.id = r.part_id
        LEFT JOIN master.container_types ct ON ct.id = r.container_type_id
        LEFT JOIN master.layout_slots src ON src.id = r.source_slot_id
        LEFT JOIN master.layout_slots dst ON dst.id = r.destination_slot_id
        LEFT JOIN LATERAL (
            SELECT e.device_id
            FROM ops.transport_request_events e
            WHERE e.transport_request_id = r.id AND e.event_type = 'REQUEST_CREATED'
            ORDER BY e.created_at, e.id
            LIMIT 1) created ON true
        LEFT JOIN audit.client_devices d ON d.id = created.device_id
        LEFT JOIN integration.rds_tasks rds ON rds.transport_request_id = r.id
        LEFT JOIN LATERAL (
            SELECT e.created_at, e.actor_user_id, e.device_id, e.data_json
            FROM ops.transport_request_events e
            WHERE e.transport_request_id = r.id AND e.event_type = 'REQUEST_CANCELLED'
            ORDER BY e.created_at DESC, e.id DESC
            LIMIT 1) cancel ON true
        LEFT JOIN auth.users cu ON cu.id = cancel.actor_user_id
        LEFT JOIN audit.client_devices cd ON cd.id = cancel.device_id
        LEFT JOIN LATERAL (
            SELECT pal.placed_by, pal.placed_device_id, area.display_name
            FROM ops.preparation_pallets pal
            JOIN ops.preparation_areas area ON area.id = pal.preparation_area_id
            WHERE pal.transport_request_id = r.id
            ORDER BY pal.placed_at, pal.id
            LIMIT 1) prep ON true
        LEFT JOIN auth.users pu ON pu.id = prep.placed_by
        LEFT JOIN audit.client_devices pd ON pd.id = prep.placed_device_id
        """;

    private const string TaskWhere = """
        WHERE r.created_at >= @from AND r.created_at < @to
          AND (@machine_id IS NULL OR r.machine_id = @machine_id)
          AND (@employee_no IS NULL OR lower(btrim(u.employee_no)) = lower(btrim(@employee_no)))
          AND (@device_id IS NULL OR created.device_id = @device_id)
          AND (@status IS NULL OR r.status = @status)
          AND (@operation IS NULL OR tt.code = @operation)
          AND (@part IS NULL OR p.part_code ILIKE @part ESCAPE '\' OR p.description ILIKE @part ESCAPE '\')
          AND (@request_no IS NULL OR r.request_no = @request_no)
          AND (@agv_id IS NULL OR rds.agv_id = @agv_id)
          AND (@errors_only = false OR r.status = 'FAILED')
          AND (@cancelled_only = false OR r.status = 'CANCELLED')
        """;

    private const string TaskSelect = """
        SELECT r.id, r.request_no, r.created_at, r.requested_by, u.full_name, u.employee_no, u.username,
               created.device_id, d.device_code, d.name,
               m.id, m.code, m.display_name, v.code, n.code, tt.code,
               p.part_code, p.description, ct.code, src.code, COALESCE(dst.code, r.destination_business_code),
               r.status, r.failure_code, r.failure_message,
               CASE
                   WHEN cancel.created_at IS NULL THEN NULL
                   WHEN jsonb_exists(cancel.data_json, 'predecessor_request_id')
                        AND EXISTS (SELECT 1 FROM ops.production_end_run_items i WHERE i.transport_request_id = r.id)
                       THEN 'PRODUCTION_END'
                   WHEN jsonb_exists(cancel.data_json, 'predecessor_request_id') THEN 'DEPENDENCY'
                   WHEN cancel.data_json ->> 'cancellation_source' = 'RDS_CONFIRMED' THEN 'SYSTEM'
                   WHEN cancel.actor_user_id IS NOT NULL THEN 'OPERATOR'
                   ELSE 'SYSTEM'
               END,
               cancel.created_at, cancel.actor_user_id, cu.full_name, cu.employee_no, cancel.device_id, cd.device_code,
               rds.rds_task_record_id, rds.agv_id, rds.rds_status, rds.ended_reason,
               (SELECT min(e.created_at) FROM ops.transport_request_events e WHERE e.transport_request_id = r.id AND e.event_type = 'REQUEST_CREATED'),
               (SELECT min(e.created_at) FROM ops.transport_request_events e WHERE e.transport_request_id = r.id AND e.event_type = 'WAREHOUSE_PREPARATION_STARTED'),
               (SELECT min(e.created_at) FROM ops.transport_request_events e WHERE e.transport_request_id = r.id AND e.event_type = 'WAREHOUSE_PREPARATION_READY'),
               (SELECT min(e.created_at) FROM ops.transport_request_events e WHERE e.transport_request_id = r.id AND e.event_type = 'ROBOT_TASK_DISPATCHED'),
               (SELECT min(e.created_at) FROM ops.transport_request_events e WHERE e.transport_request_id = r.id AND e.event_type = 'ROBOT_TASK_STARTED'),
               (SELECT min(e.created_at) FROM ops.transport_request_events e WHERE e.transport_request_id = r.id AND e.event_type = 'REQUEST_COMPLETED'),
               (SELECT min(e.created_at) FROM ops.transport_request_events e WHERE e.transport_request_id = r.id AND e.event_type = 'REQUEST_FAILED'),
               (SELECT min(e.created_at) FROM ops.transport_request_events e WHERE e.transport_request_id = r.id AND e.event_type = 'ROBOT_TASK_COMPLETED'),
               pu.full_name, pu.employee_no, prep.placed_device_id, pd.device_code, prep.display_name,
               r.correlation_id
        """;

    public async Task<OperationalReportPage<TaskReportRow>> ListTasksAsync(TaskReportQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 50 : query.PageSize, 1, 500);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var total = await CountTasksAsync(connection, query, cancellationToken);
        var items = new List<TaskReportRow>();
        await using var command = new NpgsqlCommand(
            $"""
            {TaskSelect}
            {TaskFrom}
            {TaskWhere}
            ORDER BY r.created_at DESC, r.id DESC
            LIMIT @limit OFFSET @offset
            """,
            connection);
        BindTask(command, query);
        command.Parameters.AddWithValue("limit", pageSize);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadTask(reader));
        return new OperationalReportPage<TaskReportRow>(items, total, page, pageSize);
    }

    public async Task<TaskReportDetail?> ReadTaskAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        TaskReportRow? row = null;
        await using (var command = new NpgsqlCommand(
            $"""
            {TaskSelect}
            {TaskFrom}
            WHERE r.id = @id
            """,
            connection))
        {
            command.Parameters.AddWithValue("id", requestId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) row = ReadTask(reader);
        }

        if (row is null) return null;
        var timeline = new List<TaskTimelineEntry>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT e.event_type, e.created_at, u.full_name, u.employee_no, d.device_code, e.data_json ->> 'message'
            FROM ops.transport_request_events e
            LEFT JOIN auth.users u ON u.id = e.actor_user_id
            LEFT JOIN audit.client_devices d ON d.id = e.device_id
            WHERE e.transport_request_id = @id
            ORDER BY e.created_at, e.id
            """,
            connection))
        {
            command.Parameters.AddWithValue("id", requestId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var eventType = reader.GetString(0);
                timeline.Add(new TaskTimelineEntry(
                    reader.GetFieldValue<DateTimeOffset>(1),
                    OperationalReportText.EventTitle(eventType),
                    ActorLine(Text(reader, 2), Text(reader, 3), Text(reader, 4), eventType == "ROBOT_TASK_STARTED" ? row.AgvId : null)));
            }
        }

        return new TaskReportDetail(row, timeline);
    }

    public async Task<SessionReportPage> ListSessionsAsync(SessionReportQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 50 : query.PageSize, 1, 500);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var where = """
            WHERE s.created_at >= @from AND s.created_at < @to
              AND (@employee_no IS NULL OR lower(btrim(u.employee_no)) = lower(btrim(@employee_no)))
              AND (@device_id IS NULL OR s.device_id = @device_id)
              AND (@machine_id IS NULL OR (snap.machine_id IS NOT NULL AND snap.machine_id = @machine_id::text))
              AND (@activity IS NULL
                   OR (@activity = 'active'
                       AND s.revoked_at IS NULL
                       AND s.lease_expires_at > clock_timestamp()
                       AND s.token_expires_at > clock_timestamp())
                   OR (@activity = 'ended'
                       AND (s.revoked_at IS NOT NULL
                            OR s.lease_expires_at <= clock_timestamp()
                            OR s.token_expires_at <= clock_timestamp())))
              AND (@end_reason IS NULL OR COALESCE(
                    s.revocation_reason,
                    CASE
                        WHEN s.token_expires_at <= clock_timestamp() THEN 'TOKEN_EXPIRED'
                        WHEN s.lease_expires_at <= clock_timestamp() THEN 'LEASE_EXPIRED'
                    END) = @end_reason)
            """;
        var fromSql = """
            FROM auth.user_sessions s
            JOIN auth.users u ON u.id = s.user_id
            JOIN audit.client_devices d ON d.id = s.device_id
            LEFT JOIN LATERAL (
                SELECT a.after_json ->> 'machineId' AS machine_id,
                       a.after_json ->> 'machineCode' AS machine_code
                FROM audit.audit_logs a
                WHERE a.action = 'AUTH_SESSION_CREATED'
                  AND a.entity_type = 'AUTH_SESSION'
                  AND a.entity_id = s.id::text
                ORDER BY a.created_at, a.id
                LIMIT 1) snap ON true
            """;

        int total;
        await using (var count = new NpgsqlCommand(
            $"""
            SELECT count(*)
            {fromSql}
            {where}
            """,
            connection))
        {
            BindSession(count, query);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<SessionReportRow>();
        await using (var command = new NpgsqlCommand(
            $"""
            SELECT s.id, u.id, u.full_name, u.employee_no, u.username, s.created_at,
                   CASE
                       WHEN s.revoked_at IS NOT NULL THEN s.revoked_at
                       WHEN s.token_expires_at <= clock_timestamp() OR s.lease_expires_at <= clock_timestamp()
                           THEN LEAST(s.token_expires_at, s.lease_expires_at)
                       ELSE NULL
                   END AS effective_ended_at,
                   COALESCE(
                       s.revocation_reason,
                       CASE
                           WHEN s.token_expires_at <= clock_timestamp() THEN 'TOKEN_EXPIRED'
                           WHEN s.lease_expires_at <= clock_timestamp() THEN 'LEASE_EXPIRED'
                       END) AS effective_end_reason,
                   d.id, d.device_code, d.name, d.device_type,
                   snap.machine_id, snap.machine_code,
                   COALESCE((
                       SELECT jsonb_exists(a.after_json, 'machineId')
                       FROM audit.audit_logs a
                       WHERE a.action = 'AUTH_SESSION_CREATED' AND a.entity_type = 'AUTH_SESSION' AND a.entity_id = s.id::text
                       ORDER BY a.created_at, a.id
                       LIMIT 1), false),
                   COALESCE((
                       SELECT string_agg(r.code, ',' ORDER BY r.code)
                       FROM auth.user_roles ur
                       JOIN auth.roles r ON r.id = ur.role_id
                       WHERE ur.user_id = u.id), '')
            {fromSql}
            {where}
            ORDER BY s.created_at DESC, s.id DESC
            LIMIT @limit OFFSET @offset
            """,
            connection))
        {
            BindSession(command, query);
            command.Parameters.AddWithValue("limit", pageSize);
            command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var ended = Timestamp(reader, 6);
                var reason = Text(reader, 7);
                var active = ended is null;
                var snapshotExists = reader.GetBoolean(14);
                var machineIdText = Text(reader, 12);
                Guid? machineId = Guid.TryParse(machineIdText, out var parsed) ? parsed : null;
                var machineCode = Text(reader, 13);
                var machineState = !snapshotExists ? "UNKNOWN" : machineId is null && string.IsNullOrWhiteSpace(machineCode) ? "UNBOUND" : "RECORDED";
                var roles = reader.GetString(15);
                items.Add(new SessionReportRow(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    Text(reader, 3),
                    reader.GetString(4),
                    string.IsNullOrEmpty(roles) ? [] : roles.Split(','),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    ended,
                    active ? null : OperationalReportText.Seconds(reader.GetFieldValue<DateTimeOffset>(5), ended),
                    reader.GetGuid(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    machineState,
                    machineId,
                    machineCode,
                    OperationalReportText.SessionEndLabel(reason, active),
                    reason,
                    active ? null : OperationalReportText.SessionEndLabel(reason, false)));
            }
        }

        var blocked = await ReadBlockedAsync(connection, query, cancellationToken);
        return new SessionReportPage(items, total, page, pageSize, blocked);
    }

    public async Task<OperationsSummary> ReadSummaryAsync(DateTimeOffset from, DateTimeOffset to, Guid? machineId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        int total = 0, completed = 0, failed = 0, cancelled = 0, active = 0;
        double? avgTotal = null, avgWarehouse = null, avgRobot = null;
        await using (var command = new NpgsqlCommand(
            """
            SELECT count(*)::int,
                   count(*) FILTER (WHERE r.status = 'COMPLETED')::int,
                   count(*) FILTER (WHERE r.status = 'FAILED')::int,
                   count(*) FILTER (WHERE r.status = 'CANCELLED')::int,
                   count(*) FILTER (WHERE r.status IN ('CREATED','VALIDATING','QUEUED','SENT_TO_RDS','RUNNING'))::int,
                   avg(EXTRACT(EPOCH FROM (times.done_at - times.created_event)))
                       FILTER (WHERE times.created_event IS NOT NULL AND times.done_at IS NOT NULL),
                   avg(EXTRACT(EPOCH FROM (times.ready_at - times.created_event)))
                       FILTER (WHERE times.created_event IS NOT NULL AND times.ready_at IS NOT NULL),
                   avg(EXTRACT(EPOCH FROM (times.robot_done - times.robot_started)))
                       FILTER (WHERE times.robot_started IS NOT NULL AND times.robot_done IS NOT NULL)
            FROM ops.transport_requests r
            LEFT JOIN LATERAL (
                SELECT min(e.created_at) FILTER (WHERE e.event_type = 'REQUEST_CREATED') AS created_event,
                       min(e.created_at) FILTER (WHERE e.event_type = 'REQUEST_COMPLETED') AS done_at,
                       min(e.created_at) FILTER (WHERE e.event_type = 'WAREHOUSE_PREPARATION_READY') AS ready_at,
                       min(e.created_at) FILTER (WHERE e.event_type = 'ROBOT_TASK_STARTED') AS robot_started,
                       min(e.created_at) FILTER (WHERE e.event_type = 'ROBOT_TASK_COMPLETED') AS robot_done
                FROM ops.transport_request_events e
                WHERE e.transport_request_id = r.id) times ON true
            WHERE r.created_at >= @from AND r.created_at < @to
              AND (@machine_id IS NULL OR r.machine_id = @machine_id)
            """,
            connection))
        {
            BindWindow(command, from, to, machineId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                total = reader.GetInt32(0);
                completed = reader.GetInt32(1);
                failed = reader.GetInt32(2);
                cancelled = reader.GetInt32(3);
                active = reader.GetInt32(4);
                avgTotal = Double(reader, 5);
                avgWarehouse = Double(reader, 6);
                avgRobot = Double(reader, 7);
            }
        }

        var finished = completed + failed + cancelled;
        var rate = finished == 0 ? (double?)null : completed / (double)finished;
        var byMachine = await GroupAsync(connection, from, to, machineId, "m.code", "m.display_name", """
            JOIN master.machines m ON m.id = r.machine_id
            """, "m.code, m.display_name", cancellationToken);
        var byOperation = await GroupAsync(connection, from, to, machineId, "tt.code", "tt.code", """
            JOIN master.task_types tt ON tt.id = r.task_type_id
            """, "tt.code", cancellationToken);
        var byUser = await GroupAsync(connection, from, to, machineId, "COALESCE(u.employee_no, u.username, 'sistem')", "COALESCE(u.full_name, 'Sistem')", """
            LEFT JOIN auth.users u ON u.id = r.requested_by
            """, "COALESCE(u.employee_no, u.username, 'sistem'), COALESCE(u.full_name, 'Sistem')", cancellationToken);
        var failures = new List<ReportCount>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT COALESCE(NULLIF(r.failure_code, ''), NULLIF(rds.ended_reason, ''), 'Bilinmeyen'),
                   count(*)::int
            FROM ops.transport_requests r
            LEFT JOIN integration.rds_tasks rds ON rds.transport_request_id = r.id
            WHERE r.created_at >= @from AND r.created_at < @to
              AND r.status = 'FAILED'
              AND (@machine_id IS NULL OR r.machine_id = @machine_id)
            GROUP BY 1
            ORDER BY count(*) DESC, 1
            LIMIT 10
            """,
            connection))
        {
            BindWindow(command, from, to, machineId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                failures.Add(new ReportCount(reader.GetString(0), reader.GetString(0), reader.GetInt32(1)));
            }
        }

        return new OperationsSummary(total, completed, failed, cancelled, active, rate, avgTotal, avgWarehouse, avgRobot, byMachine, LabelOperations(byOperation), byUser, failures);
    }

    private static IReadOnlyList<ReportCount> LabelOperations(IReadOnlyList<ReportCount> rows) =>
        rows.Select(row => row with { Label = OperationalReportText.OperationLabel(row.Key) }).ToArray();

    private async Task<int> CountTasksAsync(NpgsqlConnection connection, TaskReportQuery query, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) {TaskFrom} {TaskWhere}", connection);
        BindTask(command, query);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<ReportCount>> GroupAsync(
        NpgsqlConnection connection,
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? machineId,
        string keySql,
        string labelSql,
        string joins,
        string groupSql,
        CancellationToken cancellationToken)
    {
        var rows = new List<ReportCount>();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT {keySql}, {labelSql}, count(*)::int
            FROM ops.transport_requests r
            {joins}
            WHERE r.created_at >= @from AND r.created_at < @to
              AND (@machine_id IS NULL OR r.machine_id = @machine_id)
            GROUP BY {groupSql}
            ORDER BY count(*) DESC, {keySql}
            LIMIT 20
            """,
            connection);
        BindWindow(command, from, to, machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new ReportCount(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return rows;
    }

    private static async Task<IReadOnlyList<BlockedLoginRow>> ReadBlockedAsync(
        NpgsqlConnection connection,
        SessionReportQuery query,
        CancellationToken cancellationToken)
    {
        var rows = new List<BlockedLoginRow>();
        await using var command = new NpgsqlCommand(
            """
            SELECT a.created_at, a.actor_user_id, u.full_name, COALESCE(a.after_json ->> 'employeeNo', u.employee_no), u.username,
                   a.after_json ->> 'attemptedDevice', a.after_json ->> 'existingDeviceCode'
            FROM audit.audit_logs a
            LEFT JOIN auth.users u ON u.id = a.actor_user_id
            WHERE a.action = 'AUTH_LOGIN_BLOCKED_ACTIVE_SESSION'
              AND a.created_at >= @from AND a.created_at < @to
              AND (@employee_no IS NULL OR lower(btrim(COALESCE(a.after_json ->> 'employeeNo', u.employee_no))) = lower(btrim(@employee_no)))
            ORDER BY a.created_at DESC, a.id DESC
            LIMIT 100
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = query.From.ToUniversalTime() });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = query.To.ToUniversalTime() });
        AddText(command, "employee_no", string.IsNullOrWhiteSpace(query.EmployeeNo) ? null : query.EmployeeNo.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BlockedLoginRow(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                Text(reader, 2),
                Text(reader, 3),
                Text(reader, 4),
                Text(reader, 5),
                Text(reader, 6),
                "Bu sicil başka bir cihazda zaten aktifti."));
        }

        return rows;
    }

    private static TaskReportRow ReadTask(NpgsqlDataReader reader)
    {
        var operation = reader.GetString(15);
        var status = reader.GetString(21);
        var createdEvent = Timestamp(reader, 35);
        var readyEvent = Timestamp(reader, 37);
        var robotStarted = Timestamp(reader, 39);
        var completedEvent = Timestamp(reader, 40);
        var robotCompleted = Timestamp(reader, 42);
        var kind = Text(reader, 24);
        var preparationArea = Text(reader, 47);
        return new TaskReportRow(
            reader.GetGuid(0),
            reader.GetInt64(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            Uuid(reader, 3),
            Text(reader, 4),
            Text(reader, 5),
            Text(reader, 6),
            Uuid(reader, 7),
            Text(reader, 8),
            Text(reader, 9),
            reader.GetGuid(10),
            reader.GetString(11),
            reader.GetString(12),
            Text(reader, 13),
            Text(reader, 14),
            operation,
            OperationalReportText.OperationLabel(operation),
            Text(reader, 16),
            Text(reader, 17),
            Text(reader, 18),
            Text(reader, 19),
            Text(reader, 20),
            status,
            OperationalReportText.StatusLabel(status),
            status == "COMPLETED",
            Text(reader, 22),
            Text(reader, 23),
            status == "FAILED" ? OperationalReportText.FailureSummary(operation) : null,
            kind,
            OperationalReportText.CancellationLabel(kind),
            Timestamp(reader, 25),
            Uuid(reader, 26),
            Text(reader, 27),
            Text(reader, 28),
            Uuid(reader, 29),
            Text(reader, 30),
            Text(reader, 31),
            Text(reader, 32),
            reader.IsDBNull(33) ? null : reader.GetInt32(33),
            Text(reader, 34),
            createdEvent,
            Timestamp(reader, 36),
            readyEvent,
            Timestamp(reader, 38),
            robotStarted,
            completedEvent,
            Timestamp(reader, 41),
            OperationalReportText.Seconds(createdEvent, completedEvent),
            OperationalReportText.Seconds(createdEvent, readyEvent),
            robotStarted is null ? null : OperationalReportText.Seconds(robotStarted, robotCompleted),
            Text(reader, 43),
            Text(reader, 44),
            Uuid(reader, 45),
            Text(reader, 46),
            preparationArea,
            reader.GetGuid(48));
    }

    private static void BindTask(NpgsqlCommand command, TaskReportQuery query)
    {
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = query.From.ToUniversalTime() });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = query.To.ToUniversalTime() });
        AddUuid(command, "machine_id", query.MachineId);
        AddText(command, "employee_no", string.IsNullOrWhiteSpace(query.EmployeeNo) ? null : query.EmployeeNo.Trim());
        AddUuid(command, "device_id", query.DeviceId);
        AddText(command, "status", string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim());
        AddText(command, "operation", string.IsNullOrWhiteSpace(query.Operation) ? null : query.Operation.Trim());
        AddText(command, "part", string.IsNullOrWhiteSpace(query.Part) ? null : Like(query.Part.Trim()));
        command.Parameters.Add(new NpgsqlParameter("request_no", NpgsqlDbType.Bigint) { Value = query.RequestNo.HasValue ? query.RequestNo.Value : DBNull.Value });
        AddText(command, "agv_id", string.IsNullOrWhiteSpace(query.AgvId) ? null : query.AgvId.Trim());
        command.Parameters.AddWithValue("errors_only", query.ErrorsOnly);
        command.Parameters.AddWithValue("cancelled_only", query.CancelledOnly);
    }

    private static void BindSession(NpgsqlCommand command, SessionReportQuery query)
    {
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = query.From.ToUniversalTime() });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = query.To.ToUniversalTime() });
        AddText(command, "employee_no", string.IsNullOrWhiteSpace(query.EmployeeNo) ? null : query.EmployeeNo.Trim());
        AddUuid(command, "device_id", query.DeviceId);
        AddUuid(command, "machine_id", query.MachineId);
        AddText(command, "activity", string.IsNullOrWhiteSpace(query.Activity) ? null : query.Activity.Trim().ToLowerInvariant());
        AddText(command, "end_reason", string.IsNullOrWhiteSpace(query.EndReason) ? null : query.EndReason.Trim());
    }

    private static void BindWindow(NpgsqlCommand command, DateTimeOffset from, DateTimeOffset to, Guid? machineId)
    {
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from.ToUniversalTime() });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to.ToUniversalTime() });
        AddUuid(command, "machine_id", machineId);
    }

    private static string? ActorLine(string? name, string? employeeNo, string? deviceCode, string? robot)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(name)) parts.Add(name);
        if (!string.IsNullOrWhiteSpace(employeeNo)) parts.Add($"Sicil {employeeNo}");
        if (!string.IsNullOrWhiteSpace(deviceCode)) parts.Add(deviceCode);
        if (!string.IsNullOrWhiteSpace(robot)) parts.Add($"Robot: {robot}");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string Like(string value) =>
        "%" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value });

    private static void AddUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value.HasValue ? value.Value : DBNull.Value });

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Guid? Uuid(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    private static DateTimeOffset? Timestamp(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    private static double? Double(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
