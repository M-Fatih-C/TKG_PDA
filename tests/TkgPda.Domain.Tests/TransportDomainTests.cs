using TkgPda.Domain;
using Xunit;

namespace TkgPda.Domain.Tests;

public sealed class TransportDomainTests
{
    public static TheoryData<TransportRequestStatus, TransportRequestStatus> LegalTransitions => new()
    {
        { TransportRequestStatus.Created, TransportRequestStatus.Validating },
        { TransportRequestStatus.Created, TransportRequestStatus.Cancelled },
        { TransportRequestStatus.Validating, TransportRequestStatus.Queued },
        { TransportRequestStatus.Validating, TransportRequestStatus.Failed },
        { TransportRequestStatus.Validating, TransportRequestStatus.Cancelled },
        { TransportRequestStatus.Queued, TransportRequestStatus.SentToRds },
        { TransportRequestStatus.Queued, TransportRequestStatus.Cancelled },
        { TransportRequestStatus.SentToRds, TransportRequestStatus.Running },
        { TransportRequestStatus.SentToRds, TransportRequestStatus.Failed },
        { TransportRequestStatus.Running, TransportRequestStatus.Completed },
        { TransportRequestStatus.Running, TransportRequestStatus.Failed }
    };

    [Fact]
    public void Sql_transport_statuses_are_complete_and_deterministic()
    {
        var codes = Enum.GetValues<TransportRequestStatus>().Select(WorkflowCode.ToDatabase).ToArray();

        Assert.Equal(
            ["CREATED", "VALIDATING", "QUEUED", "SENT_TO_RDS", "RUNNING", "COMPLETED", "FAILED", "CANCELLED"],
            codes);
        Assert.All(codes, code => Assert.Equal(code, WorkflowCode.ParseTransportStatus(code).ToDatabase()));
    }

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void Legal_transition_table_is_accepted(TransportRequestStatus current, TransportRequestStatus next)
    {
        Assert.True(TransportStateMachine.CanTransition(current, next));
        TransportStateMachine.EnsureTransition(current, next);
    }

    [Theory]
    [InlineData(TransportRequestStatus.Completed)]
    [InlineData(TransportRequestStatus.Failed)]
    [InlineData(TransportRequestStatus.Cancelled)]
    public void Terminal_states_have_no_outgoing_transition(TransportRequestStatus status)
    {
        Assert.Empty(TransportStateMachine.NextStates(status));
    }

    [Fact]
    public void Queued_cannot_skip_directly_to_completed()
    {
        var error = Assert.Throws<WorkflowRuleException>(() =>
            TransportStateMachine.EnsureTransition(TransportRequestStatus.Queued, TransportRequestStatus.Completed));

        Assert.Equal("ILLEGAL_STATUS_TRANSITION", error.Code);
    }

    [Theory]
    [InlineData(TransportTaskType.BringEmpty, OccupancyState.Vacant, LoadState.Unknown)]
    [InlineData(TransportTaskType.RemoveFull, OccupancyState.Occupied, LoadState.Full)]
    [InlineData(TransportTaskType.RemoveEmpty, OccupancyState.Occupied, LoadState.Empty)]
    public void Operation_accepts_its_required_initial_state(
        TransportTaskType taskType,
        OccupancyState occupancy,
        LoadState load)
    {
        var request = Request(taskType);
        var slot = Slot(request.ReservedSlotId, occupancy, load);

        TransportRules.ValidateReservation(request, slot);
    }

    [Theory]
    [InlineData(TransportTaskType.BringEmpty, OccupancyState.Occupied, LoadState.Empty)]
    [InlineData(TransportTaskType.RemoveFull, OccupancyState.Occupied, LoadState.Empty)]
    [InlineData(TransportTaskType.RemoveEmpty, OccupancyState.Vacant, LoadState.Unknown)]
    public void Operation_rejects_an_incompatible_initial_state(
        TransportTaskType taskType,
        OccupancyState occupancy,
        LoadState load)
    {
        var request = Request(taskType);
        var error = Assert.Throws<WorkflowRuleException>(() =>
            TransportRules.ValidateReservation(request, Slot(request.ReservedSlotId, occupancy, load)));

        Assert.Equal("SLOT_STATE_MISMATCH", error.Code);
    }

    [Theory]
    [InlineData(TransportTaskType.BringEmpty)]
    [InlineData(TransportTaskType.RemoveFull)]
    [InlineData(TransportTaskType.RemoveEmpty)]
    public void Machine_region_can_never_be_a_transport_endpoint(TransportTaskType taskType)
    {
        var request = Request(taskType);
        var state = taskType switch
        {
            TransportTaskType.BringEmpty => (OccupancyState.Vacant, LoadState.Unknown),
            TransportTaskType.RemoveFull => (OccupancyState.Occupied, LoadState.Full),
            _ => (OccupancyState.Occupied, LoadState.Empty)
        };
        var slot = Slot(request.ReservedSlotId, state.Item1, state.Item2) with { Role = SlotRole.Machine };

        var error = Assert.Throws<WorkflowRuleException>(() => TransportRules.ValidateReservation(request, slot));
        Assert.Equal("MACHINE_REGION_FORBIDDEN", error.Code);
    }

    [Theory]
    [InlineData(TransportTaskType.BringEmpty, OccupancyState.Vacant, LoadState.Unknown)]
    [InlineData(TransportTaskType.RemoveFull, OccupancyState.Occupied, LoadState.Full)]
    [InlineData(TransportTaskType.RemoveEmpty, OccupancyState.Occupied, LoadState.Empty)]
    public void Empty_storage_can_never_be_the_machine_side_endpoint(
        TransportTaskType taskType,
        OccupancyState occupancy,
        LoadState load)
    {
        var request = Request(taskType);
        var slot = Slot(request.ReservedSlotId, occupancy, load) with
        {
            Role = SlotRole.EmptyStorage,
        };

        var error = Assert.Throws<WorkflowRuleException>(() =>
            TransportRules.ValidateReservation(request, slot));

        Assert.Equal("EMPTY_STORAGE_MACHINE_SLOT_FORBIDDEN", error.Code);
    }

    [Fact]
    public void Unknown_part_and_container_associations_do_not_block()
    {
        var request = Request(TransportTaskType.BringEmpty) with
        {
            PartId = Guid.NewGuid(),
            ContainerTypeId = Guid.NewGuid()
        };
        var slot = Slot(request.ReservedSlotId, OccupancyState.Vacant, LoadState.Unknown);

        TransportRules.ValidateReservation(request, slot);
    }

    [Fact]
    public void Explicit_part_conflict_blocks()
    {
        var request = Request(TransportTaskType.RemoveFull) with { PartId = Guid.NewGuid() };
        var slot = Slot(request.ReservedSlotId, OccupancyState.Occupied, LoadState.Full) with
        {
            KnownPartIds = new HashSet<Guid> { Guid.NewGuid() }
        };

        var error = Assert.Throws<WorkflowRuleException>(() => TransportRules.ValidateReservation(request, slot));
        Assert.Equal("PART_CONFLICT", error.Code);
    }

    [Fact]
    public void Explicit_container_conflict_blocks()
    {
        var request = Request(TransportTaskType.RemoveEmpty) with { ContainerTypeId = Guid.NewGuid() };
        var slot = Slot(request.ReservedSlotId, OccupancyState.Occupied, LoadState.Empty) with
        {
            DefaultContainerTypeId = Guid.NewGuid()
        };

        var error = Assert.Throws<WorkflowRuleException>(() => TransportRules.ValidateReservation(request, slot));
        Assert.Equal("CONTAINER_CONFLICT", error.Code);
    }

    [Fact]
    public void Explicit_part_mapping_allows_a_part_container_that_differs_from_slot_default()
    {
        var partId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var request = Request(TransportTaskType.BringFull) with
        {
            PartId = partId,
            ContainerTypeId = containerId
        };
        var slot = Slot(request.ReservedSlotId, OccupancyState.Vacant, LoadState.Unknown) with
        {
            DefaultContainerTypeId = Guid.NewGuid(),
            KnownPartIds = new HashSet<Guid> { partId }
        };

        TransportRules.ValidateReservation(request, slot);
    }

    [Theory]
    [InlineData(TransportTaskType.BringEmpty, OccupancyState.Vacant, LoadState.Unknown)]
    [InlineData(TransportTaskType.RemoveFull, OccupancyState.Occupied, LoadState.Full)]
    [InlineData(TransportTaskType.RemoveEmpty, OccupancyState.Occupied, LoadState.Empty)]
    public void Cancellation_restores_the_deterministic_state(
        TransportTaskType taskType,
        OccupancyState expectedOccupancy,
        LoadState expectedLoad)
    {
        var current = new SlotState(OccupancyState.Unknown, LoadState.Unknown, Guid.NewGuid(), Guid.NewGuid());
        var restored = TransportRules.CancellationState(taskType, current);

        Assert.Equal(expectedOccupancy, restored.Occupancy);
        Assert.Equal(expectedLoad, restored.Load);
        if (taskType == TransportTaskType.BringEmpty)
        {
            Assert.Null(restored.CurrentPartId);
            Assert.Null(restored.CurrentContainerTypeId);
        }
        else
        {
            Assert.Equal(current.CurrentPartId, restored.CurrentPartId);
            Assert.Equal(current.CurrentContainerTypeId, restored.CurrentContainerTypeId);
        }
    }

    [Theory]
    [InlineData(TransportTaskType.BringEmpty, OccupancyState.Occupied, LoadState.Empty)]
    [InlineData(TransportTaskType.RemoveFull, OccupancyState.Vacant, LoadState.Unknown)]
    [InlineData(TransportTaskType.RemoveEmpty, OccupancyState.Vacant, LoadState.Unknown)]
    public void Completion_applies_the_final_slot_state(
        TransportTaskType taskType,
        OccupancyState expectedOccupancy,
        LoadState expectedLoad)
    {
        var completed = TransportRules.CompletionState(taskType, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(expectedOccupancy, completed.Occupancy);
        Assert.Equal(expectedLoad, completed.Load);
    }

    private static CreateTransportRequest Request(TransportTaskType taskType)
    {
        var deliversToMachine = taskType is TransportTaskType.BringEmpty or TransportTaskType.BringFull;
        Guid? source = deliversToMachine ? null : Guid.NewGuid();
        Guid? destination = deliversToMachine ? Guid.NewGuid() : null;
        return new CreateTransportRequest(
            "test-key",
            RequestOrigin.LineOperator,
            null,
            MachineId,
            VariantId,
            LayoutId,
            null,
            null,
            null,
            null,
            taskType,
            source,
            destination);
    }

    private static SlotSnapshot Slot(Guid id, OccupancyState occupancy, LoadState load) => new(
        id,
        MachineId,
        LayoutId,
        SlotRole.Other,
        true,
        null,
        new HashSet<Guid>(),
        new SlotState(occupancy, load, null, null));

    private static readonly Guid MachineId = Guid.NewGuid();
    private static readonly Guid VariantId = Guid.NewGuid();
    private static readonly Guid LayoutId = Guid.NewGuid();
}
