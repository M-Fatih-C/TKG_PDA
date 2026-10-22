namespace TkgPda.Domain;

public sealed class WorkflowRuleException : InvalidOperationException
{
    public WorkflowRuleException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class WorkflowErrorCodes
{
    public const string CancellationAfterExecutionDeferred = "CANCELLATION_AFTER_EXECUTION_DEFERRED";
    public const string RdsCancellationPending = "RDS_CANCELLATION_PENDING";
}

public sealed record SlotState(
    OccupancyState Occupancy,
    LoadState Load,
    Guid? CurrentPartId,
    Guid? CurrentContainerTypeId);

public sealed record SlotSnapshot(
    Guid Id,
    Guid MachineId,
    Guid LayoutId,
    SlotRole Role,
    bool IsEnabled,
    Guid? DefaultContainerTypeId,
    IReadOnlySet<Guid> KnownPartIds,
    SlotState State);

public sealed record CreateTransportRequest(
    string IdempotencyKey,
    RequestOrigin Origin,
    Guid? RequestedBy,
    Guid MachineId,
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    Guid? ProductGroupId,
    Guid? PartId,
    Guid? ContainerTypeId,
    TransportTaskType TaskType,
    Guid? SourceSlotId,
    Guid? DestinationSlotId,
    int Priority = 100,
    bool RequiresPreparation = false,
    bool RequiresFinishedGoodsDestination = false,
    string? DestinationBusinessCode = null,
    string? SourceBusinessCode = null)
{
    /// <summary>
    /// WP-08: a REMOVE_FULL created by the machine-side container flow (Kasa
    /// Doldu / remove-all) is only valid when its server-resolved destination
    /// is linked to an enabled finished-goods store location. The store never
    /// learns about a delivery the transport cannot attribute, so the request
    /// is refused before anything is reserved, persisted or dispatched.
    /// Ignored for every other operation.
    /// </summary>
    public bool EnforcesFinishedGoodsDestination =>
        RequiresFinishedGoodsDestination && TaskType == TransportTaskType.RemoveFull;

    /// <summary>
    /// A repeated alt-part demand creates BRING_FULL while the machine area
    /// is still occupied by the pallet the predecessor will remove. The
    /// successor is not execution-eligible until that predecessor completes.
    /// </summary>
    public bool AllowsDependentMachineOccupancy { get; init; }

    /// <summary>
    /// Alt-part orders first create one independent warehouse preparation
    /// demand per selected part.  While this flag is set, the mapped machine
    /// position is an intended destination rather than an active physical
    /// reservation.  The reservation is activated only after that request's
    /// pallet is ready and the position can be used safely.
    /// </summary>
    public bool DefersMachineSlotReservation { get; init; }

    /// <summary>
    /// A requested empty container is prepared against the demand in one of
    /// the finished-goods warehouse's six preparation areas. No source pallet
    /// exists when the request is created, so source allocation is deferred
    /// until that exact pallet is placed and confirmed ready.
    /// </summary>
    public bool DefersStoreSourceReservation =>
        TaskType == TransportTaskType.BringEmpty && RequiresPreparation;

    /// <summary>
    /// WP-05A: the BUSINESS readiness a new request starts at.
    /// RequiresPreparation=false (the default) preserves every existing
    /// workflow exactly: NotRequired is already execution-eligible under
    /// the existing rules.
    ///
    /// BRING_FULL ignores the flag and always waits. Its container has to be
    /// physically filled with the right material by a warehouse operator
    /// before anything may collect it, so letting a caller pass false would
    /// make a material feed execution-eligible with nothing prepared.
    /// </summary>
    public TransportReadinessState InitialReadinessState =>
        RequiresPreparation || TaskType == TransportTaskType.BringFull
            ? TransportReadinessState.WaitingPreparation
            : TransportReadinessState.NotRequired;

    /// <summary>
    /// The machine-side slot, once known. Customer UX — Automatic Machine
    /// Slot Resolution: this is no longer required to be supplied up front —
    /// omitting it asks the store to resolve and reserve one atomically from
    /// master.layout_slot_parts (see PostgresTransportWorkflowStore.
    /// ResolveMachineSlotAsync) — but by the time anything downstream reads
    /// this, one side or the other must have been filled in.
    /// </summary>
    public Guid ReservedSlotId => TransportRules.DeliversToMachine(TaskType)
        ? DestinationSlotId ?? throw new WorkflowRuleException("DESTINATION_REQUIRED", $"{TaskType.ToDatabase()} requires a machine destination slot.")
        : SourceSlotId ?? throw new WorkflowRuleException("SOURCE_REQUIRED", $"{TaskType.ToDatabase()} requires a machine source slot.");

    public void ValidateShape()
    {
        if (string.IsNullOrWhiteSpace(IdempotencyKey))
        {
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_REQUIRED", "An idempotency key is required.");
        }

        if (IdempotencyKey.Length > 200)
        {
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_TOO_LONG", "The idempotency key cannot exceed 200 characters.");
        }

        if (Priority < 0)
        {
            throw new WorkflowRuleException("INVALID_PRIORITY", "Priority cannot be negative.");
        }

        if (SourceSlotId.HasValue && SourceSlotId == DestinationSlotId)
        {
            throw new WorkflowRuleException("SAME_SOURCE_DESTINATION", "Source and destination slots must be different.");
        }

        if (DefersMachineSlotReservation && TaskType != TransportTaskType.BringFull)
        {
            throw new WorkflowRuleException(
                "DEFERRED_SLOT_RESERVATION_NOT_SUPPORTED",
                "Only warehouse-prepared BRING_FULL demands may defer their machine-slot reservation.");
        }
    }
}

public sealed record TransportRequestSnapshot(
    Guid Id,
    long RequestNo,
    string IdempotencyKey,
    TransportRequestStatus Status,
    TransportTaskType TaskType,
    RequestOrigin Origin,
    Guid? RequestedBy,
    Guid MachineId,
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    Guid? ProductGroupId,
    Guid? PartId,
    Guid? ContainerTypeId,
    Guid? SourceSlotId,
    Guid? DestinationSlotId,
    int Priority,
    TransportReadinessState ReadinessState,
    DateTimeOffset? ReadyAt,
    string? DestinationBusinessCode = null,
    string? SourceBusinessCode = null);

/// <summary>
/// WP-05A: readiness transition rules. Separate from
/// <see cref="TransportStateMachine"/> (the execution state machine), which
/// this never touches. Only WaitingPreparation -> Ready is allowed;
/// Ready -> Ready is an idempotent no-op; no other transition (including any
/// regression back to WaitingPreparation) is permitted.
/// </summary>
public static class TransportReadinessRules
{
    public static bool IsMarkReadyNoOp(TransportReadinessState current) => current == TransportReadinessState.Ready;

    public static void EnsureCanMarkReady(TransportRequestStatus executionStatus, TransportReadinessState readiness)
    {
        if (executionStatus is TransportRequestStatus.Completed or TransportRequestStatus.Failed or TransportRequestStatus.Cancelled)
        {
            throw new WorkflowRuleException("REQUEST_TERMINAL", $"A {executionStatus.ToDatabase()} transport request's readiness cannot be changed.");
        }

        if (readiness != TransportReadinessState.WaitingPreparation)
        {
            throw new WorkflowRuleException(
                "READINESS_NOT_APPLICABLE",
                $"Only a WAITING_PREPARATION request can be marked ready (current readiness: {readiness.ToDatabase()}).");
        }
    }
}

public sealed record CreateTransportResult(TransportRequestSnapshot Request, bool WasIdempotentReplay);

/// <summary>
/// WP-04: a TransportBatch is only a business grouping boundary for 2+
/// TransportRequests created together — it has no execution status of its
/// own and never gets its own RDS task. Every item is validated and
/// persisted through the exact same <see cref="CreateTransportRequest"/>
/// shape/rules as a standalone request; batch creation is all-or-nothing.
/// </summary>
public sealed record CreateTransportBatchRequest(
    string IdempotencyKey,
    RequestOrigin Origin,
    Guid? RequestedBy,
    Guid MachineId,
    IReadOnlyList<CreateTransportRequest> Items)
{
    public void ValidateShape()
    {
        if (string.IsNullOrWhiteSpace(IdempotencyKey))
        {
            throw new WorkflowRuleException("BATCH_IDEMPOTENCY_KEY_REQUIRED", "A batch idempotency key is required.");
        }

        if (IdempotencyKey.Length > 200)
        {
            throw new WorkflowRuleException("BATCH_IDEMPOTENCY_KEY_TOO_LONG", "The batch idempotency key cannot exceed 200 characters.");
        }

        if (Items.Count == 0)
        {
            throw new WorkflowRuleException("BATCH_ITEMS_REQUIRED", "A batch must contain at least one item.");
        }

        var itemKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            item.ValidateShape();
            if (item.MachineId != MachineId)
            {
                throw new WorkflowRuleException("BATCH_MACHINE_MISMATCH", "Every batch item must target the batch's machine.");
            }

            if (!itemKeys.Add(item.IdempotencyKey))
            {
                throw new WorkflowRuleException("BATCH_ITEM_IDEMPOTENCY_KEY_DUPLICATED", "Each batch item must use a distinct idempotency key.");
            }
        }
    }
}

public sealed record TransportBatchResult(
    Guid BatchId,
    string IdempotencyKey,
    Guid MachineId,
    IReadOnlyList<TransportRequestSnapshot> Items,
    bool WasIdempotentReplay);

public sealed record CancelTransportResult(TransportRequestSnapshot Request, bool WasAlreadyCancelled);

public sealed record LoadStateUpdateRequest(
    Guid MachineId,
    Guid SlotId,
    LoadState TargetLoadState,
    Guid ActorUserId);

public sealed record LoadStateUpdateResult(
    Guid MachineId,
    Guid SlotId,
    OccupancyState OccupancyState,
    LoadState LoadState,
    long RowVersion,
    bool WasIdempotentReplay);

public sealed record ExecutionOutcome(
    TransportRequestStatus Status,
    string? FailureCode = null,
    string? FailureMessage = null,
    string? EvidenceType = null);

public sealed record VariantSelection(
    Guid MachineId,
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    Guid? SelectedBy);
