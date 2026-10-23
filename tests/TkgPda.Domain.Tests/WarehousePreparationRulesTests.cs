using TkgPda.Domain;
using Xunit;

namespace TkgPda.Domain.Tests;

public sealed class WarehousePreparationRulesTests
{
    private static readonly Guid Container = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Part = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Machine = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Request = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static WarehousePalletContent Full => new(Container, LoadState.Full, Part);
    private static WarehousePalletContent Empty => new(Container, LoadState.Empty, null);
    private static WarehousePreparationDemand Demand => new(Request, Machine, Part, Container,
        LoadState.Full, TransportRequestStatus.Queued, TransportReadinessState.WaitingPreparation);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Exactly_four_logical_preparation_positions_are_supported(int area) =>
        WarehousePreparationRules.EnsureAreaCanReceivePallet(area, OccupancyState.Vacant, false);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5)]
    public void Out_of_scope_area_is_rejected(int area) => Error("PREPARATION_AREA_INVALID",
        () => WarehousePreparationRules.EnsureAreaCanReceivePallet(area, OccupancyState.Vacant, false));

    [Theory]
    [InlineData(OccupancyState.Occupied)]
    [InlineData(OccupancyState.Unknown)]
    [InlineData((OccupancyState)999)]
    public void Second_pallet_or_unverified_area_is_rejected(OccupancyState occupancy) =>
        Error("PREPARATION_AREA_NOT_VACANT", () => WarehousePreparationRules.EnsureAreaCanReceivePallet(1, occupancy, false));

    [Fact]
    public void Reservation_is_not_occupancy_but_still_blocks_placement() => Error("PREPARATION_AREA_ALLOCATED",
        () => WarehousePreparationRules.EnsureAreaCanReceivePallet(1, OccupancyState.Vacant, true));

    [Fact]
    public void Empty_stock_can_be_prepared_before_any_order() =>
        WarehousePreparationRules.EnsurePalletMatchesDemand(Empty, null);

    [Fact]
    public void Semi_finished_stock_cannot_be_prepared_without_an_order() => Error("PREPARATION_DEMAND_REQUIRED",
        () => WarehousePreparationRules.EnsurePalletMatchesDemand(Full, null));

    [Fact]
    public void Full_pallet_has_one_requested_part_and_machine() =>
        WarehousePreparationRules.EnsurePalletMatchesDemand(Full, Demand);

    [Fact]
    public void Empty_stock_can_be_matched_to_a_later_empty_container_demand() =>
        WarehousePreparationRules.EnsurePalletMatchesDemand(Empty, Demand with { RequestedLoad = LoadState.Empty });

    [Theory]
    [InlineData(LoadState.Unknown)]
    [InlineData((LoadState)999)]
    public void Unknown_content_is_not_guessed(LoadState load) => Error("PREPARATION_LOAD_UNKNOWN",
        () => WarehousePreparationRules.EnsurePalletMatchesDemand(Empty with { Load = load }, null));

    [Fact]
    public void Empty_stock_does_not_claim_to_contain_material() => Error("EMPTY_PALLET_HAS_MATERIAL",
        () => WarehousePreparationRules.EnsurePalletMatchesDemand(Empty with { PartId = Part }, null));

    [Fact]
    public void Missing_unbound_container_is_not_guessed() => Error("PREPARATION_CONTAINER_REQUIRED",
        () => WarehousePreparationRules.EnsurePalletMatchesDemand(Empty with { ContainerTypeId = Guid.Empty }, null));

    [Fact]
    public void Exact_empty_request_can_preserve_an_unresolved_customer_container_type()
    {
        var pallet = Empty with { ContainerTypeId = null };
        var demand = Demand with { ContainerTypeId = null, RequestedLoad = LoadState.Empty };

        WarehousePreparationRules.EnsurePalletMatchesDemand(pallet, demand);
    }

    [Fact]
    public void Missing_full_pallet_part_is_not_guessed() => Error("PREPARATION_PART_REQUIRED",
        () => WarehousePreparationRules.EnsurePalletMatchesDemand(Full with { PartId = null }, Demand));

    [Fact]
    public void Empty_is_not_a_substitute_for_requested_full_material() => Error("PREPARATION_LOAD_MISMATCH",
        () => WarehousePreparationRules.EnsurePalletMatchesDemand(Empty, Demand));

    [Fact]
    public void Wrong_container_is_rejected() => Error("PREPARATION_CONTAINER_MISMATCH",
        () => WarehousePreparationRules.EnsurePalletMatchesDemand(Full with { ContainerTypeId = Guid.NewGuid() }, Demand));

    [Fact]
    public void Wrong_part_is_rejected() => Error("PREPARATION_PART_MISMATCH",
        () => WarehousePreparationRules.EnsurePalletMatchesDemand(Full with { PartId = Guid.NewGuid() }, Demand));

    [Theory]
    [InlineData(TransportRequestStatus.SentToRds)]
    [InlineData(TransportRequestStatus.Running)]
    [InlineData(TransportRequestStatus.Completed)]
    [InlineData(TransportRequestStatus.Cancelled)]
    [InlineData(TransportRequestStatus.Failed)]
    [InlineData((TransportRequestStatus)999)]
    public void Dispatched_terminal_or_unknown_request_cannot_be_prepared(TransportRequestStatus status) =>
        Error("PREPARATION_REQUEST_NOT_WAITING", () => WarehousePreparationRules.EnsurePalletMatchesDemand(Full, Demand with { Status = status }));

    [Theory]
    [InlineData(TransportReadinessState.Ready)]
    [InlineData(TransportReadinessState.NotRequired)]
    [InlineData((TransportReadinessState)999)]
    public void Readiness_cannot_be_reassigned_or_regressed(TransportReadinessState readiness) =>
        Error("PREPARATION_READINESS_CONFLICT", () => WarehousePreparationRules.EnsurePalletMatchesDemand(Full, Demand with { Readiness = readiness }));

    [Theory]
    [InlineData(OccupancyState.Vacant)]
    [InlineData(OccupancyState.Unknown)]
    public void Ready_requires_actual_placement(OccupancyState occupancy) => Error("PREPARATION_PALLET_NOT_PLACED",
        () => WarehousePreparationRules.EnsureCanMarkReady(occupancy, Full, Demand));

    [Fact]
    public void Prestaged_empty_pallet_is_not_dispatch_ready_without_a_request() => Error("PREPARATION_DEMAND_REQUIRED",
        () => WarehousePreparationRules.EnsureCanMarkReady(OccupancyState.Occupied, Empty, null));

    [Fact]
    public void Ready_validation_has_no_execution_or_pallet_content_side_effects()
    {
        var demand = Demand;
        var pallet = Full;
        WarehousePreparationRules.EnsureCanMarkReady(OccupancyState.Occupied, pallet, demand);
        Assert.Equal(TransportRequestStatus.Queued, demand.Status);
        Assert.Equal(TransportReadinessState.WaitingPreparation, demand.Readiness);
        Assert.Equal(Full, pallet);
    }

    [Fact]
    public void Matching_parts_are_allowed_in_different_areas_for_different_orders()
    {
        WarehousePreparationRules.EnsureAreaCanReceivePallet(1, OccupancyState.Vacant, false);
        WarehousePreparationRules.EnsurePalletMatchesDemand(Full, Demand);
        WarehousePreparationRules.EnsureAreaCanReceivePallet(2, OccupancyState.Vacant, false);
        WarehousePreparationRules.EnsurePalletMatchesDemand(Full, Demand with { RequestId = Guid.NewGuid(), MachineId = Guid.NewGuid() });
    }

    [Theory]
    [InlineData(OccupancyState.Occupied, LoadState.Empty)]
    [InlineData(OccupancyState.Occupied, LoadState.Full)]
    [InlineData(OccupancyState.Unknown, LoadState.Unknown)]
    [InlineData(OccupancyState.Vacant, LoadState.Empty)]
    public void Filled_supply_cannot_be_requested_before_machine_position_is_empty(OccupancyState occupancy, LoadState load) =>
        Error("FILLED_SUPPLY_POSITION_NOT_VACANT", () => WarehousePreparationRules.EnsureMachineCanReceiveFilledPallet(new(occupancy, load, null, null), false));

    [Fact]
    public void Stale_machine_content_is_not_silently_cleared() => Error("FILLED_SUPPLY_POSITION_NOT_VACANT",
        () => WarehousePreparationRules.EnsureMachineCanReceiveFilledPallet(new(OccupancyState.Vacant, LoadState.Unknown, Part, Container), false));

    [Fact]
    public void Removal_request_still_owning_position_blocks_new_supply() => Error("FILLED_SUPPLY_POSITION_RESERVED",
        () => WarehousePreparationRules.EnsureMachineCanReceiveFilledPallet(new(OccupancyState.Vacant, LoadState.Unknown, null, null), true));

    [Fact]
    public void Verified_vacant_unreserved_machine_position_allows_filled_supply() =>
        WarehousePreparationRules.EnsureMachineCanReceiveFilledPallet(new(OccupancyState.Vacant, LoadState.Unknown, null, null), false);

    private static void Error(string code, Action action) => Assert.Equal(code, Assert.Throws<WorkflowRuleException>(action).Code);
}
