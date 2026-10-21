using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresTransportWorkflowStore
{
    public async Task<IReadOnlyList<ContainerPositionRecord>> ListPositionsAsync(
        ContainerPositionQuery query,
        PdaActorContext actor,
        bool canOperate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsurePositionProjectionContextAsync(connection, query, cancellationToken);
        var rows = await ReadPartPositionsAsync(connection, transaction: null, query, cancellationToken);
        var result = new List<ContainerPositionRecord>(rows.Count);
        var eta = new TransportEtaService();
        // WP-08: Kasa Doldu is only offered while its finished-goods destination is configured.
        var finishedGoodsCode = await FinishedGoodsAvailabilityCodeAsync(
            connection, query.MachineId, query.LayoutId, query.VariantId, query.ProductGroupId, cancellationToken);
        var navigationId = await ActiveNavigationAsync(connection, null, query.MachineId, cancellationToken);
        var hasAvailableEmptyStoreUnit = await HasAvailableEmptyStoreUnitAsync(connection, null, cancellationToken);
        var hasVacantStoreLocation = await HasVacantStoreLocationAsync(connection, null, cancellationToken);
        foreach (var row in rows)
        {
            var transportState = row.ActiveRequest is null ? "READY" : ProjectExecutionState(row.ActiveRequest);
            var full = Availability(
                OperatorContainerAction.Full,
                row,
                actor,
                canOperate,
                transportState,
                finishedGoodsCode,
                hasAvailableEmptyStoreUnit,
                hasVacantStoreLocation);
            var empty = Availability(
                OperatorContainerAction.Empty,
                row,
                actor,
                canOperate,
                transportState,
                finishedGoodsCode,
                hasAvailableEmptyStoreUnit,
                hasVacantStoreLocation);
            if (row.ActiveRequest is null)
            {
                var physicalCode = await SlotPhysicalBlockAsync(connection, null, row.SlotId, navigationId, row.Occupancy.ToDatabase(), cancellationToken);
                if (physicalCode is not null)
                {
                    full = new(false, "READY", physicalCode, "TKG konumu ve fiziksel RDS durumunu doğrulayıp ekranı yenileyin.");
                    empty = new(false, "READY", physicalCode, "TKG konumu ve fiziksel RDS durumunu doğrulayıp ekranı yenileyin.");
                }
            }
            var etaProjection = row.ActiveRequest is null
                ? eta.Unavailable()
                : eta.Project(
                    row.ActiveRequest.Status,
                    row.ActiveRequest.RdsStatus,
                    row.ActiveRequest.AgvId,
                    row.ActiveRequest.StartedAt,
                    row.QueuePosition,
                    readinessState: row.ActiveRequest.ReadinessState);
            result.Add(new ContainerPositionRecord(
                row.PositionNumber,
                $"{row.PositionNumber}. Kasa",
                full,
                empty,
                transportState,
                row.QueuePosition,
                etaProjection.Seconds,
                etaProjection.State));
        }

        return result;
    }

    public async Task<ContainerActionResult> CreateAsync(
        ContainerActionCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockMachineRuntimeAsync(connection, transaction, command.MachineId, cancellationToken);
        await LockIdempotencyKeyAsync(connection, transaction, command.IdempotencyKey, cancellationToken);

        var query = new ContainerPositionQuery(
            command.MachineId,
            command.VariantId,
            command.LayoutId,
            command.ProductGroupId,
            command.PartId);
        var expectedOperation = command.Action == OperatorContainerAction.Full
            ? TransportTaskType.RemoveFull
            : TransportTaskType.BringEmpty;

        var existing = await FindByIdempotencyKeyAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            var replayRows = await ReadPartPositionsAsync(connection, transaction, query, cancellationToken);
            var replayPosition = replayRows.SingleOrDefault(item => item.PositionNumber == command.PositionNumber)
                ?? throw new WorkflowRuleException("CONTAINER_POSITION_NOT_FOUND", "Seçilen kasa konumu artık mevcut değildir.");
            var replayRequest = BuildTransportRequest(command, expectedOperation, replayPosition.SlotId, expectedOperation == TransportTaskType.BringEmpty);
            EnsureEquivalentPayload(existing, replayRequest);
            await ValidateExpectedProductionAsync(connection, transaction, command.MachineId, command.ProductionSessionId, [existing.Id], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ContainerActionResult(command.PositionNumber, command.Action.ToApi(), existing, WasIdempotentReplay: true);
        }

        var contextRequest = BuildTransportRequest(command, expectedOperation, machineSlotId: null, expectedOperation == TransportTaskType.BringEmpty);
        await LockAndValidateRuntimeContextAsync(connection, transaction, contextRequest, cancellationToken);
        await RejectIfProductionEndingAsync(connection, transaction, command.MachineId, cancellationToken);
        await ValidateExpectedProductionAsync(connection, transaction, command.MachineId, command.ProductionSessionId, null, cancellationToken);
        var positions = await ReadPartPositionsAsync(connection, transaction, query, cancellationToken);
        var position = positions.SingleOrDefault(item => item.PositionNumber == command.PositionNumber)
            ?? throw new WorkflowRuleException("CONTAINER_POSITION_NOT_FOUND", "Seçilen kasa konumu aktif varyant ve parça için mevcut değildir.");

        // Keep the Phase 03 global lock order intact even when the operation
        // later allocates an EMPTY_STORAGE endpoint inside the existing
        // workflow. All possibly involved slot runtime rows are acquired in
        // UUID order after the machine runtime lock.
        var actionSlotIds = await ReadActionSlotIdsForLockAsync(
            connection,
            transaction,
            command.LayoutId,
            position.SlotId,
            cancellationToken);
        var lockedSlots = new Dictionary<Guid, SlotSnapshot>();
        foreach (var slotId in actionSlotIds)
        {
            lockedSlots[slotId] = await LockSlotAsync(connection, transaction, slotId, cancellationToken);
        }

        var slot = lockedSlots[position.SlotId];

        await EnsureSlotsAreAvailableAsync(connection, transaction, [slot.Id], exceptRequestId: null, cancellationToken);
        var resolution = ContainerActionResolutionPolicy.ResolveSingle(command.Action, slot.State.Occupancy, slot.State.Load);
        if (resolution.Operation == TransportTaskType.BringEmpty && !resolution.RequiresPreparation)
        {
            if (!await HasAvailableEmptyStoreUnitAsync(connection, transaction, cancellationToken))
            {
                throw new WorkflowRuleException(
                    "FINISHED_GOODS_NO_EMPTY_CONTAINER",
                    "Bitmiş ürün deposunda hazır boş kasa bulunmuyor. Lütfen depoya boş kasa yerleştirin.");
            }
        }
        else if (resolution.Operation == TransportTaskType.RemoveFull)
        {
            if (!await HasVacantStoreLocationAsync(connection, transaction, cancellationToken))
            {
                throw new WorkflowRuleException(
                    "FINISHED_GOODS_STORE_FULL",
                    "Bitmiş ürün deposunda boş alan bulunmuyor. Lütfen depodan mamul sevkiyatı yapın.");
            }
        }

        if (resolution.MarkFullBeforeCreate)
        {
            await UpdateSlotStateAsync(
                connection,
                transaction,
                slot.Id,
                slot.State with
                {
                    Load = LoadState.Full,
                    CurrentPartId = slot.State.CurrentPartId ?? command.PartId
                },
                command.ActorUserId,
                cancellationToken);
        }

        var request = BuildTransportRequest(command, resolution.Operation, slot.Id, resolution.RequiresPreparation);
        var created = await CreateAndQueueWithinTransactionAsync(connection, transaction, request, batchId: null, command.EventContext, cancellationToken);
        if (resolution.MarkFullBeforeCreate)
        {
            await InsertEventAsync(
                connection,
                transaction,
                created.Request.Id,
                command.ActorUserId,
                "CONTAINER_REPORTED_FULL",
                created.Request.Status,
                created.Request.Status,
                "Operator reported the selected logical container position FULL before REMOVE_FULL creation.",
                new { logical_position = command.PositionNumber },
                cancellationToken,
                command.EventContext);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ContainerActionResult(command.PositionNumber, command.Action.ToApi(), created.Request, created.WasIdempotentReplay);
    }

    public async Task<RemoveAllContainersResult> RemoveAllAsync(
        RemoveAllContainersCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockMachineRuntimeAsync(connection, transaction, command.MachineId, cancellationToken);
        await LockIdempotencyKeyAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        var existing = await FindBatchByIdempotencyKeyAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            EnsureBatchContextEquivalent(
                existing,
                command.ActorUserId,
                command.MachineId,
                command.VariantId,
                command.LayoutId,
                command.NavigationProfileId,
                command.Priority,
                "remove-all:");

            await ValidateExpectedProductionAsync(connection, transaction, command.MachineId, command.ProductionSessionId, existing.Items.Select(item => item.Id).ToArray(), cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new RemoveAllContainersResult(existing with { WasIdempotentReplay = true }, []);
        }

        var contextRequest = new CreateTransportRequest(
            command.IdempotencyKey,
            RequestOrigin.Api,
            command.ActorUserId,
            command.MachineId,
            command.VariantId,
            command.LayoutId,
            command.NavigationProfileId,
            null,
            null,
            null,
            TransportTaskType.RemoveFull,
            null,
            null,
            command.Priority);
        await LockAndValidateRuntimeContextAsync(connection, transaction, contextRequest, cancellationToken);
        await RejectIfProductionEndingAsync(connection, transaction, command.MachineId, cancellationToken);

        var candidates = await ReadVariantPositionsAsync(connection, transaction, command.LayoutId, cancellationToken);
        await ValidateExpectedProductionAsync(connection, transaction, command.MachineId, command.ProductionSessionId, null, cancellationToken);
        var allSlotIds = await ReadActionSlotIdsForLockAsync(
            connection,
            transaction,
            command.LayoutId,
            selectedMachineSlotId: null,
            cancellationToken);
        var locked = new Dictionary<Guid, SlotSnapshot>();
        foreach (var slotId in allSlotIds)
        {
            locked[slotId] = await LockSlotAsync(connection, transaction, slotId, cancellationToken);
        }

        var issues = new List<RemoveAllPositionIssue>();
        var ignored = new List<int>();
        var items = new List<CreateTransportRequest>();
        foreach (var candidate in candidates)
        {
            var slot = locked[candidate.SlotId];
            TransportTaskType? operation;
            try
            {
                operation = ContainerActionResolutionPolicy.ResolveRemoveAll(slot.State.Occupancy, slot.State.Load);
            }
            catch (WorkflowRuleException error)
            {
                issues.Add(new(candidate.PositionNumber, error.Code, error.Message));
                continue;
            }

            if (operation is null)
            {
                ignored.Add(candidate.PositionNumber);
                continue;
            }

            if (candidate.HasActiveRequest)
            {
                issues.Add(new(candidate.PositionNumber, "SLOT_BUSY", "Bu konumu kullanan aktif bir taşıma talebi bulunuyor."));
                continue;
            }

            var partId = slot.State.CurrentPartId;
            if (!partId.HasValue)
            {
                partId = slot.KnownPartIds.Count == 1 ? slot.KnownPartIds.Single() : null;
            }

            if (!partId.HasValue)
            {
                issues.Add(new(candidate.PositionNumber, "PART_NOT_RESOLVED", "Konumdaki parçanın kimliği güvenli biçimde çözülemedi."));
                continue;
            }

            var group = await ResolveSingleProductGroupAsync(connection, transaction, command.VariantId, partId.Value, cancellationToken);
            if (!group.HasValue)
            {
                issues.Add(new(candidate.PositionNumber, "PRODUCT_GROUP_NOT_RESOLVED", "Konumdaki parça için tek bir ürün grubu çözülemedi."));
                continue;
            }

            var machineSlotSource = operation == TransportTaskType.BringEmpty ? (Guid?)null : slot.Id;
            var machineSlotDestination = operation == TransportTaskType.BringEmpty ? slot.Id : (Guid?)null;
            items.Add(new CreateTransportRequest(
                ChildIdempotencyKey("remove-all:", command.IdempotencyKey, slot.Id),
                RequestOrigin.Api,
                command.ActorUserId,
                command.MachineId,
                command.VariantId,
                command.LayoutId,
                command.NavigationProfileId,
                group,
                partId,
                slot.State.CurrentContainerTypeId ?? slot.DefaultContainerTypeId,
                operation.Value,
                machineSlotSource,
                machineSlotDestination,
                command.Priority,
                RequiresFinishedGoodsDestination: operation.Value == TransportTaskType.RemoveFull));
        }

        if (issues.Count > 0)
        {
            var detail = string.Join("; ", issues.Select(issue => $"{issue.PositionNumber}. Kasa [{issue.Code}]: {issue.Message}"));
            throw new WorkflowRuleException("REMOVE_ALL_NOT_READY", detail);
        }

        if (items.Count == 0)
        {
            throw new WorkflowRuleException("REMOVE_ALL_NOTHING_TO_REMOVE", "Aktif varyantta kaldırılacak dolu bir kasa bulunmuyor.");
        }

        var batchRequest = new CreateTransportBatchRequest(
            command.IdempotencyKey,
            RequestOrigin.Api,
            command.ActorUserId,
            command.MachineId,
            items);
        batchRequest.ValidateShape();
        var batchId = await InsertBatchAsync(connection, transaction, batchRequest, cancellationToken);
        var children = new List<TransportRequestSnapshot>(items.Count);
        foreach (var item in items)
        {
            var child = await CreateAndQueueWithinTransactionAsync(connection, transaction, item, batchId, command.EventContext, cancellationToken);
            children.Add(child.Request);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RemoveAllContainersResult(
            new TransportBatchResult(batchId, command.IdempotencyKey, command.MachineId, children, WasIdempotentReplay: false),
            ignored);
    }

    public async Task<TransportBatchResult> CreateBatchAsync(
        ContainerActionBatchCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockMachineRuntimeAsync(connection, transaction, command.MachineId, cancellationToken);
        await LockIdempotencyKeyAsync(connection, transaction, command.IdempotencyKey, cancellationToken);
        var existing = await FindBatchByIdempotencyKeyAsync(connection, transaction, command.IdempotencyKey, cancellationToken);

        var contextRequest = new CreateTransportRequest(
            command.IdempotencyKey,
            RequestOrigin.Api,
            command.ActorUserId,
            command.MachineId,
            command.VariantId,
            command.LayoutId,
            command.NavigationProfileId,
            null,
            null,
            null,
            TransportTaskType.RemoveFull,
            null,
            null,
            command.Priority);
        await LockAndValidateRuntimeContextAsync(connection, transaction, contextRequest, cancellationToken);
        await RejectIfProductionEndingAsync(connection, transaction, command.MachineId, cancellationToken);

        var resolved = new List<(ContainerActionBatchItem Item, Guid SlotId)>();
        await ValidateExpectedProductionAsync(connection, transaction, command.MachineId, command.ProductionSessionId, existing?.Items.Select(item => item.Id).ToArray(), cancellationToken);
        foreach (var item in command.Items)
        {
            var positions = await ReadPartPositionsAsync(
                connection,
                transaction,
                new ContainerPositionQuery(
                    command.MachineId,
                    command.VariantId,
                    command.LayoutId,
                    item.ProductGroupId,
                    item.PartId),
                cancellationToken);
            var position = positions.SingleOrDefault(candidate => candidate.PositionNumber == item.PositionNumber)
                ?? throw new WorkflowRuleException(
                    "CONTAINER_POSITION_NOT_FOUND",
                    $"{item.PositionNumber}. Kasa seçilen parça için etkin yerleşimde bulunamadı.");
            resolved.Add((item, position.SlotId));
        }

        if (resolved.Select(item => item.SlotId).Distinct().Count() != resolved.Count)
        {
            throw new WorkflowRuleException(
                "CONTAINER_BATCH_DUPLICATE_POSITION",
                "Aynı fiziksel kasa konumu grup içinde birden fazla kez seçilemez.");
        }

        var expectedRequests = resolved.Select(entry => new CreateTransportRequest(
            ChildIdempotencyKey("container-action-batch:", command.IdempotencyKey, entry.SlotId),
            RequestOrigin.Api,
            command.ActorUserId,
            command.MachineId,
            command.VariantId,
            command.LayoutId,
            command.NavigationProfileId,
            entry.Item.ProductGroupId,
            entry.Item.PartId,
            null,
            entry.Item.Action == OperatorContainerAction.Full
                ? TransportTaskType.RemoveFull
                : TransportTaskType.BringEmpty,
            entry.Item.Action == OperatorContainerAction.Full ? entry.SlotId : null,
            entry.Item.Action == OperatorContainerAction.Empty ? entry.SlotId : null,
            command.Priority,
            RequiresFinishedGoodsDestination: entry.Item.Action == OperatorContainerAction.Full)).ToArray();

        if (existing is not null)
        {
            EnsureBatchContextEquivalent(
                existing,
                command.ActorUserId,
                command.MachineId,
                command.VariantId,
                command.LayoutId,
                command.NavigationProfileId,
                command.Priority,
                "container-action-batch:");
            EnsureBatchItemsEquivalent(existing, expectedRequests);
            await transaction.CommitAsync(cancellationToken);
            return existing with { WasIdempotentReplay = true };
        }

        var lockIds = new HashSet<Guid>();
        foreach (var item in resolved)
        {
            lockIds.Add(item.SlotId);
        }

        foreach (var slotId in await ReadActionSlotIdsForLockAsync(
                     connection,
                     transaction,
                     command.LayoutId,
                     selectedMachineSlotId: null,
                     cancellationToken))
        {
            // Remove-all mode of the helper returns all machine positions and
            // buffers. Locking that superset keeps one deterministic global
            // order and prevents later allocator locks from inverting it.
            lockIds.Add(slotId);
        }

        var slots = new Dictionary<Guid, SlotSnapshot>();
        foreach (var slotId in lockIds.Order())
        {
            slots[slotId] = await LockSlotAsync(connection, transaction, slotId, cancellationToken);
        }

        await EnsureSlotsAreAvailableAsync(
            connection,
            transaction,
            resolved.Select(item => item.SlotId).Order().ToArray(),
            exceptRequestId: null,
            cancellationToken);

        var requests = new List<(CreateTransportRequest Request, bool MarkFull, int PositionNumber)>();
        foreach (var entry in resolved)
        {
            var slot = slots[entry.SlotId];
            var resolution = ContainerActionResolutionPolicy.ResolveSingle(
                entry.Item.Action,
                slot.State.Occupancy,
                slot.State.Load);
            requests.Add((
                new CreateTransportRequest(
                    ChildIdempotencyKey("container-action-batch:", command.IdempotencyKey, entry.SlotId),
                    RequestOrigin.Api,
                    command.ActorUserId,
                    command.MachineId,
                    command.VariantId,
                    command.LayoutId,
                    command.NavigationProfileId,
                    entry.Item.ProductGroupId,
                    entry.Item.PartId,
                    null,
                    resolution.Operation,
                    resolution.Operation == TransportTaskType.BringEmpty ? null : entry.SlotId,
                    resolution.Operation == TransportTaskType.BringEmpty ? entry.SlotId : null,
                    command.Priority,
                    RequiresPreparation: resolution.RequiresPreparation,
                    RequiresFinishedGoodsDestination: resolution.Operation == TransportTaskType.RemoveFull),
                resolution.MarkFullBeforeCreate,
                entry.Item.PositionNumber));
        }

        foreach (var entry in requests.Where(item => item.MarkFull))
        {
            var slotId = entry.Request.SourceSlotId!.Value;
            var slot = slots[slotId];
            await UpdateSlotStateAsync(
                connection,
                transaction,
                slotId,
                slot.State with
                {
                    Load = LoadState.Full,
                    CurrentPartId = slot.State.CurrentPartId ?? entry.Request.PartId
                },
                command.ActorUserId,
                cancellationToken);
        }

        var batchRequest = new CreateTransportBatchRequest(
            command.IdempotencyKey,
            RequestOrigin.Api,
            command.ActorUserId,
            command.MachineId,
            requests.Select(item => item.Request).ToArray());
        batchRequest.ValidateShape();
        var batchId = await InsertBatchAsync(connection, transaction, batchRequest, cancellationToken);
        var children = new List<TransportRequestSnapshot>(requests.Count);
        foreach (var entry in requests)
        {
            var child = await CreateAndQueueWithinTransactionAsync(
                connection,
                transaction,
                entry.Request,
                batchId,
                command.EventContext,
                cancellationToken);
            children.Add(child.Request);
            if (entry.MarkFull)
            {
                await InsertEventAsync(
                    connection,
                    transaction,
                    child.Request.Id,
                    command.ActorUserId,
                    "CONTAINER_REPORTED_FULL",
                    child.Request.Status,
                    child.Request.Status,
                    "Operator reported the logical container position FULL as part of a container-action batch.",
                    new { logical_position = entry.PositionNumber },
                    cancellationToken,
                    command.EventContext);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new TransportBatchResult(
            batchId,
            command.IdempotencyKey,
            command.MachineId,
            children,
            WasIdempotentReplay: false);
    }

    private static CreateTransportRequest BuildTransportRequest(
        ContainerActionCommand command,
        TransportTaskType operation,
        Guid? machineSlotId,
        bool requiresPreparation = false) => new(
            command.IdempotencyKey,
            RequestOrigin.Api,
            command.ActorUserId,
            command.MachineId,
            command.VariantId,
            command.LayoutId,
            command.NavigationProfileId,
            command.ProductGroupId,
            command.PartId,
            null,
            operation,
            operation == TransportTaskType.BringEmpty ? null : machineSlotId,
            operation == TransportTaskType.BringEmpty ? machineSlotId : null,
            command.Priority,
            requiresPreparation,
            RequiresFinishedGoodsDestination: operation == TransportTaskType.RemoveFull);

    private static async Task<IReadOnlyList<PositionRow>> ReadPartPositionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ContainerPositionQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             WITH positions AS (
                 SELECT s.id,
                        row_number() OVER (ORDER BY s.display_order, s.code, s.id)::integer AS position_number,
                        rs.occupancy_state,
                        rs.load_state
                 FROM master.layout_slots s
                 JOIN master.layouts l ON l.id = s.layout_id
                     AND l.machine_id = @machine_id AND l.status = 'PUBLISHED'
                 JOIN master.variant_layout_assignments vla ON vla.layout_id = l.id
                     AND vla.machine_id = @machine_id AND vla.variant_id = @variant_id
                 JOIN master.layout_slot_parts lsp ON lsp.layout_slot_id = s.id AND lsp.part_id = @part_id
                 JOIN master.product_groups pg ON pg.id = @product_group_id
                     AND pg.variant_id = @variant_id AND pg.is_active
                 JOIN ops.slot_runtime_state rs ON rs.layout_slot_id = s.id
                 WHERE s.layout_id = @layout_id
                   AND s.is_enabled
                   AND s.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
                   AND (pg.main_part_id = @part_id OR EXISTS (
                       SELECT 1 FROM master.product_group_parts gpp
                       WHERE gpp.product_group_id = pg.id AND gpp.part_id = @part_id AND gpp.is_active))
             ), active AS (
                 SELECT p.*,
                        r.id AS request_id,
                        r.status,
                        r.failure_code,
                        rt.rds_status,
                        rt.agv_id,
                        rt.started_at,
                        r.readiness_state,
                        row_number() OVER (
                            PARTITION BY p.id
                            ORDER BY r.created_at DESC NULLS LAST, r.request_no DESC NULLS LAST) AS request_rank
                 FROM positions p
                 LEFT JOIN ops.transport_requests r ON r.status IN ({ActiveStatusesSql})
                    AND (r.source_slot_id = p.id OR r.destination_slot_id = p.id)
                 LEFT JOIN integration.rds_tasks rt ON rt.transport_request_id = r.id
             ), queue_rank AS (
                 SELECT r.id,
                        row_number() OVER (
                            ORDER BY CASE r.status
                                WHEN 'RUNNING' THEN 0
                                WHEN 'SENT_TO_RDS' THEN 1
                                WHEN 'QUEUED' THEN 2
                                ELSE 3 END,
                                r.created_at, r.request_no)::integer AS queue_position
                 FROM ops.transport_requests r
                 WHERE r.status IN ({ActiveStatusesSql})
             )
             SELECT a.id, a.position_number, a.occupancy_state, a.load_state,
                    a.request_id, a.status, a.failure_code, a.rds_status,
                    a.agv_id, a.started_at, qr.queue_position, a.readiness_state
             FROM active a
             LEFT JOIN queue_rank qr ON qr.id = a.request_id
             WHERE a.request_rank = 1
             ORDER BY a.position_number;
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("part_id", query.PartId);
        command.Parameters.AddWithValue("machine_id", query.MachineId);
        command.Parameters.AddWithValue("product_group_id", query.ProductGroupId);
        command.Parameters.AddWithValue("variant_id", query.VariantId);
        command.Parameters.AddWithValue("layout_id", query.LayoutId);
        var result = new List<PositionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ActivePositionRequest? active = reader.IsDBNull(4)
                ? null
                : new(
                    WorkflowCode.ParseTransportStatus(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                    reader.IsDBNull(11) ? TransportReadinessState.NotRequired : WorkflowCode.ParseReadinessState(reader.GetString(11)));
            result.Add(new(
                reader.GetGuid(0),
                reader.GetInt32(1),
                WorkflowCode.ParseOccupancy(reader.GetString(2)),
                WorkflowCode.ParseLoad(reader.GetString(3)),
                active,
                reader.IsDBNull(10) ? null : reader.GetInt32(10)));
        }

        return result;
    }

    private static async Task EnsurePositionProjectionContextAsync(
        NpgsqlConnection connection,
        ContainerPositionQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM ops.machine_runtime_state runtime
            JOIN master.variants v ON v.id = @variant_id
                AND v.machine_id = runtime.machine_id AND v.is_active
            JOIN master.layouts l ON l.id = @layout_id
                AND l.machine_id = runtime.machine_id AND l.status = 'PUBLISHED'
            JOIN master.variant_layout_assignments assignment
              ON assignment.variant_id = v.id
             AND assignment.layout_id = l.id
             AND assignment.machine_id = runtime.machine_id
            WHERE runtime.machine_id = @machine_id
              AND runtime.active_variant_id = @variant_id
              AND runtime.active_layout_id = @layout_id
            """,
            connection);
        command.Parameters.AddWithValue("machine_id", query.MachineId);
        command.Parameters.AddWithValue("variant_id", query.VariantId);
        command.Parameters.AddWithValue("layout_id", query.LayoutId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new WorkflowRuleException(
                "RUNTIME_CONTEXT_MISMATCH",
                "Kasa konumları yalnız makinenin etkin varyant ve yerleşim bağlamında görüntülenebilir.");
        }
    }

    private static async Task<IReadOnlyList<Guid>> ReadActionSlotIdsForLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid layoutId,
        Guid? selectedMachineSlotId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT ls.id
            FROM master.layout_slots ls
            WHERE ls.layout_id = @layout_id
              AND ls.is_enabled
              AND (ls.slot_role = 'EMPTY_STORAGE'
                   OR (@selected_slot_id IS NULL AND ls.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE'))
                   OR ls.id = @selected_slot_id)
            ORDER BY ls.id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("layout_id", layoutId);
        AddNullableUuid(command, "selected_slot_id", selectedMachineSlotId);
        var result = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetGuid(0));
        }

        if (selectedMachineSlotId.HasValue && !result.Contains(selectedMachineSlotId.Value))
        {
            throw new WorkflowRuleException("CONTAINER_POSITION_NOT_FOUND", "Seçilen kasa konumu etkin yerleşimde mevcut değildir.");
        }

        return result;
    }

    private static async Task<IReadOnlyList<VariantPositionRow>> ReadVariantPositionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid layoutId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             SELECT s.id,
                    row_number() OVER (ORDER BY s.display_order, s.code, s.id)::integer AS position_number,
                    EXISTS (
                        SELECT 1 FROM ops.transport_requests r
                        WHERE r.status IN ({ActiveStatusesSql})
                          AND (r.source_slot_id = s.id OR r.destination_slot_id = s.id)) AS has_active_request
             FROM master.layout_slots s
             WHERE s.layout_id = @layout_id
               AND s.is_enabled
               AND s.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
             ORDER BY position_number
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("layout_id", layoutId);
        var result = new List<VariantPositionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(reader.GetGuid(0), reader.GetInt32(1), reader.GetBoolean(2)));
        }

        return result;
    }

    private static async Task<Guid?> ResolveSingleProductGroupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid variantId,
        Guid partId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT pg.id
            FROM master.product_groups pg
            WHERE pg.variant_id = @variant_id
              AND pg.is_active
              AND (pg.main_part_id = @part_id OR EXISTS (
                  SELECT 1 FROM master.product_group_parts gpp
                  WHERE gpp.product_group_id = pg.id AND gpp.part_id = @part_id AND gpp.is_active))
            ORDER BY pg.id
            LIMIT 2
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("variant_id", variantId);
        command.Parameters.AddWithValue("part_id", partId);
        var values = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetGuid(0));
        }

        return values.Count == 1 ? values[0] : null;
    }

    private static ContainerActionAvailabilityRecord Availability(
        OperatorContainerAction action,
        PositionRow row,
        PdaActorContext actor,
        bool canOperate,
        string transportState,
        string? finishedGoodsCode,
        bool hasAvailableEmptyStoreUnit,
        bool hasVacantStoreLocation)
    {
        if (row.ActiveRequest is not null)
        {
            return new(false, transportState, "SLOT_BUSY", "Bu kasa konumunda aktif bir taşıma talebi bulunuyor.");
        }

        if (!actor.IsDeviceBound || !canOperate)
        {
            return new(false, "READY", "MACHINE_OPERATION_NOT_PERMITTED", "Bu makinede operasyon yetkiniz bulunmuyor.");
        }

        var canRemove = actor.Roles.Contains(KnownRoleCodes.Admin)
            || actor.Roles.Contains(KnownRoleCodes.Supervisor)
            || actor.Roles.Contains(KnownRoleCodes.LineOperator);
        var allowed = action == OperatorContainerAction.Empty
            ? canRemove || actor.Roles.Contains(KnownRoleCodes.WarehouseOperator)
            : canRemove;
        if (!allowed)
        {
            return new(false, "READY", "OPERATION_NOT_ALLOWED_FOR_ROLE", "Rolünüz bu kasa işlemini başlatmaya yetkili değildir.");
        }

        try
        {
            var resolution = ContainerActionResolutionPolicy.ResolveSingle(action, row.Occupancy, row.Load);
            if (resolution.Operation == TransportTaskType.RemoveFull)
            {
                if (finishedGoodsCode is not null)
                {
                    return new(false, "READY", finishedGoodsCode, finishedGoodsCode == "FINISHED_GOODS_DESTINATION_NOT_CONFIGURED"
                        ? FinishedGoodsDestinationNotConfiguredMessage : "Uygun fiziksel bitmiş ürün alanı yok. Alanı ve RDS durumunu kontrol edip ekranı yenileyin.");
                }

                if (!hasVacantStoreLocation)
                {
                    return new(false, "READY", "FINISHED_GOODS_STORE_FULL", "Bitmiş ürün alanında boş yer yok.");
                }
            }
            else if (resolution.Operation == TransportTaskType.BringEmpty && !resolution.RequiresPreparation)
            {
                if (!hasAvailableEmptyStoreUnit)
                {
                    return new(false, "READY", "FINISHED_GOODS_NO_EMPTY_CONTAINER", "Bitmiş ürün alanında hazır boş kasa yok.");
                }
            }

            return new(true, "READY", null, null);
        }
        catch (WorkflowRuleException error)
        {
            return new(false, "READY", error.Code, error.Message);
        }
    }

    private static string ProjectExecutionState(ActivePositionRequest request)
    {
        if (request.Status == TransportRequestStatus.Failed || request.RdsStatus is 1004 or 1005 or 1006)
        {
            return "ERROR";
        }

        if (request.Status == TransportRequestStatus.Completed || request.RdsStatus == 1003)
        {
            return "COMPLETED";
        }

        if (request.ReadinessState == TransportReadinessState.WaitingPreparation)
        {
            return "WAITING_PREPARATION";
        }

        return request.Status switch
        {
            TransportRequestStatus.Created or TransportRequestStatus.Validating => "REQUESTED",
            TransportRequestStatus.Queued => "QUEUED",
            TransportRequestStatus.SentToRds when !string.IsNullOrWhiteSpace(request.AgvId) => "ROBOT_ASSIGNED",
            TransportRequestStatus.SentToRds => "QUEUED",
            TransportRequestStatus.Running => "IN_PROGRESS",
            _ => "REQUESTED"
        };
    }

    private static void EnsureBatchContextEquivalent(
        TransportBatchResult existing,
        Guid actorUserId,
        Guid machineId,
        Guid variantId,
        Guid layoutId,
        Guid? navigationProfileId,
        int priority,
        string childKeyPrefix)
    {
        var equivalent = existing.MachineId == machineId
            && existing.Items.Count > 0
            && existing.Items.All(item =>
                item.IdempotencyKey.StartsWith(childKeyPrefix, StringComparison.Ordinal)
                && item.Origin == RequestOrigin.Api
                && item.RequestedBy == actorUserId
                && item.MachineId == machineId
                && item.VariantId == variantId
                && item.LayoutId == layoutId
                && item.NavigationProfileId == navigationProfileId
                && item.Priority == priority);
        if (!equivalent)
        {
            throw new WorkflowRuleException(
                "IDEMPOTENCY_KEY_REUSED",
                "The batch idempotency key belongs to a different container-action payload.");
        }
    }

    private static void EnsureBatchItemsEquivalent(
        TransportBatchResult existing,
        IReadOnlyList<CreateTransportRequest> expected)
    {
        if (existing.Items.Count != expected.Count)
        {
            throw new WorkflowRuleException(
                "IDEMPOTENCY_KEY_REUSED",
                "The batch idempotency key belongs to a different container-action item set.");
        }

        var expectedByKey = expected.ToDictionary(item => item.IdempotencyKey, StringComparer.Ordinal);
        foreach (var item in existing.Items)
        {
            if (!expectedByKey.TryGetValue(item.IdempotencyKey, out var request))
            {
                throw new WorkflowRuleException(
                    "IDEMPOTENCY_KEY_REUSED",
                    "The batch idempotency key belongs to a different container-action item set.");
            }

            EnsureEquivalentPayload(item, request);
        }
    }

    private static string ChildIdempotencyKey(string prefix, string batchKey, Guid slotId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{batchKey}|{slotId:D}"));
        return $"{prefix}{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private sealed record ActivePositionRequest(
        TransportRequestStatus Status,
        string? FailureCode,
        int? RdsStatus,
        string? AgvId,
        DateTimeOffset? StartedAt,
        TransportReadinessState ReadinessState = TransportReadinessState.NotRequired);

    private sealed record PositionRow(
        Guid SlotId,
        int PositionNumber,
        OccupancyState Occupancy,
        LoadState Load,
        ActivePositionRequest? ActiveRequest,
        int? QueuePosition);

    private sealed record VariantPositionRow(Guid SlotId, int PositionNumber, bool HasActiveRequest);

    private static async Task<bool> HasAvailableEmptyStoreUnitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM ops.store_units u
            JOIN ops.store_locations sl ON sl.id = u.store_location_id
            WHERE u.load_state = 'EMPTY'
              AND u.removed_at IS NULL
              AND sl.is_enabled
            LIMIT 1
            """,
            connection,
            transaction);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasVacantStoreLocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM ops.store_locations sl
            WHERE sl.is_enabled
              AND NOT EXISTS (
                  SELECT 1 FROM ops.store_units u
                  WHERE u.store_location_id = sl.id
                    AND u.removed_at IS NULL
              )
            LIMIT 1
            """,
            connection,
            transaction);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
