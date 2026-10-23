using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

public sealed class PostgresOperationalEvidenceStore(NpgsqlDataSource dataSource) : IOperationalEvidenceStore
{
    private static readonly string[] Operations = ["BRING_EMPTY", "BRING_FULL", "REMOVE_FULL", "REMOVE_EMPTY"];

    public async Task<TransportEvidence?> ReadTransportEvidenceAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var exists = new NpgsqlCommand(
            "SELECT 1 FROM ops.transport_requests WHERE id = @id",
            connection);
        exists.Parameters.AddWithValue("id", requestId);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null) return null;

        var events = new List<(string EventType, DateTimeOffset OccurredAt)>();
        var failures = new List<TransportFailureEvidence>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT event_type, created_at, actor_user_id, message
            FROM ops.transport_request_events
            WHERE transport_request_id = @id
            ORDER BY created_at, id
            """,
            connection))
        {
            command.Parameters.AddWithValue("id", requestId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var eventType = reader.GetString(0);
                var occurredAt = reader.GetFieldValue<DateTimeOffset>(1);
                events.Add((eventType, occurredAt));
                if (eventType is TransportEventTypes.RequestFailed or TransportEventTypes.RequestCancelled)
                {
                    failures.Add(new TransportFailureEvidence(
                        eventType,
                        occurredAt,
                        reader.IsDBNull(2) ? null : reader.GetGuid(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }
        }

        var durations = TransportDurationMetrics.Calculate(events);
        RobotIntegrationEvidence? robot = null;
        await using (var command = new NpgsqlCommand(
            """
            SELECT rds_task_record_id, rds_status, ended_reason, started_at, ended_at
            FROM integration.rds_tasks
            WHERE transport_request_id = @id
            """,
            connection))
        {
            command.Parameters.AddWithValue("id", requestId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                robot = new RobotIntegrationEvidence(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
            }
        }

        return new TransportEvidence(
            requestId,
            Seconds(durations.Total),
            Seconds(durations.Warehouse),
            Seconds(durations.Robot),
            failures,
            robot);
    }

    public async Task<IReadOnlyList<ProductionEndPriority>> ListProductionEndPrioritiesAsync(CancellationToken cancellationToken)
    {
        var configured = new Dictionary<string, (int Priority, DateTimeOffset UpdatedAt)>(StringComparer.Ordinal);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT operation_code, priority, updated_at FROM master.production_end_priorities",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            configured[reader.GetString(0)] = (reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2));
        }

        return Operations.Select(code => configured.TryGetValue(code, out var row)
            ? new ProductionEndPriority(code, row.Priority, row.UpdatedAt)
            : new ProductionEndPriority(code, null, null)).ToArray();
    }

    public async Task SetProductionEndPriorityAsync(
        string operationCode,
        int? priority,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var code = NormalizeOperation(operationCode);
        if (priority is < 1)
        {
            throw new WorkflowRuleException("INVALID_PRIORITY", "Üretim bitiş önceliği 1 veya daha büyük olmalıdır.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (priority is null)
        {
            await using var delete = new NpgsqlCommand(
                "DELETE FROM master.production_end_priorities WHERE operation_code = @code",
                connection);
            delete.Parameters.AddWithValue("code", code);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            await InsertPriorityAuditAsync(connection, code, null, actorUserId, cancellationToken);
            return;
        }

        await using var upsert = new NpgsqlCommand(
            """
            INSERT INTO master.production_end_priorities (operation_code, priority, updated_at, updated_by)
            VALUES (@code, @priority, clock_timestamp(), @actor)
            ON CONFLICT (operation_code) DO UPDATE
            SET priority = EXCLUDED.priority,
                updated_at = clock_timestamp(),
                updated_by = EXCLUDED.updated_by
            """,
            connection);
        upsert.Parameters.AddWithValue("code", code);
        upsert.Parameters.AddWithValue("priority", priority.Value);
        upsert.Parameters.AddWithValue("actor", actorUserId);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
        await InsertPriorityAuditAsync(connection, code, priority, actorUserId, cancellationToken);
    }

    private static async Task InsertPriorityAuditAsync(
        NpgsqlConnection connection,
        string operationCode,
        int? priority,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_logs (actor_user_id, action, entity_type, entity_id, after_json)
            VALUES (@actor, 'PRODUCTION_END_PRIORITY_SET', 'PRODUCTION_END_PRIORITY', @code,
                    jsonb_build_object('operationCode', @code, 'priority', to_jsonb(@priority)))
            """,
            connection);
        audit.Parameters.AddWithValue("actor", actorUserId);
        audit.Parameters.AddWithValue("code", operationCode);
        audit.Parameters.Add("priority", NpgsqlDbType.Integer).Value = priority.HasValue ? priority.Value : DBNull.Value;
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RequireProductionEndPriorityAsync(string operationCode, CancellationToken cancellationToken)
    {
        var code = NormalizeOperation(operationCode);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT priority FROM master.production_end_priorities WHERE operation_code = @code",
            connection);
        command.Parameters.AddWithValue("code", code);
        if (await command.ExecuteScalarAsync(cancellationToken) is int priority) return priority;
        throw new WorkflowRuleException(
            "PRODUCTION_END_PRIORITY_NOT_CONFIGURED",
            $"Üretimi Bitir için {code} önceliği tanımlanmamış.");
    }

    public async Task<IReadOnlyList<ProductionEndAreaPriority>> ListProductionEndAreaPrioritiesAsync(CancellationToken cancellationToken)
    {
        var rows = new List<ProductionEndAreaPriority>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            WITH eligible_parts AS (
                SELECT pg.id AS product_group_id, pg.main_part_id AS part_id, 'MAIN_PART'::varchar(30) AS part_role
                FROM master.product_groups pg
                WHERE pg.is_active
                UNION ALL
                SELECT gpp.product_group_id, gpp.part_id, gpp.relation_type
                FROM master.product_group_parts gpp
                JOIN master.product_groups pg ON pg.id = gpp.product_group_id AND pg.is_active
                WHERE gpp.is_active AND gpp.part_id <> pg.main_part_id
            )
            SELECT m.id, m.code, m.display_name,
                   v.id, v.code, v.name,
                   pg.id, main_part.part_code,
                   l.id, l.code, l.name,
                   ls.id, ls.code, ls.display_name,
                   p.id, p.part_code, ep.part_role,
                   configured.priority, configured.updated_at
            FROM eligible_parts ep
            JOIN master.product_groups pg ON pg.id = ep.product_group_id
            JOIN master.parts main_part ON main_part.id = pg.main_part_id
            JOIN master.parts p ON p.id = ep.part_id AND p.is_active
            JOIN master.variants v ON v.id = pg.variant_id AND v.is_active
            JOIN master.machines m ON m.id = v.machine_id AND m.is_active
            JOIN master.variant_layout_assignments assignment ON assignment.variant_id = v.id
            JOIN master.layouts l ON l.id = assignment.layout_id AND l.machine_id = m.id
            JOIN master.layout_slots ls ON ls.layout_id = l.id AND ls.is_enabled
            JOIN master.layout_slot_parts lsp ON lsp.layout_slot_id = ls.id AND lsp.part_id = ep.part_id
            LEFT JOIN master.production_end_area_priorities configured
                   ON configured.product_group_id = pg.id
                  AND configured.layout_slot_id = ls.id
                  AND configured.part_id = ep.part_id
            WHERE ls.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
            ORDER BY m.code, v.display_order, v.code, pg.display_order, main_part.part_code,
                     CASE WHEN ep.part_role = 'MAIN_PART' THEN 0 ELSE 1 END,
                     p.part_code, l.code, ls.display_order, ls.code
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ProductionEndAreaPriority(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetGuid(3), reader.GetString(4), reader.GetString(5),
                reader.GetGuid(6), reader.GetString(7),
                reader.GetGuid(8), reader.GetString(9), reader.GetString(10),
                reader.GetGuid(11), reader.GetString(12), reader.GetString(13),
                reader.GetGuid(14), reader.GetString(15), reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetInt32(17),
                reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18)));
        }

        return rows;
    }

    public async Task SetProductionEndAreaPriorityAsync(
        Guid productGroupId,
        Guid layoutSlotId,
        Guid partId,
        int? priority,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        if (priority is < 1)
        {
            throw new WorkflowRuleException("INVALID_PRIORITY", "Üretim bitiş önceliği 1 veya daha büyük olmalıdır.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var eligible = new NpgsqlCommand(
            """
            SELECT 1
            FROM master.product_groups pg
            JOIN master.variants v ON v.id = pg.variant_id AND v.is_active
            JOIN master.variant_layout_assignments assignment ON assignment.variant_id = v.id
            JOIN master.layouts l ON l.id = assignment.layout_id AND l.machine_id = v.machine_id
            JOIN master.machines m ON m.id = v.machine_id AND m.is_active
            JOIN master.layout_slots ls ON ls.layout_id = l.id
            JOIN master.layout_slot_parts lsp ON lsp.layout_slot_id = ls.id
            JOIN master.parts p ON p.id = lsp.part_id AND p.is_active
            WHERE pg.id = @group
              AND ls.id = @slot
              AND lsp.part_id = @part
              AND pg.is_active
              AND ls.is_enabled
              AND ls.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
              AND (
                  pg.main_part_id = @part
                  OR EXISTS (
                      SELECT 1
                      FROM master.product_group_parts gpp
                      WHERE gpp.product_group_id = pg.id
                        AND gpp.part_id = @part
                        AND gpp.is_active))
            LIMIT 1
            """,
            connection,
            transaction))
        {
            eligible.Parameters.AddWithValue("group", productGroupId);
            eligible.Parameters.AddWithValue("slot", layoutSlotId);
            eligible.Parameters.AddWithValue("part", partId);
            if (await eligible.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new WorkflowRuleException(
                    "PRODUCTION_END_AREA_MAPPING_INVALID",
                    "Seçilen makine, varyant, parça ve alan eşleşmesi yetkili saha tanımlarında bulunamadı.");
            }
        }

        if (priority is null)
        {
            await using var delete = new NpgsqlCommand(
                """
                DELETE FROM master.production_end_area_priorities
                WHERE product_group_id = @group AND layout_slot_id = @slot AND part_id = @part
                """,
                connection,
                transaction);
            delete.Parameters.AddWithValue("group", productGroupId);
            delete.Parameters.AddWithValue("slot", layoutSlotId);
            delete.Parameters.AddWithValue("part", partId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var upsert = new NpgsqlCommand(
                """
                INSERT INTO master.production_end_area_priorities
                    (product_group_id, layout_slot_id, part_id, priority, updated_at, updated_by)
                VALUES (@group, @slot, @part, @priority, clock_timestamp(), @actor)
                ON CONFLICT (product_group_id, layout_slot_id, part_id) DO UPDATE
                SET priority = EXCLUDED.priority,
                    updated_at = clock_timestamp(),
                    updated_by = EXCLUDED.updated_by
                """,
                connection,
                transaction);
            upsert.Parameters.AddWithValue("group", productGroupId);
            upsert.Parameters.AddWithValue("slot", layoutSlotId);
            upsert.Parameters.AddWithValue("part", partId);
            upsert.Parameters.AddWithValue("priority", priority.Value);
            upsert.Parameters.AddWithValue("actor", actorUserId);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var audit = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_logs (actor_user_id, action, entity_type, entity_id, after_json)
            VALUES (@actor, 'PRODUCTION_END_AREA_PRIORITY_SET', 'PRODUCTION_END_AREA_PRIORITY',
                    concat(@group::text, ':', @slot::text, ':', @part::text),
                    jsonb_build_object(
                        'productGroupId', @group,
                        'layoutSlotId', @slot,
                        'partId', @part,
                        'priority', to_jsonb(@priority)))
            """,
            connection,
            transaction))
        {
            audit.Parameters.AddWithValue("actor", actorUserId);
            audit.Parameters.AddWithValue("group", productGroupId);
            audit.Parameters.AddWithValue("slot", layoutSlotId);
            audit.Parameters.AddWithValue("part", partId);
            audit.Parameters.Add("priority", NpgsqlDbType.Integer).Value = priority.HasValue ? priority.Value : DBNull.Value;
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string NormalizeOperation(string operationCode)
    {
        var code = operationCode.Trim().ToUpperInvariant();
        return Operations.Contains(code, StringComparer.Ordinal)
            ? code
            : throw new WorkflowRuleException("TASK_TYPE_NOT_FOUND", "Üretim bitiş önceliği yalnızca tanımlı taşıma işlemleri için tutulur.");
    }

    private static long? Seconds(TimeSpan? value) => value is null ? null : (long)Math.Round(value.Value.TotalSeconds);
}
