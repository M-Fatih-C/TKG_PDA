namespace TkgPda.Domain;

public static class TransportRules
{
    public static void ValidateSuppliedSlot(CreateTransportRequest request, SlotSnapshot slot)
    {
        if (!slot.IsEnabled)
        {
            throw new WorkflowRuleException("SLOT_DISABLED", $"Slot {slot.Id} is disabled.");
        }

        if (slot.Role == SlotRole.Machine)
        {
            throw new WorkflowRuleException("MACHINE_REGION_FORBIDDEN", "Machine/cell display regions cannot start or receive a transport.");
        }

        if (slot.MachineId != request.MachineId || slot.LayoutId != request.LayoutId)
        {
            throw new WorkflowRuleException("CROSS_MACHINE_SLOT", "Every supplied slot must belong to the request's active machine layout.");
        }
    }

    public static void ValidateReservation(CreateTransportRequest request, SlotSnapshot reservedSlot)
    {
        ValidateSuppliedSlot(request, reservedSlot);

        if (reservedSlot.Role == SlotRole.EmptyStorage)
        {
            throw new WorkflowRuleException(
                "EMPTY_STORAGE_MACHINE_SLOT_FORBIDDEN",
                "EMPTY_STORAGE is reserved for backend buffer allocation and cannot be used as the machine-side transport slot.");
        }

        if (request.AllowsDependentMachineOccupancy && request.TaskType == TransportTaskType.BringFull)
        {
            return;
        }

        var expected = request.TaskType switch
        {
            // Both inbound operations need the machine position already clear:
            // BRING_EMPTY to have somewhere to put the empty container,
            // BRING_FULL because the customer rule is that the spent container
            // must physically leave before filled material is requested.
            TransportTaskType.BringEmpty or TransportTaskType.BringFull =>
                new SlotState(OccupancyState.Vacant, LoadState.Unknown, null, null),
            TransportTaskType.RemoveFull => reservedSlot.State with { Occupancy = OccupancyState.Occupied, Load = LoadState.Full },
            TransportTaskType.RemoveEmpty => reservedSlot.State with { Occupancy = OccupancyState.Occupied, Load = LoadState.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.TaskType, null)
        };

        if (reservedSlot.State.Occupancy != expected.Occupancy || reservedSlot.State.Load != expected.Load)
        {
            throw new WorkflowRuleException(
                "SLOT_STATE_MISMATCH",
                $"{request.TaskType.ToDatabase()} cannot reserve slot state {reservedSlot.State.Occupancy}/{reservedSlot.State.Load}.");
        }

        ValidateAssociations(request, reservedSlot);
    }

    /// <summary>
    /// Validates the intended machine endpoint of a queued operator intent.
    /// Current occupancy and current pallet identity are deliberately excluded:
    /// they govern later physical dispatch, not whether an operator may order
    /// a separately prepared pallet.
    /// </summary>
    public static void ValidateDeferredMachineEndpoint(CreateTransportRequest request, SlotSnapshot destination)
    {
        ValidateSuppliedSlot(request, destination);
        if (!request.DefersMachineSlotReservation)
        {
            throw new WorkflowRuleException(
                "DEFERRED_SLOT_RESERVATION_NOT_SUPPORTED",
                "The request does not declare a deferred machine endpoint.");
        }

        if (destination.Role == SlotRole.EmptyStorage)
        {
            throw new WorkflowRuleException(
                "EMPTY_STORAGE_MACHINE_SLOT_FORBIDDEN",
                "EMPTY_STORAGE is reserved for backend buffer allocation and cannot be used as the machine-side transport slot.");
        }

        if (!request.PartId.HasValue || !destination.KnownPartIds.Contains(request.PartId.Value))
        {
            throw new WorkflowRuleException(
                "PART_SLOT_NOT_CONFIGURED",
                "Bu parça için makine alanı tanımlı değil.");
        }

        if (request.ContainerTypeId.HasValue
            && destination.DefaultContainerTypeId.HasValue
            && destination.DefaultContainerTypeId != request.ContainerTypeId)
        {
            throw new WorkflowRuleException(
                "CONTAINER_CONFLICT",
                "Talep edilen kasa tipi konumun kaynak verideki kasa tipiyle uyumlu değil.");
        }
    }

    public static SlotState CancellationState(TransportTaskType taskType, SlotState current) => taskType switch
    {
        // Nothing was delivered, so the position returns to clear.
        TransportTaskType.BringEmpty or TransportTaskType.BringFull =>
            new SlotState(OccupancyState.Vacant, LoadState.Unknown, null, null),
        TransportTaskType.RemoveFull => current with { Occupancy = OccupancyState.Occupied, Load = LoadState.Full },
        TransportTaskType.RemoveEmpty => current with { Occupancy = OccupancyState.Occupied, Load = LoadState.Empty },
        _ => throw new ArgumentOutOfRangeException(nameof(taskType), taskType, null)
    };

    public static SlotState CompletionState(TransportTaskType taskType, Guid? partId, Guid? containerTypeId) => taskType switch
    {
        TransportTaskType.BringEmpty => new SlotState(OccupancyState.Occupied, LoadState.Empty, partId, containerTypeId),
        // The whole reason BRING_FULL exists: the delivered container holds
        // material. Recording it Empty here is the silent corruption that made
        // reusing BRING_EMPTY for inbound feed unsafe.
        TransportTaskType.BringFull => new SlotState(OccupancyState.Occupied, LoadState.Full, partId, containerTypeId),
        TransportTaskType.RemoveFull or TransportTaskType.RemoveEmpty => new SlotState(OccupancyState.Vacant, LoadState.Unknown, null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(taskType), taskType, null)
    };

    /// <summary>
    /// Customer Workflow — Empty Container Buffer: BRING_EMPTY draws an empty
    /// container from a configured EMPTY_STORAGE slot (its source);
    /// REMOVE_EMPTY returns one to a configured EMPTY_STORAGE slot (its
    /// destination). REMOVE_FULL never touches the buffer. Returns null for
    /// task types that have no buffer side.
    ///
    /// BRING_FULL has none either: its container comes from a shared warehouse
    /// preparation area holding material, not from a layout's EMPTY_STORAGE
    /// buffer. Claiming a buffer side here would let the existing allocator
    /// hand it an empty container from the wrong place.
    /// </summary>
    public static bool RequiresBufferSlot(TransportTaskType taskType) =>
        taskType is TransportTaskType.BringEmpty or TransportTaskType.RemoveEmpty;

    public static Guid? BufferSlotId(TransportTaskType taskType, Guid? sourceSlotId, Guid? destinationSlotId) =>
        taskType switch
        {
            TransportTaskType.BringEmpty => sourceSlotId,
            TransportTaskType.RemoveEmpty => destinationSlotId,
            _ => null
        };

    /// <summary>
    /// The buffer slot's required state at request-creation time: a full
    /// container waiting to be brought in (BRING_EMPTY's source), or empty
    /// space waiting to receive one (REMOVE_EMPTY's destination).
    /// </summary>
    public static SlotState ExpectedBufferState(TransportTaskType taskType) => taskType switch
    {
        TransportTaskType.BringEmpty => new SlotState(OccupancyState.Occupied, LoadState.Empty, null, null),
        TransportTaskType.RemoveEmpty => new SlotState(OccupancyState.Vacant, LoadState.Unknown, null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(taskType), taskType, "This task type has no buffer slot.")
    };

    /// <summary>
    /// The buffer slot's state once the transport completes: emptied out
    /// (BRING_EMPTY took its container), or now holding the container that
    /// was just removed (REMOVE_EMPTY's destination).
    /// </summary>
    public static SlotState BufferCompletionState(TransportTaskType taskType, Guid? partId, Guid? containerTypeId) =>
        taskType switch
        {
            TransportTaskType.BringEmpty => new SlotState(OccupancyState.Vacant, LoadState.Unknown, null, null),
            TransportTaskType.RemoveEmpty => new SlotState(OccupancyState.Occupied, LoadState.Empty, partId, containerTypeId),
            _ => throw new ArgumentOutOfRangeException(nameof(taskType), taskType, "This task type has no buffer slot.")
        };

    /// <summary>
    /// Customer UX — Automatic Machine Slot Resolution: the machine-side
    /// occupancy/load state an eligible, part-mapped slot must currently be
    /// in for this operation. BRING_EMPTY needs an empty destination to fill;
    /// REMOVE_FULL/REMOVE_EMPTY need a source already holding what they take
    /// away. Mirrors ValidateReservation's own per-operation expectation, but
    /// as a plain lookup the SQL-side resolver can use directly.
    /// </summary>
    public static (OccupancyState Occupancy, LoadState Load) ExpectedMachineSlotState(TransportTaskType taskType) => taskType switch
    {
        TransportTaskType.BringEmpty or TransportTaskType.BringFull => (OccupancyState.Vacant, LoadState.Unknown),
        TransportTaskType.RemoveFull => (OccupancyState.Occupied, LoadState.Full),
        TransportTaskType.RemoveEmpty => (OccupancyState.Occupied, LoadState.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(taskType), taskType, null)
    };

    /// <summary>
    /// Whether this operation delivers to the machine position rather than
    /// taking from it. The machine slot is the transport's destination and the
    /// reservation is held on that side.
    /// </summary>
    public static bool DeliversToMachine(TransportTaskType taskType) =>
        taskType is TransportTaskType.BringEmpty or TransportTaskType.BringFull;

    /// <summary>
    /// A buffer slot must be configured master data (slot_role =
    /// EMPTY_STORAGE) — never inferred from name, WorkSite, machine code or
    /// coordinates — and must currently be in the state this operation
    /// expects of it.
    /// </summary>
    public static void ValidateBufferSlot(CreateTransportRequest request, SlotSnapshot bufferSlot)
    {
        if (bufferSlot.Role != SlotRole.EmptyStorage)
        {
            throw new WorkflowRuleException(
                "EMPTY_BUFFER_SLOT_NOT_CONFIGURED",
                $"Slot {bufferSlot.Id} is not a configured EMPTY_STORAGE buffer slot.");
        }

        var expected = ExpectedBufferState(request.TaskType);
        if (bufferSlot.State.Occupancy != expected.Occupancy || bufferSlot.State.Load != expected.Load)
        {
            throw new WorkflowRuleException(
                "EMPTY_BUFFER_STATE_MISMATCH",
                $"{request.TaskType.ToDatabase()} buffer slot must be {expected.Occupancy}/{expected.Load} but was {bufferSlot.State.Occupancy}/{bufferSlot.State.Load}.");
        }

        ValidateAssociations(request, bufferSlot);
    }

    private static void ValidateAssociations(CreateTransportRequest request, SlotSnapshot slot)
    {
        if (request.PartId.HasValue)
        {
            if (slot.KnownPartIds.Count > 0 && !slot.KnownPartIds.Contains(request.PartId.Value))
            {
                throw new WorkflowRuleException("PART_CONFLICT", "Seçilen parça konumun tanımlı parça eşlemesiyle uyumlu değil.");
            }

            if (slot.State.CurrentPartId.HasValue && slot.State.CurrentPartId != request.PartId)
            {
                throw new WorkflowRuleException("PART_CONFLICT", "Seçilen parça konumdaki mevcut parçayla uyumlu değil.");
            }
        }

        if (request.ContainerTypeId.HasValue)
        {
            if (slot.DefaultContainerTypeId.HasValue && slot.DefaultContainerTypeId != request.ContainerTypeId)
            {
                throw new WorkflowRuleException("CONTAINER_CONFLICT", "The requested container conflicts with the slot's source-backed container mapping.");
            }

            if (slot.State.CurrentContainerTypeId.HasValue && slot.State.CurrentContainerTypeId != request.ContainerTypeId)
            {
                throw new WorkflowRuleException("CONTAINER_CONFLICT", "The requested container conflicts with the slot's current known container.");
            }
        }
    }
}
