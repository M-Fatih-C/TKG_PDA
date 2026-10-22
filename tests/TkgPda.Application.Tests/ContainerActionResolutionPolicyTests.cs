using TkgPda.Application;
using TkgPda.Domain;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class ContainerActionResolutionPolicyTests
{
    // ---------------------------------------------------------------------
    // Rejection reasons must be specific and actionable. A VACANT slot is only
    // ever stored with LoadState.Unknown, so the broad unknown-state arm used to
    // shadow the VACANT arm entirely and operators saw a generic message.
    // ---------------------------------------------------------------------

    [Fact]
    public void Full_on_a_vacant_position_reports_that_a_container_is_required()
    {
        var error = Assert.Throws<WorkflowRuleException>(() =>
            ContainerActionResolutionPolicy.ResolveSingle(
                OperatorContainerAction.Full,
                OccupancyState.Vacant,
                LoadState.Unknown));

        Assert.Equal("OCCUPIED_CONTAINER_REQUIRED", error.Code);
    }

    [Fact]
    public void Full_on_an_occupied_position_with_unknown_load_still_reports_insufficient_state()
    {
        // Occupied but with an unproven load is genuinely not enough information,
        // so this must keep the generic reason rather than borrow the VACANT one.
        var error = Assert.Throws<WorkflowRuleException>(() =>
            ContainerActionResolutionPolicy.ResolveSingle(
                OperatorContainerAction.Full,
                OccupancyState.Occupied,
                LoadState.Unknown));

        Assert.Equal("BUSINESS_RULE_NOT_CONFIGURED", error.Code);
    }

    [Fact]
    public void Unknown_occupancy_still_reports_insufficient_state_for_both_actions()
    {
        var full = Assert.Throws<WorkflowRuleException>(() =>
            ContainerActionResolutionPolicy.ResolveSingle(
                OperatorContainerAction.Full, OccupancyState.Unknown, LoadState.Unknown));
        var empty = Assert.Throws<WorkflowRuleException>(() =>
            ContainerActionResolutionPolicy.ResolveSingle(
                OperatorContainerAction.Empty, OccupancyState.Unknown, LoadState.Unknown));

        Assert.Equal("BUSINESS_RULE_NOT_CONFIGURED", full.Code);
        Assert.Equal("BUSINESS_RULE_NOT_CONFIGURED", empty.Code);
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

        var alreadyFull = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Full, OccupancyState.Occupied, LoadState.Full);
        Assert.Equal(TransportTaskType.RemoveFull, alreadyFull.Operation);
        Assert.False(alreadyFull.MarkFullBeforeCreate);

        var bringEmpty = ContainerActionResolutionPolicy.ResolveSingle(
            OperatorContainerAction.Empty, OccupancyState.Vacant, LoadState.Unknown);
        Assert.Equal(TransportTaskType.BringEmpty, bringEmpty.Operation);
        Assert.True(bringEmpty.RequiresPreparation);

        var occupied = Assert.Throws<WorkflowRuleException>(() =>
            ContainerActionResolutionPolicy.ResolveSingle(
                OperatorContainerAction.Empty, OccupancyState.Occupied, LoadState.Full));
        Assert.Equal("CONTAINER_POSITION_OCCUPIED", occupied.Code);
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
    public void Kasa_doldu_fails_closed_without_an_occupied_container(
        OccupancyState occupancy,
        LoadState load)
    {
        Assert.Throws<WorkflowRuleException>(() =>
            ContainerActionResolutionPolicy.ResolveSingle(OperatorContainerAction.Full, occupancy, load));
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
    public void Kasa_bos_rejects_occupied_positions(LoadState load)
    {
        var error = Assert.Throws<WorkflowRuleException>(() =>
            ContainerActionResolutionPolicy.ResolveSingle(
                OperatorContainerAction.Empty,
                OccupancyState.Occupied,
                load));

        Assert.Equal("CONTAINER_POSITION_OCCUPIED", error.Code);
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
