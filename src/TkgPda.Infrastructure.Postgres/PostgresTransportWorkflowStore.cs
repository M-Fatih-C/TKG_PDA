using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresTransportWorkflowStore : ITransportWorkflowStore, IContainerActionStore, IPartDemandStore, IAsyncDisposable
{
    private const string ActiveStatusesSql = "'CREATED','VALIDATING','QUEUED','SENT_TO_RDS','RUNNING'";
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly RdsPhysicalAvailabilityPolicy _physicalPolicy;

    public PostgresTransportWorkflowStore(string connectionString)
        : this(NpgsqlDataSource.Create(connectionString), ownsDataSource: true)
    {
    }

    public PostgresTransportWorkflowStore(NpgsqlDataSource dataSource, bool ownsDataSource = false, RdsPhysicalAvailabilityPolicy? physicalPolicy = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _ownsDataSource = ownsDataSource;
        _physicalPolicy = physicalPolicy ?? new RdsPhysicalAvailabilityPolicy();
    }

    public Task<CreateTransportResult> CreateAndQueueAsync(
        CreateTransportRequest request,
        CancellationToken cancellationToken = default) =>
        CreateAndQueueAsync(request, null, cancellationToken);

    public async Task<CreateTransportResult> CreateAndQueueAsync(
        CreateTransportRequest request,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateShape();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockMachineRuntimeAsync(connection, transaction, request.MachineId, cancellationToken);
        var result = await CreateAndQueueWithinTransactionAsync(connection, transaction, request, batchId: null, eventContext, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<TransportBatchResult> CreateBatchAndQueueAsync(
        CreateTransportBatchRequest request,
        CancellationToken cancellationToken = default) =>
        CreateBatchAndQueueAsync(request, null, cancellationToken);

    public async Task<TransportBatchResult> CreateBatchAndQueueAsync(
        CreateTransportBatchRequest request,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateShape();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await LockMachineRuntimeAsync(connection, transaction, request.MachineId, cancellationToken);
        await LockIdempotencyKeyAsync(connection, transaction, request.IdempotencyKey, cancellationToken);
        var existingBatch = await FindBatchByIdempotencyKeyAsync(connection, transaction, request.IdempotencyKey, cancellationToken);
        if (existingBatch is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingBatch with { WasIdempotentReplay = true };
        }

        var batchId = await InsertBatchAsync(connection, transaction, request, cancellationToken);

        var children = new List<TransportRequestSnapshot>(request.Items.Count);
        foreach (var item in request.Items)
        {
            var childResult = await CreateAndQueueWithinTransactionAsync(connection, transaction, item, batchId, eventContext, cancellationToken, reservationOwnedByRequestId: null);
            children.Add(childResult.Request);
        }

        await transaction.CommitAsync(cancellationToken);
        return new TransportBatchResult(batchId, request.IdempotencyKey, request.MachineId, children, WasIdempotentReplay: false);
    }

    private async Task<CreateTransportResult> CreateAndQueueWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateTransportRequest request,
        Guid? batchId,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken,
        Guid? reservationOwnedByRequestId = null)
    {
        await LockIdempotencyKeyAsync(connection, transaction, request.IdempotencyKey, cancellationToken);
        request = await InheritActiveNavigationAsync(connection, transaction, request, cancellationToken);
        var existing = await FindByIdempotencyKeyAsync(connection, transaction, request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            EnsureEquivalentPayload(existing, request);
            return new CreateTransportResult(existing, WasIdempotentReplay: true);
        }

        await LockAndValidateRuntimeContextAsync(connection, transaction, request, cancellationToken);
        await ValidateProductionTransportContextAsync(connection, transaction, request, cancellationToken);
        var taskTypeId = await ResolveAndValidateMasterDataAsync(connection, transaction, request, cancellationToken);

        var registryProfile = await IsCommissioningRegistryProfileAsync(connection, transaction, request.NavigationProfileId, cancellationToken);
        if (registryProfile)
        {
            request = await AssignRegistrySharedEndpointAsync(connection, transaction, request, cancellationToken);
        }

        // Layout EMPTY_STORAGE remains the buffer only when this request is not
        // on a commissioning navigation profile. Registry requests resolve
        // EMPTY_SOURCE / EMPTY_RETURN through profile_shared_endpoint_bindings.
        if (!registryProfile
            && TransportRules.RequiresBufferSlot(request.TaskType)
            && TransportRules.BufferSlotId(request.TaskType, request.SourceSlotId, request.DestinationSlotId) is null)
        {
            var allocatedBufferSlotId = await AllocateBufferSlotAsync(connection, transaction, request, cancellationToken);
            request = request.TaskType == TransportTaskType.BringEmpty
                ? request with { SourceSlotId = allocatedBufferSlotId }
                : request with { DestinationSlotId = allocatedBufferSlotId };
        }

        // Customer UX — Automatic Machine Slot Resolution: when the operator
        // did not pick a specific physical machine-side slot themselves,
        // resolve one now from slots explicitly mapped (master.
        // layout_slot_parts) to the request's part on the active layout —
        // the Operator is never asked to understand SLOT-001/SLOT-002.
        var suppliedReservedSlotId = TransportRules.DeliversToMachine(request.TaskType)
            ? request.DestinationSlotId
            : request.SourceSlotId;
        if (!suppliedReservedSlotId.HasValue)
        {
            if (!request.PartId.HasValue)
            {
                throw new WorkflowRuleException(
                    "PART_REQUIRED_FOR_AUTOMATIC_SLOT_RESOLUTION",
                    "Otomatik konum seçimi için geçerli bir parça gereklidir.");
            }

            var resolvedMachineSlotId = await ResolveMachineSlotAsync(connection, transaction, request, cancellationToken);
            request = TransportRules.DeliversToMachine(request.TaskType)
                ? request with { DestinationSlotId = resolvedMachineSlotId }
                : request with { SourceSlotId = resolvedMachineSlotId };
        }

        var suppliedSlotIds = new[] { request.SourceSlotId, request.DestinationSlotId }
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Distinct()
            .Order()
            .ToArray();

        var slots = new Dictionary<Guid, SlotSnapshot>();
        foreach (var slotId in suppliedSlotIds)
        {
            var slot = await LockSlotAsync(connection, transaction, slotId, cancellationToken);
            TransportRules.ValidateSuppliedSlot(request, slot);
            slots.Add(slotId, slot);
        }

        if (request.DefersMachineSlotReservation)
        {
            TransportRules.ValidateDeferredMachineDestination(request, slots[request.ReservedSlotId]);
        }
        else
        {
            await EnsureSlotsAreAvailableAsync(connection, transaction, suppliedSlotIds, reservationOwnedByRequestId, cancellationToken);
            TransportRules.ValidateReservation(request, slots[request.ReservedSlotId]);
        }

        var bufferSlotId = TransportRules.BufferSlotId(request.TaskType, request.SourceSlotId, request.DestinationSlotId);
        if (bufferSlotId.HasValue)
        {
            TransportRules.ValidateBufferSlot(request, slots[bufferSlotId.Value]);
        }

        // WP-08: Kasa Doldu / remove-all. The destination the robot will use is
        // resolved here, validated against the finished-goods mapping and stored
        // on the request, so dispatch and completion use exactly what was
        // validated. It is deliberately not part of the slot reservation above:
        // many machines and positions share one drop position.
        if (request.EnforcesFinishedGoodsDestination && request.DestinationBusinessCode is null)
        {
            request = request with
            {
                DestinationSlotId = await ResolveFinishedGoodsDestinationAsync(connection, transaction, request, cancellationToken)
            };
        }

        foreach (var slotId in new[] { request.SourceSlotId, request.DestinationSlotId }.OfType<Guid>().Distinct().Order())
        {
            if (request.DefersMachineSlotReservation && slotId == request.DestinationSlotId)
            {
                continue;
            }

            var slot = slots.TryGetValue(slotId, out var existingSlot) ? existingSlot
                : await LockSlotAsync(connection, transaction, slotId, cancellationToken);
            await RequireSlotPhysicalConsistencyAsync(connection, transaction, slot, request.NavigationProfileId, cancellationToken);
        }

        var created = await InsertRequestAsync(connection, transaction, request, taskTypeId, batchId, cancellationToken);
        await ReserveStoreEndpointAsync(
            connection,
            transaction,
            created.Id,
            created.TaskType,
            request.NavigationProfileId,
            request.DestinationSlotId,
            request.SourceBusinessCode,
            request.DestinationBusinessCode,
            cancellationToken);
        await InsertEventAsync(
            connection,
            transaction,
            created.Id,
            request.RequestedBy,
            TransportEventTypes.RequestCreated,
            null,
            TransportRequestStatus.Created,
            "Transport request created by Phase 03 workflow engine.",
            new
            {
                idempotency_key = request.IdempotencyKey,
                batch_id = batchId,
                request_origin = request.Origin.ToDatabase(),
                task_type = request.TaskType.ToDatabase(),
                destination_business_code = request.DestinationBusinessCode,
                source_business_code = request.SourceBusinessCode
            },
            cancellationToken,
            eventContext);

        created = await TransitionAsync(
            connection,
            transaction,
            created,
            TransportRequestStatus.Validating,
            request.RequestedBy,
            "VALIDATION_STARTED",
            cancellationToken,
            eventContext: eventContext);

        created = await TransitionAsync(
            connection,
            transaction,
            created,
            TransportRequestStatus.Queued,
            request.RequestedBy,
            request.DefersMachineSlotReservation ? "PREPARATION_DEMAND_QUEUED" : "SLOT_RESERVED_AND_QUEUED",
            cancellationToken,
            new
            {
                reserved_slot_id = request.ReservedSlotId,
                reservation_kind = request.DefersMachineSlotReservation
                    ? "DEFERRED_DESTINATION"
                    : TransportRules.DeliversToMachine(request.TaskType) ? "DESTINATION" : "SOURCE",
                machine_slot_id = request.ReservedSlotId,
                empty_storage_slot_id = bufferSlotId,
                machine_slot_reservation_active = !request.DefersMachineSlotReservation
            },
            eventContext: eventContext);

        return new CreateTransportResult(created, WasIdempotentReplay: false);
    }

    public Task<CancelTransportResult> CancelAsync(
        Guid requestId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default) =>
        CancelAsync(requestId, actorUserId, null, cancellationToken);

    public async Task<CancelTransportResult> CancelAsync(
        Guid requestId,
        Guid? actorUserId,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lockedWorkflow = await LockWorkflowRequestAsync(connection, transaction, requestId, cancellationToken);
        var request = lockedWorkflow.Request;

        if (request.Status == TransportRequestStatus.Cancelled)
        {
            await transaction.CommitAsync(cancellationToken);
            return new CancelTransportResult(request, WasAlreadyCancelled: true);
        }

        if (!TransportStateMachine.CanCancelBeforeExecution(request.Status))
        {
            throw new WorkflowRuleException(
                WorkflowErrorCodes.CancellationAfterExecutionDeferred,
                $"Cancellation from {request.Status.ToDatabase()} is deferred to the executor integration phase.");
        }

        TransportStateMachine.EnsureTransition(request.Status, TransportRequestStatus.Cancelled);
        var previousStatus = request.Status;
        var resolvedActor = actorUserId ?? eventContext?.ActorUserId;
        var slotId = ReservedSlotId(request);
        var slot = lockedWorkflow.Slots[slotId];
        var reservationActive = await IsMachineSlotReservationActiveAsync(connection, transaction, request.Id, cancellationToken);
        var waitsForRemoval = request.TaskType == TransportTaskType.BringFull
            && await HasPredecessorAsync(connection, transaction, request.Id, cancellationToken);
        var restoresSlot = reservationActive && !waitsForRemoval;
        var restored = slot.State;
        if (restoresSlot)
        {
            restored = TransportRules.CancellationState(request.TaskType, slot.State);
            await UpdateSlotStateAsync(connection, transaction, slotId, restored, resolvedActor, cancellationToken);
        }

        request = await UpdateRequestStatusAsync(
            connection,
            transaction,
            request,
            TransportRequestStatus.Cancelled,
            cancellationToken);
        await ReleaseStoreReservationAsync(connection, transaction, request.Id, cancellationToken);
        await InsertEventAsync(
            connection,
            transaction,
            request.Id,
            resolvedActor,
            TransportEventTypes.RequestCancelled,
            previousStatus,
            TransportRequestStatus.Cancelled,
            restoresSlot
                ? "Pre-execution cancellation restored the deterministic slot state."
                : "Warehouse preparation demand was cancelled without changing the current machine slot state.",
            new
            {
                cancellation_source = "PRE_EXECUTION",
                evidence_type = "OPERATOR_COMMAND",
                restored_slot_id = restoresSlot ? slotId : (Guid?)null,
                occupancy_state = restored.Occupancy.ToDatabase(),
                load_state = restored.Load.ToDatabase(),
                machine_slot_reservation_active = reservationActive,
                waits_for_removal = waitsForRemoval
            },
            cancellationToken,
            eventContext);
        await CancelWaitingSuccessorsAsync(connection, transaction, request.Id, cancellationToken, eventContext);
        await AdvanceProductionEndRunAsync(connection, transaction, request.Id, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new CancelTransportResult(request, WasAlreadyCancelled: false);
    }

    public Task<TransportRequestSnapshot> MarkReadyAsync(
        Guid requestId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default) =>
        MarkReadyAsync(requestId, actorUserId, null, cancellationToken);

    public async Task<TransportRequestSnapshot> MarkReadyAsync(
        Guid requestId,
        Guid? actorUserId,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var request = await LockRequestAsync(connection, transaction, requestId, cancellationToken);

        if (TransportReadinessRules.IsMarkReadyNoOp(request.ReadinessState))
        {
            await transaction.CommitAsync(cancellationToken);
            return request;
        }

        TransportReadinessRules.EnsureCanMarkReady(request.Status, request.ReadinessState);

        // A material feed is only ready when a pallet was physically prepared
        // for it and confirmed in a preparation area. Without this the legacy
        // endpoint would open dispatch for a BRING_FULL with nothing standing
        // anywhere, and the robot would be sent to collect air.
        if (request.TaskType == TransportTaskType.BringFull)
        {
            await using var prepared = new NpgsqlCommand("""
                SELECT 1 FROM ops.preparation_pallets
                WHERE transport_request_id = @id AND released_at IS NULL AND ready_at IS NOT NULL
                """, connection, transaction);
            prepared.Parameters.AddWithValue("id", requestId);
            if (await prepared.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new WorkflowRuleException(
                    "PREPARATION_PALLET_REQUIRED",
                    "Dolu malzeme talebi ancak hazırlık alanında onaylanmış bir paletle hazır sayılabilir.");
            }
        }

        var readyAt = DateTimeOffset.UtcNow;
        await using (var command = new NpgsqlCommand(
                         """
                         UPDATE ops.transport_requests
                         SET readiness_state = 'READY', ready_at = @ready_at, updated_at = now()
                         WHERE id = @id
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("id", requestId);
            command.Parameters.AddWithValue("ready_at", readyAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertEventAsync(
            connection,
            transaction,
            requestId,
            actorUserId ?? eventContext?.ActorUserId,
            TransportEventTypes.WarehousePreparationReady,
            null,
            null,
            "Warehouse preparation completed; request marked READY.",
            new { ready_at = readyAt },
            cancellationToken,
            eventContext);

        await transaction.CommitAsync(cancellationToken);
        return request with { ReadinessState = TransportReadinessState.Ready, ReadyAt = readyAt };
    }

    public Task<TransportRequestSnapshot> RecordExecutionTransitionAsync(
        Guid requestId,
        TransportRequestStatus nextStatus,
        CancellationToken cancellationToken = default) =>
        RecordExecutionTransitionAsync(requestId, nextStatus, null, cancellationToken);

    public async Task<TransportRequestSnapshot> RecordExecutionTransitionAsync(
        Guid requestId,
        TransportRequestStatus nextStatus,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        if (nextStatus is not (TransportRequestStatus.SentToRds or TransportRequestStatus.Running or TransportRequestStatus.Completed))
        {
            throw new WorkflowRuleException("UNSUPPORTED_EXECUTION_UPDATE", "Only dispatch, running and successful completion are implemented in Phase 03.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lockedWorkflow = await LockWorkflowRequestAsync(connection, transaction, requestId, cancellationToken);
        var request = lockedWorkflow.Request;

        if (request.Status == nextStatus)
        {
            await transaction.CommitAsync(cancellationToken);
            return request;
        }

        TransportStateMachine.EnsureTransition(request.Status, nextStatus);
        var previousStatus = request.Status;

        if (nextStatus == TransportRequestStatus.Completed)
        {
            var slotId = ReservedSlotId(request);
            var completedState = TransportRules.CompletionState(request.TaskType, request.PartId, request.ContainerTypeId);
            await UpdateSlotStateAsync(connection, transaction, slotId, completedState, updatedBy: null, cancellationToken);

            var bufferSlotId = TransportRules.BufferSlotId(request.TaskType, request.SourceSlotId, request.DestinationSlotId);
            if (bufferSlotId.HasValue)
            {
                var bufferState = TransportRules.BufferCompletionState(request.TaskType, request.PartId, request.ContainerTypeId);
                await UpdateSlotStateAsync(connection, transaction, bufferSlotId.Value, bufferState, updatedBy: null, cancellationToken);
            }

            // A full container leaving a machine physically ends up in the
            // finished-goods store. Recorded in the same transaction as the
            // completion so the store can never disagree with the transport
            // that filled it. A non-finished return is a real position and is
            // occupied here instead of becoming a store delivery.
            await ApplyNonFinishedReturnCompletionAsync(connection, transaction, request, cancellationToken);
            await RecordStoreDeliveryAsync(connection, transaction, request, cancellationToken);
            await RecordStoreEmptyCollectionAsync(connection, transaction, request, cancellationToken);
        }

        request = await UpdateRequestStatusAsync(connection, transaction, request, nextStatus, cancellationToken);

        if (nextStatus == TransportRequestStatus.SentToRds)
        {
            await InsertEventAsync(
                connection,
                transaction,
                request.Id,
                actorUserId: null,
                TransportEventTypes.RobotTaskDispatched,
                previousStatus,
                nextStatus,
                "RDS task dispatched and confirmed.",
                new { executor_http_called = true },
                cancellationToken,
                eventContext);
        }
        else if (nextStatus == TransportRequestStatus.Running)
        {
            await InsertEventAsync(
                connection,
                transaction,
                request.Id,
                actorUserId: null,
                TransportEventTypes.RobotTaskStarted,
                previousStatus,
                nextStatus,
                "RDS task execution started.",
                new { executor_http_called = false },
                cancellationToken,
                eventContext);
        }
        else if (nextStatus == TransportRequestStatus.Completed)
        {
            await InsertEventAsync(
                connection,
                transaction,
                request.Id,
                actorUserId: null,
                TransportEventTypes.RobotTaskCompleted,
                previousStatus,
                nextStatus,
                "RDS task execution completed.",
                new { executor_http_called = false },
                cancellationToken,
                eventContext);

            await InsertEventAsync(
                connection,
                transaction,
                request.Id,
                actorUserId: null,
                TransportEventTypes.RequestCompleted,
                previousStatus,
                nextStatus,
                "Transport request completed.",
                new { executor_http_called = false },
                cancellationToken,
                eventContext);
        }

        if (nextStatus is TransportRequestStatus.Completed or TransportRequestStatus.Failed or TransportRequestStatus.Cancelled)
        {
            await ReleaseStoreReservationAsync(connection, transaction, request.Id, cancellationToken);
        }

        await AdvanceProductionEndRunAsync(connection, transaction, request.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return request;
    }

    public Task<TransportRequestSnapshot> RecordExecutionOutcomeAsync(
        Guid requestId,
        ExecutionOutcome outcome,
        CancellationToken cancellationToken = default) =>
        RecordExecutionOutcomeAsync(requestId, outcome, null, cancellationToken);

    public async Task<TransportRequestSnapshot> RecordExecutionOutcomeAsync(
        Guid requestId,
        ExecutionOutcome outcome,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Status is not (TransportRequestStatus.Failed or TransportRequestStatus.Cancelled))
        {
            throw new WorkflowRuleException("UNSUPPORTED_EXECUTION_OUTCOME", "Only executor-confirmed failure and cancellation outcomes are supported.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lockedWorkflow = await LockWorkflowRequestAsync(connection, transaction, requestId, cancellationToken);
        var request = lockedWorkflow.Request;

        if (request.Status == outcome.Status || request.Status is TransportRequestStatus.Completed or TransportRequestStatus.Failed or TransportRequestStatus.Cancelled)
        {
            await transaction.CommitAsync(cancellationToken);
            return request;
        }

        TransportStateMachine.EnsureTransition(request.Status, outcome.Status);
        var previousStatus = request.Status;
        request = await UpdateRequestOutcomeAsync(connection, transaction, request, outcome, cancellationToken);
        if (previousStatus is TransportRequestStatus.SentToRds or TransportRequestStatus.Running)
        {
            // Once execution was handed to the robot, a failure/cancellation
            // does not prove whether the physical pallet moved. Keep the exact
            // PICKUP/DROP endpoint held until an operator reconciles the area.
            foreach (var endpointSlotId in EndpointSlotIds(request))
            {
                await UpdateSlotStateAsync(
                    connection,
                    transaction,
                    endpointSlotId,
                    new SlotState(OccupancyState.Unknown, LoadState.Unknown, null, null),
                    updatedBy: null,
                    cancellationToken);
            }

            await MarkStoreReservationForReconciliationAsync(connection, transaction, request.Id, cancellationToken);
        }
        else
        {
            await ReleaseStoreReservationAsync(connection, transaction, request.Id, cancellationToken);
        }

        if (outcome.Status == TransportRequestStatus.Cancelled)
        {
            await InsertEventAsync(
                connection,
                transaction,
                request.Id,
                actorUserId: null,
                TransportEventTypes.RequestCancelled,
                previousStatus,
                outcome.Status,
                "RDS confirmed that execution was stopped.",
                new
                {
                    cancellation_source = "RDS_CONFIRMED",
                    evidence_type = outcome.EvidenceType,
                    failure_code = outcome.FailureCode,
                    failure_message = outcome.FailureMessage
                },
                cancellationToken,
                eventContext);
        }
        else
        {
            await InsertEventAsync(
                connection,
                transaction,
                request.Id,
                actorUserId: null,
                TransportEventTypes.RequestFailed,
                previousStatus,
                outcome.Status,
                "RDS reported a terminal execution failure.",
                new
                {
                    failure_code = outcome.FailureCode,
                    failure_message = outcome.FailureMessage,
                    evidence_type = outcome.EvidenceType
                },
                cancellationToken,
                eventContext);
        }

        await CancelWaitingSuccessorsAsync(connection, transaction, request.Id, cancellationToken, eventContext);
        await AdvanceProductionEndRunAsync(connection, transaction, request.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return request;
    }

    public async Task<LoadStateUpdateResult> UpdateLoadStateAsync(
        LoadStateUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TargetLoadState == LoadState.Unknown)
        {
            throw new WorkflowRuleException("LOAD_STATE_NOT_SELECTABLE", "UNKNOWN cannot be selected by an operator.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await LockMachineRuntimeAsync(connection, transaction, request.MachineId, cancellationToken);
        Guid? activeLayoutId;
        await using (var runtime = new NpgsqlCommand(
                         "SELECT active_layout_id FROM ops.machine_runtime_state WHERE machine_id = @machine_id",
                         connection,
                         transaction))
        {
            runtime.Parameters.AddWithValue("machine_id", request.MachineId);
            var value = await runtime.ExecuteScalarAsync(cancellationToken);
            activeLayoutId = value is null or DBNull ? null : (Guid)value;
        }

        var slot = await LockSlotAsync(connection, transaction, request.SlotId, cancellationToken);
        if (slot.MachineId != request.MachineId || activeLayoutId != slot.LayoutId)
        {
            throw new WorkflowRuleException("SLOT_NOT_IN_ACTIVE_LAYOUT", "The slot must belong to the machine's active layout.");
        }

        if (!slot.IsEnabled)
        {
            throw new WorkflowRuleException("SLOT_DISABLED", "The slot is disabled.");
        }

        if (slot.Role == SlotRole.Machine)
        {
            throw new WorkflowRuleException("MACHINE_REGION_FORBIDDEN", "Machine/cell display regions cannot report load state.");
        }

        if (slot.State.Occupancy != OccupancyState.Occupied)
        {
            throw new WorkflowRuleException("OCCUPIED_SLOT_REQUIRED", "Load state can be reported only for an occupied slot.");
        }

        await using (var active = new NpgsqlCommand(
                         $"""
                         SELECT 1
                         FROM ops.transport_requests
                         WHERE status IN ({ActiveStatusesSql})
                           AND machine_slot_reservation_active
                           AND (source_slot_id = @slot_id OR destination_slot_id = @slot_id)
                         LIMIT 1
                         """,
                         connection,
                         transaction))
        {
            active.Parameters.AddWithValue("slot_id", request.SlotId);
            if (await active.ExecuteScalarAsync(cancellationToken) is not null)
            {
                throw new WorkflowRuleException("SLOT_BUSY", "Load state cannot change while an active transport uses the slot.");
            }
        }

        if (slot.State.Load == request.TargetLoadState)
        {
            await using var version = new NpgsqlCommand(
                "SELECT row_version FROM ops.slot_runtime_state WHERE layout_slot_id = @slot_id",
                connection,
                transaction);
            version.Parameters.AddWithValue("slot_id", request.SlotId);
            var currentVersion = Convert.ToInt64(await version.ExecuteScalarAsync(cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new(request.MachineId, request.SlotId, slot.State.Occupancy, slot.State.Load, currentVersion, true);
        }

        long rowVersion;
        await using (var update = new NpgsqlCommand(
                         """
                         UPDATE ops.slot_runtime_state
                         SET load_state = @load_state,
                             updated_by = @updated_by,
                             row_version = row_version + 1,
                             updated_at = now()
                         WHERE layout_slot_id = @slot_id
                         RETURNING row_version
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("slot_id", request.SlotId);
            update.Parameters.AddWithValue("load_state", request.TargetLoadState.ToDatabase());
            update.Parameters.AddWithValue("updated_by", request.ActorUserId);
            rowVersion = Convert.ToInt64(await update.ExecuteScalarAsync(cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return new(request.MachineId, request.SlotId, OccupancyState.Occupied, request.TargetLoadState, rowVersion, false);
    }

    public async Task SwitchVariantAsync(VariantSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await LockMachineRuntimeAsync(connection, transaction, selection.MachineId, cancellationToken);
        var navigationProfileId = await ResolvePublishedNavigationAsync(
            connection,
            transaction,
            selection.MachineId,
            selection.VariantId,
            selection.NavigationProfileId,
            cancellationToken);
        selection = selection with { NavigationProfileId = navigationProfileId };
        await GuardProductionVariantSwitchAsync(connection, transaction, selection, cancellationToken);

        await using (var active = new NpgsqlCommand(
                         $"SELECT id FROM ops.transport_requests WHERE machine_id = @machine_id AND status IN ({ActiveStatusesSql}) LIMIT 1",
                         connection,
                         transaction))
        {
            active.Parameters.AddWithValue("machine_id", selection.MachineId);
            if (await active.ExecuteScalarAsync(cancellationToken) is not null)
            {
                throw new WorkflowRuleException("ACTIVE_TRANSPORT_EXISTS", "Variant changes are blocked while a transport reservation is active.");
            }
        }

        await ValidateVariantLayoutAssignmentAsync(
            connection,
            transaction,
            selection.MachineId,
            selection.VariantId,
            selection.LayoutId,
            selection.NavigationProfileId,
            cancellationToken);

        await using (var update = new NpgsqlCommand(
                         """
                         UPDATE ops.machine_runtime_state
                         SET active_variant_id = @variant_id,
                             active_layout_id = @layout_id,
                             active_navigation_id = @navigation_id,
                             selected_by = @selected_by,
                             selected_at = now(),
                             row_version = row_version + 1,
                             updated_at = now()
                         WHERE machine_id = @machine_id
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("machine_id", selection.MachineId);
            update.Parameters.AddWithValue("variant_id", selection.VariantId);
            update.Parameters.AddWithValue("layout_id", selection.LayoutId);
            AddNullableUuid(update, "navigation_id", selection.NavigationProfileId);
            AddNullableUuid(update, "selected_by", selection.SelectedBy);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync();
        }
    }

    private static async Task LockIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@idempotency_key, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task LockMachineRuntimeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid machineId,
        CancellationToken cancellationToken,
        bool requireConfigured = true)
    {
        await using var command = new NpgsqlCommand(
            "SELECT machine_id FROM ops.machine_runtime_state WHERE machine_id = @machine_id FOR UPDATE",
            connection,
            transaction);
        command.Parameters.AddWithValue("machine_id", machineId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null && requireConfigured)
        {
            throw new WorkflowRuleException("RUNTIME_STATE_NOT_CONFIGURED", "Machine runtime state must exist before changing workflow state.");
        }
    }

    private static async Task LockAndValidateRuntimeContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateTransportRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT active_variant_id, active_layout_id, active_navigation_id
            FROM ops.machine_runtime_state
            WHERE machine_id = @machine_id
            FOR UPDATE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("machine_id", request.MachineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new WorkflowRuleException("RUNTIME_STATE_NOT_CONFIGURED", "The machine has no active runtime selection.");
        }

        Guid? activeVariant = reader.IsDBNull(0) ? null : reader.GetGuid(0);
        Guid? activeLayout = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        Guid? activeNavigation = reader.IsDBNull(2) ? null : reader.GetGuid(2);

        if (activeVariant != request.VariantId || activeLayout != request.LayoutId)
        {
            throw new WorkflowRuleException("RUNTIME_CONTEXT_MISMATCH", "Request machine, variant and layout must match the active runtime context.");
        }

        if (request.NavigationProfileId.HasValue && activeNavigation != request.NavigationProfileId)
        {
            throw new WorkflowRuleException("NAVIGATION_CONTEXT_MISMATCH", "A supplied navigation profile must match the active profile.");
        }
    }

    private static async Task<Guid> ResolveAndValidateMasterDataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateTransportRequest request,
        CancellationToken cancellationToken)
    {
        await ValidateVariantLayoutAssignmentAsync(
            connection,
            transaction,
            request.MachineId,
            request.VariantId,
            request.LayoutId,
            request.NavigationProfileId,
            cancellationToken);

        if (request.ProductGroupId.HasValue)
        {
            await EnsureExistsAsync(
                connection,
                transaction,
                "SELECT 1 FROM master.product_groups WHERE id = @id AND variant_id = @scope_id",
                request.ProductGroupId.Value,
                request.VariantId,
                "PRODUCT_GROUP_CONFLICT",
                "The product group does not belong to the active variant.",
                cancellationToken);
        }

        if (request.PartId.HasValue)
        {
            await EnsureExistsAsync(
                connection,
                transaction,
                "SELECT 1 FROM master.parts WHERE id = @id AND is_active",
                request.PartId.Value,
                null,
                "PART_NOT_FOUND",
                "Seçilen aktif parça bulunamadı.",
                cancellationToken);

            if (request.ProductGroupId.HasValue)
            {
                await using var membership = new NpgsqlCommand(
                    """
                    SELECT 1
                    FROM master.product_groups pg
                    WHERE pg.id = @product_group_id
                      AND pg.variant_id = @variant_id
                      AND pg.is_active
                      AND (
                          pg.main_part_id = @part_id
                          OR EXISTS (
                              SELECT 1
                              FROM master.product_group_parts gpp
                              WHERE gpp.product_group_id = pg.id
                                AND gpp.part_id = @part_id
                                AND gpp.is_active
                          )
                      )
                    """,
                    connection,
                    transaction);
                membership.Parameters.AddWithValue("product_group_id", request.ProductGroupId.Value);
                membership.Parameters.AddWithValue("variant_id", request.VariantId);
                membership.Parameters.AddWithValue("part_id", request.PartId.Value);
                if (await membership.ExecuteScalarAsync(cancellationToken) is null)
                {
                    throw new WorkflowRuleException(
                        "PART_PRODUCT_GROUP_CONFLICT",
                        "Seçilen parça ürün grubu ve varyantla uyumlu değil.");
                }
            }
            else
            {
                await using var membership = new NpgsqlCommand(
                    """
                    SELECT 1
                    FROM master.product_groups pg
                    WHERE pg.variant_id = @variant_id
                      AND pg.is_active
                      AND (
                          pg.main_part_id = @part_id
                          OR EXISTS (
                              SELECT 1
                              FROM master.product_group_parts gpp
                              WHERE gpp.product_group_id = pg.id
                                AND gpp.part_id = @part_id
                                AND gpp.is_active
                          )
                      )
                    LIMIT 1
                    """,
                    connection,
                    transaction);
                membership.Parameters.AddWithValue("variant_id", request.VariantId);
                membership.Parameters.AddWithValue("part_id", request.PartId.Value);
                if (await membership.ExecuteScalarAsync(cancellationToken) is null)
                {
                    throw new WorkflowRuleException(
                        "PART_VARIANT_CONFLICT",
                        "Seçilen parça aktif varyanta ait değil.");
                }
            }
        }

        if (request.ContainerTypeId.HasValue)
        {
            await EnsureExistsAsync(connection, transaction, "SELECT 1 FROM master.container_types WHERE id = @id", request.ContainerTypeId.Value, null, "CONTAINER_NOT_FOUND", "The supplied container type does not exist.", cancellationToken);
        }

        await using var taskType = new NpgsqlCommand("SELECT id FROM master.task_types WHERE code = @code AND is_active", connection, transaction);
        taskType.Parameters.AddWithValue("code", request.TaskType.ToDatabase());
        return await taskType.ExecuteScalarAsync(cancellationToken) is Guid id
            ? id
            : throw new WorkflowRuleException("TASK_TYPE_NOT_FOUND", "The requested logistics task type is not configured.");
    }

    private static async Task<CreateTransportRequest> InheritActiveNavigationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateTransportRequest request,
        CancellationToken cancellationToken)
    {
        if (request.NavigationProfileId.HasValue)
        {
            return request;
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT active_navigation_id
            FROM ops.machine_runtime_state
            WHERE machine_id = @machine_id
            FOR UPDATE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("machine_id", request.MachineId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid navigationId
            ? request with { NavigationProfileId = navigationId }
            : request;
    }

    private static async Task<Guid?> ResolvePublishedNavigationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid machineId,
        Guid variantId,
        Guid? requestedNavigationProfileId,
        CancellationToken cancellationToken)
    {
        if (requestedNavigationProfileId.HasValue)
        {
            return requestedNavigationProfileId;
        }

        var published = new List<(Guid Id, bool IsDefault)>();
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT p.id, a.is_default
                         FROM master.variant_navigation_profile_assignments a
                         JOIN master.navigation_profiles p
                           ON p.id = a.navigation_profile_id AND p.machine_id = a.machine_id
                         WHERE a.variant_id = @variant_id
                           AND a.machine_id = @machine_id
                           AND p.status = 'PUBLISHED'
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("machine_id", machineId);
            command.Parameters.AddWithValue("variant_id", variantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                published.Add((reader.GetGuid(0), reader.GetBoolean(1)));
            }
        }

        if (published.Count == 0)
        {
            return null;
        }

        if (published.Count == 1)
        {
            return published[0].Id;
        }

        var defaults = published.Where(row => row.IsDefault).ToArray();
        if (defaults.Length == 1)
        {
            return defaults[0].Id;
        }

        throw new WorkflowRuleException(
            "NAVIGATION_PROFILE_AMBIGUOUS",
            "More than one published navigation profile is assigned, and no single default identifies the map.");
    }

    private static async Task ValidateVariantLayoutAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid machineId,
        Guid variantId,
        Guid layoutId,
        Guid? navigationProfileId,
        CancellationToken cancellationToken)
    {
        await using (var assignment = new NpgsqlCommand(
                         """
                         SELECT 1
                         FROM master.variants v
                         JOIN master.variant_layout_assignments a
                           ON a.variant_id = v.id AND a.machine_id = v.machine_id
                         JOIN master.layouts l
                           ON l.id = a.layout_id AND l.machine_id = a.machine_id
                         WHERE v.id = @variant_id
                           AND v.machine_id = @machine_id
                           AND l.id = @layout_id
                           AND l.status = 'PUBLISHED'
                         """,
                         connection,
                         transaction))
        {
            assignment.Parameters.AddWithValue("machine_id", machineId);
            assignment.Parameters.AddWithValue("variant_id", variantId);
            assignment.Parameters.AddWithValue("layout_id", layoutId);
            if (await assignment.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new WorkflowRuleException("VARIANT_LAYOUT_CONFLICT", "The published layout is not assigned to this machine variant.");
            }
        }

        if (!navigationProfileId.HasValue)
        {
            return;
        }

        await using var navigation = new NpgsqlCommand(
            """
            SELECT 1
            FROM master.variant_navigation_profile_assignments a
            JOIN master.navigation_profiles p
              ON p.id = a.navigation_profile_id AND p.machine_id = a.machine_id
            WHERE a.variant_id = @variant_id
              AND a.machine_id = @machine_id
              AND p.id = @navigation_id
              AND p.status = 'PUBLISHED'
            """,
            connection,
            transaction);
        navigation.Parameters.AddWithValue("machine_id", machineId);
        navigation.Parameters.AddWithValue("variant_id", variantId);
        navigation.Parameters.AddWithValue("navigation_id", navigationProfileId.Value);
        if (await navigation.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new WorkflowRuleException("NAVIGATION_PROFILE_CONFLICT", "The supplied navigation profile is not a published assignment for this variant.");
        }
    }

    private static async Task EnsureExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid id,
        Guid? scopeId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        if (scopeId.HasValue)
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
        }

        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new WorkflowRuleException(errorCode, errorMessage);
        }
    }

    private static async Task<SlotSnapshot> LockSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid slotId,
        CancellationToken cancellationToken)
    {
        await using (var ensureState = new NpgsqlCommand(
                         """
                         INSERT INTO ops.slot_runtime_state (layout_slot_id)
                         SELECT id FROM master.layout_slots WHERE id = @slot_id
                         ON CONFLICT (layout_slot_id) DO NOTHING
                         """,
                         connection,
                         transaction))
        {
            ensureState.Parameters.AddWithValue("slot_id", slotId);
            await ensureState.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT s.id,
                   l.machine_id,
                   s.layout_id,
                   s.slot_role,
                   s.is_enabled,
                   s.default_container_type_id,
                   rs.occupancy_state,
                   rs.load_state,
                   rs.current_part_id,
                   rs.current_container_type_id,
                   COALESCE(ARRAY(
                       SELECT lsp.part_id
                       FROM master.layout_slot_parts lsp
                       WHERE lsp.layout_slot_id = s.id
                       ORDER BY lsp.part_id
                   ), ARRAY[]::uuid[]) AS known_part_ids
            FROM master.layout_slots s
            JOIN master.layouts l ON l.id = s.layout_id
            JOIN ops.slot_runtime_state rs ON rs.layout_slot_id = s.id
            WHERE s.id = @slot_id
            FOR UPDATE OF rs
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("slot_id", slotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new WorkflowRuleException("SLOT_NOT_FOUND", $"Layout slot {slotId} does not exist.");
        }

        return new SlotSnapshot(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            WorkflowCode.ParseSlotRole(reader.GetString(3)),
            reader.GetBoolean(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.GetFieldValue<Guid[]>(10).ToHashSet(),
            new SlotState(
                WorkflowCode.ParseOccupancy(reader.GetString(6)),
                WorkflowCode.ParseLoad(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9)));
    }

    /// <summary>
    /// Customer Workflow — Empty Container Buffer: atomically picks one
    /// configured EMPTY_STORAGE slot on the request's layout that is
    /// currently in the state this operation requires and has no active
    /// transport reservation. <c>FOR UPDATE ... SKIP LOCKED</c> makes this
    /// safe under concurrency without any broader workflow redesign: two
    /// concurrent callers competing for the same candidate set simply land
    /// on different rows (or, if only one candidate exists, the second
    /// caller correctly finds none available rather than double-booking it).
    /// Ordering by the slot's own configured display order keeps the choice
    /// deterministic.
    /// </summary>
    private static async Task<Guid> AllocateBufferSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateTransportRequest request,
        CancellationToken cancellationToken)
    {
        var expected = TransportRules.ExpectedBufferState(request.TaskType);
        await using var command = new NpgsqlCommand(
            $"""
             SELECT ls.id
             FROM master.layout_slots ls
             JOIN ops.slot_runtime_state rs ON rs.layout_slot_id = ls.id
             WHERE ls.layout_id = @layout_id
               AND ls.slot_role = 'EMPTY_STORAGE'
               AND ls.is_enabled
               AND rs.occupancy_state = @occupancy_state
               AND rs.load_state = @load_state
               AND NOT EXISTS (
                   SELECT 1 FROM ops.transport_requests tr
                   WHERE tr.status IN ({ActiveStatusesSql})
                     AND tr.machine_slot_reservation_active
                     AND (tr.source_slot_id = ls.id OR tr.destination_slot_id = ls.id)
               )
             ORDER BY ls.display_order, ls.code
             FOR UPDATE OF rs SKIP LOCKED
             LIMIT 1
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("layout_id", request.LayoutId);
        command.Parameters.AddWithValue("occupancy_state", expected.Occupancy.ToDatabase());
        command.Parameters.AddWithValue("load_state", expected.Load.ToDatabase());
        var slotId = await command.ExecuteScalarAsync(cancellationToken);
        return slotId is Guid id
            ? id
            : throw new WorkflowRuleException(
                "EMPTY_BUFFER_UNAVAILABLE",
                $"No configured EMPTY_STORAGE buffer slot is currently {expected.Occupancy.ToDatabase()}/{expected.Load.ToDatabase()} and available on this layout.");
    }

    /// <summary>
    /// Customer UX — Automatic Machine Slot Resolution. Only slots explicitly
    /// curated in master.layout_slot_parts for this part are eligible — the
    /// unstructured metadata_json evidence on master.layout_slots is never
    /// consulted here. Safe under concurrency via the same FOR UPDATE ...
    /// SKIP LOCKED pattern as the EMPTY_STORAGE buffer allocator: two
    /// concurrent callers competing for the same candidate set land on
    /// different rows, or correctly find none left rather than double-
    /// booking one. Ordering by the slot's own configured display order
    /// keeps the choice deterministic.
    /// </summary>
    private static async Task<Guid> ResolveMachineSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateTransportRequest request,
        CancellationToken cancellationToken)
    {
        var (notFoundCode, notFoundMessage) = request.TaskType switch
        {
            TransportTaskType.BringEmpty => ("BRING_EMPTY_SLOT_NOT_FOUND", "Bu parça için boş konum bulunamadı."),
            TransportTaskType.RemoveFull => ("REMOVE_FULL_SLOT_NOT_FOUND", "Bu parça için dolu kasa bulunamadı."),
            TransportTaskType.RemoveEmpty => ("REMOVE_EMPTY_SLOT_NOT_FOUND", "Bu parça için boş kasa bulunamadı."),
            TransportTaskType.BringFull => ("BRING_FULL_SLOT_NOT_FOUND", "Bu parça için boşalmış konum bulunamadı."),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.TaskType, null)
        };
        var expected = TransportRules.ExpectedMachineSlotState(request.TaskType);

        await using var command = new NpgsqlCommand(
            $"""
             SELECT ls.id
             FROM master.layout_slots ls
             JOIN master.layout_slot_parts lsp ON lsp.layout_slot_id = ls.id
             JOIN ops.slot_runtime_state rs ON rs.layout_slot_id = ls.id
             WHERE ls.layout_id = @layout_id
               AND lsp.part_id = @part_id
               AND ls.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
               AND ls.is_enabled
               AND rs.occupancy_state = @occupancy_state
               AND rs.load_state = @load_state
               AND NOT EXISTS (
                   SELECT 1 FROM ops.transport_requests tr
                   WHERE tr.status IN ({ActiveStatusesSql})
                     AND tr.machine_slot_reservation_active
                     AND (tr.source_slot_id = ls.id OR tr.destination_slot_id = ls.id)
               )
             ORDER BY ls.display_order, ls.code, ls.id
             FOR UPDATE OF rs SKIP LOCKED
             LIMIT 1
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("layout_id", request.LayoutId);
        AddNullableUuid(command, "part_id", request.PartId);
        command.Parameters.AddWithValue("occupancy_state", expected.Occupancy.ToDatabase());
        command.Parameters.AddWithValue("load_state", expected.Load.ToDatabase());
        var slotId = await command.ExecuteScalarAsync(cancellationToken);
        return slotId is Guid id ? id : throw new WorkflowRuleException(notFoundCode, notFoundMessage);
    }

    private static async Task EnsureSlotsAreAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] slotIds,
        Guid? exceptRequestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             SELECT id
             FROM ops.transport_requests
             WHERE status IN ({ActiveStatusesSql})
               AND machine_slot_reservation_active
               AND (source_slot_id = ANY(@slot_ids) OR destination_slot_id = ANY(@slot_ids))
               AND (@except::uuid IS NULL OR id <> @except)
             LIMIT 1
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("slot_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, slotIds);
        command.Parameters.AddWithValue("except", (object?)exceptRequestId ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(cancellationToken) is not null)
        {
            throw new WorkflowRuleException("SLOT_ALREADY_RESERVED", "A supplied slot already has an active transport reservation.");
        }
    }

    private static async Task<TransportRequestSnapshot> InsertRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateTransportRequest request,
        Guid taskTypeId,
        Guid? batchId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ops.transport_requests (
                id, idempotency_key, request_origin, requested_by,
                machine_id, variant_id, layout_id, navigation_id,
                product_group_id, part_id, container_type_id, task_type_id,
                source_slot_id, destination_slot_id, destination_business_code, source_business_code, status, priority, batch_id,
                readiness_state, production_session_id, machine_slot_reservation_active)
            VALUES (
                @id, @idempotency_key, @request_origin, @requested_by,
                @machine_id, @variant_id, @layout_id, @navigation_id,
                @product_group_id, @part_id, @container_type_id, @task_type_id,
                @source_slot_id, @destination_slot_id, @destination_business_code, @source_business_code, 'CREATED', @priority, @batch_id,
                @readiness_state,
                (SELECT id FROM ops.production_sessions WHERE machine_id = @machine_id AND ended_at IS NULL),
                @machine_slot_reservation_active)
            RETURNING request_no
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("request_origin", request.Origin.ToDatabase());
        AddNullableUuid(command, "requested_by", request.RequestedBy);
        command.Parameters.AddWithValue("machine_id", request.MachineId);
        command.Parameters.AddWithValue("variant_id", request.VariantId);
        command.Parameters.AddWithValue("layout_id", request.LayoutId);
        AddNullableUuid(command, "navigation_id", request.NavigationProfileId);
        AddNullableUuid(command, "product_group_id", request.ProductGroupId);
        AddNullableUuid(command, "part_id", request.PartId);
        AddNullableUuid(command, "container_type_id", request.ContainerTypeId);
        command.Parameters.AddWithValue("task_type_id", taskTypeId);
        AddNullableUuid(command, "source_slot_id", request.SourceSlotId);
        AddNullableUuid(command, "destination_slot_id", request.DestinationSlotId);
        AddNullableText(command, "destination_business_code", request.DestinationBusinessCode);
        AddNullableText(command, "source_business_code", request.SourceBusinessCode);
        command.Parameters.AddWithValue("priority", request.Priority);
        AddNullableUuid(command, "batch_id", batchId);
        command.Parameters.AddWithValue("readiness_state", request.InitialReadinessState.ToDatabase());
        command.Parameters.AddWithValue("machine_slot_reservation_active", !request.DefersMachineSlotReservation);
        var requestNo = (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Request insert did not return a request number."));

        return new TransportRequestSnapshot(
            id,
            requestNo,
            request.IdempotencyKey,
            TransportRequestStatus.Created,
            request.TaskType,
            request.Origin,
            request.RequestedBy,
            request.MachineId,
            request.VariantId,
            request.LayoutId,
            request.NavigationProfileId,
            request.ProductGroupId,
            request.PartId,
            request.ContainerTypeId,
            request.SourceSlotId,
            request.DestinationSlotId,
            request.Priority,
            request.InitialReadinessState,
            ReadyAt: null,
            request.DestinationBusinessCode,
            request.SourceBusinessCode);
    }

    private static async Task<TransportRequestSnapshot?> FindByIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RequestSelectSql + " WHERE r.idempotency_key = @key", connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRequest(reader) : null;
    }

    private static async Task<Guid> InsertBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateTransportBatchRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ops.transport_batches (idempotency_key, request_origin, requested_by, machine_id)
            VALUES (@idempotency_key, @request_origin, @requested_by, @machine_id)
            RETURNING id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("request_origin", request.Origin.ToDatabase());
        AddNullableUuid(command, "requested_by", request.RequestedBy);
        command.Parameters.AddWithValue("machine_id", request.MachineId);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Batch insert did not return an id."));
    }

    private static async Task<TransportBatchResult?> FindBatchByIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        Guid batchId;
        Guid machineId;
        await using (var command = new NpgsqlCommand(
                         "SELECT id, machine_id FROM ops.transport_batches WHERE idempotency_key = @key",
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("key", key);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            batchId = reader.GetGuid(0);
            machineId = reader.GetGuid(1);
        }

        var children = new List<TransportRequestSnapshot>();
        await using (var command = new NpgsqlCommand(
                         RequestSelectSql + " WHERE r.batch_id = @batch_id ORDER BY r.request_no",
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("batch_id", batchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                children.Add(ReadRequest(reader));
            }
        }

        return new TransportBatchResult(batchId, key, machineId, children, WasIdempotentReplay: false);
    }

    private static async Task<TransportRequestSnapshot> FindRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RequestSelectSql + " WHERE r.id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadRequest(reader)
            : throw new WorkflowRuleException("REQUEST_NOT_FOUND", $"Transport request {requestId} does not exist.");
    }

    private static async Task<LockedWorkflowRequest> LockWorkflowRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        // The first read discovers immutable lock-scope identifiers without taking a row lock.
        // Every mutation then follows the global order: machine -> UUID-ordered slots -> request.
        var discovered = await FindRequestAsync(connection, transaction, requestId, cancellationToken);
        await LockMachineRuntimeAsync(connection, transaction, discovered.MachineId, cancellationToken);

        var slots = new Dictionary<Guid, SlotSnapshot>();
        foreach (var slotId in EndpointSlotIds(discovered))
        {
            slots.Add(slotId, await LockSlotAsync(connection, transaction, slotId, cancellationToken));
        }

        var locked = await LockRequestAsync(connection, transaction, requestId, cancellationToken);
        if (locked.MachineId != discovered.MachineId
            || locked.TaskType != discovered.TaskType
            || locked.SourceSlotId != discovered.SourceSlotId
            || locked.DestinationSlotId != discovered.DestinationSlotId)
        {
            throw new WorkflowRuleException(
                "REQUEST_LOCK_SCOPE_CHANGED",
                "Transport request lock-scope fields changed while acquiring the workflow lock hierarchy.");
        }

        return new LockedWorkflowRequest(locked, slots);
    }

    private static async Task<TransportRequestSnapshot> LockRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RequestSelectSql + " WHERE r.id = @id FOR UPDATE OF r", connection, transaction);
        command.Parameters.AddWithValue("id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadRequest(reader)
            : throw new WorkflowRuleException("REQUEST_NOT_FOUND", $"Transport request {requestId} does not exist.");
    }

    private static TransportRequestSnapshot ReadRequest(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetInt64(1),
        reader.GetString(2),
        WorkflowCode.ParseTransportStatus(reader.GetString(3)),
        WorkflowCode.ParseTaskType(reader.GetString(4)),
        WorkflowCode.ParseRequestOrigin(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetGuid(6),
        reader.GetGuid(7),
        reader.GetGuid(8),
        reader.GetGuid(9),
        reader.IsDBNull(10) ? null : reader.GetGuid(10),
        reader.IsDBNull(11) ? null : reader.GetGuid(11),
        reader.IsDBNull(12) ? null : reader.GetGuid(12),
        reader.IsDBNull(13) ? null : reader.GetGuid(13),
        reader.IsDBNull(14) ? null : reader.GetGuid(14),
        reader.IsDBNull(15) ? null : reader.GetGuid(15),
        reader.GetInt32(16),
        WorkflowCode.ParseReadinessState(reader.GetString(17)),
        reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
        reader.IsDBNull(19) ? null : reader.GetString(19),
        reader.IsDBNull(20) ? null : reader.GetString(20));

    private static void EnsureEquivalentPayload(TransportRequestSnapshot existing, CreateTransportRequest request)
    {
        // Customer Workflow — Empty Container Buffer: a retry that again omits
        // the buffer-side slot is still the same logical request even though
        // the stored copy now carries whichever buffer and/or machine slot
        // backend allocation resolved the first time. A retry that keeps an
        // endpoint omitted is therefore equivalent; an explicitly supplied
        // endpoint must still match exactly.
        var defersBufferSource = request.TaskType == TransportTaskType.BringEmpty && request.SourceSlotId is null;
        var defersBufferDestination = request.TaskType == TransportTaskType.RemoveEmpty && request.DestinationSlotId is null;
        var defersMachineSource = request.TaskType is (TransportTaskType.RemoveFull or TransportTaskType.RemoveEmpty)
            && request.SourceSlotId is null;
        var defersMachineDestination = TransportRules.DeliversToMachine(request.TaskType)
            && request.DestinationSlotId is null;
        // WP-08: the finished-goods destination is resolved server-side on the
        // first attempt; a retry that omits it is the same logical request.
        var defersFinishedGoodsDestination = request.EnforcesFinishedGoodsDestination
            && request.DestinationSlotId is null;
        var defersBusinessCode = request.DestinationBusinessCode is null;

        var equivalent = existing.Origin == request.Origin
            && existing.RequestedBy == request.RequestedBy
            && existing.MachineId == request.MachineId
            && existing.VariantId == request.VariantId
            && existing.LayoutId == request.LayoutId
            && existing.NavigationProfileId == request.NavigationProfileId
            && existing.ProductGroupId == request.ProductGroupId
            && existing.PartId == request.PartId
            && existing.ContainerTypeId == request.ContainerTypeId
            && existing.TaskType == request.TaskType
            && (defersBufferSource || defersMachineSource || existing.SourceSlotId == request.SourceSlotId)
            && (defersBufferDestination || defersMachineDestination || defersFinishedGoodsDestination || existing.DestinationSlotId == request.DestinationSlotId)
            && (defersBusinessCode || existing.DestinationBusinessCode == request.DestinationBusinessCode)
            && existing.Priority == request.Priority;

        if (!equivalent)
        {
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_REUSED", "The idempotency key belongs to a different request payload.");
        }
    }

    private static async Task<TransportRequestSnapshot> TransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransportRequestSnapshot request,
        TransportRequestStatus nextStatus,
        Guid? actorUserId,
        string eventType,
        CancellationToken cancellationToken,
        object? data = null,
        OperationalEventContext? eventContext = null)
    {
        var previousStatus = request.Status;
        TransportStateMachine.EnsureTransition(previousStatus, nextStatus);
        var updated = await UpdateRequestStatusAsync(connection, transaction, request, nextStatus, cancellationToken);
        await InsertEventAsync(
            connection,
            transaction,
            request.Id,
            actorUserId ?? eventContext?.ActorUserId,
            eventType,
            previousStatus,
            nextStatus,
            $"Status changed from {previousStatus.ToDatabase()} to {nextStatus.ToDatabase()}.",
            data,
            cancellationToken,
            eventContext);
        return updated;
    }

    private static async Task<TransportRequestSnapshot> UpdateRequestStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransportRequestSnapshot request,
        TransportRequestStatus nextStatus,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE ops.transport_requests
            SET status = @status,
                queued_at = CASE WHEN @status = 'QUEUED' THEN COALESCE(queued_at, now()) ELSE queued_at END,
                started_at = CASE WHEN @status = 'RUNNING' THEN COALESCE(started_at, now()) ELSE started_at END,
                completed_at = CASE WHEN @status = 'COMPLETED' THEN COALESCE(completed_at, now()) ELSE completed_at END,
                cancelled_at = CASE WHEN @status = 'CANCELLED' THEN COALESCE(cancelled_at, now()) ELSE cancelled_at END,
                updated_at = now()
            WHERE id = @id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", request.Id);
        command.Parameters.AddWithValue("status", nextStatus.ToDatabase());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return request with { Status = nextStatus };
    }

    private static async Task<TransportRequestSnapshot> UpdateRequestOutcomeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransportRequestSnapshot request,
        ExecutionOutcome outcome,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE ops.transport_requests
            SET status = @status,
                failure_code = @failure_code,
                failure_message = @failure_message,
                cancelled_at = CASE WHEN @status = 'CANCELLED' THEN COALESCE(cancelled_at, now()) ELSE cancelled_at END,
                updated_at = now()
            WHERE id = @id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", request.Id);
        command.Parameters.AddWithValue("status", outcome.Status.ToDatabase());
        command.Parameters.AddWithValue("failure_code", (object?)outcome.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("failure_message", (object?)outcome.FailureMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return request with { Status = outcome.Status };
    }

    private static async Task UpdateSlotStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid slotId,
        SlotState state,
        Guid? updatedBy,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE ops.slot_runtime_state
            SET occupancy_state = @occupancy_state,
                load_state = @load_state,
                current_part_id = @part_id,
                current_container_type_id = @container_type_id,
                updated_by = @updated_by,
                row_version = row_version + 1,
                updated_at = now()
            WHERE layout_slot_id = @slot_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("slot_id", slotId);
        command.Parameters.AddWithValue("occupancy_state", state.Occupancy.ToDatabase());
        command.Parameters.AddWithValue("load_state", state.Load.ToDatabase());
        AddNullableUuid(command, "part_id", state.CurrentPartId);
        AddNullableUuid(command, "container_type_id", state.CurrentContainerTypeId);
        AddNullableUuid(command, "updated_by", updatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        Guid? actorUserId,
        string eventType,
        TransportRequestStatus? oldStatus,
        TransportRequestStatus? newStatus,
        string message,
        object? data,
        CancellationToken cancellationToken,
        OperationalEventContext? eventContext = null)
    {
        var resolvedActor = actorUserId ?? eventContext?.ActorUserId;
        var deviceId = eventContext?.DeviceId;
        var correlationId = eventContext?.CorrelationId;

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ops.transport_request_events (
                transport_request_id, actor_user_id, device_id, correlation_id, event_type,
                old_status, new_status, message, data_json, created_at)
            VALUES (@request_id, @actor_user_id, @device_id, @correlation_id, @event_type,
                    @old_status, @new_status, @message, @data_json, clock_timestamp())
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("request_id", requestId);
        AddNullableUuid(command, "actor_user_id", resolvedActor);
        AddNullableUuid(command, "device_id", deviceId);
        AddNullableUuid(command, "correlation_id", correlationId);
        command.Parameters.AddWithValue("event_type", eventType);
        AddNullableText(command, "old_status", oldStatus?.ToDatabase());
        AddNullableText(command, "new_status", newStatus?.ToDatabase());
        command.Parameters.AddWithValue("message", message);
        command.Parameters.Add("data_json", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(data ?? new { });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CancelWaitingSuccessorsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid predecessorId,
        CancellationToken cancellationToken,
        OperationalEventContext? eventContext)
    {
        var successors = new List<Guid>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT successor_request_id
            FROM ops.transport_request_dependencies
            WHERE predecessor_request_id = @id
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", predecessorId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) successors.Add(reader.GetGuid(0));
        }

        foreach (var successorId in successors)
        {
            var successor = await LockRequestAsync(connection, transaction, successorId, cancellationToken);
            if (successor.Status is not (TransportRequestStatus.Created or TransportRequestStatus.Validating or TransportRequestStatus.Queued))
            {
                continue;
            }

            // Status only. The successor was never delivered, so its slot
            // occupancy, load and current part stay as the predecessor left them.
            var previous = successor.Status;
            successor = await UpdateRequestStatusAsync(connection, transaction, successor, TransportRequestStatus.Cancelled, cancellationToken);
            await InsertEventAsync(
                connection,
                transaction,
                successor.Id,
                eventContext?.ActorUserId,
                TransportEventTypes.RequestCancelled,
                previous,
                TransportRequestStatus.Cancelled,
                "Bağımlı talep, önceki palet kaldırma tamamlanmadığı için iptal edildi.",
                new { predecessor_request_id = predecessorId },
                cancellationToken,
                eventContext);
        }
    }

    private static Guid ReservedSlotId(TransportRequestSnapshot request) => TransportRules.DeliversToMachine(request.TaskType)
        ? request.DestinationSlotId ?? throw new WorkflowRuleException("DESTINATION_REQUIRED", $"{request.TaskType.ToDatabase()} request has no destination slot.")
        : request.SourceSlotId ?? throw new WorkflowRuleException("SOURCE_REQUIRED", "Removal request has no source slot.");

    private static Guid[] EndpointSlotIds(TransportRequestSnapshot request) =>
        new[] { request.SourceSlotId, request.DestinationSlotId }
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Distinct()
            .Order()
            .ToArray();

    private static async Task<bool> IsMachineSlotReservationActiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT machine_slot_reservation_active FROM ops.transport_requests WHERE id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", requestId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<bool> HasPredecessorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM ops.transport_request_dependencies WHERE successor_request_id = @id)",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", requestId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private sealed record LockedWorkflowRequest(
        TransportRequestSnapshot Request,
        IReadOnlyDictionary<Guid, SlotSnapshot> Slots);

    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value.HasValue ? value.Value : DBNull.Value;

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value is null ? DBNull.Value : value;

    private const string RequestSelectSql =
        """
        SELECT r.id,
               r.request_no,
               r.idempotency_key,
               r.status,
               tt.code,
               r.request_origin,
               r.requested_by,
               r.machine_id,
               r.variant_id,
               r.layout_id,
               r.navigation_id,
               r.product_group_id,
               r.part_id,
               r.container_type_id,
               r.source_slot_id,
               r.destination_slot_id,
               r.priority,
               r.readiness_state,
               r.ready_at,
               r.destination_business_code,
               r.source_business_code
        FROM ops.transport_requests r
        JOIN master.task_types tt ON tt.id = r.task_type_id
        """;

    private static async Task ReserveStoreEndpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        TransportTaskType taskType,
        Guid? navigationProfileId,
        Guid? destinationSlotId,
        string? sourceBusinessCode,
        string? destinationBusinessCode,
        CancellationToken cancellationToken)
    {
        if (taskType == TransportTaskType.BringEmpty)
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT u.id, sl.id
                FROM ops.store_units u
                JOIN ops.store_locations sl ON sl.id = u.store_location_id
                WHERE u.load_state = 'EMPTY'
                  AND u.removed_at IS NULL
                  AND sl.is_enabled
                  AND (@sourceCode IS NULL OR EXISTS (
                      SELECT 1
                      FROM integration.profile_shared_endpoint_bindings b
                      WHERE b.navigation_profile_id = @navProfile
                        AND b.endpoint_class = 'EMPTY_SOURCE'
                        AND b.business_code = @sourceCode
                        AND (b.store_location_id = sl.id OR (b.store_location_id IS NULL AND b.business_code = sl.code))
                  ))
                  AND NOT EXISTS (
                      SELECT 1 FROM ops.store_reservations sr
                      WHERE sr.store_location_id = sl.id AND sr.released_at IS NULL
                  )
                ORDER BY sl.code ASC, u.delivered_at ASC
                LIMIT 1
                FOR UPDATE OF u, sl
                """,
                connection,
                transaction);
            AddNullableText(cmd, "sourceCode", sourceBusinessCode);
            AddNullableUuid(cmd, "navProfile", navigationProfileId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var unitId = reader.GetGuid(0);
                var locId = reader.GetGuid(1);
                await reader.CloseAsync();

                await using var res = new NpgsqlCommand(
                    """
                    INSERT INTO ops.store_reservations (store_location_id, store_unit_id, transport_request_id, reservation_type)
                    VALUES (@loc, @unit, @req, 'PICKUP')
                    """,
                    connection,
                    transaction);
                res.Parameters.AddWithValue("loc", locId);
                res.Parameters.AddWithValue("unit", unitId);
                res.Parameters.AddWithValue("req", requestId);
                if (await res.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new WorkflowRuleException("FINISHED_GOODS_NO_EMPTY_CONTAINER", "Seçilen boş kasa kaynağı rezerve edilemedi.");
            }
            else
            {
                throw new WorkflowRuleException(
                    "FINISHED_GOODS_NO_EMPTY_CONTAINER",
                    sourceBusinessCode is null
                        ? "Bitmiş ürün alanında hazır boş kasa yok."
                        : "Seçilen fiziksel boş kasa kaynağında hazır kasa yok.");
            }
        }
        else if (taskType == TransportTaskType.RemoveFull)
        {
            var hasSharedBinding = false;
            if (destinationBusinessCode is not null && navigationProfileId is Guid profileId)
            {
                await using var bound = new NpgsqlCommand(
                    """
                    SELECT 1
                    FROM integration.profile_shared_endpoint_bindings
                    WHERE navigation_profile_id = @profile
                      AND endpoint_class = 'FINISHED_GOODS'
                      AND business_code = @code
                    LIMIT 1
                    """,
                    connection,
                    transaction);
                bound.Parameters.AddWithValue("profile", profileId);
                bound.Parameters.AddWithValue("code", destinationBusinessCode);
                hasSharedBinding = await bound.ExecuteScalarAsync(cancellationToken) is not null;
            }

            var hasMappedSlot = false;
            if (destinationSlotId is Guid mappedSlot)
            {
                await using var mapped = new NpgsqlCommand(
                    "SELECT 1 FROM ops.store_location_delivery_slots WHERE layout_slot_id = @slot LIMIT 1",
                    connection,
                    transaction);
                mapped.Parameters.AddWithValue("slot", mappedSlot);
                hasMappedSlot = await mapped.ExecuteScalarAsync(cancellationToken) is not null;
            }

            if (!hasSharedBinding && !hasMappedSlot) return;

            await using var cmd = new NpgsqlCommand(
                """
                SELECT sl.id
                FROM ops.store_locations sl
                WHERE sl.is_enabled
                  AND (
                      (@useSharedBinding AND EXISTS (
                          SELECT 1
                          FROM integration.profile_shared_endpoint_bindings b
                          WHERE b.navigation_profile_id = @navProfile
                            AND b.endpoint_class = 'FINISHED_GOODS'
                            AND b.business_code = @destCode
                            AND (b.store_location_id = sl.id OR (b.store_location_id IS NULL AND b.business_code = sl.code))
                      ))
                      OR (NOT @useSharedBinding AND EXISTS (
                          SELECT 1 FROM ops.store_location_delivery_slots d
                          WHERE d.layout_slot_id = @destinationSlot AND d.store_location_id = sl.id
                      ))
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM ops.store_units u WHERE u.store_location_id = sl.id AND u.removed_at IS NULL
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM ops.store_reservations sr WHERE sr.store_location_id = sl.id AND sr.released_at IS NULL
                  )
                LIMIT 1
                FOR UPDATE OF sl
                """,
                connection,
                transaction);
            AddNullableText(cmd, "destCode", destinationBusinessCode);
            AddNullableUuid(cmd, "navProfile", navigationProfileId);
            AddNullableUuid(cmd, "destinationSlot", destinationSlotId);
            cmd.Parameters.AddWithValue("useSharedBinding", hasSharedBinding);
            if (await cmd.ExecuteScalarAsync(cancellationToken) is Guid locId)
            {
                await using var res = new NpgsqlCommand(
                    """
                    INSERT INTO ops.store_reservations (store_location_id, transport_request_id, reservation_type)
                    VALUES (@loc, @req, 'DROP')
                    """,
                    connection,
                    transaction);
                res.Parameters.AddWithValue("loc", locId);
                res.Parameters.AddWithValue("req", requestId);
                if (await res.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new WorkflowRuleException("FINISHED_GOODS_STORE_FULL", "Seçilen mamul alanı rezerve edilemedi.");
            }
            else
            {
                throw new WorkflowRuleException(
                    "FINISHED_GOODS_STORE_FULL",
                    "Seçilen mamul alanı dolu veya başka bir taşıma için rezerve edilmiş.");
            }
        }
    }

    private static async Task ReleaseStoreReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE ops.store_reservations
            SET released_at = clock_timestamp()
            WHERE transport_request_id = @req AND released_at IS NULL
            """,
            connection,
            transaction);
        cmd.Parameters.AddWithValue("req", requestId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkStoreReservationForReconciliationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE ops.store_reservations
            SET reconciliation_required = true
            WHERE transport_request_id = @req AND released_at IS NULL
            """,
            connection,
            transaction);
        cmd.Parameters.AddWithValue("req", requestId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
