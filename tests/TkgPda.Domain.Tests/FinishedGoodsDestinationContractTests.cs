using TkgPda.Domain;
using Xunit;

namespace TkgPda.Domain.Tests;

public sealed class FinishedGoodsDestinationContractTests
{
    [Fact]
    public void Remove_full_from_the_machine_container_flow_enforces_a_finished_goods_destination()
    {
        var request = Request(TransportTaskType.RemoveFull, requiresFinishedGoodsDestination: true);

        Assert.True(request.EnforcesFinishedGoodsDestination);
    }

    [Theory]
    [InlineData(TransportTaskType.BringEmpty)]
    [InlineData(TransportTaskType.BringFull)]
    [InlineData(TransportTaskType.RemoveEmpty)]
    public void Non_remove_full_operations_never_enforce_a_finished_goods_destination(TransportTaskType taskType)
    {
        var request = Request(taskType, requiresFinishedGoodsDestination: true);

        Assert.False(request.EnforcesFinishedGoodsDestination);
    }

    [Fact]
    public void Explicit_slot_remove_full_callers_are_not_forced_onto_the_mapping_contract()
    {
        var request = Request(TransportTaskType.RemoveFull, requiresFinishedGoodsDestination: false);

        Assert.False(request.EnforcesFinishedGoodsDestination);
    }

    private static CreateTransportRequest Request(
        TransportTaskType taskType,
        bool requiresFinishedGoodsDestination) => new(
            IdempotencyKey: Guid.NewGuid().ToString("N"),
            Origin: RequestOrigin.Api,
            RequestedBy: Guid.NewGuid(),
            MachineId: Guid.NewGuid(),
            VariantId: Guid.NewGuid(),
            LayoutId: Guid.NewGuid(),
            NavigationProfileId: null,
            ProductGroupId: Guid.NewGuid(),
            PartId: Guid.NewGuid(),
            ContainerTypeId: null,
            TaskType: taskType,
            SourceSlotId: null,
            DestinationSlotId: null,
            RequiresFinishedGoodsDestination: requiresFinishedGoodsDestination);
}
