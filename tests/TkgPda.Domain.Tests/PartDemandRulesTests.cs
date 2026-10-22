using TkgPda.Domain;
using Xunit;

namespace TkgPda.Domain.Tests;

public sealed class PartDemandRulesTests
{
    [Fact]
    public void Vacant_area_requests_bring_full_only()
    {
        var plan = PartDemandRules.Decide(OccupancyState.Vacant, LoadState.Unknown);

        Assert.Null(plan.Predecessor);
        Assert.Equal(TransportTaskType.BringFull, plan.Successor);
    }

    [Fact]
    public void Occupied_full_removes_full_then_brings_full()
    {
        var plan = PartDemandRules.Decide(OccupancyState.Occupied, LoadState.Full);

        Assert.Equal(TransportTaskType.RemoveFull, plan.Predecessor);
        Assert.Equal(TransportTaskType.BringFull, plan.Successor);
    }

    [Fact]
    public void Occupied_empty_removes_empty_then_brings_full()
    {
        var plan = PartDemandRules.Decide(OccupancyState.Occupied, LoadState.Empty);

        Assert.Equal(TransportTaskType.RemoveEmpty, plan.Predecessor);
        Assert.Equal(TransportTaskType.BringFull, plan.Successor);
    }

    [Theory]
    [InlineData(OccupancyState.Unknown, LoadState.Unknown)]
    [InlineData(OccupancyState.Unknown, LoadState.Full)]
    [InlineData(OccupancyState.Occupied, LoadState.Unknown)]
    public void Unknown_state_fails_closed(OccupancyState occupancy, LoadState load)
    {
        var error = Assert.Throws<WorkflowRuleException>(() => PartDemandRules.Decide(occupancy, load));

        Assert.Equal("PART_AREA_STATE_UNKNOWN", error.Code);
    }

    [Fact]
    public void Same_part_occupied_full_is_removed_before_the_new_delivery()
    {
        var part = Guid.NewGuid();
        var slot = Guid.NewGuid();
        var selection = PartDemandRules.Select(part, [new(slot, OccupancyState.Occupied, LoadState.Full, part)]);

        Assert.Equal(slot, selection.SlotId);
        Assert.Equal(TransportTaskType.RemoveFull, selection.Plan.Predecessor);
    }

    [Fact]
    public void Same_part_occupied_empty_is_removed_before_the_new_delivery()
    {
        var part = Guid.NewGuid();
        var selection = PartDemandRules.Select(part, [new(Guid.NewGuid(), OccupancyState.Occupied, LoadState.Empty, part)]);

        Assert.Equal(TransportTaskType.RemoveEmpty, selection.Plan.Predecessor);
    }

    [Fact]
    public void A_different_part_does_not_block_a_vacant_mapped_slot()
    {
        var requested = Guid.NewGuid();
        var vacant = Guid.NewGuid();
        var selection = PartDemandRules.Select(requested,
        [
            new(Guid.NewGuid(), OccupancyState.Occupied, LoadState.Full, Guid.NewGuid()),
            new(vacant, OccupancyState.Vacant, LoadState.Unknown, null)
        ]);

        Assert.Equal(vacant, selection.SlotId);
        Assert.Null(selection.Plan.Predecessor);
        Assert.Equal(TransportTaskType.BringFull, selection.Plan.Successor);
    }

    [Fact]
    public void A_different_part_with_no_vacant_slot_is_rejected()
    {
        var error = Assert.Throws<WorkflowRuleException>(() => PartDemandRules.Select(
            Guid.NewGuid(),
            [new(Guid.NewGuid(), OccupancyState.Occupied, LoadState.Full, Guid.NewGuid())]));

        Assert.Equal("PART_AREA_OCCUPIED_BY_OTHER_PART", error.Code);
    }

    [Fact]
    public void An_unknown_physical_part_with_no_vacant_slot_is_rejected()
    {
        var error = Assert.Throws<WorkflowRuleException>(() => PartDemandRules.Select(
            Guid.NewGuid(),
            [new(Guid.NewGuid(), OccupancyState.Occupied, LoadState.Full, null)]));

        Assert.Equal("PART_AREA_CURRENT_PART_UNKNOWN", error.Code);
    }

    [Fact]
    public void Two_occupied_copies_of_the_same_part_are_ambiguous()
    {
        var part = Guid.NewGuid();
        var error = Assert.Throws<WorkflowRuleException>(() => PartDemandRules.Select(part,
        [
            new(Guid.NewGuid(), OccupancyState.Occupied, LoadState.Full, part),
            new(Guid.NewGuid(), OccupancyState.Occupied, LoadState.Empty, part)
        ]));

        Assert.Equal("PART_AREA_AMBIGUOUS", error.Code);
    }

    [Fact]
    public void Unknown_candidate_state_still_fails_closed()
    {
        var part = Guid.NewGuid();
        var error = Assert.Throws<WorkflowRuleException>(() => PartDemandRules.Select(part,
        [
            new(Guid.NewGuid(), OccupancyState.Vacant, LoadState.Unknown, null),
            new(Guid.NewGuid(), OccupancyState.Unknown, LoadState.Unknown, part)
        ]));

        Assert.Equal("PART_AREA_STATE_UNKNOWN", error.Code);
    }

    [Fact]
    public void Dependent_bring_full_may_be_created_while_the_area_is_still_occupied()
    {
        var machineId = Guid.NewGuid();
        var layoutId = Guid.NewGuid();
        var slot = new SlotSnapshot(
            Guid.NewGuid(),
            machineId,
            layoutId,
            SlotRole.Drop,
            true,
            null,
            new HashSet<Guid>(),
            new SlotState(OccupancyState.Occupied, LoadState.Full, Guid.NewGuid(), Guid.NewGuid()));
        var request = new CreateTransportRequest(
            "again",
            RequestOrigin.Api,
            Guid.NewGuid(),
            machineId,
            Guid.NewGuid(),
            layoutId,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            TransportTaskType.BringFull,
            null,
            slot.Id,
            RequiresPreparation: true)
        {
            AllowsDependentMachineOccupancy = true
        };

        TransportRules.ValidateReservation(request, slot);
    }

    [Fact]
    public void Bring_full_without_the_dependency_flag_still_rejects_an_occupied_area()
    {
        var machineId = Guid.NewGuid();
        var layoutId = Guid.NewGuid();
        var slot = new SlotSnapshot(
            Guid.NewGuid(),
            machineId,
            layoutId,
            SlotRole.Drop,
            true,
            null,
            new HashSet<Guid>(),
            new SlotState(OccupancyState.Occupied, LoadState.Full, null, null));
        var request = new CreateTransportRequest(
            "first",
            RequestOrigin.Api,
            Guid.NewGuid(),
            machineId,
            Guid.NewGuid(),
            layoutId,
            null,
            null,
            Guid.NewGuid(),
            null,
            TransportTaskType.BringFull,
            null,
            slot.Id);

        var error = Assert.Throws<WorkflowRuleException>(() => TransportRules.ValidateReservation(request, slot));

        Assert.Equal("SLOT_STATE_MISMATCH", error.Code);
    }

    [Theory]
    [InlineData(OccupancyState.Occupied, LoadState.Full)]
    [InlineData(OccupancyState.Occupied, LoadState.Empty)]
    [InlineData(OccupancyState.Unknown, LoadState.Unknown)]
    public void Deferred_preparation_demand_validates_mapping_without_claiming_current_capacity(
        OccupancyState occupancy,
        LoadState load)
    {
        var machineId = Guid.NewGuid();
        var layoutId = Guid.NewGuid();
        var requestedPart = Guid.NewGuid();
        var slot = new SlotSnapshot(
            Guid.NewGuid(),
            machineId,
            layoutId,
            SlotRole.Drop,
            true,
            null,
            new HashSet<Guid> { requestedPart },
            new SlotState(occupancy, load, Guid.NewGuid(), Guid.NewGuid()));
        var request = new CreateTransportRequest(
            "separate-pallet",
            RequestOrigin.Api,
            Guid.NewGuid(),
            machineId,
            Guid.NewGuid(),
            layoutId,
            null,
            Guid.NewGuid(),
            requestedPart,
            null,
            TransportTaskType.BringFull,
            null,
            slot.Id,
            RequiresPreparation: true)
        {
            DefersMachineSlotReservation = true
        };

        TransportRules.ValidateDeferredMachineDestination(request, slot);
    }

    [Fact]
    public void Deferred_preparation_demand_still_requires_authoritative_part_slot_mapping()
    {
        var machineId = Guid.NewGuid();
        var layoutId = Guid.NewGuid();
        var slot = new SlotSnapshot(
            Guid.NewGuid(),
            machineId,
            layoutId,
            SlotRole.Drop,
            true,
            null,
            new HashSet<Guid>(),
            new SlotState(OccupancyState.Vacant, LoadState.Unknown, null, null));
        var request = new CreateTransportRequest(
            "missing-map",
            RequestOrigin.Api,
            Guid.NewGuid(),
            machineId,
            Guid.NewGuid(),
            layoutId,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            TransportTaskType.BringFull,
            null,
            slot.Id,
            RequiresPreparation: true)
        {
            DefersMachineSlotReservation = true
        };

        var error = Assert.Throws<WorkflowRuleException>(
            () => TransportRules.ValidateDeferredMachineDestination(request, slot));

        Assert.Equal("PART_SLOT_NOT_CONFIGURED", error.Code);
    }
}
