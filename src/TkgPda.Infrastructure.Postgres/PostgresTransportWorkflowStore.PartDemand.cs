using System.Data;
using Npgsql;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresTransportWorkflowStore
{
    public async Task<PartDemandResult> CreateAsync(PartDemandCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var result = await CreatePartDemandWithinTransactionAsync(connection, transaction, command, batchId: null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<PartDemandResult>> CreateBatchAsync(PartDemandBatchCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var batchId = await InsertPartDemandBatchAsync(connection, transaction, command, cancellationToken);
        var results = new List<PartDemandResult>(command.PartIds.Count);
        foreach (var partId in command.PartIds)
        {
            var itemKey = $"{command.IdempotencyKey.Trim()}:{partId:N}";
            results.Add(await CreatePartDemandWithinTransactionAsync(
                connection,
                transaction,
                new PartDemandCommand(itemKey, command.ActorUserId, command.MachineId, partId, command.EventContext),
                batchId,
                cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    private static async Task<Guid> InsertPartDemandBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PartDemandBatchCommand command,
        CancellationToken cancellationToken)
    {
        await using var commandText = new NpgsqlCommand(
            """
            INSERT INTO ops.transport_batches (idempotency_key, request_origin, requested_by, machine_id)
            SELECT @key, 'API', @actor, @machine
            WHERE NOT EXISTS (
                SELECT 1 FROM ops.transport_batches WHERE idempotency_key = @key)
            RETURNING id
            """,
            connection,
            transaction);
        commandText.Parameters.AddWithValue("key", command.IdempotencyKey.Trim());
        commandText.Parameters.AddWithValue("actor", command.ActorUserId);
        commandText.Parameters.AddWithValue("machine", command.MachineId);
        if (await commandText.ExecuteScalarAsync(cancellationToken) is Guid created)
        {
            return created;
        }

        await using var existing = new NpgsqlCommand(
            "SELECT id FROM ops.transport_batches WHERE idempotency_key = @key",
            connection,
            transaction);
        existing.Parameters.AddWithValue("key", command.IdempotencyKey.Trim());
        return await existing.ExecuteScalarAsync(cancellationToken) is Guid found
            ? found
            : throw new WorkflowRuleException("IDEMPOTENCY_KEY_REUSED", "The part-demand batch key could not be reused.");
    }

    private static async Task<PartDemandResult> CreatePartDemandWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PartDemandCommand command,
        Guid? batchId,
        CancellationToken cancellationToken)
    {
        await LockIdempotencyKeyAsync(connection, transaction, command.IdempotencyKey.Trim(), cancellationToken);
        var existing = await FindByIdempotencyKeyAsync(connection, transaction, command.IdempotencyKey.Trim(), cancellationToken);
        if (existing is not null)
        {
            if (existing.MachineId != command.MachineId || existing.PartId != command.PartId)
            {
                throw new WorkflowRuleException("IDEMPOTENCY_KEY_REUSED", "The idempotency key belongs to a different part demand.");
            }

            return new PartDemandResult(existing, await FindPredecessorSnapshotAsync(connection, transaction, existing.Id, cancellationToken), WasIdempotentReplay: true);
        }

        var session = await LockActiveProductionAsync(connection, transaction, command.MachineId, cancellationToken);
        var containerTypeId = await RequireAltPartAsync(connection, transaction, session.ProductGroupId, command.PartId, cancellationToken);
        var removeKey = command.IdempotencyKey.Trim() + ":remove";
        var active = await LockOtherActiveDemandAsync(connection, transaction, command.MachineId, command.PartId, command.IdempotencyKey.Trim(), removeKey, cancellationToken);
        if (active is { } blocking)
        {
            throw new WorkflowRuleException(
                "PART_REQUEST_ALREADY_ACTIVE",
                $"Bu alt parça için aktif talep zaten var. Talep #{blocking}.");
        }

        var area = await LockPartAreaAsync(connection, transaction, session.LayoutId, command.PartId, cancellationToken);
        var plan = PartDemandRules.Decide(area.Occupancy, area.Load);
        TransportRequestSnapshot? predecessor = null;
        if (plan.Predecessor is { } removal)
        {
            var removalRequest = new CreateTransportRequest(
                removeKey,
                RequestOrigin.Api,
                command.ActorUserId,
                command.MachineId,
                session.VariantId,
                session.LayoutId,
                null,
                session.ProductGroupId,
                command.PartId,
                containerTypeId,
                removal,
                removal == TransportTaskType.RemoveEmpty ? area.SlotId : area.SlotId,
                null,
                100,
                RequiresPreparation: false)
            {
                // Source is the occupied machine area. Destination stays unresolved
                // for a full pallet: no customer return point is configured.
            };
            if (removal == TransportTaskType.RemoveEmpty)
            {
                removalRequest = removalRequest with { SourceSlotId = area.SlotId, DestinationSlotId = null };
            }

            var removed = await CreateAndQueueWithinTransactionAsync(
                connection, transaction, removalRequest, batchId, command.EventContext, cancellationToken);
            predecessor = removed.Request;
        }

        var bring = new CreateTransportRequest(
            command.IdempotencyKey.Trim(),
            RequestOrigin.Api,
            command.ActorUserId,
            command.MachineId,
            session.VariantId,
            session.LayoutId,
            null,
            session.ProductGroupId,
            command.PartId,
            containerTypeId,
            TransportTaskType.BringFull,
            null,
            predecessor is null ? null : area.SlotId,
            100,
            RequiresPreparation: true)
        {
            AllowsDependentMachineOccupancy = predecessor is not null
        };
        var created = await CreateAndQueueWithinTransactionAsync(
            connection,
            transaction,
            bring,
            batchId,
            command.EventContext,
            cancellationToken,
            predecessor?.Id);
        if (predecessor is not null)
        {
            await using var link = new NpgsqlCommand(
                """
                INSERT INTO ops.transport_request_dependencies (predecessor_request_id, successor_request_id)
                VALUES (@predecessor, @successor)
                ON CONFLICT (predecessor_request_id) DO NOTHING
                """,
                connection,
                transaction);
            link.Parameters.AddWithValue("predecessor", predecessor.Id);
            link.Parameters.AddWithValue("successor", created.Request.Id);
            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        return new PartDemandResult(created.Request, predecessor, created.WasIdempotentReplay);
    }

    private static async Task<TransportRequestSnapshot?> FindPredecessorSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid successorId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT predecessor_request_id
            FROM ops.transport_request_dependencies
            WHERE successor_request_id = @id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", successorId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid predecessorId
            ? await LockRequestAsync(connection, transaction, predecessorId, cancellationToken)
            : null;
    }

    private sealed record ProductionDemandContext(Guid SessionId, Guid VariantId, Guid LayoutId, Guid ProductGroupId);

    private sealed record PartArea(Guid SlotId, OccupancyState Occupancy, LoadState Load);

    private static async Task<ProductionDemandContext> LockActiveProductionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid machineId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT s.id, s.variant_id, s.product_group_id, m.active_layout_id
            FROM ops.production_sessions s
            JOIN ops.machine_runtime_state m ON m.machine_id = s.machine_id
            WHERE s.machine_id = @machine AND s.ended_at IS NULL
            FOR UPDATE OF s
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("machine", machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new WorkflowRuleException(
                "PRODUCTION_SESSION_NOT_FOUND",
                "Alt parça talebi için bu makinede aktif üretim oturumu yok.");
        }

        if (reader.IsDBNull(3))
        {
            throw new WorkflowRuleException(
                "ACTIVE_LAYOUT_REQUIRED",
                "Aktif üretim için makine yerleşimi seçili değil.");
        }

        return new ProductionDemandContext(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(3), reader.GetGuid(2));
    }

    private static async Task<Guid?> RequireAltPartAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid productGroupId,
        Guid partId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT container_type_id
            FROM master.product_group_parts
            WHERE product_group_id = @group AND part_id = @part AND relation_type = 'ALT_PART' AND is_active
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("group", productGroupId);
        command.Parameters.AddWithValue("part", partId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new WorkflowRuleException(
                "PART_NOT_ALT_PART",
                "İstenen parça aktif üretimin alt parçası değil.");
        }

        return reader.IsDBNull(0) ? null : reader.GetGuid(0);
    }

    private static async Task<long?> LockOtherActiveDemandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid machineId,
        Guid partId,
        string bringKey,
        string removeKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             SELECT request_no
             FROM ops.transport_requests
             WHERE machine_id = @machine
               AND part_id = @part
               AND status IN ({ActiveStatusesSql})
               AND idempotency_key <> @bring
               AND idempotency_key <> @remove
             ORDER BY request_no
             LIMIT 1
             FOR UPDATE
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("machine", machineId);
        command.Parameters.AddWithValue("part", partId);
        command.Parameters.AddWithValue("bring", bringKey);
        command.Parameters.AddWithValue("remove", removeKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long number ? number : null;
    }

    private static async Task<PartArea> LockPartAreaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid layoutId,
        Guid partId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT ls.id, rs.occupancy_state, rs.load_state
            FROM master.layout_slots ls
            JOIN master.layout_slot_parts lsp ON lsp.layout_slot_id = ls.id AND lsp.part_id = @part
            JOIN ops.slot_runtime_state rs ON rs.layout_slot_id = ls.id
            WHERE ls.layout_id = @layout
              AND ls.is_enabled
              AND ls.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
            ORDER BY ls.display_order, ls.code
            FOR UPDATE OF rs
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("layout", layoutId);
        command.Parameters.AddWithValue("part", partId);
        var rows = new List<PartArea>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PartArea(
                reader.GetGuid(0),
                WorkflowCode.ParseOccupancy(reader.GetString(1)),
                WorkflowCode.ParseLoad(reader.GetString(2))));
        }

        if (rows.Count == 0)
        {
            throw new WorkflowRuleException(
                "PART_SLOT_NOT_CONFIGURED",
                "Bu alt parça için makine alanı tanımlı değil.");
        }

        if (rows.Count(row => row.Occupancy == OccupancyState.Occupied) > 1)
        {
            throw new WorkflowRuleException(
                "PART_AREA_AMBIGUOUS",
                "Bu alt parça birden fazla dolu alanda duruyor. Hangisinin kaldırılacağı bilinmiyor.");
        }

        return rows.FirstOrDefault(row => row.Occupancy == OccupancyState.Occupied) ?? rows[0];
    }
}
