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
        // Production start/end and callbacks acquire machine before session/slot/request.
        await LockMachineRuntimeAsync(connection, transaction, command.MachineId, cancellationToken, requireConfigured: false);
        var result = await CreatePartDemandWithinTransactionAsync(connection, transaction, command, batchId: null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<PartDemandResult>> CreateBatchAsync(PartDemandBatchCommand command, CancellationToken cancellationToken = default)
    {
        var requestedParts = command.PartIds.ToArray();
        if (requestedParts.Length != requestedParts.Distinct().Count())
        {
            throw new WorkflowRuleException("PART_DEMAND_DUPLICATE", "Aynı alt parça toplu talepte bir kez seçilebilir.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockMachineRuntimeAsync(connection, transaction, command.MachineId, cancellationToken, requireConfigured: false);
        var key = command.IdempotencyKey.Trim();
        await LockIdempotencyKeyAsync(connection, transaction, key, cancellationToken);
        var existing = await FindPartDemandBatchAsync(connection, transaction, key, cancellationToken);
        Guid batchId;
        IReadOnlyList<Guid> parts;
        if (existing is not null)
        {
            if (!string.Equals(existing.Origin, "API", StringComparison.Ordinal)
                || existing.RequestedBy != command.ActorUserId
                || existing.MachineId != command.MachineId
                || !existing.BringFullPartIds.ToHashSet().SetEquals(requestedParts))
            {
                throw new WorkflowRuleException(
                    "IDEMPOTENCY_KEY_REUSED",
                    "The part-demand batch key belongs to a different request.");
            }

            batchId = existing.Id;
            parts = existing.BringFullPartIds;
        }
        else
        {
            batchId = await InsertPartDemandBatchAsync(connection, transaction, command, cancellationToken);
            parts = requestedParts;
        }

        var results = new List<PartDemandResult>(parts.Count);
        foreach (var partId in parts)
        {
            var itemKey = $"{key}:{partId:N}";
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

    private sealed record StoredPartDemandBatch(Guid Id, string Origin, Guid? RequestedBy, Guid MachineId, IReadOnlyList<Guid> BringFullPartIds);

    private static async Task<StoredPartDemandBatch?> FindPartDemandBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Guid id;
        string origin;
        Guid? requestedBy;
        Guid machineId;
        await using (var command = new NpgsqlCommand(
            """
            SELECT id, request_origin, requested_by, machine_id
            FROM ops.transport_batches
            WHERE idempotency_key = @key
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("key", idempotencyKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(0);
            origin = reader.GetString(1);
            requestedBy = reader.IsDBNull(2) ? null : reader.GetGuid(2);
            machineId = reader.GetGuid(3);
        }

        var parts = new List<Guid>();
        await using var items = new NpgsqlCommand(
            """
            SELECT r.part_id
            FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE r.batch_id = @batch AND tt.code = 'BRING_FULL' AND r.part_id IS NOT NULL
            ORDER BY r.request_no
            """,
            connection,
            transaction);
        items.Parameters.AddWithValue("batch", id);
        await using var itemReader = await items.ExecuteReaderAsync(cancellationToken);
        while (await itemReader.ReadAsync(cancellationToken))
        {
            parts.Add(itemReader.GetGuid(0));
        }

        return new StoredPartDemandBatch(id, origin, requestedBy, machineId, parts);
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

    private async Task<PartDemandResult> CreatePartDemandWithinTransactionAsync(
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
            if (existing.Origin != RequestOrigin.Api
                || existing.RequestedBy != command.ActorUserId
                || existing.MachineId != command.MachineId
                || existing.PartId != command.PartId)
            {
                throw new WorkflowRuleException(
                    "IDEMPOTENCY_KEY_REUSED",
                    "The idempotency key belongs to a different part demand.");
            }

            return new PartDemandResult(existing, await FindPredecessorSnapshotAsync(connection, transaction, existing.Id, cancellationToken), WasIdempotentReplay: true);
        }

        var session = await LockActiveProductionAsync(connection, transaction, command.MachineId, cancellationToken);
        var containerTypeId = await RequireAltPartAsync(connection, transaction, session.ProductGroupId, command.PartId, cancellationToken);
        var removeKey = command.IdempotencyKey.Trim() + ":slot-remove";
        var active = await LockOtherActiveDemandAsync(connection, transaction, command.MachineId, command.PartId, command.IdempotencyKey.Trim(), removeKey, cancellationToken);
        if (active is { } blocking)
        {
            throw new WorkflowRuleException(
                "PART_REQUEST_ALREADY_ACTIVE",
                $"Bu alt parça için aktif talep zaten var. Talep #{blocking}.");
        }

        var destinationSlotId = await LockPartPreparationDestinationAsync(
            connection,
            transaction,
            session.LayoutId,
            command.PartId,
            cancellationToken);

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
            destinationSlotId,
            100,
            RequiresPreparation: true)
        {
            DefersMachineSlotReservation = true
        };
        var created = await CreateAndQueueWithinTransactionAsync(
            connection, transaction, bring, batchId, command.EventContext, cancellationToken);

        return new PartDemandResult(created.Request, Predecessor: null, created.WasIdempotentReplay);
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

    public async Task<int> ActivatePreparedPartDemandsAsync(
        int limit,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        var activationLimit = Math.Clamp(limit, 1, 100);
        var candidateIds = new List<Guid>();
        await using (var connection = await _dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(
            """
            SELECT r.id
            FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE r.status = 'QUEUED'
              AND r.readiness_state = 'READY'
              AND tt.code = 'BRING_FULL'
              AND NOT r.machine_slot_reservation_active
            ORDER BY r.ready_at, r.request_no
            """,
            connection))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidateIds.Add(reader.GetGuid(0));
            }
        }

        var activated = 0;
        foreach (var requestId in candidateIds)
        {
            if (activated >= activationLimit)
            {
                break;
            }

            try
            {
                if (await TryActivatePreparedPartDemandAsync(requestId, eventContext, cancellationToken))
                {
                    activated++;
                }
            }
            catch (WorkflowRuleException)
            {
                // A preparation demand is allowed to wait while its current
                // physical route, return endpoint or destination is not yet
                // usable.  Dispatch remains closed and a later worker cycle
                // re-evaluates the current state.
            }
        }

        return activated;
    }

    private async Task<bool> TryActivatePreparedPartDemandAsync(
        Guid requestId,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lockedWorkflow = await LockWorkflowRequestAsync(connection, transaction, requestId, cancellationToken);
        var request = lockedWorkflow.Request;

        Guid? batchId;
        bool reservationActive;
        await using (var state = new NpgsqlCommand(
            "SELECT batch_id, machine_slot_reservation_active FROM ops.transport_requests WHERE id = @id",
            connection,
            transaction))
        {
            state.Parameters.AddWithValue("id", requestId);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new WorkflowRuleException("REQUEST_NOT_FOUND", "Hazırlık talebi bulunamadı.");
            }

            batchId = reader.IsDBNull(0) ? null : reader.GetGuid(0);
            reservationActive = reader.GetBoolean(1);
        }

        if (reservationActive
            || request.Status != TransportRequestStatus.Queued
            || request.ReadinessState != TransportReadinessState.Ready
            || request.TaskType != TransportTaskType.BringFull
            || request.DestinationSlotId is not { } destinationSlotId)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await using (var competing = new NpgsqlCommand(
            $"""
             SELECT 1
             FROM ops.transport_requests
             WHERE id <> @id
               AND status IN ({ActiveStatusesSql})
               AND machine_slot_reservation_active
               AND (source_slot_id = @slot OR destination_slot_id = @slot)
             LIMIT 1
             """,
            connection,
            transaction))
        {
            competing.Parameters.AddWithValue("id", requestId);
            competing.Parameters.AddWithValue("slot", destinationSlotId);
            if (await competing.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }

        var destination = lockedWorkflow.Slots[destinationSlotId];
        TransportRequestSnapshot? predecessor = null;
        if (destination.State.Occupancy == OccupancyState.Vacant
            && destination.State.Load == LoadState.Unknown)
        {
            // The slot is clear now.  Physical WorkSite consistency is still
            // checked by the dispatch coordinator immediately before motion.
        }
        else if (destination.State.Occupancy == OccupancyState.Occupied
                 && destination.State.Load is LoadState.Empty or LoadState.Full
                 && destination.State.CurrentPartId is { } currentPartId
                 && destination.State.CurrentContainerTypeId is { } currentContainerTypeId)
        {
            if (destination.State.Load == LoadState.Full
                && request.PartId is { } requestedPart
                && destination.KnownPartIds.Contains(requestedPart)
                && destination.KnownPartIds.Contains(currentPartId))
            {
                // Both parts share the same physical slot (either exact same part or co-assigned mix pallet).
                // The delivered full pallet already provides usable material at the slot.
                // Complete this demand as fulfilled without cycling the freshly delivered pallet.
                request = await UpdateRequestStatusAsync(
                    connection,
                    transaction,
                    request,
                    TransportRequestStatus.Completed,
                    cancellationToken);

                var reason = currentPartId == requestedPart
                    ? "İstenen parça alanda zaten mevcut ve dolu durumda."
                    : "Miks palet teslimatı ile talep karşılandı.";

                await InsertEventAsync(
                    connection,
                    transaction,
                    request.Id,
                    eventContext?.ActorUserId,
                    TransportEventTypes.RequestCompleted,
                    TransportRequestStatus.Queued,
                    TransportRequestStatus.Completed,
                    reason,
                    new { mix_pallet_fulfilled = true, slot_id = destinationSlotId, current_part_id = currentPartId },
                    cancellationToken,
                    eventContext);

                await ReleasePreparationPalletIfLinkedWithinTransactionAsync(
                    connection,
                    transaction,
                    request.Id,
                    DateTimeOffset.UtcNow,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return true;
            }

            var removal = destination.State.Load == LoadState.Empty
                ? TransportTaskType.RemoveEmpty
                : TransportTaskType.RemoveFull;
            var removeRequest = new CreateTransportRequest(
                request.IdempotencyKey + ":slot-remove",
                request.Origin,
                request.RequestedBy,
                request.MachineId,
                request.VariantId,
                request.LayoutId,
                request.NavigationProfileId,
                request.ProductGroupId,
                currentPartId,
                currentContainerTypeId,
                removal,
                destinationSlotId,
                null,
                request.Priority,
                RequiresPreparation: false);
            var removed = await CreateAndQueueWithinTransactionAsync(
                connection,
                transaction,
                removeRequest,
                batchId,
                eventContext,
                cancellationToken);
            predecessor = removed.Request;

            await using var link = new NpgsqlCommand(
                """
                INSERT INTO ops.transport_request_dependencies (predecessor_request_id, successor_request_id)
                VALUES (@predecessor, @successor)
                ON CONFLICT DO NOTHING
                """,
                connection,
                transaction);
            link.Parameters.AddWithValue("predecessor", predecessor.Id);
            link.Parameters.AddWithValue("successor", requestId);
            await link.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await using (var activate = new NpgsqlCommand(
            """
            UPDATE ops.transport_requests
            SET machine_slot_reservation_active = true, updated_at = now()
            WHERE id = @id AND NOT machine_slot_reservation_active
            """,
            connection,
            transaction))
        {
            activate.Parameters.AddWithValue("id", requestId);
            if (await activate.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }

        await InsertEventAsync(
            connection,
            transaction,
            requestId,
            eventContext?.ActorUserId,
            TransportEventTypes.MachineSlotReservationActivated,
            null,
            null,
            "Prepared alt-part pallet claimed its mapped machine position.",
            new
            {
                destination_slot_id = destinationSlotId,
                predecessor_request_id = predecessor?.Id,
                predecessor_request_no = predecessor?.RequestNo
            },
            cancellationToken,
            eventContext);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private sealed record ProductionDemandContext(Guid SessionId, Guid VariantId, Guid LayoutId, Guid ProductGroupId);

    private static async Task<ProductionDemandContext> LockActiveProductionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid machineId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT s.id, s.variant_id, s.product_group_id, m.active_layout_id, s.status
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

        if (reader.GetString(4) == "ENDING")
        {
            throw new WorkflowRuleException(
                "PRODUCTION_ENDING",
                "Üretim bitiriliyor. Yeni alt parça talebi açılamaz.");
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

        if (reader.IsDBNull(0))
        {
            throw new WorkflowRuleException(
                "CONTAINER_TYPE_MISSING",
                "Taşıma kabı bilgisi bekleniyor. Talep oluşturulmadı.");
        }

        return reader.GetGuid(0);
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

    private static async Task<Guid> LockPartPreparationDestinationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid layoutId,
        Guid partId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT ls.id
            FROM master.layout_slots ls
            JOIN master.layout_slot_parts lsp ON lsp.layout_slot_id = ls.id AND lsp.part_id = @part
            JOIN ops.slot_runtime_state rs ON rs.layout_slot_id = ls.id
            WHERE ls.layout_id = @layout
              AND ls.is_enabled
              AND ls.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
            ORDER BY
              CASE
                WHEN rs.occupancy_state = 'OCCUPIED' AND rs.current_part_id = @part THEN 0
                WHEN rs.occupancy_state = 'VACANT' THEN 1
                ELSE 2
              END,
              ls.display_order, ls.code, ls.id
            FOR UPDATE OF rs
            LIMIT 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("layout", layoutId);
        command.Parameters.AddWithValue("part", partId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid slotId
            ? slotId
            : throw new WorkflowRuleException(
                "PART_SLOT_NOT_CONFIGURED",
                "Bu alt parça için makine alanı tanımlı değil.");
    }

    private static async Task ReleasePreparationPalletIfLinkedWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var releaseCmd = new NpgsqlCommand(
            """
            WITH released AS (
                UPDATE ops.preparation_pallets
                SET released_at = @now,
                    released_by = COALESCE(ready_by, placed_by),
                    released_reason = CASE WHEN ready_at IS NOT NULL THEN 'COLLECTED' ELSE 'DISPATCH_BYPASS' END,
                    row_version = row_version + 1
                WHERE transport_request_id = @id AND released_at IS NULL
                RETURNING id, preparation_area_id, part_id, container_type_id, transport_request_id, ready_by, placed_by, placed_device_id
            )
            INSERT INTO ops.preparation_events (
                preparation_area_id, preparation_pallet_id, event_type, part_id, container_type_id,
                transport_request_id, actor_user_id, device_id, occurred_at, data_json
            )
            SELECT r.preparation_area_id, r.id, 'PALLET_COLLECTED', r.part_id, r.container_type_id,
                   r.transport_request_id, COALESCE(r.ready_by, r.placed_by), r.placed_device_id, @now, '{"simulated": true}'::jsonb
            FROM released r;
            """,
            connection,
            transaction);
        releaseCmd.Parameters.AddWithValue("id", requestId);
        releaseCmd.Parameters.AddWithValue("now", now);
        await releaseCmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
