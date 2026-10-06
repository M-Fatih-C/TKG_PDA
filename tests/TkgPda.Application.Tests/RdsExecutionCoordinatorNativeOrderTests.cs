using System.Text.Json;
using TkgPda.Application;
using TkgPda.Domain;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class RdsExecutionCoordinatorNativeOrderTests
{
    private static readonly Guid RequestId = Guid.Parse("44444444-5555-6666-7777-888888888888");

    [Fact]
    public async Task WaitingPreparation_readiness_state_never_dispatches_native_order()
    {
        var fixture = Fixture(readinessState: TransportReadinessState.WaitingPreparation);
        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("READINESS_WAITING_PREPARATION", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
        Assert.Equal(0, fixture.ExecutionClient.CreateCount);
        Assert.Empty(fixture.WorkflowStore.Transitions);
    }

    [Fact]
    public async Task Duplicate_worker_attempt_aborts_when_lease_cannot_be_acquired()
    {
        var fixture = Fixture();
        fixture.Store.LeaseAvailable = false;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.Null(result);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
        Assert.Equal(0, fixture.ExecutionClient.CreateCount);
    }

    [Fact]
    public async Task Native_dispatch_uses_deterministic_order_and_block_identities()
    {
        var fixture = Fixture();
        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.True(result.Dispatched);
        Assert.Equal(1, fixture.NativeClient.SetOrderCount);
        var order = fixture.NativeClient.LastOrder;
        Assert.NotNull(order);
        Assert.Equal(RequestId.ToString("D"), order.Id);
        Assert.Equal($"{RequestId:D}:pickup", order.Blocks[0].BlockId);
        Assert.Equal($"{RequestId:D}:dropoff", order.Blocks[1].BlockId);
        Assert.Contains(TransportRequestStatus.SentToRds, fixture.WorkflowStore.Transitions);
    }

    [Fact]
    public async Task Ambiguous_setOrder_network_timeout_recovers_via_details_query()
    {
        var fixture = Fixture();
        fixture.NativeClient.SetOrderError = new RdsCoreNativeOrderException("TIMEOUT", "Ambiguous network timeout");
        fixture.NativeClient.DetailsState = RdsCoreKnownOrderState.Running;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.True(result.Dispatched);
        Assert.Contains(TransportRequestStatus.SentToRds, fixture.WorkflowStore.Transitions);
        Assert.Contains(TransportRequestStatus.Running, fixture.WorkflowStore.Transitions);
    }

    [Fact]
    public async Task Finished_native_order_reconciles_to_Completed()
    {
        var fixture = Fixture(activeTaskStatus: TransportRequestStatus.SentToRds);
        fixture.NativeClient.DetailsState = RdsCoreKnownOrderState.Finished;

        await fixture.Coordinator.ReconcileAsync(10, CancellationToken.None);

        Assert.Contains(TransportRequestStatus.Completed, fixture.WorkflowStore.Transitions);
    }

    [Fact]
    public async Task Stopped_native_order_reconciles_to_Cancelled_when_cancellation_requested()
    {
        var fixture = Fixture(activeTaskStatus: TransportRequestStatus.Running, cancellationRequested: true);
        fixture.NativeClient.DetailsState = RdsCoreKnownOrderState.Stopped;

        await fixture.Coordinator.ReconcileAsync(10, CancellationToken.None);

        Assert.Contains(fixture.WorkflowStore.Outcomes, o => o.Status == TransportRequestStatus.Cancelled);
    }

    [Fact]
    public async Task Failed_native_order_reconciles_to_Failed_safely()
    {
        var fixture = Fixture(activeTaskStatus: TransportRequestStatus.Running);
        fixture.NativeClient.DetailsState = RdsCoreKnownOrderState.Failed;

        await fixture.Coordinator.ReconcileAsync(10, CancellationToken.None);

        Assert.Contains(fixture.WorkflowStore.Outcomes, o => o.Status == TransportRequestStatus.Failed);
    }

    [Fact]
    public async Task Unknown_RDS_state_does_not_mutate_request()
    {
        var fixture = Fixture(activeTaskStatus: TransportRequestStatus.SentToRds);
        fixture.NativeClient.DetailsState = RdsCoreKnownOrderState.Unknown;

        await fixture.Coordinator.ReconcileAsync(10, CancellationToken.None);

        Assert.Empty(fixture.WorkflowStore.Transitions);
        Assert.Empty(fixture.WorkflowStore.Outcomes);
    }

    [Fact]
    public async Task Native_and_Wind_mode_are_mutually_exclusive()
    {
        // Native mode: executionClient (WindTask) must NOT be invoked
        var nativeFixture = Fixture(mode: RdsExecutionMode.RdsCoreNativeOrder);
        await nativeFixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());
        Assert.Equal(1, nativeFixture.NativeClient.SetOrderCount);
        Assert.Equal(0, nativeFixture.ExecutionClient.CreateCount);

        // WindTask mode: nativeClient must NOT be invoked
        var windFixture = Fixture(mode: RdsExecutionMode.WindTask);
        await windFixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());
        Assert.Equal(0, windFixture.NativeClient.SetOrderCount);
        Assert.Equal(1, windFixture.ExecutionClient.CreateCount);
    }

    [Fact]
    public async Task Repeated_cancel_is_idempotent()
    {
        var fixture = Fixture(requestStatus: TransportRequestStatus.Cancelled);
        var first = await fixture.Coordinator.CancelAsync(RequestId, null, Guid.NewGuid());
        Assert.True(first.WasIdempotentReplay);

        var second = await fixture.Coordinator.CancelAsync(RequestId, null, Guid.NewGuid());
        Assert.True(second.WasIdempotentReplay);
        Assert.Equal(0, fixture.NativeClient.TerminateCount);
    }

    [Fact]
    public async Task Same_map_bypasses_switch_map_and_dispatches_transport()
    {
        var fixture = Fixture();
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak";
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-A", "DEV-MAP-A", "ACTIVE", "26.7.2026_map_optimak", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.True(result.Dispatched);
        Assert.Equal(0, fixture.NativeClient.SwitchMapCount);
        Assert.Equal(1, fixture.NativeClient.SetOrderCount);
        Assert.Equal(RequestId.ToString("D"), fixture.NativeClient.LastOrder?.Id);
    }

    [Fact]
    public async Task Different_map_orchestrates_navigation_to_transition_station_locks_switches_unlocks_and_dispatches_transport()
    {
        var fixture = Fixture();
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak";
        fixture.NativeClient.CurrentRobotStation = "AP21";
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        // Cycle 1: Robot is at AP21, not at transition station SM11 -> dispatches transition navigation order
        var result1 = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result1);
        Assert.True(result1.Dispatched);
        Assert.Equal("MAP_PREP_DISPATCHED", result1.Code);
        Assert.Equal(1, fixture.NativeClient.SetOrderCount);
        var prepOrder = fixture.NativeClient.Orders[0];
        Assert.Equal($"{RequestId:D}:map-prep:26.7.2026_map_optimak_1", prepOrder.Id);
        Assert.Equal("SM11", prepOrder.Blocks[0].Location);
        Assert.Equal(0, fixture.NativeClient.SwitchMapCount);

        // Robot arrives at SM11, transition navigation finished
        fixture.NativeClient.CurrentRobotStation = "SM11";
        fixture.NativeClient.TransitionOrderState = RdsCoreKnownOrderState.Finished;

        // Cycle 2: Robot at SM11 -> locks, switches map, unlocks, verifies, dispatches main transport order
        var result2 = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result2);
        Assert.True(result2.Dispatched);
        Assert.Equal(2, fixture.NativeClient.SetOrderCount);
        Assert.Equal(1, fixture.NativeClient.LockCount);
        Assert.Equal(1, fixture.NativeClient.SwitchMapCount);
        Assert.Equal("26.7.2026_map_optimak_1", fixture.NativeClient.LastSwitchedMap);
        Assert.Equal(1, fixture.NativeClient.UnlockCount);
        var mainOrder = fixture.NativeClient.Orders[1];
        Assert.Equal(RequestId.ToString("D"), mainOrder.Id);
    }

    [Fact]
    public async Task Different_map_when_already_at_transition_station_skips_preliminary_navigation()
    {
        var fixture = Fixture();
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak";
        fixture.NativeClient.CurrentRobotStation = "SM11";
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.True(result.Dispatched);
        Assert.Equal(1, fixture.NativeClient.SetOrderCount);
        Assert.Equal(RequestId.ToString("D"), fixture.NativeClient.Orders[0].Id);
        Assert.Equal(1, fixture.NativeClient.LockCount);
        Assert.Equal(1, fixture.NativeClient.SwitchMapCount);
        Assert.Equal(1, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Fail_closed_when_NavigationProfile_unresolved()
    {
        var fixture = Fixture();
        fixture.Store.NavigationProfileSummary = null;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("NAVIGATION_PROFILE_UNRESOLVED", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Fail_closed_when_required_map_unresolved()
    {
        var fixture = Fixture();
        fixture.Store.NavigationProfileSummary = fixture.Store.NavigationProfileSummary! with { RobotMapName = "" };

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("REQUIRED_MAP_UNRESOLVED", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Fail_closed_when_vehicle_unresolved()
    {
        var fixture = Fixture();
        fixture.Store.NavigationProfileSummary = fixture.Store.NavigationProfileSummary! with { Vehicle = "" };

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("VEHICLE_UNRESOLVED", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Fail_closed_when_robot_offline()
    {
        var fixture = Fixture();
        fixture.NativeClient.RobotConnectionStatus = 0;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("ROBOT_OFFLINE", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Fail_closed_when_robot_not_dispatchable()
    {
        var fixture = Fixture();
        fixture.NativeClient.RobotDispatchable = false;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("ROBOT_NOT_DISPATCHABLE", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Fail_closed_when_robot_has_blocking_alarm()
    {
        var fixture = Fixture();
        fixture.NativeClient.RobotIsError = true;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("ROBOT_HAS_BLOCKING_ALARM", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Fail_closed_when_robot_has_incompatible_order()
    {
        var fixture = Fixture();
        fixture.NativeClient.RobotCurrentOrderId = "other-incompatible-order";

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("ROBOT_BUSY_INCOMPATIBLE_ORDER", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Fail_closed_when_switch_transition_station_unresolved()
    {
        var fixture = Fixture();
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak";
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", null, "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("SWITCH_TRANSITION_UNRESOLVED", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Fail_closed_when_switchMap_fails()
    {
        var fixture = Fixture();
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak";
        fixture.NativeClient.CurrentRobotStation = "SM11";
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);
        fixture.NativeClient.SwitchMapError = new RdsCoreNativeOrderException("SWITCH_ERR", "SwitchMap rejected by RDS");

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("SWITCHMAP_FAILED", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Fail_closed_when_switchMap_verification_fails()
    {
        var fixture = Fixture();
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak";
        fixture.NativeClient.CurrentRobotStation = "SM11";
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);
        fixture.NativeClient.KeepOldMapOnSwitch = true;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("SWITCHMAP_VERIFICATION_FAILED", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Fail_closed_when_post_switch_alarm_detected()
    {
        var fixture = Fixture();
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak";
        fixture.NativeClient.CurrentRobotStation = "SM11";
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);
        fixture.NativeClient.AlarmAfterSwitch = true;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("POST_SWITCH_ALARM", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Restart_recovery_when_map_already_switched_resumes_without_duplicate_switch()
    {
        var fixture = Fixture();
        // Previous run already switched robot to Map B
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak_1";
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.True(result.Dispatched);
        Assert.Equal(0, fixture.NativeClient.SwitchMapCount);
        Assert.Equal(1, fixture.NativeClient.SetOrderCount);
        Assert.Equal(RequestId.ToString("D"), fixture.NativeClient.LastOrder?.Id);
    }

    [Fact]
    public async Task ExecutionMode_missing_fails_closed_with_EXECUTION_MODE_UNRESOLVED()
    {
        var fixture = Fixture();
        fixture.Store.Context = Context(TransportReadinessState.Ready, executionMode: null);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("EXECUTION_MODE_UNRESOLVED", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
        Assert.Equal(0, fixture.ExecutionClient.CreateCount);
    }

    [Fact]
    public async Task ExecutionMode_invalid_fails_closed_with_EXECUTION_MODE_INVALID()
    {
        var fixture = Fixture();
        fixture.Store.Context = Context(TransportReadinessState.Ready, executionMode: "INVALID_MODE");

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("EXECUTION_MODE_INVALID", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
        Assert.Equal(0, fixture.ExecutionClient.CreateCount);
    }

    [Fact]
    public async Task Unmapped_variant_fails_closed_with_NAVIGATION_PROFILE_UNRESOLVED()
    {
        var fixture = Fixture();
        fixture.Store.NavigationProfileSummary = null;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("NAVIGATION_PROFILE_UNRESOLVED", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
        Assert.Equal(0, fixture.NativeClient.SwitchMapCount);
    }

    [Fact]
    public async Task Wrong_machine_or_different_variant_never_resolves_profile()
    {
        var fixture = Fixture();
        var expectedVariant = Guid.NewGuid();
        var wrongVariant = Guid.NewGuid();
        fixture.Store.ExpectedVariantId = expectedVariant;
        fixture.Store.Context = Context(TransportReadinessState.Ready, variantId: wrongVariant);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("NAVIGATION_PROFILE_UNRESOLVED", result.Code);
    }

    [Fact]
    public async Task Switch_transition_unconfirmed_when_robot_station_does_not_match_transition_station()
    {
        var fixture = Fixture();
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak";
        fixture.NativeClient.CurrentRobotStation = "AP21";
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        // Prep order finished, but robot telemetry reports current_station still at AP21 (not SM11)
        fixture.NativeClient.TransitionOrderState = RdsCoreKnownOrderState.Finished;
        fixture.NativeClient.CurrentRobotStation = "AP21";

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("SWITCH_TRANSITION_NOT_CONFIRMED", result.Code);
        Assert.Equal(0, fixture.NativeClient.LockCount);
        Assert.Equal(0, fixture.NativeClient.SwitchMapCount);
    }

    [Fact]
    public async Task Switch_transition_unconfirmed_when_robot_has_alarm_at_transition_station()
    {
        var fixture = Fixture();
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak";
        fixture.NativeClient.CurrentRobotStation = "SM11";
        fixture.NativeClient.TransitionOrderState = RdsCoreKnownOrderState.Finished;
        fixture.NativeClient.RobotIsError = true;
        fixture.NativeClient.RobotAlarms.Add(new RdsParsedAlarm("52201", RdsAlarmSeverity.Error, "blocking sensor fault", null, 1));
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("ROBOT_HAS_BLOCKING_ALARM", result.Code);
        Assert.Equal(0, fixture.NativeClient.LockCount);
        Assert.Equal(0, fixture.NativeClient.SwitchMapCount);
    }

    [Fact]
    public async Task Crash_recovery_scenario_A_lock_acquired_before_switchMap_crashed_fails_closed()
    {
        var fixture = Fixture();
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak";
        fixture.NativeClient.RobotDispatchable = true;
        fixture.Store.HasUnreleasedLock = true;
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("MAP_SWITCH_LOCK_RECOVERY_REQUIRED", result.Code);
        Assert.Equal(0, fixture.NativeClient.SwitchMapCount);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Crash_race_A_TKG_lock_fingerprint_matches_after_restart_safe_recovery_behavior()
    {
        var fixture = Fixture();
        var lockInfo = new RdsCoreRobotLockInfo(true, "10.0.0.1", "TKG-NATIVE", 5000, "1634023715", 2, "ok");
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak_1";
        fixture.NativeClient.RobotDispatchable = false;
        fixture.NativeClient.RobotLockInfo = lockInfo;
        fixture.Store.MapSwitchLockSnapshot = new RdsMapSwitchLockSnapshot(true, "Sfl-Cbd15", "26.7.2026_map_optimak_1", lockInfo);
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.True(result.Dispatched);
        Assert.Equal(1, fixture.NativeClient.UnlockCount);
        Assert.Equal(1, fixture.NativeClient.SetOrderCount);
    }

    // ---------------------------------------------------------------------
    // Automatic /unlock recovery must revalidate the recorded lock context, not
    // just the fingerprint: the record has to belong to THIS robot and THIS target
    // map. Previously the store took a vehicle argument it never used in the query
    // and the coordinator ignored the record's Vehicle/TargetMap entirely.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Lock_recovery_is_blocked_when_the_record_names_a_different_robot()
    {
        var fixture = Fixture();
        var lockInfo = new RdsCoreRobotLockInfo(true, "10.0.0.1", "TKG-NATIVE", 5000, "1634023715", 2, "ok");
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak_1";
        fixture.NativeClient.RobotDispatchable = false;
        fixture.NativeClient.RobotLockInfo = lockInfo;
        // Same fingerprint, but the record was written for another robot.
        fixture.Store.MapSwitchLockSnapshot = new RdsMapSwitchLockSnapshot(true, "OTHER-ROBOT-99", "26.7.2026_map_optimak_1", lockInfo);
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("MAP_SWITCH_LOCK_RECOVERY_REQUIRED", result.Code);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    // ---------------------------------------------------------------------
    // Correlation proves the lock is OURS. It does not prove that releasing it is
    // SAFE right now. The blocking-alarm and incompatible-order checks used to run
    // AFTER the recovery /unlock had already been issued, and procBusiness was never
    // consulted at all. Every one of these cases must reach UnlockCount == 0.
    // ---------------------------------------------------------------------

    /// <summary>A correlated lock on an idle, quiet robot: recovery is allowed.</summary>
    private static void ArrangeCorrelatedRecovery(NativeTestFixture fixture, RdsCoreRobotLockInfo lockInfo)
    {
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak_1";
        fixture.NativeClient.RobotDispatchable = false;
        fixture.NativeClient.RobotLockInfo = lockInfo;
        fixture.NativeClient.RobotProcBusiness = false;
        fixture.Store.MapSwitchLockSnapshot = new RdsMapSwitchLockSnapshot(
            true, "Sfl-Cbd15", "26.7.2026_map_optimak_1", lockInfo);
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);
    }

    private static RdsCoreRobotLockInfo TkgLock() =>
        new(true, "10.0.0.1", "TKG-NATIVE", 5000, "1634023715", 2, "ok");

    // ---------------------------------------------------------------------
    // Ownership is not safety. An earlier round exempted this request's own main
    // order and its own MAP_PREP order from the active-order check, so automatic
    // recovery could release a lock while the very map preparation the lock exists
    // for was still running. SEER documents no order state that is provably safe to
    // unlock beneath, so ANY active order blocks.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("RUNNING")]
    [InlineData("SUSPENDED")]
    [InlineData("WAITING")]
    public async Task Lock_recovery_is_blocked_by_this_requests_own_active_main_order(string orderState)
    {
        var fixture = Fixture();
        ArrangeCorrelatedRecovery(fixture, TkgLock());
        // The robot is running THIS request's own order.
        fixture.NativeClient.RobotCurrentOrderId = RequestId.ToString("D");
        fixture.NativeClient.RobotCurrentOrderState = orderState;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    [Theory]
    [InlineData("RUNNING")]
    [InlineData("SUSPENDED")]
    public async Task Lock_recovery_is_blocked_by_this_requests_own_active_map_prep_order(string orderState)
    {
        var fixture = Fixture();
        ArrangeCorrelatedRecovery(fixture, TkgLock());
        // A MAP_PREP order still in progress is the most dangerous exemption of all:
        // the map preparation this lock was taken for has not finished.
        fixture.NativeClient.RobotCurrentOrderId =
            RdsCoreNormalOrder.TransitionOrderId(RequestId, "26.7.2026_map_optimak_1");
        fixture.NativeClient.RobotCurrentOrderState = orderState;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Lock_recovery_continues_when_the_robots_own_order_is_terminal()
    {
        // A terminal own order is not active, so it does not block: this proves the
        // rule above rejects ACTIVE execution rather than merely "any order id".
        var fixture = Fixture();
        ArrangeCorrelatedRecovery(fixture, TkgLock());
        fixture.NativeClient.RobotCurrentOrderId = RequestId.ToString("D");
        fixture.NativeClient.RobotCurrentOrderState = "FINISHED";

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.Equal(1, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Lock_recovery_does_not_treat_procBusiness_false_as_proof_of_idle()
    {
        // procBusiness=false is a NECESSARY condition, never sufficient. With an
        // active order present, SEER reporting "no user order" must not unlock.
        var fixture = Fixture();
        ArrangeCorrelatedRecovery(fixture, TkgLock());
        fixture.NativeClient.RobotProcBusiness = false;
        fixture.NativeClient.RobotCurrentOrderId = "SOME-ACTIVE-ORDER";
        fixture.NativeClient.RobotCurrentOrderState = "RUNNING";

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    // ---------------------------------------------------------------------
    // Robot-scoped distributed lease. Deterministic: contention is created by
    // holding a lease across an awaited TaskCompletionSource, never by sleeping.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Expert_mutation_cannot_run_while_another_operation_holds_the_robot_lease()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);

        // Another TKG operation is already inside the critical section for this robot.
        await using var holder = fixture.Store.RobotLeases.TryAcquire("Sfl-Cbd15");
        Assert.NotNull(holder);

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("ROBOT_BUSY", error.Code);
        Assert.Equal(0, fixture.NativeClient.RedoCount);
    }

    [Fact]
    public async Task Two_aliases_of_the_same_robot_take_the_same_lease()
    {
        // The recorded identity is the uuid while telemetry reports the vehicle_id.
        // If these produced two lease keys, two operations could enter the critical
        // section for one physical robot.
        var fixture = ExpertFixture(
            RdsCoreKnownOrderState.Failed,
            robotCurrentOrderId: null,
            recordedAgvId: "hu-test-1",
            robotIdentity: new RdsRobotIdentity("hu-test-1", "AMB-01"));
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);

        // Hold the lease under the OTHER alias before the expert action runs.
        await using var holder = fixture.Store.RobotLeases.TryAcquire("AMB-01");
        Assert.NotNull(holder);

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("ROBOT_BUSY", error.Code);
        Assert.Equal(0, fixture.NativeClient.RedoCount);
    }

    [Fact]
    public async Task A_lease_on_a_different_robot_does_not_block()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);

        await using var unrelated = fixture.Store.RobotLeases.TryAcquire("SOME-OTHER-ROBOT");
        Assert.NotNull(unrelated);

        var result = await fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid());

        Assert.True(result.Accepted);
        Assert.Equal(1, fixture.NativeClient.RedoCount);
    }

    [Fact]
    public async Task The_robot_lease_is_released_after_a_successful_mutation()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);

        await fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid());

        Assert.Equal(0, fixture.Store.RobotLeases.HeldCount);
    }

    [Fact]
    public async Task The_robot_lease_is_released_when_the_adapter_throws()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);
        fixture.NativeClient.RedoError = new RdsCoreNativeOrderException("RDSCORE_UNAVAILABLE", "core down");

        await Assert.ThrowsAsync<RdsCoreNativeOrderException>(
            () => fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid()));

        Assert.Equal(0, fixture.Store.RobotLeases.HeldCount);
    }

    [Fact]
    public async Task The_robot_lease_is_released_when_the_caller_cancels()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);
        fixture.NativeClient.RedoError = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid()));

        Assert.Equal(0, fixture.Store.RobotLeases.HeldCount);
    }

    [Fact]
    public async Task A_safety_stop_is_not_blocked_by_a_held_robot_lease()
    {
        // Pause is SAFETY_STOP: it reduces motion. Making it queue behind a
        // motion-enabling lease would delay a stop, which is the wrong direction.
        var fixture = Fixture();
        await using var holder = fixture.Store.RobotLeases.TryAcquire("Sfl-Cbd15");
        Assert.NotNull(holder);

        var result = await fixture.Coordinator.RobotMaintenanceAsync(
            RdsRobotMaintenanceAction.Pause, ["Sfl-Cbd15"], null, Guid.NewGuid());

        Assert.True(result.CoreAccepted);
    }

    // ---------------------------------------------------------------------
    // Core-level alarms. These sit on the /robotsStatus root, are RDSCore/scene
    // problems rather than one robot's, and used to reach no caller at all.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_core_level_fatal_blocks_dispatch_even_when_every_robot_is_healthy()
    {
        var fixture = Fixture(dispatchEnabled: true);
        fixture.NativeClient.GlobalAlarms.Add(
            new RdsParsedAlarm("52101", RdsAlarmSeverity.Fatal, "have no charge point in map.", null, 1));

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("RDSCORE_HAS_BLOCKING_ALARM", result.Code);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Theory]
    [InlineData(RdsAlarmSeverity.Warning)]
    [InlineData(RdsAlarmSeverity.Notice)]
    public async Task A_core_level_warning_or_notice_does_not_block_dispatch(RdsAlarmSeverity severity)
    {
        var fixture = Fixture(dispatchEnabled: true);
        fixture.NativeClient.GlobalAlarms.Add(
            new RdsParsedAlarm("55001", severity, "dispatching state.", null, 1));

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.NotEqual("RDSCORE_HAS_BLOCKING_ALARM", result.Code);
        Assert.Equal(1, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task A_core_level_fatal_blocks_automatic_lock_recovery()
    {
        // Otherwise fully recoverable: right robot, right map, correlated fingerprint,
        // no active order, procBusiness false. The core alarm alone must stop it.
        var fixture = Fixture();
        ArrangeCorrelatedRecovery(fixture, TkgLock());
        fixture.NativeClient.GlobalAlarms.Add(
            new RdsParsedAlarm("52103", RdsAlarmSeverity.Error, "have no park point in map.", null, 1));

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    // ---------------------------------------------------------------------
    // Held lease vs final mutation target. The lease is taken from a candidate alias
    // BEFORE the authoritative in-lease re-read, so the re-read can land on another
    // physical robot. Mutating it while holding somebody else's lease defeats the
    // lease entirely.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task An_expert_mutation_is_refused_when_the_robot_changes_after_the_lease()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);
        // The lease is resolved from the recorded alias on the first read; the second
        // read inside the lease reports the order on a DIFFERENT machine.
        fixture.NativeClient.IdentityAfterFirstRead = new RdsRobotIdentity("Sfl-Cbd15", "MOVED-ROBOT-2");

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_ROBOT_CHANGED_DURING_VALIDATION", error.Code);
        Assert.Equal(0, fixture.NativeClient.RedoCount);
        Assert.Equal(0, fixture.Store.RobotLeases.HeldCount);
    }

    // ---------------------------------------------------------------------
    // Alias ambiguity. "X" can be robot A's uuid AND robot B's vehicle_id. Picking
    // the first array entry meant payload order decided which machine moved.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task An_alias_matching_two_physical_robots_is_refused()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null, recordedAgvId: "SHARED-X");
        fixture.NativeClient.RobotIdentity = new RdsRobotIdentity("SHARED-X", "ROBOT-A");
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);
        fixture.NativeClient.ExtraRobot = new RdsCoreRobotTelemetry(
            "ROBOT-B", "ROBOT-B", "m", "s", null, 1, true, false, false, 90.0,
            0, 0, 0, null, null, null, [], null,
            new RdsRobotIdentity("uuid-b", "SHARED-X"), false);

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_CURRENT_STATE_UNAVAILABLE", error.Code);
        Assert.Equal(0, fixture.NativeClient.RedoCount);
    }

    [Fact]
    public async Task Ambiguity_does_not_depend_on_payload_order()
    {
        // Same fleet, robots reported in the other order: still refused.
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null, recordedAgvId: "SHARED-X");
        fixture.NativeClient.RobotIdentity = new RdsRobotIdentity("uuid-b", "SHARED-X");
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);
        fixture.NativeClient.ExtraRobot = new RdsCoreRobotTelemetry(
            "ROBOT-A", "ROBOT-A", "m", "s", null, 1, true, false, false, 90.0,
            0, 0, 0, null, null, null, [], null,
            new RdsRobotIdentity("SHARED-X", "ROBOT-A"), false);

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_CURRENT_STATE_UNAVAILABLE", error.Code);
        Assert.Equal(0, fixture.NativeClient.RedoCount);
    }

    [Fact]
    public async Task One_robot_whose_uuid_equals_its_vehicle_id_is_not_ambiguous()
    {
        // The same string in both spaces is ONE machine, so this must still work.
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null, recordedAgvId: "Sfl-Cbd15");
        fixture.NativeClient.RobotIdentity = new RdsRobotIdentity("Sfl-Cbd15", "Sfl-Cbd15");
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);

        var result = await fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid());

        Assert.True(result.Accepted);
        Assert.Equal(1, fixture.NativeClient.RedoCount);
    }

    // ---------------------------------------------------------------------
    // Fleet maintenance must send SERVER-RESOLVED canonical targets, not whatever
    // alias the caller happened to type.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Fleet_maintenance_sends_the_canonical_mutation_target_not_the_caller_alias()
    {
        var fixture = Fixture(dispatchEnabled: true);
        fixture.NativeClient.RobotIdentity = new RdsRobotIdentity("hu-test-1", "AMB-01");

        // The caller names the robot by its uuid; RDS must be called with vehicle_id.
        await fixture.Coordinator.RobotMaintenanceAsync(
            RdsRobotMaintenanceAction.Resume, ["hu-test-1"], null, Guid.NewGuid());

        Assert.Equal(["AMB-01"], fixture.NativeClient.LastMaintenanceVehicles);
    }

    [Fact]
    public async Task Fleet_maintenance_deduplicates_two_aliases_for_one_physical_robot()
    {
        var fixture = Fixture(dispatchEnabled: true);
        fixture.NativeClient.RobotIdentity = new RdsRobotIdentity("hu-test-1", "AMB-01");

        await fixture.Coordinator.RobotMaintenanceAsync(
            RdsRobotMaintenanceAction.Resume, ["hu-test-1", "AMB-01"], null, Guid.NewGuid());

        // One machine -> one target, and one lease.
        Assert.Equal(["AMB-01"], fixture.NativeClient.LastMaintenanceVehicles);
        Assert.Equal(0, fixture.Store.RobotLeases.HeldCount);
    }

    [Fact]
    public async Task Fleet_maintenance_refuses_an_ambiguous_alias_before_mutating()
    {
        var fixture = Fixture(dispatchEnabled: true);
        fixture.NativeClient.RobotIdentity = new RdsRobotIdentity("SHARED-X", "ROBOT-A");
        fixture.NativeClient.ExtraRobot = new RdsCoreRobotTelemetry(
            "ROBOT-B", "ROBOT-B", "m", "s", null, 1, true, false, false, 90.0,
            0, 0, 0, null, null, null, [], null,
            new RdsRobotIdentity("uuid-b", "SHARED-X"), false);

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RobotMaintenanceAsync(
                RdsRobotMaintenanceAction.Resume, ["SHARED-X"], null, Guid.NewGuid()));

        Assert.Equal("ROBOT_ALIAS_AMBIGUOUS", error.Code);
        Assert.Equal(0, fixture.NativeClient.ResumeCount);
    }

    [Fact]
    public async Task A_safety_stop_still_uses_the_caller_alias_and_is_never_refused_for_identity()
    {
        // Pause reduces motion. It must not be blocked by identity bookkeeping.
        var fixture = Fixture(dispatchEnabled: false);
        fixture.NativeClient.RobotIdentity = new RdsRobotIdentity("SHARED-X", "ROBOT-A");
        fixture.NativeClient.ExtraRobot = new RdsCoreRobotTelemetry(
            "ROBOT-B", "ROBOT-B", "m", "s", null, 1, true, false, false, 90.0,
            0, 0, 0, null, null, null, [], null,
            new RdsRobotIdentity("uuid-b", "SHARED-X"), false);

        var result = await fixture.Coordinator.RobotMaintenanceAsync(
            RdsRobotMaintenanceAction.Pause, ["SHARED-X"], null, Guid.NewGuid());

        Assert.True(result.CoreAccepted);
        Assert.Equal(["SHARED-X"], fixture.NativeClient.LastMaintenanceVehicles);
    }

    [Fact]
    public async Task Lock_recovery_is_blocked_when_the_robot_runs_an_incompatible_order()
    {
        var fixture = Fixture();
        ArrangeCorrelatedRecovery(fixture, TkgLock());
        // Fingerprint, robot and map all correlate, but the robot is actively running
        // somebody else's order. Unlocking underneath it is unsafe.
        fixture.NativeClient.RobotCurrentOrderId = "SOME-OTHER-ACTIVE-ORDER";

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Lock_recovery_is_blocked_when_the_robot_has_a_blocking_alarm()
    {
        var fixture = Fixture();
        ArrangeCorrelatedRecovery(fixture, TkgLock());
        fixture.NativeClient.RobotAlarms.Add(
            new RdsParsedAlarm("52201", RdsAlarmSeverity.Error, "blocking fault", null, 1));

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Lock_recovery_is_blocked_when_the_robot_is_executing_business_work()
    {
        var fixture = Fixture();
        ArrangeCorrelatedRecovery(fixture, TkgLock());
        // SEER documents idle as dispatchable && !procBusiness. procBusiness == true
        // means the robot is running a business waybill.
        fixture.NativeClient.RobotProcBusiness = true;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Lock_recovery_fails_closed_when_business_state_is_unknown()
    {
        var fixture = Fixture();
        ArrangeCorrelatedRecovery(fixture, TkgLock());
        // procBusiness absent from telemetry: UNKNOWN, never assumed idle.
        fixture.NativeClient.RobotProcBusiness = null;

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Lock_recovery_is_blocked_when_the_live_fingerprint_is_external()
    {
        var fixture = Fixture();
        ArrangeCorrelatedRecovery(fixture, TkgLock());
        // The robot is locked, but by somebody else: a different host, session and
        // acquisition time. The recorded TKG fingerprint cannot correlate with it.
        fixture.NativeClient.RobotLockInfo =
            new RdsCoreRobotLockInfo(true, "192.168.9.9", "ENGINEER-LAPTOP", 61111, "1699999999", 255, "external");

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("MAP_SWITCH_LOCK_RECOVERY_REQUIRED", result.Code);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Lock_recovery_still_succeeds_on_a_correlated_idle_robot()
    {
        // The counterpart to the blocked cases: with every safety condition met the
        // unlock happens exactly once and dispatch proceeds. Without this the tests
        // above could pass simply because recovery never runs.
        var fixture = Fixture();
        ArrangeCorrelatedRecovery(fixture, TkgLock());

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.True(result.Dispatched);
        Assert.Equal(1, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Lock_recovery_is_blocked_when_the_record_targets_a_different_map()
    {
        var fixture = Fixture();
        var lockInfo = new RdsCoreRobotLockInfo(true, "10.0.0.1", "TKG-NATIVE", 5000, "1634023715", 2, "ok");
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak_1";
        fixture.NativeClient.RobotDispatchable = false;
        fixture.NativeClient.RobotLockInfo = lockInfo;
        // Right robot and fingerprint, but the record was written for another map.
        fixture.Store.MapSwitchLockSnapshot = new RdsMapSwitchLockSnapshot(true, "Sfl-Cbd15", "some_other_map", lockInfo);
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("MAP_SWITCH_LOCK_RECOVERY_REQUIRED", result.Code);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Lock_recovery_is_blocked_when_the_record_has_no_robot_identity()
    {
        var fixture = Fixture();
        var lockInfo = new RdsCoreRobotLockInfo(true, "10.0.0.1", "TKG-NATIVE", 5000, "1634023715", 2, "ok");
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak_1";
        fixture.NativeClient.RobotDispatchable = false;
        fixture.NativeClient.RobotLockInfo = lockInfo;
        fixture.Store.MapSwitchLockSnapshot = new RdsMapSwitchLockSnapshot(true, null, null, lockInfo);
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Lock_recovery_matches_the_recorded_robot_across_identity_spaces()
    {
        // The audit row may have recorded the internal uuid while telemetry reports
        // the dispatch vehicle id. Correlation must still succeed — and must not be
        // done by naive string equality.
        var fixture = Fixture();
        var lockInfo = new RdsCoreRobotLockInfo(true, "10.0.0.1", "TKG-NATIVE", 5000, "1634023715", 2, "ok");
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak_1";
        fixture.NativeClient.RobotDispatchable = false;
        fixture.NativeClient.RobotLockInfo = lockInfo;
        fixture.NativeClient.RobotIdentity = new RdsRobotIdentity("ROBOT-INTERNAL-123", "Sfl-Cbd15");
        fixture.Store.MapSwitchLockSnapshot = new RdsMapSwitchLockSnapshot(true, "ROBOT-INTERNAL-123", "26.7.2026_map_optimak_1", lockInfo);
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.True(result.Dispatched);
        Assert.Equal(1, fixture.NativeClient.UnlockCount);
    }

    [Fact]
    public async Task Crash_race_B_TKG_historical_unreleased_audit_exists_but_current_lock_info_differs_never_unlocks()
    {
        var fixture = Fixture();
        var tkgLock = new RdsCoreRobotLockInfo(true, "10.0.0.1", "TKG-NATIVE", 5000, "1634023715", 2, "ok");
        var externalLock = new RdsCoreRobotLockInfo(true, "10.0.0.99", "EXT-ENG", 5500, "1634029999", 2, "commissioning");
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak_1";
        fixture.NativeClient.RobotDispatchable = false;
        fixture.NativeClient.RobotLockInfo = externalLock;
        fixture.Store.MapSwitchLockSnapshot = new RdsMapSwitchLockSnapshot(true, "Sfl-Cbd15", "26.7.2026_map_optimak_1", tkgLock);
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("MAP_SWITCH_LOCK_RECOVERY_REQUIRED", result.Code);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Crash_race_C_TKG_historical_unreleased_audit_exists_but_current_lock_info_unavailable_never_unlocks()
    {
        var fixture = Fixture();
        var tkgLock = new RdsCoreRobotLockInfo(true, "10.0.0.1", "TKG-NATIVE", 5000, "1634023715", 2, "ok");
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak_1";
        fixture.NativeClient.RobotDispatchable = false;
        fixture.NativeClient.RobotLockInfo = null;
        fixture.Store.MapSwitchLockSnapshot = new RdsMapSwitchLockSnapshot(true, "Sfl-Cbd15", "26.7.2026_map_optimak_1", tkgLock);
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("MAP_SWITCH_LOCK_RECOVERY_REQUIRED", result.Code);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Crash_race_D_unlock_succeeded_but_release_audit_lost_then_external_lock_acquired_never_unlocks_external_lock()
    {
        var fixture = Fixture();
        var oldTkgLock = new RdsCoreRobotLockInfo(true, "10.0.0.1", "TKG-NATIVE", 5000, "1634023715", 2, "ok");
        var externalLock = new RdsCoreRobotLockInfo(true, "192.168.1.88", "ENG-ROBOSHOP", 6000, "1634030000", 255, "tuning");
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak_1";
        fixture.NativeClient.RobotDispatchable = false;
        fixture.NativeClient.RobotLockInfo = externalLock;
        fixture.Store.MapSwitchLockSnapshot = new RdsMapSwitchLockSnapshot(true, "Sfl-Cbd15", "26.7.2026_map_optimak_1", oldTkgLock);
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("MAP_SWITCH_LOCK_RECOVERY_REQUIRED", result.Code);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    [Fact]
    public async Task Crash_race_E_same_TKG_lock_still_exists_after_crash_and_target_map_already_switched_safe_correlated_recovery_unlock()
    {
        var fixture = Fixture();
        var tkgLock = new RdsCoreRobotLockInfo(true, "10.0.0.1", "TKG-NATIVE", 5000, "1634023715", 2, "ok");
        fixture.NativeClient.CurrentRobotMap = "26.7.2026_map_optimak_1";
        fixture.NativeClient.RobotDispatchable = false;
        fixture.NativeClient.RobotLockInfo = tkgLock;
        fixture.Store.MapSwitchLockSnapshot = new RdsMapSwitchLockSnapshot(true, "Sfl-Cbd15", "26.7.2026_map_optimak_1", tkgLock);
        fixture.Store.NavigationProfileSummary = new(
            Guid.NewGuid(), "DEV-MAP-B", "DEV-MAP-B", "ACTIVE", "26.7.2026_map_optimak_1", "SM11", "Sfl-Cbd15", JsonDocument.Parse("{}").RootElement);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.True(result.Dispatched);
        Assert.Equal(1, fixture.NativeClient.UnlockCount);
        Assert.Equal(1, fixture.NativeClient.SetOrderCount);
    }

    // ---------------------------------------------------------------------
    // P0 — Global physical mutation gate.
    // Rds:DispatchEnabled=false must mean ZERO normal physical RDS mutation,
    // even when the native engine is fully enabled and correctly configured.
    // Before the fix IsDispatchEnabled was `(... ) || IsNativeMode`, so native
    // enablement silently bypassed the gate and these tests failed.
    // ---------------------------------------------------------------------

    [Fact]
    public void Global_dispatch_gate_closed_keeps_mutation_ineligible_even_when_native_is_fully_enabled()
    {
        var fixture = Fixture(mode: RdsExecutionMode.RdsCoreNativeOrder, dispatchEnabled: false);

        Assert.True(fixture.NativeClient.MutationEnabled);
        Assert.Equal(RdsExecutionMode.RdsCoreNativeOrder, fixture.NativeClient.Mode);
        Assert.False(fixture.Coordinator.IsDispatchEnabled);
    }

    [Fact]
    public void Global_dispatch_gate_open_allows_native_to_become_eligible()
    {
        var fixture = Fixture(mode: RdsExecutionMode.RdsCoreNativeOrder, dispatchEnabled: true);

        Assert.True(fixture.Coordinator.IsDispatchEnabled);
    }

    [Fact]
    public async Task Global_dispatch_gate_closed_blocks_dispatch_without_issuing_any_rdscore_mutation()
    {
        var fixture = Fixture(mode: RdsExecutionMode.RdsCoreNativeOrder, dispatchEnabled: false);

        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.NotNull(result);
        Assert.False(result.Dispatched);
        Assert.Equal("RDS_DISPATCH_DISABLED", result.Code);

        // No /setOrder, /lock, /switchMap or /unlock may leave the process.
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
        Assert.Equal(0, fixture.NativeClient.LockCount);
        Assert.Equal(0, fixture.NativeClient.SwitchMapCount);
        Assert.Equal(0, fixture.NativeClient.UnlockCount);
        Assert.Equal(0, fixture.ExecutionClient.CreateCount);
        Assert.Contains(fixture.Store.Audits, a => a.Code == "RDS_DISPATCH_DISABLED");
    }

    [Fact]
    public async Task Global_dispatch_gate_closed_yields_no_dispatch_from_the_queued_worker_path()
    {
        var fixture = Fixture(mode: RdsExecutionMode.RdsCoreNativeOrder, dispatchEnabled: false);

        var results = await fixture.Coordinator.DispatchQueuedAsync(10);

        Assert.Empty(results);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
        Assert.Equal(0, fixture.ExecutionClient.CreateCount);
    }

    [Fact]
    public async Task Global_dispatch_gate_closed_still_permits_read_only_reconciliation()
    {
        // Read-only telemetry and Admin diagnostics must survive a closed gate.
        var fixture = Fixture(
            requestStatus: TransportRequestStatus.SentToRds,
            activeTaskStatus: TransportRequestStatus.SentToRds,
            mode: RdsExecutionMode.RdsCoreNativeOrder,
            dispatchEnabled: false);

        await fixture.Coordinator.ReconcileAsync(10);

        Assert.True(fixture.NativeClient.OrderDetailsCount > 0);
        Assert.Equal(0, fixture.NativeClient.SetOrderCount);
    }

    // ---------------------------------------------------------------------
    // Global mutation gate beyond the dispatch path.
    // Rds:DispatchEnabled=false must also refuse expert and fleet-maintenance
    // operations that START, RESUME, RE-ENABLE or ADVANCE physical work, while
    // still permitting the safety-reducing ones.
    // ---------------------------------------------------------------------

    // ---------------------------------------------------------------------
    // manualFinished / redoFailedOrder are VEHICLE-scoped: they act on whatever the
    // named robot is doing right now. A historical integration.rds_tasks.agv_id is
    // therefore not authority for them. Every blocked case below must reach a zero
    // call count on the adapter.
    // ---------------------------------------------------------------------

    private static NativeTestFixture ExpertFixture(
        RdsCoreKnownOrderState orderState,
        string? robotCurrentOrderId,
        string recordedAgvId = "Sfl-Cbd15",
        RdsRobotIdentity? robotIdentity = null)
    {
        var fixture = Fixture(
            requestStatus: TransportRequestStatus.SentToRds,
            activeTaskStatus: TransportRequestStatus.SentToRds);
        fixture.Store.TaskProjection = fixture.Store.TaskProjection! with { AgvId = recordedAgvId };
        fixture.NativeClient.DetailsState = orderState;
        fixture.NativeClient.RobotCurrentOrderId = robotCurrentOrderId;
        fixture.NativeClient.RobotIdentity = robotIdentity;
        return fixture;
    }

    private static string OrderIdOf(NativeTestFixture fixture) => fixture.Store.TaskProjection!.TaskRecordId;

    // SEER (docx 4.3, /manualFinished): "Yalnız mevcut order hata durumundayken veya
    // AGV hata raporluyorken çalışır." — only while the current order is in an error
    // state or the AGV is reporting an error. RUNNING/WAITING/SUSPENDED on their own
    // are NOT permission; an earlier round accepted them, which asked RDSCore to
    // advance a block that was executing normally.

    [Theory]
    [InlineData(RdsCoreKnownOrderState.Running)]
    [InlineData(RdsCoreKnownOrderState.Waiting)]
    [InlineData(RdsCoreKnownOrderState.Suspended)]
    public async Task Manual_finish_is_blocked_without_the_documented_error_condition(RdsCoreKnownOrderState state)
    {
        var fixture = ExpertFixture(state, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);
        fixture.NativeClient.RobotIsError = false;

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.ManualFinishAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_ORDER_STATE_INCOMPATIBLE", error.Code);
        Assert.Equal(0, fixture.NativeClient.ManualFinishedCount);
    }

    [Theory]
    [InlineData(RdsCoreKnownOrderState.Failed)]
    [InlineData(RdsCoreKnownOrderState.Error)]
    public async Task Manual_finish_is_allowed_when_the_order_is_in_an_error_state(RdsCoreKnownOrderState state)
    {
        var fixture = ExpertFixture(state, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);

        var result = await fixture.Coordinator.ManualFinishAsync(RequestId, Guid.NewGuid());

        Assert.True(result.Accepted);
        Assert.Equal(1, fixture.NativeClient.ManualFinishedCount);
    }

    [Fact]
    public async Task Manual_finish_is_allowed_when_the_robot_itself_reports_an_error()
    {
        // The documented precondition is a disjunction: an order that is merely RUNNING
        // still qualifies when the AGV is the thing reporting the error.
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Running, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);
        fixture.NativeClient.RobotIsError = true;

        var result = await fixture.Coordinator.ManualFinishAsync(RequestId, Guid.NewGuid());

        Assert.True(result.Accepted);
        Assert.Equal(1, fixture.NativeClient.ManualFinishedCount);
    }

    [Fact]
    public async Task Manual_finish_accepts_a_blocking_alarm_as_the_robot_error_condition()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Running, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);
        fixture.NativeClient.RobotAlarms.Add(
            new RdsParsedAlarm("52201", RdsAlarmSeverity.Error, "blocking", null, 1));

        var result = await fixture.Coordinator.ManualFinishAsync(RequestId, Guid.NewGuid());

        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task Manual_finish_does_not_treat_a_notice_as_a_robot_error_condition()
    {
        // Severity matters: a notice is not "the AGV is reporting an error".
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Running, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);
        fixture.NativeClient.RobotAlarms.Add(
            new RdsParsedAlarm("57001", RdsAlarmSeverity.Notice, "param system error", null, 1));

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.ManualFinishAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_ORDER_STATE_INCOMPATIBLE", error.Code);
        Assert.Equal(0, fixture.NativeClient.ManualFinishedCount);
    }

    [Fact]
    public async Task Manual_finish_is_blocked_when_the_robot_now_runs_a_different_order()
    {
        // The historical agv_id still names this robot, but it has moved on, so
        // finishing "the current block" would advance an unrelated order.
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, "SOME-OTHER-ORDER");

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.ManualFinishAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_ORDER_NOT_CURRENTLY_EXECUTING", error.Code);
        Assert.Equal(0, fixture.NativeClient.ManualFinishedCount);
    }

    [Fact]
    public async Task Manual_finish_is_blocked_when_no_robot_reports_the_order()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null);

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.ManualFinishAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_ORDER_NOT_CURRENTLY_EXECUTING", error.Code);
        Assert.Equal(0, fixture.NativeClient.ManualFinishedCount);
    }

    [Fact]
    public async Task Manual_finish_is_blocked_when_the_order_is_terminal()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Finished, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.ManualFinishAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_ORDER_STATE_INCOMPATIBLE", error.Code);
        Assert.Equal(0, fixture.NativeClient.ManualFinishedCount);
    }

    [Fact]
    public async Task Manual_finish_is_blocked_when_a_different_robot_executes_the_order()
    {
        // The recorded robot is present and identifiable, but a DIFFERENT robot in the
        // fleet is the one actually executing the order.
        var fixture = ExpertFixture(
            RdsCoreKnownOrderState.Failed,
            robotCurrentOrderId: null,
            recordedAgvId: "Sfl-Cbd15");
        fixture.NativeClient.RobotCurrentOrderId = null;
        fixture.NativeClient.ExtraRobot = new RdsCoreRobotTelemetry(
            "OTHER-ROBOT-9", "OTHER-ROBOT-9", "map", "st", null, 1, true, false, false, 90.0,
            0, 0, 0, null, OrderIdOf(fixture), null, [], null,
            new RdsRobotIdentity("uuid-of-other", "OTHER-ROBOT-9"), false);

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.ManualFinishAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_ROBOT_MISMATCH", error.Code);
        Assert.Equal(0, fixture.NativeClient.ManualFinishedCount);
    }

    [Fact]
    public async Task Manual_finish_corroborates_across_identity_spaces()
    {
        // The recorded agv_id is the internal uuid while telemetry reports the
        // dispatch vehicle_id. Same robot, so it must be allowed — and the mutation
        // must target the vehicle-space identifier.
        var fixture = ExpertFixture(
            RdsCoreKnownOrderState.Failed,
            robotCurrentOrderId: null,
            recordedAgvId: "hu-test-1",
            robotIdentity: new RdsRobotIdentity("hu-test-1", "AMB-01"));
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);

        var result = await fixture.Coordinator.ManualFinishAsync(RequestId, Guid.NewGuid());

        Assert.True(result.Accepted);
        Assert.Equal("AMB-01", result.Vehicle);
        Assert.Equal(1, fixture.NativeClient.ManualFinishedCount);
    }

    [Fact]
    public async Task Redo_is_allowed_only_for_a_currently_failed_order()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);

        var result = await fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid());

        Assert.True(result.Accepted);
        Assert.Equal(1, fixture.NativeClient.RedoCount);
    }

    // SEER (docx 3.18, /redoFailedOrder): "Yalnız order durumu FAILED iken etkilidir;
    // diğer durumlarda komut yok sayılır." FAILED only. Error is a DIFFERENT documented
    // state ("parametre hatası nedeniyle yürütülemez") and an earlier round wrongly
    // treated the two as equivalent.
    [Theory]
    [InlineData(RdsCoreKnownOrderState.Error)]
    [InlineData(RdsCoreKnownOrderState.Running)]
    [InlineData(RdsCoreKnownOrderState.Suspended)]
    [InlineData(RdsCoreKnownOrderState.Finished)]
    public async Task Redo_is_blocked_for_every_state_other_than_failed(RdsCoreKnownOrderState state)
    {
        var fixture = ExpertFixture(state, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_ORDER_STATE_INCOMPATIBLE", error.Code);
        Assert.Equal(0, fixture.NativeClient.RedoCount);
    }

    [Fact]
    public async Task Redo_is_blocked_when_the_robot_now_runs_a_different_order()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, "SOME-OTHER-ORDER");

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_ORDER_NOT_CURRENTLY_EXECUTING", error.Code);
        Assert.Equal(0, fixture.NativeClient.RedoCount);
    }

    [Fact]
    public async Task Expert_actions_are_blocked_when_current_rds_evidence_cannot_be_read()
    {
        var fixture = ExpertFixture(RdsCoreKnownOrderState.Failed, robotCurrentOrderId: null);
        fixture.NativeClient.RobotCurrentOrderId = OrderIdOf(fixture);
        fixture.NativeClient.RobotsStatusError =
            new RdsCoreNativeOrderException("RDSCORE_UNAVAILABLE", "core down");

        var error = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("EXPERT_CURRENT_STATE_UNAVAILABLE", error.Code);
        Assert.Equal(0, fixture.NativeClient.RedoCount);
    }

    [Fact]
    public async Task Gate_closed_blocks_manual_finish_redo_and_priority()
    {
        var fixture = Fixture(
            requestStatus: TransportRequestStatus.SentToRds,
            activeTaskStatus: TransportRequestStatus.SentToRds,
            dispatchEnabled: false);

        var manualFinish = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.ManualFinishAsync(RequestId, Guid.NewGuid()));
        var redo = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RedoFailedAsync(RequestId, Guid.NewGuid()));
        var priority = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.SetRequestPriorityAsync(RequestId, 5, Guid.NewGuid()));

        Assert.Equal("RDS_DISPATCH_DISABLED", manualFinish.Code);
        Assert.Equal("RDS_DISPATCH_DISABLED", redo.Code);
        Assert.Equal("RDS_DISPATCH_DISABLED", priority.Code);
        Assert.Equal(0, fixture.NativeClient.ManualFinishedCount);
        Assert.Equal(0, fixture.NativeClient.RedoCount);
        Assert.Equal(0, fixture.NativeClient.SetPriorityCount);
    }

    [Fact]
    public async Task Gate_closed_blocks_resume_clear_errors_and_making_a_robot_dispatchable()
    {
        var fixture = Fixture(dispatchEnabled: false);

        foreach (var (action, type) in new (RdsRobotMaintenanceAction, string?)[]
                 {
                     (RdsRobotMaintenanceAction.Resume, null),
                     (RdsRobotMaintenanceAction.ClearErrors, null),
                     (RdsRobotMaintenanceAction.SetDispatchable, "dispatchable")
                 })
        {
            var error = await Assert.ThrowsAsync<RdsExecutionException>(
                () => fixture.Coordinator.RobotMaintenanceAsync(action, ["AGV-01"], type, Guid.NewGuid()));
            Assert.Equal("RDS_DISPATCH_DISABLED", error.Code);
        }

        Assert.Equal(0, fixture.NativeClient.ResumeCount);
        Assert.Equal(0, fixture.NativeClient.ClearErrorsCount);
        Assert.Equal(0, fixture.NativeClient.DispatchableCount);
    }

    [Fact]
    public async Task Gate_closed_still_permits_pause_and_making_a_robot_undispatchable()
    {
        // Safety-reducing operations stay reachable: they remove motion, not cause it.
        var fixture = Fixture(dispatchEnabled: false);

        await fixture.Coordinator.RobotMaintenanceAsync(RdsRobotMaintenanceAction.Pause, ["AGV-01"], null, Guid.NewGuid());
        await fixture.Coordinator.RobotMaintenanceAsync(
            RdsRobotMaintenanceAction.SetDispatchable, ["AGV-01"], "undispatchable_unignore", Guid.NewGuid());

        Assert.Equal(1, fixture.NativeClient.PauseCount);
        Assert.Equal(1, fixture.NativeClient.DispatchableCount);
    }

    [Fact]
    public async Task Gate_closed_still_permits_cancellation_because_terminate_stops_work()
    {
        var fixture = Fixture(
            requestStatus: TransportRequestStatus.SentToRds,
            activeTaskStatus: TransportRequestStatus.SentToRds,
            dispatchEnabled: false);

        await fixture.Coordinator.CancelAsync(RequestId, null, Guid.NewGuid());

        Assert.Equal(1, fixture.NativeClient.TerminateCount);
    }

    [Fact]
    public async Task Gate_open_permits_the_motion_enabling_maintenance_operations()
    {
        var fixture = Fixture(dispatchEnabled: true);

        // The robot must be identifiable in current telemetry: these are
        // motion-enabling actions and they take the robot-scoped lease, which cannot
        // be keyed on a robot RDSCore does not report.
        await fixture.Coordinator.RobotMaintenanceAsync(RdsRobotMaintenanceAction.Resume, ["Sfl-Cbd15"], null, Guid.NewGuid());
        await fixture.Coordinator.RobotMaintenanceAsync(RdsRobotMaintenanceAction.ClearErrors, ["Sfl-Cbd15"], null, Guid.NewGuid());

        Assert.Equal(1, fixture.NativeClient.ResumeCount);
        Assert.Equal(1, fixture.NativeClient.ClearErrorsCount);
        // And both released their leases.
        Assert.Equal(0, fixture.Store.RobotLeases.HeldCount);
    }

    [Fact]
    public async Task Robot_maintenance_requires_an_explicit_non_empty_vehicle_list()
    {
        var fixture = Fixture(dispatchEnabled: true);

        var empty = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RobotMaintenanceAsync(RdsRobotMaintenanceAction.Pause, [], null, Guid.NewGuid()));
        var blank = await Assert.ThrowsAsync<RdsExecutionException>(
            () => fixture.Coordinator.RobotMaintenanceAsync(RdsRobotMaintenanceAction.Pause, ["  "], null, Guid.NewGuid()));

        Assert.Equal("VEHICLES_REQUIRED", empty.Code);
        Assert.Equal("VEHICLES_REQUIRED", blank.Code);
        Assert.Equal(0, fixture.NativeClient.PauseCount);
    }

    private static NativeTestFixture Fixture(
        TransportRequestStatus requestStatus = TransportRequestStatus.Queued,
        TransportReadinessState readinessState = TransportReadinessState.Ready,
        TransportRequestStatus? activeTaskStatus = null,
        bool cancellationRequested = false,
        RdsExecutionMode mode = RdsExecutionMode.RdsCoreNativeOrder,
        bool dispatchEnabled = true)
    {
        var executionMode = mode == RdsExecutionMode.WindTask ? "WIND_TASK" : "RDSCORE_NATIVE_ORDER";
        var context = Context(readinessState: readinessState, executionMode: executionMode);
        var store = new FakeNativeStore
        {
            Context = context,
            RequestStatus = requestStatus,
            CancellationRequested = cancellationRequested
        };

        if (activeTaskStatus is { } activeStatus)
        {
            var proj = new RdsTaskProjection(
                RequestId, RequestId.ToString("D"), "TKG_TRANSPORT", 1000, null, null, null,
                JsonDocument.Parse("{}").RootElement, JsonDocument.Parse("{}").RootElement, JsonDocument.Parse("{}").RootElement,
                cancellationRequested, activeStatus);
            store.Active = [proj];
            store.TaskProjection = proj;
        }

        var workflowStore = new FakeNativeWorkflowStore(store.Request);
        // The Wind client mirrors production binding (Rds:Enabled && Rds:DispatchEnabled),
        // so closing the global gate closes it here too. Native stays fully configured
        // in order to prove it cannot rescue a closed gate.
        var executionClient = new FakeNativeExecutionClient(dispatchEnabled);
        var readClient = new FakeNativeReadClient();
        var reconciler = new FakeNativeWorkSiteReconciler();
        var nativeClient = new FakeNativeOrderClient(mode);
        var settings = new RdsExecutionSettings(dispatchEnabled, TimeSpan.FromMinutes(5), "", "");

        var coordinator = new RdsExecutionCoordinator(
            store,
            executionClient,
            readClient,
            reconciler,
            new TransportWorkflowService(workflowStore),
            settings,
            TimeProvider.System,
            nativeOrderClient: nativeClient);

        return new(coordinator, store, workflowStore, executionClient, nativeClient);
    }

    private static RdsDispatchContext Context(
        TransportReadinessState readinessState,
        string? executionMode = "RDSCORE_NATIVE_ORDER",
        Guid? variantId = null,
        Guid? machineId = null) => new(
        new TransportRequestSnapshot(
            RequestId, 1, "test-key", TransportRequestStatus.Queued, TransportTaskType.BringEmpty,
            RequestOrigin.Api, Guid.NewGuid(), machineId ?? Guid.NewGuid(), variantId ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100,
            readinessState, null),
        "SH-01-A", "V1", "71550", "715A2", "KCK", Guid.NewGuid(),
        "TKG_TRANSPORT", 100,
        executionMode is not null
            ? JsonSerializer.SerializeToElement(new
            {
                executionMode = executionMode,
                rdsInputBindings = new[]
                {
                    new { param = "fromSite", source = "SOURCE_RDS_SITE_ID", required = true, value = (string?)null },
                    new { param = "toSite", source = "DESTINATION_RDS_SITE_ID", required = true, value = (string?)null }
                }
            })
            : JsonSerializer.SerializeToElement(new
            {
                rdsInputBindings = new[]
                {
                    new { param = "fromSite", source = "SOURCE_RDS_SITE_ID", required = true, value = (string?)null },
                    new { param = "toSite", source = "DESTINATION_RDS_SITE_ID", required = true, value = (string?)null }
                }
            }),
        new RdsDispatchEndpoint(Guid.NewGuid(), "SOURCE_STATION", "GRP", "STAGING", "SOURCE_STATION", 1, 0, 0, 0, 0, DateTimeOffset.UtcNow, true),
        new RdsDispatchEndpoint(Guid.NewGuid(), "DEST_STATION", "GRP", "STAGING", "DEST_STATION", 1, 0, 0, 0, 0, DateTimeOffset.UtcNow, true));

    private sealed record NativeTestFixture(
        RdsExecutionCoordinator Coordinator,
        FakeNativeStore Store,
        FakeNativeWorkflowStore WorkflowStore,
        FakeNativeExecutionClient ExecutionClient,
        FakeNativeOrderClient NativeClient);

    private sealed class FakeNativeExecutionLease : IRdsExecutionLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeNativeStore : IRdsExecutionStore
    {
        public RdsDispatchContext Context { get; set; } = Context(TransportReadinessState.Ready);
        public TransportRequestStatus RequestStatus { get; set; }
        public bool CancellationRequested { get; set; }
        public bool LeaseAvailable { get; set; } = true;
        public IReadOnlyList<RdsTaskProjection> Active { get; set; } = [];
        public RdsTaskProjection? TaskProjection { get; set; }
        public TransportRequestSnapshot Request => Context.Request with { Status = RequestStatus };
        public RdsNavigationProfileSummary? NavigationProfileSummary { get; set; } = new(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "DEV-RDS-SIM-MAP-A",
            "DEV-RDS-SIM-MAP-A",
            "ACTIVE",
            "26.7.2026_map_optimak",
            "SM11",
            "Sfl-Cbd15",
            JsonDocument.Parse("{}").RootElement);

        public bool HasUnreleasedLock { get; set; } = false;
        public RdsMapSwitchLockSnapshot? MapSwitchLockSnapshot { get; set; }
        public Guid? ExpectedVariantId { get; set; }
        public Guid? ExpectedMachineId { get; set; }

        public Task<IReadOnlyList<Guid>> ListQueuedRequestIdsAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([RequestId]);
        public Task<IReadOnlyList<RdsTaskProjection>> ListActiveTasksAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(Active);
        public Task<IReadOnlyList<Guid>> ListPendingWorkSiteRefreshRequestIdsAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<IRdsExecutionLease?> TryAcquireRequestLeaseAsync(Guid requestId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IRdsExecutionLease?>(LeaseAvailable ? new FakeNativeExecutionLease() : null);

        /// <summary>
        /// Models the PostgreSQL session advisory lock: exclusive per key, released on
        /// dispose. Held keys are shared across every store instance created from the
        /// same registry so two coordinators can contend exactly as two TKG processes
        /// would. Also records which keys were requested, so a test can prove that two
        /// identity aliases of one robot resolve to the SAME key.
        /// </summary>
        public RobotLeaseRegistry RobotLeases { get; set; } = new();

        public Task<IRdsExecutionLease?> TryAcquireRobotLeaseAsync(string robotLeaseKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(RobotLeases.TryAcquire(robotLeaseKey));
        public Task<RdsDispatchContext?> GetDispatchContextAsync(Guid requestId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RdsDispatchContext?>(Context with { Request = Request });
        public Task<TransportRequestSnapshot?> FindRequestAsync(Guid requestId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TransportRequestSnapshot?>(Request);
        public Task<RdsTaskProjection?> FindTaskByRequestAsync(Guid requestId, CancellationToken cancellationToken = default) =>
            Task.FromResult(TaskProjection);
        public Task<RdsTaskProjection?> FindTaskByRecordIdAsync(string taskRecordId, CancellationToken cancellationToken = default) =>
            Task.FromResult(TaskProjection);
        public Task EnsureTaskRecordAsync(Guid requestId, string taskRecordId, string taskLabel, JsonElement requestPayload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RecordDispatchResponseAsync(Guid requestId, JsonElement responsePayload, DateTimeOffset syncedAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RecordTaskStatusAsync(Guid requestId, RdsWindTaskSnapshot task, DateTimeOffset syncedAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task MarkCancellationRequestedAsync(Guid requestId, JsonElement responsePayload, DateTimeOffset requestedAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public List<(string Action, string Code, string Message)> Audits { get; } = [];
        public Task WriteExecutionAuditAsync(Guid requestId, string action, string code, string message, Guid correlationId, CancellationToken cancellationToken = default)
        {
            Audits.Add((action, code, message));
            return Task.CompletedTask;
        }
        public Task WriteExecutionAuditAsync(Guid requestId, string action, string code, string message, Guid correlationId, object? extraDetails, CancellationToken cancellationToken = default)
        {
            Audits.Add((action, code, message));
            return Task.CompletedTask;
        }
        public Task<RdsMappedWorkSite?> FindMappedWorkSiteAsync(Guid slotId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RdsMappedWorkSite?>(null);
        public Task<RdsNavigationProfileSummary?> GetNavigationProfileSummaryAsync(Guid? profileId, Guid variantId, Guid machineId, CancellationToken cancellationToken = default)
        {
            if (ExpectedVariantId.HasValue && variantId != ExpectedVariantId.Value) return Task.FromResult<RdsNavigationProfileSummary?>(null);
            if (ExpectedMachineId.HasValue && machineId != ExpectedMachineId.Value) return Task.FromResult<RdsNavigationProfileSummary?>(null);
            return Task.FromResult(NavigationProfileSummary);
        }
        public Task<bool> HasUnreleasedMapSwitchLockAsync(Guid requestId, string vehicle, CancellationToken cancellationToken = default) =>
            Task.FromResult(HasUnreleasedLock || MapSwitchLockSnapshot?.HasUnreleasedLock == true);
        public Task<RdsMapSwitchLockSnapshot?> GetUnreleasedMapSwitchLockAsync(Guid requestId, string vehicle, CancellationToken cancellationToken = default) =>
            Task.FromResult(MapSwitchLockSnapshot ?? (HasUnreleasedLock ? new RdsMapSwitchLockSnapshot(true, vehicle, null, null) : null));
        public Task<IReadOnlyList<RdsTaskTemplateSummary>> ListTaskTemplatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RdsTaskTemplateSummary>>([]);
        public Task EnrichRemainingTimesAsync(IReadOnlyDictionary<string, double> agvRemainingTimes, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeNativeWorkflowStore(TransportRequestSnapshot initial) : ITransportWorkflowStore
    {
        public List<TransportRequestStatus> Transitions { get; } = [];
        public List<ExecutionOutcome> Outcomes { get; } = [];

        public Task<CreateTransportResult> CreateAndQueueAsync(CreateTransportRequest request, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransportBatchResult> CreateBatchAndQueueAsync(CreateTransportBatchRequest request, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CancelTransportResult> CancelAsync(Guid requestId, Guid? actorUserId, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CancelTransportResult(initial with { Status = TransportRequestStatus.Cancelled }, initial.Status == TransportRequestStatus.Cancelled));
        public Task<TransportRequestSnapshot> MarkReadyAsync(Guid requestId, Guid? actorUserId, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransportRequestSnapshot> RecordExecutionTransitionAsync(Guid requestId, TransportRequestStatus nextStatus, OperationalEventContext? eventContext, CancellationToken cancellationToken = default)
        {
            Transitions.Add(nextStatus);
            return Task.FromResult(initial with { Status = nextStatus });
        }
        public Task<TransportRequestSnapshot> RecordExecutionOutcomeAsync(Guid requestId, ExecutionOutcome outcome, OperationalEventContext? eventContext, CancellationToken cancellationToken = default)
        {
            Outcomes.Add(outcome);
            return Task.FromResult(initial with { Status = outcome.Status });
        }
        public Task<LoadStateUpdateResult> UpdateLoadStateAsync(LoadStateUpdateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SwitchVariantAsync(VariantSelection selection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeNativeExecutionClient(bool dispatchEnabled = true) : IRdsExecutionClient
    {
        public bool DispatchEnabled => dispatchEnabled;
        public int CreateCount { get; private set; }
        public Task<RdsValidationResult> ValidateTransportAsync(RdsValidateTransportRequest request, Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdsCreateTransportResult> CreateTransportAsync(RdsCreateTransportRequest request, Guid correlationId, CancellationToken cancellationToken = default)
        {
            CreateCount++;
            return Task.FromResult(new RdsCreateTransportResult(true, false, 200, "ok", JsonDocument.Parse("{}").RootElement));
        }
        public Task<RdsStopTaskResult> StopTaskAsync(string taskRecordId, Guid correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RdsStopTaskResult(200, "ok", JsonDocument.Parse("{}").RootElement));
        public Task<RdsContentUpdateResult> UpdateContentAsync(string siteId, string content, Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeNativeReadClient : IRdsReadClient
    {
        public bool Enabled => true;
        public string? SafeBaseAddress => "http://127.0.0.1:8080";
        public DateTimeOffset? LastSuccessfulRead => DateTimeOffset.UtcNow;
        public Task<RdsReadResult<IReadOnlyList<RdsWorkSiteSnapshot>>> GetWorkSitesAsync(Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdsReadResult<RdsWindTaskPage>> QueryWindTasksAsync(RdsWindTaskQuery query, Guid correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RdsReadResult<RdsWindTaskPage>(new(1, 1, 100, 1, [new RdsWindTaskSnapshot(RequestId.ToString(), "TKG", 1000, null, null, JsonDocument.Parse("{}").RootElement, null)]), 200, "ok", "/api/queryWindTask", correlationId, DateTimeOffset.UtcNow));
        public Task<RdsReadResult<RdsRobotAlarmReport>> GetRobotAlarmsAsync(Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeNativeWorkSiteReconciler : IRdsWorkSiteReconciler
    {
        public Task<RdsWorkSiteSyncRecord> SyncWorkSitesAsync(Guid navigationProfileId, Guid correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RdsWorkSiteSyncRecord(navigationProfileId, 1, 0, 1, DateTimeOffset.UtcNow, 0, "ok"));
    }

    private sealed class FakeNativeOrderClient(RdsExecutionMode mode) : IRdsCoreNativeOrderClient
    {
        public bool MutationEnabled => true;
        public RdsExecutionMode Mode => mode;
        public int SetOrderCount { get; private set; }
        public int TerminateCount { get; private set; }
        public int LockCount { get; private set; }
        public int UnlockCount { get; private set; }
        public int SwitchMapCount { get; private set; }
        public string? LastSwitchedMap { get; private set; }
        public List<RdsCoreNormalOrder> Orders { get; } = [];
        public RdsCoreNormalOrder? LastOrder => Orders.LastOrDefault();
        public Exception? SetOrderError { get; set; }
        public Exception? LockError { get; set; }
        public Exception? UnlockError { get; set; }
        public Exception? SwitchMapError { get; set; }
        public Exception? RobotsStatusError { get; set; }

        public string? CurrentRobotMap { get; set; } = "26.7.2026_map_optimak";
        public string? CurrentRobotStation { get; set; } = "AP21";
        public int RobotConnectionStatus { get; set; } = 1;
        public bool RobotDispatchable { get; set; } = true;
        public bool RobotIsError { get; set; } = false;
        public List<RdsParsedAlarm> RobotAlarms { get; set; } = [];

        public bool? RobotProcBusiness { get; set; } = false;
        public string? RobotCurrentOrderId { get; set; } = null;
        public string? RobotCurrentOrderState { get; set; } = null;
        public Exception? RedoError { get; set; }
        public bool KeepOldMapOnSwitch { get; set; } = false;
        public bool AlarmAfterSwitch { get; set; } = false;

        public RdsCoreRobotLockInfo? RobotLockInfo { get; set; }

        /// <summary>Lets a test report a uuid that differs from the vehicle id.</summary>
        public RdsRobotIdentity? RobotIdentity { get; set; }

        /// <summary>A second robot in the fleet, for cases needing two distinct robots.</summary>
        public RdsCoreRobotTelemetry? ExtraRobot { get; set; }

        /// <summary>
        /// Identity reported from the SECOND robotsStatus read onwards, so a test can
        /// make the in-lease re-read land on a different physical robot than the one
        /// the lease was taken for.
        /// </summary>
        public RdsRobotIdentity? IdentityAfterFirstRead { get; set; }

        private int robotsStatusReads;

        /// <summary>Vehicles actually sent to the adapter by fleet maintenance.</summary>
        public IReadOnlyList<string> LastMaintenanceVehicles { get; private set; } = [];

        public RdsCoreKnownOrderState DetailsState { get; set; } = RdsCoreKnownOrderState.Waiting;
        public RdsCoreKnownOrderState TransitionOrderState { get; set; } = RdsCoreKnownOrderState.Unknown;

        public Task<RdsCoreSetOrderResult> SetOrderAsync(RdsCoreNormalOrder order, Guid correlationId, CancellationToken cancellationToken = default)
        {
            SetOrderCount++;
            Orders.Add(order);
            if (SetOrderError is not null) throw SetOrderError;
            return Task.FromResult(new RdsCoreSetOrderResult(
                true, true, true, 200, 0, "success", false, RdsCoreOrderDiagnostics.Empty, JsonDocument.Parse("{}").RootElement));
        }

        public int OrderDetailsCount { get; private set; }

        public Task<RdsCoreOrderDetails> GetOrderDetailsAsync(string orderId, Guid correlationId, CancellationToken cancellationToken = default)
        {
            OrderDetailsCount++;
            var isTransition = orderId.Contains(":map-prep:");
            var state = isTransition ? TransitionOrderState : DetailsState;
            return Task.FromResult(new RdsCoreOrderDetails(
                orderId,
                null,
                new RdsCoreOrderState(state, state.ToString()),
                [],
                RdsCoreOrderDiagnostics.Empty,
                JsonDocument.Parse("{}").RootElement));
        }

        /// <summary>Core-level alarms reported beside report[], not attributed to a robot.</summary>
        public List<RdsParsedAlarm> GlobalAlarms { get; set; } = [];

        public Task<RdsCoreFleetStatus> GetRobotsStatusAsync(Guid correlationId, CancellationToken cancellationToken = default)
        {
            if (RobotsStatusError is not null) throw RobotsStatusError;
            robotsStatusReads++;
            var effectiveIdentity = robotsStatusReads > 1 && IdentityAfterFirstRead is not null
                ? IdentityAfterFirstRead
                : RobotIdentity;
            var telemetry = new RdsCoreRobotTelemetry(
                "Sfl-Cbd15",
                "Sfl-Cbd15",
                CurrentRobotMap,
                CurrentRobotStation,
                null,
                RobotConnectionStatus,
                RobotDispatchable,
                false,
                RobotIsError,
                95.0,
                0, 0, 0, null,
                RobotCurrentOrderId,
                RobotCurrentOrderState,
                RobotAlarms,
                RobotLockInfo,
                effectiveIdentity,
                RobotProcBusiness);
            IReadOnlyList<RdsCoreRobotTelemetry> all = ExtraRobot is null ? [telemetry] : [telemetry, ExtraRobot];
            return Task.FromResult(new RdsCoreFleetStatus(all, GlobalAlarms));
        }

        public Task<RdsCoreSimpleResult> LockAsync(IReadOnlyList<string> vehicles, Guid correlationId, CancellationToken cancellationToken = default)
        {
            LockCount++;
            if (LockError is not null) throw LockError;
            RobotDispatchable = false;
            RobotLockInfo ??= new RdsCoreRobotLockInfo(true, "10.0.0.1", "TKG", 5000, "1634023715", 2, "ok");
            return Task.FromResult(new RdsCoreSimpleResult(true, true, 200, 0, "ok", JsonDocument.Parse("{}").RootElement));
        }

        public Task<RdsCoreSimpleResult> UnlockAsync(IReadOnlyList<string> vehicles, Guid correlationId, CancellationToken cancellationToken = default)
        {
            UnlockCount++;
            if (UnlockError is not null) throw UnlockError;
            RobotDispatchable = true;
            RobotLockInfo = new RdsCoreRobotLockInfo(false, null, null, null, null, null, null);
            return Task.FromResult(new RdsCoreSimpleResult(true, true, 200, 0, "ok", JsonDocument.Parse("{}").RootElement));
        }

        public Task<RdsCoreSimpleResult> SwitchMapAsync(string vehicle, string mapName, Guid correlationId, CancellationToken cancellationToken = default)
        {
            SwitchMapCount++;
            LastSwitchedMap = mapName;
            if (SwitchMapError is not null) throw SwitchMapError;
            if (!KeepOldMapOnSwitch)
            {
                CurrentRobotMap = mapName;
            }
            if (AlarmAfterSwitch)
            {
                RobotIsError = true;
                RobotAlarms.Add(new RdsParsedAlarm("52102", RdsAlarmSeverity.Error, "localization lost", null, 1));
            }
            return Task.FromResult(new RdsCoreSimpleResult(true, true, 200, 0, "ok", JsonDocument.Parse("{}").RootElement));
        }

        public Task<RdsCoreTerminateResult> TerminateAsync(string orderId, Guid correlationId, CancellationToken cancellationToken = default)
        {
            TerminateCount++;
            return Task.FromResult(new RdsCoreTerminateResult(true, true, 200, 0, "success", JsonDocument.Parse("{}").RootElement));
        }

        public Task<RdsCoreOrderList> GetOrdersAsync(RdsCoreOrderQuery query, Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RdsCoreOrderDetails>> GetOrderDetailsByExternalIdAsync(string externalId, Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdsCoreOrderDetails> GetOrderDetailsByBlockIdAsync(string blockId, Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdsCoreBlockDetails> GetBlockDetailsByIdAsync(string blockId, Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        // Counted rather than throwing, so a gate test can prove nothing left the process.
        public int SetPriorityCount { get; private set; }
        public int ClearErrorsCount { get; private set; }
        public int PauseCount { get; private set; }
        public int ResumeCount { get; private set; }
        public int ManualFinishedCount { get; private set; }
        public int RedoCount { get; private set; }
        public int DispatchableCount { get; private set; }

        private static Task<RdsCoreSimpleResult> Ok() =>
            Task.FromResult(new RdsCoreSimpleResult(true, true, 200, 0, "ok", JsonDocument.Parse("{}").RootElement));

        public Task<RdsCoreSimpleResult> SetPriorityAsync(string orderId, int priority, Guid correlationId, CancellationToken cancellationToken = default)
        { SetPriorityCount++; return Ok(); }
        public Task<RdsCoreSimpleResult> ClearRobotAllErrorAsync(IReadOnlyList<string> vehicles, Guid correlationId, CancellationToken cancellationToken = default)
        { LastMaintenanceVehicles = vehicles; ClearErrorsCount++; return Ok(); }
        public Task<RdsCoreSimpleResult> GotoSitePauseAsync(IReadOnlyList<string> vehicles, Guid correlationId, CancellationToken cancellationToken = default)
        { LastMaintenanceVehicles = vehicles; PauseCount++; return Ok(); }
        public Task<RdsCoreSimpleResult> GotoSiteResumeAsync(IReadOnlyList<string> vehicles, Guid correlationId, CancellationToken cancellationToken = default)
        { LastMaintenanceVehicles = vehicles; ResumeCount++; return Ok(); }
        public Task<RdsCoreSimpleResult> ManualFinishedAsync(IReadOnlyList<string> vehicles, Guid correlationId, CancellationToken cancellationToken = default)
        { ManualFinishedCount++; return Ok(); }
        public Task<RdsCoreSimpleResult> RedoFailedOrderAsync(IReadOnlyList<string> vehicles, Guid correlationId, CancellationToken cancellationToken = default)
        { RedoCount++; if (RedoError is not null) throw RedoError; return Ok(); }
        public Task<RdsCoreSimpleResult> SetDispatchableAsync(IReadOnlyList<string> vehicles, string type, Guid correlationId, CancellationToken cancellationToken = default)
        { DispatchableCount++; return Ok(); }
    }

    /// <summary>
    /// Deterministic stand-in for the distributed robot lease. No sleeps: a test drives
    /// contention by holding a lease across an awaited barrier.
    /// </summary>
    public sealed class RobotLeaseRegistry
    {
        private readonly HashSet<string> held = new(StringComparer.Ordinal);
        private readonly object gate = new();

        public List<string> RequestedKeys { get; } = [];

        public IRdsExecutionLease? TryAcquire(string key)
        {
            lock (gate)
            {
                RequestedKeys.Add(key);
                if (string.IsNullOrWhiteSpace(key)) return null;
                var normalized = key.Trim().ToLowerInvariant();
                if (!held.Add(normalized)) return null;
                return new Handle(this, normalized);
            }
        }

        public bool IsHeld(string key)
        {
            lock (gate) return held.Contains(key.Trim().ToLowerInvariant());
        }

        public int HeldCount
        {
            get { lock (gate) return held.Count; }
        }

        private void Release(string normalized)
        {
            lock (gate) held.Remove(normalized);
        }

        private sealed class Handle(RobotLeaseRegistry owner, string key) : IRdsExecutionLease
        {
            public ValueTask DisposeAsync()
            {
                owner.Release(key);
                return ValueTask.CompletedTask;
            }
        }
    }
}
