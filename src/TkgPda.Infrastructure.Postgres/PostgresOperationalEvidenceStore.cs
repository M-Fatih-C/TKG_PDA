using Npgsql;
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

    private static string NormalizeOperation(string operationCode)
    {
        var code = operationCode.Trim().ToUpperInvariant();
        return Operations.Contains(code, StringComparer.Ordinal)
            ? code
            : throw new WorkflowRuleException("TASK_TYPE_NOT_FOUND", "Üretim bitiş önceliği yalnızca tanımlı taşıma işlemleri için tutulur.");
    }

    private static long? Seconds(TimeSpan? value) => value is null ? null : (long)Math.Round(value.Value.TotalSeconds);
}
