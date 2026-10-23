using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresTransportWorkflowStore
{
    /// <summary>
    /// Plans pallet removal for the active production layout. Display order is
    /// only a stable sequence; transport priority comes from
    /// the exact area/part rule, with the approved operation priority as a
    /// compatibility fallback. Nothing is inserted until every
    /// removal has passed the same checks.
    /// </summary>
    private async Task<ProductionSessionResult> PlanProductionEndAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EndProductionSession command,
        CancellationToken cancellationToken)
    {
        var existing = await FindProductionEndRunAsync(connection, transaction, command.MachineId, command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.SessionId != command.SessionId
                || existing.RequestedBy != command.ActorUserId
                || existing.DeviceId != command.DeviceId)
            {
                throw new WorkflowRuleException(
                    "IDEMPOTENCY_KEY_REUSED",
                    "İstek anahtarı farklı bir üretim bitişine ait.");
            }

            var replay = await ReadProductionAsync(connection, transaction, command.MachineId, "id = @value", existing.SessionId, cancellationToken)
                ?? throw new WorkflowRuleException("PRODUCTION_SESSION_NOT_FOUND", "Üretim kaydı bu makinede bulunamadı.");
            return new(replay, true);
        }

        await LockProductionSessionRowAsync(connection, transaction, command.MachineId, command.SessionId, cancellationToken);
        var before = await ReadProductionAsync(connection, transaction, command.MachineId, "id = @value", command.SessionId, cancellationToken)
            ?? throw new WorkflowRuleException("PRODUCTION_SESSION_NOT_FOUND", "Üretim kaydı bu makinede bulunamadı.");
        if (before.EndedAt is not null)
        {
            return new(before, true);
        }

        if (await ProductionEndRunIsRunningAsync(connection, transaction, before.Id, cancellationToken))
        {
            throw new WorkflowRuleException("PRODUCTION_END_IN_PROGRESS", "Üretim bitişi zaten sürüyor.");
        }

        // A failed run leaves the session ENDING. Re-planning happens in this
        // transaction, which already holds the machine lock, so no other
        // request can observe the temporary ACTIVE status.
        if (before.Status == "ENDING")
        {
            await SetProductionStatusAsync(connection, transaction, before, "ACTIVE", cancellationToken);
            before = await ReadProductionAsync(connection, transaction, command.MachineId, "id = @value", command.SessionId, cancellationToken)
                ?? throw new WorkflowRuleException("PRODUCTION_SESSION_NOT_FOUND", "Üretim kaydı bu makinede bulunamadı.");
        }

        await EnsureNoProductionTransportAsync(connection, transaction, command.MachineId, cancellationToken);
        var layoutId = await ReadActiveLayoutAsync(connection, transaction, command, cancellationToken);
        var positions = await ListProductionPositionsAsync(connection, transaction, before, layoutId, cancellationToken);
        var returnSlots = await ListConfiguredReturnDestinationsAsync(connection, transaction, before, layoutId, cancellationToken);
        var locked = await LockPlanningSlotsAsync(
            connection,
            transaction,
            positions.Select(position => position.SlotId).Concat(returnSlots.Select(slot => slot.SlotId)),
            cancellationToken);
        var observed = ApplyLockedPositionState(positions, locked);
        var removals = ClassifyProductionEnd(before, observed);
        await ValidateProductionEndPlanAsync(connection, transaction, before, layoutId, removals, locked, returnSlots, cancellationToken);
        removals.Sort(static (left, right) =>
        {
            var priority = left.Priority.CompareTo(right.Priority);
            if (priority != 0) return priority;
            var display = left.DisplayOrder.CompareTo(right.DisplayOrder);
            if (display != 0) return display;
            var code = string.Compare(left.Code, right.Code, StringComparison.Ordinal);
            return code != 0 ? code : left.SlotId.CompareTo(right.SlotId);
        });

        var runId = Guid.NewGuid();
        if (removals.Count == 0)
        {
            await InsertProductionEndRunAsync(connection, transaction, runId, command, before, layoutId, "COMPLETED", cancellationToken);
            var ended = await CompleteProductionSessionAsync(connection, transaction, before, command.ActorUserId, command.DeviceId, cancellationToken);
            await AuditProductionAsync(connection, transaction, "PRODUCTION_ENDED", command.ActorUserId, command.DeviceId, before, ended, cancellationToken);
            await AuditProductionEndPlanAsync(connection, transaction, command, runId, before, layoutId, observed, removals, ended.Status, cancellationToken);
            return new(ended, false);
        }

        await InsertProductionEndRunAsync(connection, transaction, runId, command, before, layoutId, "RUNNING", cancellationToken);
        Guid? previousRequestId = null;
        for (var index = 0; index < removals.Count; index++)
        {
            var removal = removals[index];
            var registryCleanup = await IsCommissioningRegistryProfileAsync(
                connection,
                transaction,
                await ActiveNavigationAsync(connection, transaction, command.MachineId, cancellationToken),
                cancellationToken);
            if (!registryCleanup
                && !removal.FinishedGoods
                && removal.Operation == TransportTaskType.RemoveFull
                && removal.DestinationSlotId is null)
            {
                throw new WorkflowRuleException(
                    "PRODUCTION_END_REMOVAL_NOT_CONFIGURED",
                    $"{removal.Code} paletinin kaldırılacağı alan yapılandırılmamış. Üretim kapatılmadı.");
            }

            var created = await CreateAndQueueWithinTransactionAsync(
                connection,
                transaction,
                new CreateTransportRequest(
                    $"production-end:{runId:N}:{index + 1}",
                    RequestOrigin.Api,
                    command.ActorUserId,
                    command.MachineId,
                    before.VariantId,
                    layoutId,
                    null,
                    before.ProductGroupId,
                    removal.PartId,
                    removal.ContainerTypeId,
                    removal.Operation,
                    removal.SlotId,
                    removal.FinishedGoods ? null : removal.DestinationSlotId,
                    removal.Priority,
                    RequiresFinishedGoodsDestination: removal.FinishedGoods),
                null,
                OperationalEventContext.ForUser(command.ActorUserId, command.DeviceId),
                cancellationToken);
            await InsertProductionEndItemAsync(connection, transaction, runId, index + 1, removal, created.Request.Id, cancellationToken);
            if (previousRequestId is { } predecessorId)
            {
                await LinkProductionEndDependencyAsync(connection, transaction, predecessorId, created.Request.Id, cancellationToken);
            }

            previousRequestId = created.Request.Id;
        }

        var ending = await SetProductionStatusAsync(connection, transaction, before, "ENDING", cancellationToken);
        await AuditProductionEndPlanAsync(connection, transaction, command, runId, before, layoutId, observed, removals, "RUNNING", cancellationToken);
        return new(ending, false);
    }

    private static List<CleanupRemoval> ClassifyProductionEnd(
        ProductionSessionRecord session,
        IReadOnlyList<ObservedPosition> positions)
    {
        var removals = new List<CleanupRemoval>();
        foreach (var position in positions)
        {
            if (position.Occupancy == OccupancyState.Vacant)
            {
                continue;
            }

            if (position.Occupancy == OccupancyState.Unknown)
            {
                throw new WorkflowRuleException(
                    "PRODUCTION_END_POSITION_UNKNOWN",
                    $"{position.Code} konumunun doluluk durumu bilinmiyor. Üretim kapatılmadı.");
            }

            if (position.Load == LoadState.Unknown)
            {
                throw new WorkflowRuleException(
                    "PRODUCTION_END_LOAD_UNKNOWN",
                    $"{position.Code} konumunun yük durumu bilinmiyor. Üretim kapatılmadı.");
            }

            if (position.PartId is not { } partId)
            {
                throw new WorkflowRuleException(
                    "PRODUCTION_END_PART_UNKNOWN",
                    $"{position.Code} konumundaki paletin parçası bilinmiyor. Üretim kapatılmadı.");
            }

            var operation = position.Load == LoadState.Full
                ? TransportTaskType.RemoveFull
                : TransportTaskType.RemoveEmpty;
            removals.Add(new CleanupRemoval(
                position.SlotId,
                position.Code,
                position.DisplayOrder,
                position.Occupancy,
                position.Load,
                partId,
                position.ContainerTypeId,
                operation,
                Priority: 0,
                FinishedGoods: operation == TransportTaskType.RemoveFull && partId == session.MainPartId,
                DestinationSlotId: null));
        }

        return removals;
    }

    private async Task ValidateProductionEndPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionSessionRecord session,
        Guid layoutId,
        List<CleanupRemoval> removals,
        IReadOnlyDictionary<Guid, SlotSnapshot> lockedSlots,
        IReadOnlyList<ConfiguredReturnSlot> returnSlots,
        CancellationToken cancellationToken)
    {
        var emptyCount = 0;
        var activeNavigation = await ActiveNavigationAsync(connection, transaction, session.MachineId, cancellationToken);
        var registryProfile = await IsCommissioningRegistryProfileAsync(connection, transaction, activeNavigation, cancellationToken);
        for (var index = 0; index < removals.Count; index++)
        {
            var removal = removals[index];
            var priority = await RequireProductionEndPriorityAsync(
                connection,
                transaction,
                session.ProductGroupId,
                removal.SlotId,
                removal.PartId,
                removal.Operation,
                cancellationToken);

            await RequireTaskTemplateAsync(connection, transaction, session, removal.Operation, cancellationToken);
            if (removal.FinishedGoods && !registryProfile)
            {
                _ = await ResolveFinishedGoodsDestinationAsync(
                    connection,
                    transaction,
                    new CreateTransportRequest(
                        "production-end-preflight",
                        RequestOrigin.Api,
                        session.StartedBy,
                        session.MachineId,
                        session.VariantId,
                        layoutId,
                        null,
                        session.ProductGroupId,
                        removal.PartId,
                        removal.ContainerTypeId,
                        TransportTaskType.RemoveFull,
                        removal.SlotId,
                        null,
                        priority,
                        RequiresFinishedGoodsDestination: true),
                    cancellationToken);
            }
            else if (removal.Operation != TransportTaskType.RemoveFull)
            {
                emptyCount++;
            }

            removals[index] = removal with { Priority = priority };
        }

        if (registryProfile)
        {
            // Lock canonical shared endpoints in deterministic class order:
            // 1) FINISHED_GOODS, 2) NON_FINISHED_RETURN, 3) EMPTY_RETURN.
            // Within each class, endpoints are acquired in lexicographical business_code order.
            var fgCount = removals.Count(removal => removal.FinishedGoods && removal.Operation == TransportTaskType.RemoveFull);
            if (fgCount > 0)
            {
                await RequireSharedCapacityAsync(connection, transaction, activeNavigation!.Value, "FINISHED_GOODS", fgCount, "FINISHED_GOODS_MAPPING_MISSING", cancellationToken);
            }

            var altCount = removals.Count(removal => !removal.FinishedGoods && removal.Operation == TransportTaskType.RemoveFull);
            if (altCount > 0)
            {
                await RequireSharedCapacityAsync(connection, transaction, activeNavigation!.Value, "NON_FINISHED_RETURN", altCount, "NON_FINISHED_RETURN_MAPPING_MISSING", cancellationToken);
            }

            if (emptyCount > 0)
            {
                await RequireSharedCapacityAsync(connection, transaction, activeNavigation!.Value, "EMPTY_RETURN", emptyCount, "EMPTY_RETURN_MAPPING_MISSING", cancellationToken);
            }
        }
        else
        {
            await AllocateNonFinishedDestinationsAsync(
                connection,
                transaction,
                layoutId,
                removals,
                lockedSlots,
                returnSlots,
                cancellationToken);

            if (emptyCount > 0)
            {
                var available = await CountAvailableBuffersAsync(connection, transaction, layoutId, cancellationToken);
                if (available < emptyCount)
                {
                    throw new WorkflowRuleException(
                        "EMPTY_BUFFER_UNAVAILABLE",
                        "Boş kasa için uygun boş saklama alanı yok. Üretim kapatılmadı.");
                }
            }
        }
    }

    private static async Task<int> RequireProductionEndPriorityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid productGroupId,
        Guid layoutSlotId,
        Guid partId,
        TransportTaskType operation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT priority
            FROM master.production_end_area_priorities
            WHERE product_group_id = @group
              AND layout_slot_id = @slot
              AND part_id = @part
            UNION ALL
            SELECT priority
            FROM master.production_end_priorities
            WHERE operation_code = @code
              AND NOT EXISTS (
                  SELECT 1
                  FROM master.production_end_area_priorities
                  WHERE product_group_id = @group
                    AND layout_slot_id = @slot
                    AND part_id = @part)
            LIMIT 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("group", productGroupId);
        command.Parameters.AddWithValue("slot", layoutSlotId);
        command.Parameters.AddWithValue("part", partId);
        command.Parameters.AddWithValue("code", operation.ToDatabase());
        if (await command.ExecuteScalarAsync(cancellationToken) is int priority)
        {
            return priority;
        }

        throw new WorkflowRuleException(
            "PRODUCTION_END_PRIORITY_NOT_CONFIGURED",
            "Üretimi Bitir için alan/parça önceliği veya işlem varsayılanı tanımlanmamış.");
    }

    private static async Task RequireTaskTemplateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionSessionRecord session,
        TransportTaskType operation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM master.task_templates t
            JOIN master.task_types tt ON tt.id = t.task_type_id
            WHERE t.variant_id = @variant
              AND tt.code = @code
              AND t.is_active
              AND (t.product_group_id = @group OR t.product_group_id IS NULL)
            LIMIT 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("variant", session.VariantId);
        command.Parameters.AddWithValue("code", operation.ToDatabase());
        command.Parameters.AddWithValue("group", session.ProductGroupId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new WorkflowRuleException(
                "RDS_TASK_TEMPLATE_NOT_FOUND",
                "Bu kaldırma için aktif görev şablonu yok. Üretim kapatılmadı.");
        }
    }

    /// <summary>
    /// Members of the active REMOVE_FULL destination group, in allocation
    /// order. Finished-goods delivery slots are not return positions.
    /// </summary>
    private static async Task<IReadOnlyList<ConfiguredReturnSlot>> ListConfiguredReturnDestinationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionSessionRecord session,
        Guid layoutId,
        CancellationToken cancellationToken)
    {
        var slots = new List<ConfiguredReturnSlot>();
        await using var command = new NpgsqlCommand(
            """
            WITH template AS (
                SELECT t.destination_slot_group_id
                FROM master.task_templates t
                JOIN master.task_types tt ON tt.id = t.task_type_id AND tt.code = 'REMOVE_FULL'
                WHERE t.variant_id = @variant
                  AND t.is_active
                  AND t.destination_slot_group_id IS NOT NULL
                  AND (t.product_group_id = @group OR t.product_group_id IS NULL)
                ORDER BY (t.product_group_id = @group) DESC NULLS LAST, t.id
                LIMIT 1
            )
            SELECT ls.id
            FROM master.layout_slots ls
            JOIN master.slot_group_members sgm ON sgm.layout_slot_id = ls.id
            JOIN template ON template.destination_slot_group_id = sgm.slot_group_id
            WHERE ls.layout_id = @layout
              AND ls.is_enabled
              AND ls.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
              AND NOT EXISTS (
                  SELECT 1 FROM ops.store_location_delivery_slots d
                  WHERE d.layout_slot_id = ls.id)
            ORDER BY sgm.priority, ls.display_order, ls.id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("variant", session.VariantId);
        command.Parameters.AddWithValue("group", session.ProductGroupId);
        command.Parameters.AddWithValue("layout", layoutId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            slots.Add(new ConfiguredReturnSlot(reader.GetGuid(0)));
        }

        return slots;
    }

    private static async Task AllocateNonFinishedDestinationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid layoutId,
        List<CleanupRemoval> removals,
        IReadOnlyDictionary<Guid, SlotSnapshot> lockedSlots,
        IReadOnlyList<ConfiguredReturnSlot> returnSlots,
        CancellationToken cancellationToken)
    {
        var pending = new List<int>();
        for (var index = 0; index < removals.Count; index++)
        {
            if (!removals[index].FinishedGoods && removals[index].Operation == TransportTaskType.RemoveFull)
            {
                pending.Add(index);
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        if (returnSlots.Count == 0)
        {
            throw new WorkflowRuleException(
                "PRODUCTION_END_REMOVAL_NOT_CONFIGURED",
                $"{removals[pending[0]].Code} paletinin kaldırılacağı alan yapılandırılmamış. Üretim kapatılmadı.");
        }

        var candidateIds = returnSlots.Select(slot => slot.SlotId).Distinct().ToArray();
        var reserved = await ActiveEndpointSlotsAsync(connection, transaction, candidateIds, cancellationToken);
        var storeSlots = await StoreDeliverySlotsAsync(connection, transaction, candidateIds, cancellationToken);
        var sources = removals.Select(removal => removal.SlotId).ToHashSet();
        var available = new List<Guid>();
        foreach (var candidate in returnSlots)
        {
            if (available.Contains(candidate.SlotId) || !lockedSlots.TryGetValue(candidate.SlotId, out var slot))
            {
                continue;
            }

            if (!IsAvailableReturnSlot(slot, layoutId, sources, reserved, storeSlots))
            {
                continue;
            }

            available.Add(slot.Id);
        }

        if (available.Count < pending.Count)
        {
            throw new WorkflowRuleException(
                "PRODUCTION_END_REMOVAL_CAPACITY_UNAVAILABLE",
                $"Kaldırılacak {pending.Count} dolu palet için {available.Count} boş dönüş konumu var. Üretim kapatılmadı.");
        }

        for (var offset = 0; offset < pending.Count; offset++)
        {
            var index = pending[offset];
            removals[index] = removals[index] with { DestinationSlotId = available[offset] };
        }
    }

    private static bool IsAvailableReturnSlot(
        SlotSnapshot slot,
        Guid layoutId,
        IReadOnlySet<Guid> sourceSlotIds,
        IReadOnlySet<Guid> reservedSlotIds,
        IReadOnlySet<Guid> storeSlotIds) =>
        slot.IsEnabled
        && slot.LayoutId == layoutId
        && slot.Role is not (SlotRole.Machine or SlotRole.EmptyStorage)
        && !sourceSlotIds.Contains(slot.Id)
        && !storeSlotIds.Contains(slot.Id)
        && !reservedSlotIds.Contains(slot.Id)
        && slot.State.Occupancy == OccupancyState.Vacant
        && slot.State.Load == LoadState.Unknown;

    private static async Task<HashSet<Guid>> ActiveEndpointSlotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] slotIds,
        CancellationToken cancellationToken)
    {
        var reserved = new HashSet<Guid>();
        await using var command = new NpgsqlCommand(
            $"""
             SELECT source_slot_id
             FROM ops.transport_requests
             WHERE status IN ({ActiveStatusesSql})
               AND machine_slot_reservation_active
               AND source_slot_id = ANY(@slot_ids)
             UNION
             SELECT destination_slot_id
             FROM ops.transport_requests
             WHERE status IN ({ActiveStatusesSql})
               AND machine_slot_reservation_active
               AND destination_slot_id = ANY(@slot_ids)
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("slot_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, slotIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                reserved.Add(reader.GetGuid(0));
            }
        }

        return reserved;
    }

    private static async Task<HashSet<Guid>> StoreDeliverySlotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] slotIds,
        CancellationToken cancellationToken)
    {
        var storeSlots = new HashSet<Guid>();
        await using var command = new NpgsqlCommand(
            """
            SELECT layout_slot_id
            FROM ops.store_location_delivery_slots
            WHERE layout_slot_id = ANY(@slot_ids)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("slot_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, slotIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            storeSlots.Add(reader.GetGuid(0));
        }

        return storeSlots;
    }

    /// <summary>
    /// A non-finished full removal physically arrives at its reserved return
    /// slot. Finished-goods delivery keeps the shared store path instead.
    /// </summary>
    private static async Task ApplyNonFinishedReturnCompletionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransportRequestSnapshot request,
        CancellationToken cancellationToken)
    {
        if (request.TaskType != TransportTaskType.RemoveFull || request.DestinationSlotId is not Guid destinationId)
        {
            return;
        }

        if (!await IsProductionEndRemovalAsync(connection, transaction, request.Id, cancellationToken)
            || await IsStoreDeliverySlotAsync(connection, transaction, destinationId, cancellationToken))
        {
            return;
        }

        await UpdateSlotStateAsync(
            connection,
            transaction,
            destinationId,
            new SlotState(OccupancyState.Occupied, LoadState.Full, request.PartId, request.ContainerTypeId),
            updatedBy: null,
            cancellationToken);
    }

    private static async Task<bool> IsProductionEndRemovalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM ops.production_end_run_items WHERE transport_request_id = @request",
            connection,
            transaction);
        command.Parameters.AddWithValue("request", requestId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> IsStoreDeliverySlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid slotId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM ops.store_location_delivery_slots WHERE layout_slot_id = @slot",
            connection,
            transaction);
        command.Parameters.AddWithValue("slot", slotId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<int> CountAvailableBuffersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid layoutId,
        CancellationToken cancellationToken)
    {
        var expected = TransportRules.ExpectedBufferState(TransportTaskType.RemoveEmpty);
        await using var command = new NpgsqlCommand(
            $"""
             SELECT count(*)
             FROM master.layout_slots ls
             JOIN ops.slot_runtime_state rs ON rs.layout_slot_id = ls.id
             WHERE ls.layout_id = @layout
               AND ls.slot_role = 'EMPTY_STORAGE'
               AND ls.is_enabled
               AND rs.occupancy_state = @occupancy
               AND rs.load_state = @load
               AND NOT EXISTS (
                   SELECT 1 FROM ops.transport_requests tr
                   WHERE tr.status IN ({ActiveStatusesSql})
                     AND tr.machine_slot_reservation_active
                     AND (tr.source_slot_id = ls.id OR tr.destination_slot_id = ls.id))
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("layout", layoutId);
        command.Parameters.AddWithValue("occupancy", expected.Occupancy.ToDatabase());
        command.Parameters.AddWithValue("load", expected.Load.ToDatabase());
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task<IReadOnlyList<ObservedPosition>> ListProductionPositionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionSessionRecord session,
        Guid layoutId,
        CancellationToken cancellationToken)
    {
        var positions = new List<ObservedPosition>();
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT ON (s.id) s.id, s.code, s.display_order
            FROM master.layout_slots s
            JOIN master.layout_slot_parts lsp ON lsp.layout_slot_id = s.id
            JOIN master.product_groups pg ON pg.id = @group
            WHERE s.layout_id = @layout
              AND s.is_enabled
              AND s.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
              AND (
                  lsp.part_id = pg.main_part_id
                  OR EXISTS (
                      SELECT 1 FROM master.product_group_parts gpp
                      WHERE gpp.product_group_id = pg.id
                        AND gpp.part_id = lsp.part_id
                        AND gpp.is_active))
            ORDER BY s.id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("group", session.ProductGroupId);
        command.Parameters.AddWithValue("layout", layoutId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            positions.Add(new ObservedPosition(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), default, default, null, null));
        }

        return positions;
    }

    private static async Task<Dictionary<Guid, SlotSnapshot>> LockPlanningSlotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IEnumerable<Guid> slotIds,
        CancellationToken cancellationToken)
    {
        var locked = new Dictionary<Guid, SlotSnapshot>();
        foreach (var slotId in slotIds.Distinct().Order())
        {
            locked[slotId] = await LockSlotAsync(connection, transaction, slotId, cancellationToken);
        }

        return locked;
    }

    private static IReadOnlyList<ObservedPosition> ApplyLockedPositionState(
        IReadOnlyList<ObservedPosition> positions,
        IReadOnlyDictionary<Guid, SlotSnapshot> locked) =>
        positions
            .OrderBy(position => position.DisplayOrder)
            .ThenBy(position => position.Code, StringComparer.Ordinal)
            .ThenBy(position => position.SlotId)
            .Select(position =>
            {
                var state = locked[position.SlotId].State;
                return position with
                {
                    Occupancy = state.Occupancy,
                    Load = state.Load,
                    PartId = state.CurrentPartId,
                    ContainerTypeId = state.CurrentContainerTypeId
                };
            })
            .ToArray();

    private static async Task<Guid> ReadActiveLayoutAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EndProductionSession command,
        CancellationToken cancellationToken)
    {
        Guid layoutId;
        Guid? navigationId;
        Guid variantId;
        await using (var commandText = new NpgsqlCommand(
            """
            SELECT active_layout_id, active_navigation_id, active_variant_id
            FROM ops.machine_runtime_state
            WHERE machine_id = @machine
            """,
            connection,
            transaction))
        {
            commandText.Parameters.AddWithValue("machine", command.MachineId);
            await using var reader = await commandText.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) || reader.IsDBNull(2))
            {
                throw new WorkflowRuleException("ACTIVE_LAYOUT_REQUIRED", "Aktif üretim için makine yerleşimi seçili değil.");
            }

            layoutId = reader.GetGuid(0);
            navigationId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            variantId = reader.GetGuid(2);
        }

        await ValidateVariantLayoutAssignmentAsync(connection, transaction, command.MachineId, variantId, layoutId, navigationId, cancellationToken);
        return layoutId;
    }

    private async Task AdvanceProductionEndRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var run = await LockProductionEndRunForRequestAsync(connection, transaction, requestId, cancellationToken);
        if (run is null || run.Status is "COMPLETED" or "FAILED")
        {
            return;
        }

        var sawFailure = await ProductionEndHasTerminalFailureAsync(connection, transaction, run.Id, cancellationToken);
        if (sawFailure)
        {
            await CancelProductionEndTailAsync(connection, transaction, run.Id, cancellationToken);
            await MarkProductionEndFailedAsync(connection, transaction, run.Id, cancellationToken);
            return;
        }

        if (!await ProductionEndIsFullyCompletedAsync(connection, transaction, run.Id, cancellationToken))
        {
            return;
        }

        if (!await MarkProductionEndCompletedAsync(connection, transaction, run.Id, cancellationToken))
        {
            return;
        }

        var before = await ReadProductionAsync(connection, transaction, run.MachineId, "id = @value", run.SessionId, cancellationToken);
        if (before is null || before.EndedAt is not null)
        {
            return;
        }

        var after = await CompleteProductionSessionAsync(connection, transaction, before, run.RequestedBy, run.DeviceId, cancellationToken);
        await AuditProductionAsync(connection, transaction, "PRODUCTION_ENDED", run.RequestedBy, run.DeviceId, before, after, cancellationToken);
    }

    private static async Task CancelProductionEndTailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            var terminals = new List<Guid>();
            await using (var command = new NpgsqlCommand(
                """
                SELECT i.transport_request_id
                FROM ops.production_end_run_items i
                JOIN ops.transport_requests r ON r.id = i.transport_request_id
                WHERE i.run_id = @run AND r.status IN ('FAILED', 'CANCELLED')
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("run", runId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    terminals.Add(reader.GetGuid(0));
                }
            }

            foreach (var predecessorId in terminals)
            {
                var before = await CountWaitingSuccessorsAsync(connection, transaction, predecessorId, cancellationToken);
                await CancelWaitingSuccessorsAsync(connection, transaction, predecessorId, cancellationToken, null);
                var after = await CountWaitingSuccessorsAsync(connection, transaction, predecessorId, cancellationToken);
                if (after < before)
                {
                    changed = true;
                }
            }
        }
    }

    private static async Task<int> CountWaitingSuccessorsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid predecessorId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             SELECT count(*)
             FROM ops.transport_request_dependencies d
             JOIN ops.transport_requests r ON r.id = d.successor_request_id
             WHERE d.predecessor_request_id = @id
               AND r.status IN ({ActiveStatusesSql})
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", predecessorId);
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task LockProductionSessionRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid machineId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM ops.production_sessions WHERE id = @id AND machine_id = @machine FOR UPDATE",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("machine", machineId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new WorkflowRuleException("PRODUCTION_SESSION_NOT_FOUND", "Üretim kaydı bu makinede bulunamadı.");
        }
    }

    private static async Task<StoredProductionEndRun?> FindProductionEndRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid machineId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, production_session_id, requested_by, device_id, status, machine_id
            FROM ops.production_end_runs
            WHERE machine_id = @machine AND idempotency_key = @key
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("machine", machineId);
        command.Parameters.AddWithValue("key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredProductionEndRun(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4), reader.GetGuid(5))
            : null;
    }

    private static async Task<bool> ProductionEndRunIsRunningAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM ops.production_end_runs WHERE production_session_id = @id AND status = 'RUNNING' LIMIT 1",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", sessionId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task InsertProductionEndRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        EndProductionSession command,
        ProductionSessionRecord session,
        Guid layoutId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var commandText = new NpgsqlCommand(
            """
            INSERT INTO ops.production_end_runs (
                id, production_session_id, machine_id, idempotency_key, requested_by, device_id,
                variant_id, layout_id, product_group_id, main_part_id, status, completed_at)
            VALUES (
                @id, @session, @machine, @key, @actor, @device,
                @variant, @layout, @group, @part, @status,
                CASE WHEN @status = 'COMPLETED' THEN clock_timestamp() ELSE NULL END)
            """,
            connection,
            transaction);
        commandText.Parameters.AddWithValue("id", runId);
        commandText.Parameters.AddWithValue("session", session.Id);
        commandText.Parameters.AddWithValue("machine", command.MachineId);
        commandText.Parameters.AddWithValue("key", command.IdempotencyKey);
        commandText.Parameters.AddWithValue("actor", command.ActorUserId);
        commandText.Parameters.AddWithValue("device", command.DeviceId);
        commandText.Parameters.AddWithValue("variant", session.VariantId);
        commandText.Parameters.AddWithValue("layout", layoutId);
        commandText.Parameters.AddWithValue("group", session.ProductGroupId);
        commandText.Parameters.AddWithValue("part", session.MainPartId);
        commandText.Parameters.AddWithValue("status", status);
        await commandText.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertProductionEndItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        int sequence,
        CleanupRemoval removal,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ops.production_end_run_items (
                run_id, sequence_no, layout_slot_id, slot_code, occupancy_state, load_state,
                part_id, operation_code, transport_request_id)
            VALUES (@run, @sequence, @slot, @code, @occupancy, @load, @part, @operation, @request)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("slot", removal.SlotId);
        command.Parameters.AddWithValue("code", removal.Code);
        command.Parameters.AddWithValue("occupancy", removal.Occupancy.ToDatabase());
        command.Parameters.AddWithValue("load", removal.Load.ToDatabase());
        command.Parameters.AddWithValue("part", removal.PartId);
        command.Parameters.AddWithValue("operation", removal.Operation.ToDatabase());
        command.Parameters.AddWithValue("request", requestId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task LinkProductionEndDependencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid predecessorId,
        Guid successorId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ops.transport_request_dependencies (predecessor_request_id, successor_request_id)
            VALUES (@predecessor, @successor)
            ON CONFLICT (predecessor_request_id) DO NOTHING
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("predecessor", predecessorId);
        command.Parameters.AddWithValue("successor", successorId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ProductionSessionRecord> SetProductionStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionSessionRecord session,
        string status,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             UPDATE ops.production_sessions
             SET status = @status
             WHERE id = @id AND machine_id = @machine AND ended_at IS NULL
             RETURNING {ProductionColumns}
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", session.Id);
        command.Parameters.AddWithValue("machine", session.MachineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new WorkflowRuleException("PRODUCTION_SESSION_NOT_FOUND", "Üretim kaydı bu makinede bulunamadı.");
        }

        return ReadProduction(reader);
    }

    private static async Task<ProductionSessionRecord> CompleteProductionSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProductionSessionRecord session,
        Guid actorId,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             UPDATE ops.production_sessions
             SET status = 'ENDED',
                 ended_at = GREATEST(clock_timestamp(), started_at),
                 ended_by = @actor,
                 ended_device_id = @device
             WHERE id = @id AND machine_id = @machine AND ended_at IS NULL
             RETURNING {ProductionColumns}
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", session.Id);
        command.Parameters.AddWithValue("machine", session.MachineId);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("device", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new WorkflowRuleException("PRODUCTION_SESSION_NOT_FOUND", "Üretim kaydı bu makinede bulunamadı.");
        }

        return ReadProduction(reader);
    }

    private static async Task<StoredProductionEndRun?> LockProductionEndRunForRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT r.id, r.production_session_id, r.requested_by, r.device_id, r.status, r.machine_id
            FROM ops.production_end_run_items i
            JOIN ops.production_end_runs r ON r.id = i.run_id
            WHERE i.transport_request_id = @request
            FOR UPDATE OF r
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("request", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredProductionEndRun(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4), reader.GetGuid(5))
            : null;
    }

    private static async Task<bool> ProductionEndHasTerminalFailureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM ops.production_end_run_items i
            JOIN ops.transport_requests r ON r.id = i.transport_request_id
            WHERE i.run_id = @run AND r.status IN ('FAILED', 'CANCELLED')
            LIMIT 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("run", runId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> ProductionEndIsFullyCompletedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT coalesce(bool_and(r.status = 'COMPLETED'), false)
            FROM ops.production_end_run_items i
            JOIN ops.transport_requests r ON r.id = i.transport_request_id
            WHERE i.run_id = @run
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("run", runId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<bool> MarkProductionEndCompletedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE ops.production_end_runs
            SET status = 'COMPLETED', completed_at = clock_timestamp()
            WHERE id = @id AND status = 'RUNNING'
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", runId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task MarkProductionEndFailedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE ops.production_end_runs
            SET status = 'FAILED', failed_at = clock_timestamp(), failure_code = 'PRODUCTION_END_CLEANUP_INCOMPLETE'
            WHERE id = @id AND status = 'RUNNING'
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AuditProductionEndPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EndProductionSession command,
        Guid runId,
        ProductionSessionRecord session,
        Guid layoutId,
        IReadOnlyList<ObservedPosition> observed,
        IReadOnlyList<CleanupRemoval> removals,
        string runStatus,
        CancellationToken cancellationToken)
    {
        var evidence = new
        {
            runId,
            runStatus,
            sessionId = session.Id,
            machineId = session.MachineId,
            variantId = session.VariantId,
            layoutId,
            productGroupId = session.ProductGroupId,
            mainPartId = session.MainPartId,
            positions = observed.Select(position =>
            {
                var removal = removals.FirstOrDefault(item => item.SlotId == position.SlotId);
                return new
                {
                    slotCode = position.Code,
                    occupancy = position.Occupancy.ToDatabase(),
                    load = position.Load.ToDatabase(),
                    partId = position.PartId,
                    operation = removal?.Operation.ToDatabase(),
                    sequence = removal is null ? (int?)null : removals.ToList().IndexOf(removal) + 1
                };
            })
        };
        await using var commandText = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_logs (actor_user_id, device_id, action, entity_type, entity_id, after_json)
            VALUES (@actor, @device, 'PRODUCTION_END_PLANNED', 'PRODUCTION_END_RUN', @id, @after)
            """,
            connection,
            transaction);
        commandText.Parameters.AddWithValue("actor", command.ActorUserId);
        commandText.Parameters.AddWithValue("device", command.DeviceId);
        commandText.Parameters.AddWithValue("id", runId.ToString());
        commandText.Parameters.Add("after", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(evidence);
        await commandText.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record StoredProductionEndRun(
        Guid Id,
        Guid SessionId,
        Guid RequestedBy,
        Guid DeviceId,
        string Status,
        Guid MachineId);

    private sealed record ObservedPosition(
        Guid SlotId,
        string Code,
        int DisplayOrder,
        OccupancyState Occupancy,
        LoadState Load,
        Guid? PartId,
        Guid? ContainerTypeId);

    private sealed record CleanupRemoval(
        Guid SlotId,
        string Code,
        int DisplayOrder,
        OccupancyState Occupancy,
        LoadState Load,
        Guid PartId,
        Guid? ContainerTypeId,
        TransportTaskType Operation,
        int Priority,
        bool FinishedGoods,
        Guid? DestinationSlotId);

    private sealed record ConfiguredReturnSlot(Guid SlotId);
}
