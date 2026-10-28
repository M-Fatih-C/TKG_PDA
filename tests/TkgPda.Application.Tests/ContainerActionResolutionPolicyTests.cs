using TkgPda.Application;
using TkgPda.Domain;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class ContainerActionResolutionPolicyTests
{
    [Fact]
    public void Sequenced_position_allows_only_empty_after_active_remove_full()
    {
        var next = ContainerActionResolutionPolicy.ResolveSequenced(
            OperatorContainerAction.Empty,
            OccupancyState.Occupied,
            LoadState.Full,
            TransportTaskType.RemoveFull);

        Assert.Equal(TransportTaskType.BringEmpty, next.Operation);
        Assert.True(next.RequiresPreparation);
        Assert.True(next.DefersMachineSlotReservation);

        var duplicate = Assert.Throws<WorkflowRuleException>(() =>
            ContainerActionResolutionPolicy.ResolveSequenced(
                OperatorContainerAction.Full,
                OccupancyState.Occupied,
                LoadState.Full,
                TransportTaskType.RemoveFull));
        Assert.Equal("CONTAINER_FULL_ALREADY_QUEUED", duplicate.Code);
    }

    [Fact]
    public void Sequenced_position_allows_only_full_after_bring_empty_is_queued()
    {
        var next = ContainerActionResolutionPolicy.ResolveSequenced(
            OperatorContainerAction.Full,
            OccupancyState.Occupied,
            LoadState.Full,
            TransportTaskType.BringEmpty);

        Assert.Equal(TransportTaskType.RemoveFull, next.Operation);
        Assert.True(next.MarkFullBeforeCreate);
        Assert.False(next.RequiresPreparation);
        Assert.True(next.DefersMachineSlotReservation);

        var error = Assert.Throws<WorkflowRuleException>(() =>
            ContainerActionResolutionPolicy.ResolveSequenced(
                OperatorContainerAction.Empty,
                OccupancyState.Occupied,
                LoadState.Full,
                TransportTaskType.BringEmpty));

        Assert.Equal("CONTAINER_EMPTY_ALREADY_QUEUED", error.Code);
    }

    [Fact]
    public void Sequenced_position_uses_physical_state_when_there_is_no_active_chain()
    {
        Assert.Equal(
            TransportTaskType.RemoveFull,
            ContainerActionResolutionPolicy.ResolveSequenced(
                OperatorContainerAction.Full,
                OccupancyState.Occupied,
                LoadState.Empty,
                activeTailOperation: null).Operation);
        Assert.Equal(
            TransportTaskType.BringEmpty,
            ContainerActionResolutionPolicy.ResolveSequenced(
                OperatorContainerAction.Empty,
                OccupancyState.Vacant,
                LoadState.Unknown,
                activeTailOperation: null).Operation);

        var invalid = Assert.Throws<WorkflowRuleException>(() =>
            ContainerActionResolutionPolicy.ResolveSequenced(
                OperatorContainerAction.Empty,
                OccupancyState.Occupied,
                LoadState.Empty,
                activeTailOperation: null));
        Assert.Equal("CONTAINER_ACTION_SEQUENCE_REQUIRED", invalid.Code);
    }

    // ---------------------------------------------------------------------
    // Transient physical state never discards operator intent. The policy keeps
    // the requested operation and marks its machine reservation as deferred.
    // ---------------------------------------------------------------------

    [Fact]
    public void Full_on_a_vacant_position_is_deferred_until_a_container_exists()
    {
        var result = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Full,
            OccupancyState.Vacant,
            LoadState.Unknown);

        Assert.Equal(TransportTaskType.RemoveFull, result.Operation);
        Assert.True(result.DefersMachineSlotReservation);
        Assert.False(result.MarkFullBeforeCreate);
    }

    [Fact]
    public void Full_on_an_occupied_position_with_unknown_load_is_deferred()
    {
        var result = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Full,
            OccupancyState.Occupied,
            LoadState.Unknown);

        Assert.Equal(TransportTaskType.RemoveFull, result.Operation);
        Assert.True(result.DefersMachineSlotReservation);
    }

    [Fact]
    public void Unknown_occupancy_defers_both_actions_without_inference()
    {
        var full = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Full, OccupancyState.Unknown, LoadState.Unknown);
        var empty = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Empty, OccupancyState.Unknown, LoadState.Unknown);

        Assert.Equal(TransportTaskType.RemoveFull, full.Operation);
        Assert.True(full.DefersMachineSlotReservation);
        Assert.Equal(TransportTaskType.BringEmpty, empty.Operation);
        Assert.True(empty.DefersMachineSlotReservation);
        Assert.True(empty.RequiresPreparation);
    }

    [Fact]
    public void Legal_db_states_keep_their_established_resolutions()
    {
        // Guard rail: the reorder must not touch any accepted action.
        var markFull = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Full, OccupancyState.Occupied, LoadState.Empty);
        Assert.Equal(TransportTaskType.RemoveFull, markFull.Operation);
        Assert.True(markFull.MarkFullBeforeCreate);
        Assert.False(markFull.RequiresPreparation);
        Assert.False(markFull.DefersMachineSlotReservation);

        var alreadyFull = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Full, OccupancyState.Occupied, LoadState.Full);
        Assert.Equal(TransportTaskType.RemoveFull, alreadyFull.Operation);
        Assert.False(alreadyFull.MarkFullBeforeCreate);
        Assert.False(alreadyFull.DefersMachineSlotReservation);

        var bringEmpty = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Empty, OccupancyState.Vacant, LoadState.Unknown);
        Assert.Equal(TransportTaskType.BringEmpty, bringEmpty.Operation);
        Assert.True(bringEmpty.RequiresPreparation);
        Assert.False(bringEmpty.DefersMachineSlotReservation);

        var occupied = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Empty, OccupancyState.Occupied, LoadState.Full);
        Assert.Equal(TransportTaskType.BringEmpty, occupied.Operation);
        Assert.True(occupied.DefersMachineSlotReservation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("REMOVE_EMPTY")]
    public void Operator_container_action_parser_rejects_missing_or_unsupported_values(string? value)
    {
        var error = Assert.Throws<WorkflowRuleException>(() => OperatorContainerActionCode.Parse(value));

        Assert.Equal("CONTAINER_ACTION_INVALID", error.Code);
    }

    [Fact]
    public void Kasa_doldu_from_occupied_empty_marks_full_then_removes_full()
    {
        var result = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Full,
            OccupancyState.Occupied,
            LoadState.Empty);

        Assert.Equal(TransportTaskType.RemoveFull, result.Operation);
        Assert.True(result.MarkFullBeforeCreate);
        Assert.False(result.RequiresPreparation);
    }

    [Fact]
    public void Kasa_doldu_from_occupied_full_reuses_remove_full_without_state_rewrite()
    {
        var result = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Full,
            OccupancyState.Occupied,
            LoadState.Full);

        Assert.Equal(TransportTaskType.RemoveFull, result.Operation);
        Assert.False(result.MarkFullBeforeCreate);
        Assert.False(result.RequiresPreparation);
    }

    [Theory]
    [InlineData(OccupancyState.Vacant, LoadState.Unknown)]
    [InlineData(OccupancyState.Unknown, LoadState.Unknown)]
    public void Kasa_doldu_waits_without_an_occupied_container(
        OccupancyState occupancy,
        LoadState load)
    {
        var result = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Full, occupancy, load);

        Assert.Equal(TransportTaskType.RemoveFull, result.Operation);
        Assert.True(result.DefersMachineSlotReservation);
    }

    [Fact]
    public void Kasa_bos_from_vacant_unknown_brings_empty()
    {
        var result = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Empty,
            OccupancyState.Vacant,
            LoadState.Unknown);

        Assert.Equal(TransportTaskType.BringEmpty, result.Operation);
        Assert.False(result.MarkFullBeforeCreate);
        Assert.True(result.RequiresPreparation);
    }

    [Theory]
    [InlineData(LoadState.Empty)]
    [InlineData(LoadState.Full)]
    public void Kasa_bos_waits_for_occupied_positions_to_become_vacant(LoadState load)
    {
        var result = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Empty,
            OccupancyState.Occupied,
            load);

        Assert.Equal(TransportTaskType.BringEmpty, result.Operation);
        Assert.True(result.RequiresPreparation);
        Assert.True(result.DefersMachineSlotReservation);
    }

    [Theory]
    [InlineData(OccupancyState.Occupied, LoadState.Full, TransportTaskType.RemoveFull)]
    [InlineData(OccupancyState.Occupied, LoadState.Empty, TransportTaskType.RemoveEmpty)]
    public void Remove_all_selects_operation_from_tkg_business_state(
        OccupancyState occupancy,
        LoadState load,
        TransportTaskType expected)
    {
        Assert.Equal(expected, ContainerActionResolutionPolicy.ResolveRemoveAll(occupancy, load));
    }

    [Fact]
    public void Remove_all_ignores_vacant_unknown()
    {
        Assert.Null(ContainerActionResolutionPolicy.ResolveRemoveAll(
            OccupancyState.Vacant,
            LoadState.Unknown));
    }

    [Fact]
    public void Remove_all_blocks_unknown_position()
    {
        var error = Assert.Throws<WorkflowRuleException>(() =>
            ContainerActionResolutionPolicy.ResolveRemoveAll(
                OccupancyState.Unknown,
                LoadState.Unknown));

        Assert.Equal("REMOVE_ALL_POSITION_UNKNOWN", error.Code);
    }
}
