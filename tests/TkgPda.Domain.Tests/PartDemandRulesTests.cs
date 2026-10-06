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
}
