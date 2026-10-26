using System.Data;
using Npgsql;

namespace TkgPda.Api;

/// <summary>
/// Demo mode has no physical robot movement. When an in-flight simulated task
/// is cancelled, its pre-transport slot state is therefore deterministic and
/// any physical reconciliation hold created by the production workflow can be
/// closed automatically.
/// </summary>
public sealed class DemoCancellationRecovery(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    ILogger<DemoCancellationRecovery> logger)
{
    public async Task<int> ReconcileAsync(Guid? requestId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var reconciled = await ReconcileAsync(
            connection,
            requestId,
            timeProvider.GetUtcNow(),
            cancellationToken);

        if (reconciled > 0)
        {
            logger.LogInformation(
                "Demo cancellation recovery released {Count} cancelled transport reservation(s).",
                reconciled);
        }

        return reconciled;
    }

    internal static async Task<int> ReconcileAsync(
        NpgsqlConnection connection,
        Guid? requestId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var candidates = new List<CancelledDemoRequest>();
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT r.id, tt.code, r.source_slot_id, r.destination_slot_id,
                                r.part_id, r.container_type_id
                         FROM ops.transport_requests r
                         JOIN master.task_types tt ON tt.id = r.task_type_id
                         WHERE r.status = 'CANCELLED'
                           AND (@request_id IS NULL OR r.id = @request_id)
                           AND EXISTS (
                               SELECT 1
                               FROM ops.store_reservations sr
                               WHERE sr.transport_request_id = r.id
                                 AND sr.released_at IS NULL
                                 AND sr.reconciliation_required
                           )
                         ORDER BY r.id
                         FOR UPDATE OF r SKIP LOCKED
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("request_id", (object?)requestId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new CancelledDemoRequest(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5)));
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.SourceSlotId is Guid sourceSlotId)
            {
                var sourceState = candidate.TaskType switch
                {
                    "BRING_EMPTY" => new DemoSlotState("OCCUPIED", "EMPTY", null, candidate.ContainerTypeId),
                    "BRING_FULL" or "REMOVE_FULL" => new DemoSlotState("OCCUPIED", "FULL", candidate.PartId, candidate.ContainerTypeId),
                    "REMOVE_EMPTY" => new DemoSlotState("OCCUPIED", "EMPTY", candidate.PartId, candidate.ContainerTypeId),
                    _ => throw new InvalidOperationException($"Unsupported demo task type '{candidate.TaskType}'.")
                };
                await RestoreSlotIfUnclaimedAsync(
                    connection,
                    transaction,
                    candidate.Id,
                    sourceSlotId,
                    sourceState,
                    cancellationToken);
            }

            if (candidate.DestinationSlotId is Guid destinationSlotId
                && destinationSlotId != candidate.SourceSlotId)
            {
                await RestoreSlotIfUnclaimedAsync(
                    connection,
                    transaction,
                    candidate.Id,
                    destinationSlotId,
                    new DemoSlotState("VACANT", "UNKNOWN", null, null),
                    cancellationToken);
            }

            await using (var release = new NpgsqlCommand(
                             """
                             UPDATE ops.store_reservations
                             SET released_at = @occurred_at,
                                 reconciliation_required = false,
                                 reconciled_at = NULL,
                                 reconciled_by = NULL,
                                 reconciled_device_id = NULL
                             WHERE transport_request_id = @request_id
                               AND released_at IS NULL
                               AND reconciliation_required
                             """,
                             connection,
                             transaction))
            {
                release.Parameters.AddWithValue("request_id", candidate.Id);
                release.Parameters.AddWithValue("occurred_at", occurredAt);
                await release.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var request = new NpgsqlCommand(
                             """
                             UPDATE ops.transport_requests
                             SET store_endpoint_reservation_active = false,
                                 updated_at = now()
                             WHERE id = @request_id
                             """,
                             connection,
                             transaction))
            {
                request.Parameters.AddWithValue("request_id", candidate.Id);
                await request.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var audit = new NpgsqlCommand(
                             """
                             INSERT INTO ops.transport_request_events (
                                 transport_request_id, event_type, old_status, new_status,
                                 message, data_json, created_at)
                             VALUES (
                                 @request_id, 'DEMO_CANCELLATION_RECONCILED', 'CANCELLED', 'CANCELLED',
                                 'Demo cancellation restored the deterministic pre-transport state and released the store endpoint.',
                                 jsonb_build_object('evidence_type', 'DEMO_NO_PHYSICAL_MOVEMENT'),
                                 @occurred_at)
                             """,
                             connection,
                             transaction))
            {
                audit.Parameters.AddWithValue("request_id", candidate.Id);
                audit.Parameters.AddWithValue("occurred_at", occurredAt);
                await audit.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return candidates.Count;
    }

    private static async Task RestoreSlotIfUnclaimedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid cancelledRequestId,
        Guid slotId,
        DemoSlotState state,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE ops.slot_runtime_state state
            SET occupancy_state = @occupancy,
                load_state = @load,
                current_part_id = @part_id,
                current_container_type_id = @container_type_id,
                updated_by = NULL,
                row_version = state.row_version + 1,
                updated_at = now()
            WHERE state.layout_slot_id = @slot_id
              AND NOT EXISTS (
                  SELECT 1
                  FROM ops.transport_requests active
                  WHERE active.id <> @request_id
                    AND active.status IN ('CREATED', 'VALIDATING', 'QUEUED', 'SENT_TO_RDS', 'RUNNING')
                    AND active.machine_slot_reservation_active
                    AND (active.source_slot_id = @slot_id OR active.destination_slot_id = @slot_id)
              )
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("request_id", cancelledRequestId);
        command.Parameters.AddWithValue("slot_id", slotId);
        command.Parameters.AddWithValue("occupancy", state.Occupancy);
        command.Parameters.AddWithValue("load", state.Load);
        command.Parameters.AddWithValue("part_id", (object?)state.PartId ?? DBNull.Value);
        command.Parameters.AddWithValue("container_type_id", (object?)state.ContainerTypeId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record CancelledDemoRequest(
        Guid Id,
        string TaskType,
        Guid? SourceSlotId,
        Guid? DestinationSlotId,
        Guid? PartId,
        Guid? ContainerTypeId);

    private sealed record DemoSlotState(
        string Occupancy,
        string Load,
        Guid? PartId,
        Guid? ContainerTypeId);
}
