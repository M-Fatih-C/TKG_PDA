using System.Text.Json;
using TkgPda.Application;
using TkgPda.Domain;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class RdsExecutionCoordinatorTests
{
    [Theory]
    [InlineData("WIND_TASK",0,"OCCUPIED","RDS_WORKSITE_OCCUPANCY_CONFLICT")]
    [InlineData("RDSCORE_NATIVE_ORDER",0,"OCCUPIED","RDS_WORKSITE_OCCUPANCY_CONFLICT")]
    [InlineData("WIND_TASK",1,"VACANT","RDS_WORKSITE_OCCUPANCY_CONFLICT")]
    [InlineData("WIND_TASK",null,"OCCUPIED","RDS_WORKSITE_STATE_UNKNOWN")]
    public async Task Contradictory_or_unknown_mapped_slot_blocks_motion_before_any_task_identity(string mode,int? filled,string occupancy,string expected)
    {
        var context=Context() with { TemplateConfig=Json($$"""{"executionMode":"{{mode}}"}"""),
            Source=Endpoint("ISOLATED") with { Filled=filled,BusinessOccupancy=occupancy } };
        var fixture=Fixture(context:context);
        var error=await Assert.ThrowsAsync<RdsExecutionException>(()=>fixture.Coordinator.DispatchOneAsync(RequestId,Guid.NewGuid()));
        Assert.Equal(expected,error.Code);
        Assert.Equal(0,fixture.Client.CreateCount);
        Assert.Equal(0,fixture.Store.EnsureCount);
    }

    [Fact]
    public void Binder_resolves_whitelisted_sources_deterministically()
    {
        var values = RdsTaskInputBinder.Bind(Context(Bindings(
            ("fromSite", "SOURCE_RDS_SITE_ID", true, null),
            ("toSite", "DESTINATION_RDS_SITE_ID", true, null),
            ("requestId", "REQUEST_ID", true, null),
            ("literal", "LITERAL", true, "fixed"))));

        Assert.Equal("WS-SOURCE", values["fromSite"]);
        Assert.Equal("WS-DESTINATION", values["toSite"]);
        Assert.Equal(RequestId.ToString(), values["requestId"]);
        Assert.Equal("fixed", values["literal"]);
    }

    [Fact]
    public void Binder_rejects_missing_required_value()
    {
        var context = Context(Bindings(("part", "PART_CODE", true, null))) with { PartCode = null };
        var error = Assert.Throws<RdsExecutionException>(() => RdsTaskInputBinder.Bind(context));
        Assert.Equal("RDS_TASK_INPUT_UNRESOLVED", error.Code);
    }

    [Fact]
    public void Binder_rejects_unknown_source_and_duplicate_param()
    {
        var unknown = Assert.Throws<RdsExecutionException>(() => RdsTaskInputBinder.Bind(Context(Json("""{"rdsInputBindings":[{"param":"x","source":"EVAL","required":true}]}"""))));
        Assert.Equal("RDS_TASK_BINDING_INVALID", unknown.Code);
        var duplicate = Assert.Throws<RdsExecutionException>(() => RdsTaskInputBinder.Bind(Context(Bindings(("x", "REQUEST_ID", true, null), ("x", "TASK_TYPE", true, null)))));
        Assert.Equal("RDS_TASK_BINDING_DUPLICATE", duplicate.Code);
    }

    [Fact]
    public async Task Dispatch_precreates_identity_and_marks_sent_only_after_query_proof()
    {
        var fixture = Fixture();
        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());

        Assert.True(result!.Dispatched);
        Assert.Equal(RequestId.ToString(), fixture.Store.EnsuredRecordId);
        Assert.Equal(1, fixture.Store.EnsureCount);
        Assert.Equal(RequestId.ToString(), fixture.Client.LastCreate!.TaskRecordId);
        Assert.Contains(TransportRequestStatus.SentToRds, fixture.WorkflowStore.Transitions);
    }

    [Fact]
    public async Task Ambiguous_create_reconciles_same_identity_without_second_create()
    {
        var fixture = Fixture();
        fixture.Client.CreateError = new RdsExecutionException("RDS_TIMEOUT", "lost response");
        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());
        Assert.True(result!.Dispatched);
        Assert.Equal(1, fixture.Client.CreateCount);
        Assert.Equal(RequestId.ToString(), fixture.Store.EnsuredRecordId);
    }

    [Fact]
    public async Task Missing_query_proof_keeps_request_queued()
    {
        var fixture = Fixture(tasks: []);
        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid());
        Assert.False(result!.Dispatched);
        Assert.DoesNotContain(TransportRequestStatus.SentToRds, fixture.WorkflowStore.Transitions);
        // WP-06: no execution transition means no ROBOT_TASK_DISPATCHED event context was ever recorded.
        Assert.Empty(fixture.WorkflowStore.TransitionContexts);
    }

    [Fact]
    public async Task Confirmed_dispatch_carries_rds_correlation_without_inventing_actor_or_device()
    {
        var fixture = Fixture();
        var correlationId = Guid.NewGuid();
        var result = await fixture.Coordinator.DispatchOneAsync(RequestId, correlationId);

        Assert.True(result!.Dispatched);
        var context = Assert.Single(fixture.WorkflowStore.TransitionContexts);
        Assert.NotNull(context);
        Assert.Null(context!.ActorUserId);
        Assert.Null(context.DeviceId);
        Assert.Equal(correlationId, context.CorrelationId);
    }

    [Theory]
    [InlineData("MACHINE", null, null, null, "MACHINE_REGION_FORBIDDEN")]
    [InlineData("STAGING", 0, null, null, "RDS_WORKSITE_NOT_PHYSICAL")]
    [InlineData("STAGING", 1, 1, null, "RDS_WORKSITE_DISABLED")]
    [InlineData("STAGING", 1, 0, 1, "RDS_WORKSITE_SYNC_FAILED")]
    public async Task Unsafe_endpoint_blocks_dispatch(string role, int? type, int? disabled, int? syncFailed, string expected)
    {
        var context = Context() with
        {
            Source = Endpoint("WS-SOURCE") with { SlotRole = role, Type = type, Disabled = disabled, SyncFailed = syncFailed }
        };
        var fixture = Fixture(context: context);
        var error = await Assert.ThrowsAsync<RdsExecutionException>(() => fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid()));
        Assert.Equal(expected, error.Code);
        Assert.Equal(0, fixture.Store.EnsureCount);
    }

    [Fact]
    public async Task Unmapped_stale_and_locked_required_worksites_block_dispatch()
    {
        var cases = new[]
        {
            (Endpoint("WS") with { IsMapped = false }, "RDS_WORKSITE_NOT_MAPPED"),
            (Endpoint("WS") with { LastSyncAt = DateTimeOffset.UtcNow.AddHours(-1) }, "RDS_WORKSITE_STALE"),
            (Endpoint("WS") with { Locked = 1 }, "RDS_WORKSITE_LOCKED")
        };

        foreach (var (endpoint, expected) in cases)
        {
            var fixture = Fixture(context: Context() with { Source = endpoint });
            var error = await Assert.ThrowsAsync<RdsExecutionException>(() => fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid()));
            Assert.Equal(expected, error.Code);
            Assert.Equal(0, fixture.Client.CreateCount);
        }
    }

    [Fact]
    public async Task An_auto_allocated_empty_container_buffer_slot_that_is_unmapped_cannot_be_dispatched()
    {
        // Customer Workflow â€” Empty Container Buffer dispatch safety:
        // BRING_EMPTY's source IS the buffer slot that PostgresTransportWorkflowStore.
        // AllocateBufferSlotAsync auto-allocates when the caller omits one.
        // When no integration.layout_slot_rds_mapping row exists for it,
        // GetDispatchContextAsync's LEFT JOIN yields IsMapped=false AND
        // SiteId=null together (proven in
        // PostgresEmptyContainerBufferTests.Missing_worksite_mapping_...).
        // That combination is caught here by two independent, pre-existing
        // guards before any RDS call: RdsTaskInputBinder rejects the missing
        // required site value, and ValidateReferencedEndpoints/ValidateEndpoint
        // would separately reject !IsMapped even if a stale site id were
        // somehow present. No production code change was needed.
        var fixture = Fixture(context: Context() with
        {
            Source = Endpoint("WS-SOURCE") with { IsMapped = false, SiteId = null }
        });

        var error = await Assert.ThrowsAsync<RdsExecutionException>(() => fixture.Coordinator.DispatchOneAsync(RequestId, Guid.NewGuid()));

        Assert.Equal("RDS_TASK_INPUT_UNRESOLVED", error.Code);
        Assert.Equal(0, fixture.Client.CreateCount);
        Assert.Equal(0, fixture.Store.EnsureCount);
    }

    [Fact]
    public async Task Missing_template_binding_keeps_other_dispatch_cycles_recoverable()
    {
        var fixture = Fixture(context: Context(Json("""{"executionMode":"WIND_TASK"}""")));
        var results = await fixture.Coordinator.DispatchQueuedAsync(10);
        var blocked = Assert.Single(results);
        Assert.False(blocked.Dispatched);
        Assert.Equal("RDS_TASK_BINDINGS_REQUIRED", blocked.Code);
    }

    [Theory]
    [InlineData(1000, TransportRequestStatus.Running)]
    [InlineData(1003, TransportRequestStatus.Completed)]
    public async Task Reconciliation_maps_running_and_finished(int rdsStatus, TransportRequestStatus expected)
    {
        var fixture = Fixture(tasks: [WindTask(rdsStatus)], requestStatus: TransportRequestStatus.SentToRds);
        fixture.Store.Active = [Projection(TransportRequestStatus.SentToRds)];
        await fixture.Coordinator.ReconcileAsync(10);
        Assert.Contains(expected, fixture.WorkflowStore.Transitions);
        Assert.Equal(rdsStatus == 1003 ? 1 : 0, fixture.WorkSiteReconciler.SyncCount);
    }

    [Fact]
    public async Task Suspended_status_remains_nonterminal_and_termination_without_cancel_intent_fails()
    {
        var suspended = Fixture(tasks: [WindTask(1002)], requestStatus: TransportRequestStatus.Running);
        suspended.Store.Active = [Projection(TransportRequestStatus.Running)];
        await suspended.Coordinator.ReconcileAsync(10);
        Assert.Empty(suspended.WorkflowStore.Transitions);
        Assert.Empty(suspended.WorkflowStore.Outcomes);

        var terminated = Fixture(tasks: [WindTask(1001)], requestStatus: TransportRequestStatus.Running);
        terminated.Store.Active = [Projection(TransportRequestStatus.Running)];
        await terminated.Coordinator.ReconcileAsync(10);
        Assert.Contains(terminated.WorkflowStore.Outcomes, item => item.Status == TransportRequestStatus.Failed && item.FailureCode == "RDS_TASK_TERMINATED");
    }

    [Theory]
    [InlineData(1004)]
    [InlineData(1005)]
    [InlineData(1006)]
    public async Task Reconciliation_maps_abnormal_terminal_states_to_failed(int rdsStatus)
    {
        var fixture = Fixture(tasks: [WindTask(rdsStatus)], requestStatus: TransportRequestStatus.Running);
        fixture.Store.Active = [Projection(TransportRequestStatus.Running)];
        await fixture.Coordinator.ReconcileAsync(10);
        Assert.Contains(fixture.WorkflowStore.Outcomes, item => item.Status == TransportRequestStatus.Failed && item.FailureCode == $"RDS_TASK_STATUS_{rdsStatus}");
    }

    [Fact]
    public async Task Terminal_callback_worksite_refresh_remains_durable_for_the_next_worker_cycle()
    {
        var fixture = Fixture(requestStatus: TransportRequestStatus.Completed);
        fixture.Store.PendingWorkSiteRefreshes = [RequestId];

        await fixture.Coordinator.ReconcileAsync(10);

        Assert.Equal(1, fixture.WorkSiteReconciler.SyncCount);
    }

    [Fact]
    public async Task Duplicate_callback_is_harmless_and_unknown_task_is_ignored()
    {
        var fixture = Fixture(requestStatus: TransportRequestStatus.Completed);
        fixture.Store.TaskProjection = Projection(TransportRequestStatus.Completed);
        var payload = Json($$"""{"taskRecordId":"{{RequestId}}"}""");
        var first = await fixture.Coordinator.ProcessCallbackAsync(new("taskDone", payload, null));
        var unknown = await fixture.Coordinator.ProcessCallbackAsync(new("taskDone", Json("""{"taskRecordId":"other"}"""), null));
        Assert.True(first.Accepted);
        Assert.False(unknown.Mutated);
        Assert.Equal("RDS_TASK_UNKNOWN", unknown.Code);
    }

    [Fact]
    public async Task Confirmed_stop_uses_same_task_identity_and_cancels_only_after_query_proof()
    {
        var fixture = Fixture(tasks: [WindTask(1001)], requestStatus: TransportRequestStatus.Running);
        fixture.Store.TaskProjection = Projection(TransportRequestStatus.Running) with { CancellationRequested = true };
        var result = await fixture.Coordinator.CancelAsync(RequestId, Guid.NewGuid(), Guid.NewGuid());
        Assert.False(result.PendingRdsConfirmation);
        Assert.Equal(TransportRequestStatus.Cancelled, result.Request.Status);
        Assert.Equal(RequestId.ToString(), fixture.Client.LastStoppedTaskRecordId);
    }

    [Fact]
    public async Task Stop_requested_without_rds_proof_is_not_a_cancellation()
    {
        // WP-06: "stop requested" != "request cancelled". No outcome (and so no REQUEST_CANCELLED)
        // may be recorded until RDS proves the task terminated.
        var fixture = Fixture(tasks: [WindTask(1000)], requestStatus: TransportRequestStatus.Running);
        fixture.Store.TaskProjection = Projection(TransportRequestStatus.Running);
        var operatorContext = OperationalEventContext.ForUser(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var result = await fixture.Coordinator.CancelAsync(RequestId, operatorContext.ActorUserId, Guid.NewGuid(), operatorContext);

        Assert.True(result.PendingRdsConfirmation);
        Assert.Equal(RequestId.ToString(), fixture.Client.LastStoppedTaskRecordId);
        Assert.Empty(fixture.WorkflowStore.Outcomes);
        Assert.Empty(fixture.WorkflowStore.OutcomeContexts);
        Assert.Empty(fixture.WorkflowStore.CancelContexts);
    }

    [Fact]
    public async Task Confirmed_stop_records_one_cancelled_outcome_with_the_operator_context()
    {
        var fixture = Fixture(tasks: [WindTask(1001)], requestStatus: TransportRequestStatus.Running);
        fixture.Store.TaskProjection = Projection(TransportRequestStatus.Running) with { CancellationRequested = true };
        var operatorContext = OperationalEventContext.ForUser(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var result = await fixture.Coordinator.CancelAsync(RequestId, operatorContext.ActorUserId, Guid.NewGuid(), operatorContext);

        Assert.False(result.PendingRdsConfirmation);
        var outcome = Assert.Single(fixture.WorkflowStore.Outcomes);
        Assert.Equal(TransportRequestStatus.Cancelled, outcome.Status);
        Assert.Equal("RDS_QUERY_AFTER_STOP", outcome.EvidenceType);
        Assert.Same(operatorContext, Assert.Single(fixture.WorkflowStore.OutcomeContexts));
    }

    [Fact]
    public async Task Confirmed_stop_without_http_context_uses_system_context_and_invents_no_actor_or_device()
    {
        var fixture = Fixture(tasks: [WindTask(1001)], requestStatus: TransportRequestStatus.Running);
        fixture.Store.TaskProjection = Projection(TransportRequestStatus.Running) with { CancellationRequested = true };
        var correlationId = Guid.NewGuid();

        await fixture.Coordinator.CancelAsync(RequestId, null, correlationId);

        var context = Assert.Single(fixture.WorkflowStore.OutcomeContexts);
        Assert.Null(context!.ActorUserId);
        Assert.Null(context.DeviceId);
        Assert.Equal(correlationId, context.CorrelationId);
    }

    [Fact]
    public async Task Pre_execution_cancel_forwards_the_operator_context_and_records_no_rds_outcome()
    {
        var fixture = Fixture(requestStatus: TransportRequestStatus.Queued);
        var operatorContext = OperationalEventContext.ForUser(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var result = await fixture.Coordinator.CancelAsync(RequestId, operatorContext.ActorUserId, Guid.NewGuid(), operatorContext);

        Assert.False(result.PendingRdsConfirmation);
        Assert.Same(operatorContext, Assert.Single(fixture.WorkflowStore.CancelContexts));
        Assert.Empty(fixture.WorkflowStore.Outcomes);
        Assert.Null(fixture.Client.LastStoppedTaskRecordId);
    }

    [Fact]
    public async Task Repeated_running_reconciliation_issues_no_second_started_transition()
    {
        // First cycle (SENT_TO_RDS -> RUNNING) transitions once; later cycles see RUNNING and do nothing.
        var first = Fixture(tasks: [WindTask(1000)], requestStatus: TransportRequestStatus.SentToRds);
        first.Store.Active = [Projection(TransportRequestStatus.SentToRds)];
        await first.Coordinator.ReconcileAsync(10);
        Assert.Equal(new[] { TransportRequestStatus.Running }, first.WorkflowStore.Transitions);

        var repeat = Fixture(tasks: [WindTask(1000)], requestStatus: TransportRequestStatus.Running);
        repeat.Store.Active = [Projection(TransportRequestStatus.Running)];
        await repeat.Coordinator.ReconcileAsync(10);
        Assert.Empty(repeat.WorkflowStore.Transitions);
    }

    [Fact]
    public async Task Completion_reconciliation_uses_one_system_context_and_terminal_repeats_do_nothing()
    {
        var fixture = Fixture(tasks: [WindTask(1003)], requestStatus: TransportRequestStatus.SentToRds);
        fixture.Store.Active = [Projection(TransportRequestStatus.SentToRds)];
        await fixture.Coordinator.ReconcileAsync(10);

        Assert.Equal(new[] { TransportRequestStatus.Running, TransportRequestStatus.Completed }, fixture.WorkflowStore.Transitions);
        Assert.All(fixture.WorkflowStore.TransitionContexts, context =>
        {
            Assert.Null(context!.ActorUserId);
            Assert.Null(context.DeviceId);
            Assert.NotNull(context.CorrelationId);
        });
        Assert.Single(fixture.WorkflowStore.TransitionContexts.Select(context => context!.CorrelationId).Distinct());

        var terminal = Fixture(tasks: [WindTask(1003)], requestStatus: TransportRequestStatus.Completed);
        terminal.Store.Active = [Projection(TransportRequestStatus.Completed)];
        await terminal.Coordinator.ReconcileAsync(10);
        Assert.Empty(terminal.WorkflowStore.Transitions);
    }

    [Fact]
    public async Task Failed_reconciliation_records_one_failure_outcome_with_evidence_and_system_context()
    {
        var fixture = Fixture(tasks: [WindTask(1004)], requestStatus: TransportRequestStatus.Running);
        fixture.Store.Active = [Projection(TransportRequestStatus.Running)];

        await fixture.Coordinator.ReconcileAsync(10);

        var outcome = Assert.Single(fixture.WorkflowStore.Outcomes);
        Assert.Equal(TransportRequestStatus.Failed, outcome.Status);
        Assert.Equal("RDS_TASK_STATUS_1004", outcome.FailureCode);
        Assert.Equal("RECONCILIATION", outcome.EvidenceType);
        var context = Assert.Single(fixture.WorkflowStore.OutcomeContexts);
        Assert.Null(context!.ActorUserId);
        Assert.Null(context.DeviceId);
        Assert.NotNull(context.CorrelationId);
    }

    [Fact]
    public async Task Content_sync_not_configured_never_calls_Rds()
    {
        var fixture = Fixture(emptyContent: null, fullContent: null);
        var result = await fixture.Coordinator.SynchronizeLoadContentAsync(Guid.NewGuid(), LoadState.Full, Guid.NewGuid());
        Assert.Equal("NOT_CONFIGURED", result.Status);
        Assert.Equal(0, fixture.Client.ContentCount);
    }

    [Fact]
    public async Task Configured_content_sync_sends_exact_value_without_occupancy_or_filled_semantics()
    {
        var fixture = Fixture(emptyContent: "TKG_EMPTY_EXACT", fullContent: "TKG_FULL_EXACT");
        var result = await fixture.Coordinator.SynchronizeLoadContentAsync(Guid.NewGuid(), LoadState.Full, Guid.NewGuid());
        Assert.Equal("SYNCHRONIZED", result.Status);
        Assert.Equal("TKG_FULL_EXACT", fixture.Client.LastContent);
        Assert.Equal(1, fixture.Client.ContentCount);
    }

    private static readonly Guid RequestId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static TestFixture Fixture(
        RdsDispatchContext? context = null,
        IReadOnlyList<RdsWindTaskSnapshot>? tasks = null,
        TransportRequestStatus requestStatus = TransportRequestStatus.Queued,
        string? emptyContent = "EMPTY",
        string? fullContent = "FULL")
    {
        var store = new FakeExecutionStore { Context = context ?? Context(), RequestStatus = requestStatus };
        var workflowStore = new FakeWorkflowStore(store.Request);
        var client = new FakeExecutionClient();
        var read = new FakeReadClient { Tasks = tasks ?? [WindTask(1000)] };
        var workSiteReconciler = new FakeWorkSiteReconciler();
        var settings = new RdsExecutionSettings(true, TimeSpan.FromMinutes(5), emptyContent, fullContent);
        var coordinator = new RdsExecutionCoordinator(store, client, read, workSiteReconciler, new TransportWorkflowService(workflowStore), settings, TimeProvider.System);
        return new(coordinator, store, workflowStore, client, read, workSiteReconciler);
    }

    private static RdsDispatchContext Context(JsonElement? config = null) => new(
        Request(), "SH-01-A", "V1", "71550", "715A2", "KCK", Guid.NewGuid(),
        "TKG_SIM_TRANSPORT", 100,
        config ?? Bindings(("fromSite", "SOURCE_RDS_SITE_ID", true, null), ("toSite", "DESTINATION_RDS_SITE_ID", true, null)),
        Endpoint("WS-SOURCE"), Endpoint("WS-DESTINATION"));

    private static RdsDispatchEndpoint Endpoint(string siteId) => new(
        Guid.NewGuid(), siteId, "GROUP", "STAGING", siteId, 1, 0, 0, 0, 0,
        DateTimeOffset.UtcNow, true);

    private static TransportRequestSnapshot Request() => new(
        RequestId, 42, "key", TransportRequestStatus.Queued, TransportTaskType.BringEmpty,
        RequestOrigin.Api, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100,
        TransportReadinessState.NotRequired, null);

    private static RdsWindTaskSnapshot WindTask(int status) => new(
        RequestId.ToString(), "TKG_SIM_TRANSPORT", status, "sim_01", null,
        Json($$"""{"taskRecordId":"{{RequestId}}","status":{{status}},"agvId":"sim_01"}"""),
        status >= 1003 ? "done" : null);

    private static RdsTaskProjection Projection(TransportRequestStatus status) => new(
        RequestId, RequestId.ToString(), "TKG_SIM_TRANSPORT", 1000, "sim_01", null, null,
        Json("{}"), Json("{}"), Json("{}"), false, status);

    private static JsonElement Bindings(params (string Param, string Source, bool Required, object? Value)[] values) =>
        JsonSerializer.SerializeToElement(new
        {
            executionMode = "WIND_TASK",
            rdsInputBindings = values.Select(item => new { param = item.Param, source = item.Source, required = item.Required, value = item.Value })
        });

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed record TestFixture(
        RdsExecutionCoordinator Coordinator,
        FakeExecutionStore Store,
        FakeWorkflowStore WorkflowStore,
        FakeExecutionClient Client,
        FakeReadClient Read,
        FakeWorkSiteReconciler WorkSiteReconciler);

    private sealed class Lease : IRdsExecutionLease { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }

    private sealed class FakeExecutionStore : IRdsExecutionStore
    {
        public RdsDispatchContext Context { get; set; } = Context();
        public TransportRequestStatus RequestStatus { get; set; }
        public int EnsureCount { get; private set; }
        public string? EnsuredRecordId { get; private set; }
        public IReadOnlyList<RdsTaskProjection> Active { get; set; } = [];
        public IReadOnlyList<Guid> PendingWorkSiteRefreshes { get; set; } = [];
        public RdsTaskProjection? TaskProjection { get; set; }
        public TransportRequestSnapshot Request => Context.Request with { Status = RequestStatus };
        public Task<IReadOnlyList<Guid>> ListQueuedRequestIdsAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>>([RequestId]);
        public Task<IReadOnlyList<RdsTaskProjection>> ListActiveTasksAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult(Active);
        public Task<IReadOnlyList<Guid>> ListPendingWorkSiteRefreshRequestIdsAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult(PendingWorkSiteRefreshes);
        public Task<IRdsExecutionLease?> TryAcquireRequestLeaseAsync(Guid requestId, CancellationToken cancellationToken = default) => Task.FromResult<IRdsExecutionLease?>(new Lease());
        public Task<IRdsExecutionLease?> TryAcquireRobotLeaseAsync(string robotLeaseKey, CancellationToken cancellationToken = default) => Task.FromResult<IRdsExecutionLease?>(new Lease());
        public Task<RdsDispatchContext?> GetDispatchContextAsync(Guid requestId, CancellationToken cancellationToken = default) => Task.FromResult<RdsDispatchContext?>(Context with { Request = Request });
        public Task<TransportRequestSnapshot?> FindRequestAsync(Guid requestId, CancellationToken cancellationToken = default) => Task.FromResult<TransportRequestSnapshot?>(Request);
        public Task<RdsTaskProjection?> FindTaskByRequestAsync(Guid requestId, CancellationToken cancellationToken = default) => Task.FromResult(TaskProjection);
        public Task<RdsTaskProjection?> FindTaskByRecordIdAsync(string taskRecordId, CancellationToken cancellationToken = default) => Task.FromResult(taskRecordId == RequestId.ToString() ? TaskProjection : null);
        public Task EnsureTaskRecordAsync(Guid requestId, string taskRecordId, string taskLabel, JsonElement requestPayload, CancellationToken cancellationToken = default) { EnsureCount++; EnsuredRecordId = taskRecordId; TaskProjection ??= Projection(RequestStatus); return Task.CompletedTask; }
        public Task RecordDispatchResponseAsync(Guid requestId, JsonElement responsePayload, DateTimeOffset syncedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordTaskStatusAsync(Guid requestId, RdsWindTaskSnapshot task, DateTimeOffset syncedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkCancellationRequestedAsync(Guid requestId, JsonElement responsePayload, DateTimeOffset requestedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteExecutionAuditAsync(Guid requestId, string action, string code, string message, Guid correlationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteExecutionAuditAsync(Guid requestId, string action, string code, string message, Guid correlationId, object? extraDetails, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RdsMappedWorkSite?> FindMappedWorkSiteAsync(Guid slotId, CancellationToken cancellationToken = default) => Task.FromResult<RdsMappedWorkSite?>(new(slotId, Guid.NewGuid(), "WS", 1, 0, 0, DateTimeOffset.UtcNow));
        public Task<RdsNavigationProfileSummary?> GetNavigationProfileSummaryAsync(Guid? profileId, Guid variantId, Guid machineId, CancellationToken cancellationToken = default) => Task.FromResult<RdsNavigationProfileSummary?>(null);
        public Task<bool> HasUnreleasedMapSwitchLockAsync(Guid requestId, string vehicle, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<RdsMapSwitchLockSnapshot?> GetUnreleasedMapSwitchLockAsync(Guid requestId, string vehicle, CancellationToken cancellationToken = default) => Task.FromResult<RdsMapSwitchLockSnapshot?>(null);
        public Task<IReadOnlyList<RdsTaskTemplateSummary>> ListTaskTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RdsTaskTemplateSummary>>([]);
        public Task EnrichRemainingTimesAsync(IReadOnlyDictionary<string, double> agvRemainingTimes, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeExecutionClient : IRdsExecutionClient
    {
        public bool DispatchEnabled => true;
        public int CreateCount { get; private set; }
        public int ContentCount { get; private set; }
        public RdsCreateTransportRequest? LastCreate { get; private set; }
        public string? LastStoppedTaskRecordId { get; private set; }
        public string? LastContent { get; private set; }
        public RdsExecutionException? CreateError { get; set; }
        public Task<RdsValidationResult> ValidateTransportAsync(RdsValidateTransportRequest request, Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdsCreateTransportResult> CreateTransportAsync(RdsCreateTransportRequest request, Guid correlationId, CancellationToken cancellationToken = default) { CreateCount++; LastCreate = request; if (CreateError is not null) throw CreateError; return Task.FromResult(new RdsCreateTransportResult(true, false, 200, "ok", Json("{}"))); }
        public Task<RdsStopTaskResult> StopTaskAsync(string taskRecordId, Guid correlationId, CancellationToken cancellationToken = default) { LastStoppedTaskRecordId = taskRecordId; return Task.FromResult(new RdsStopTaskResult(200, "ok", Json("{}"))); }
        public Task<RdsContentUpdateResult> UpdateContentAsync(string siteId, string content, Guid correlationId, CancellationToken cancellationToken = default) { ContentCount++; LastContent = content; return Task.FromResult(new RdsContentUpdateResult(200, "ok", Json("{}"))); }
    }

    private sealed class FakeReadClient : IRdsReadClient
    {
        public IReadOnlyList<RdsWindTaskSnapshot> Tasks { get; set; } = [];
        public bool Enabled => true;
        public string? SafeBaseAddress => "http://127.0.0.1:8080";
        public DateTimeOffset? LastSuccessfulRead => DateTimeOffset.UtcNow;
        public Task<RdsReadResult<IReadOnlyList<RdsWorkSiteSnapshot>>> GetWorkSitesAsync(Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RdsReadResult<RdsWindTaskPage>> QueryWindTasksAsync(RdsWindTaskQuery query, Guid correlationId, CancellationToken cancellationToken = default) => Task.FromResult(new RdsReadResult<RdsWindTaskPage>(new(Tasks.Count, 1, 100, 1, Tasks), 200, "ok", "/api/queryWindTask", correlationId, DateTimeOffset.UtcNow));
        public Task<RdsReadResult<RdsRobotAlarmReport>> GetRobotAlarmsAsync(Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeWorkSiteReconciler : IRdsWorkSiteReconciler
    {
        public int SyncCount { get; private set; }

        public Task<RdsWorkSiteSyncRecord> SyncWorkSitesAsync(
            Guid navigationProfileId,
            Guid correlationId,
            CancellationToken cancellationToken = default)
        {
            SyncCount++;
            return Task.FromResult(new RdsWorkSiteSyncRecord(navigationProfileId, 22, 0, 22, DateTimeOffset.UtcNow, 0, "ok"));
        }
    }

    private sealed class FakeWorkflowStore(TransportRequestSnapshot initial) : ITransportWorkflowStore
    {
        public List<TransportRequestStatus> Transitions { get; } = [];
        public List<ExecutionOutcome> Outcomes { get; } = [];
        public List<OperationalEventContext?> TransitionContexts { get; } = [];
        public List<OperationalEventContext?> OutcomeContexts { get; } = [];
        public List<OperationalEventContext?> CancelContexts { get; } = [];
        public Task<CreateTransportResult> CreateAndQueueAsync(CreateTransportRequest request, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransportBatchResult> CreateBatchAndQueueAsync(CreateTransportBatchRequest request, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CancelTransportResult> CancelAsync(Guid requestId, Guid? actorUserId, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) { CancelContexts.Add(eventContext); return Task.FromResult(new CancelTransportResult(initial with { Status = TransportRequestStatus.Cancelled }, false)); }
        public Task<TransportRequestSnapshot> MarkReadyAsync(Guid requestId, Guid? actorUserId, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransportRequestSnapshot> RecordExecutionTransitionAsync(Guid requestId, TransportRequestStatus nextStatus, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) { Transitions.Add(nextStatus); TransitionContexts.Add(eventContext); return Task.FromResult(initial with { Status = nextStatus }); }
        public Task<TransportRequestSnapshot> RecordExecutionOutcomeAsync(Guid requestId, ExecutionOutcome outcome, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) { Outcomes.Add(outcome); OutcomeContexts.Add(eventContext); return Task.FromResult(initial with { Status = outcome.Status }); }
        public Task<LoadStateUpdateResult> UpdateLoadStateAsync(LoadStateUpdateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SwitchVariantAsync(VariantSelection selection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
