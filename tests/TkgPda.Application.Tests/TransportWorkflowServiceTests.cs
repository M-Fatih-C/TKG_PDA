using TkgPda.Application;
using TkgPda.Domain;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class TransportWorkflowServiceTests
{
    [Fact]
    public async Task Valid_create_is_delegated_to_store()
    {
        var store = new FakeStore();
        var service = new TransportWorkflowService(store);
        var command = Command(TransportTaskType.BringEmpty);

        var result = await service.CreateAsync(command);

        Assert.Same(store.CreateResult, result);
        Assert.Same(command, store.CreatedCommand);
    }

    [Fact]
    public async Task Blank_idempotency_key_is_rejected_before_store()
    {
        var store = new FakeStore();
        var service = new TransportWorkflowService(store);

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            service.CreateAsync(Command(TransportTaskType.BringEmpty) with { IdempotencyKey = " " }));

        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", error.Code);
        Assert.Null(store.CreatedCommand);
    }

    [Fact]
    public async Task Bring_empty_accepts_an_unresolved_warehouse_source()
    {
        var store = new FakeStore();
        var service = new TransportWorkflowService(store);
        var command = Command(TransportTaskType.BringEmpty) with { SourceSlotId = null };

        await service.CreateAsync(command);

        Assert.Null(store.CreatedCommand!.SourceSlotId);
        Assert.NotNull(store.CreatedCommand.DestinationSlotId);
    }

    // Customer UX — Automatic Machine Slot Resolution: omitting the
    // machine-side source is no longer rejected up front. The store now
    // resolves and reserves it atomically from master.layout_slot_parts
    // (PostgresTransportWorkflowStore.ResolveMachineSlotAsync) — the
    // application layer just passes the null through unchanged.
    [Theory]
    [InlineData(TransportTaskType.RemoveFull)]
    [InlineData(TransportTaskType.RemoveEmpty)]
    public async Task Removal_accepts_an_unresolved_machine_side_source(TransportTaskType taskType)
    {
        var store = new FakeStore();
        var service = new TransportWorkflowService(store);
        var command = Command(taskType) with { SourceSlotId = null };

        await service.CreateAsync(command);

        Assert.Null(store.CreatedCommand!.SourceSlotId);
    }

    [Fact]
    public async Task Cancellation_is_delegated()
    {
        var store = new FakeStore();
        var service = new TransportWorkflowService(store);
        var requestId = Guid.NewGuid();

        await service.CancelAsync(requestId, null);

        Assert.Equal(requestId, store.CancelledRequestId);
    }

    [Fact]
    public async Task Event_context_is_forwarded_to_the_store_unchanged()
    {
        var store = new FakeStore();
        var service = new TransportWorkflowService(store);
        var eventContext = OperationalEventContext.ForUser(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await service.CreateAsync(Command(TransportTaskType.BringEmpty), eventContext);
        await service.CancelAsync(Guid.NewGuid(), null, eventContext);

        Assert.Same(eventContext, store.CreateContext);
        Assert.Same(eventContext, store.CancelContext);
    }

    [Fact]
    public async Task Empty_cancellation_id_is_rejected()
    {
        var service = new TransportWorkflowService(new FakeStore());

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() => service.CancelAsync(Guid.Empty, null));

        Assert.Equal("REQUEST_ID_REQUIRED", error.Code);
    }

    [Fact]
    public async Task Unsupported_execution_transition_is_rejected()
    {
        var service = new TransportWorkflowService(new FakeStore());

        var error = await Assert.ThrowsAsync<WorkflowRuleException>(() =>
            service.RecordExecutionTransitionAsync(Guid.NewGuid(), TransportRequestStatus.Failed));

        Assert.Equal("UNSUPPORTED_EXECUTION_UPDATE", error.Code);
    }

    [Fact]
    public async Task Variant_switch_is_delegated()
    {
        var store = new FakeStore();
        var service = new TransportWorkflowService(store);
        var selection = new VariantSelection(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null);

        await service.SwitchVariantAsync(selection);

        Assert.Same(selection, store.Selection);
    }

    private static CreateTransportRequest Command(TransportTaskType taskType) => new(
        "retry-key",
        RequestOrigin.LineOperator,
        null,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        null,
        null,
        null,
        null,
        taskType,
        taskType == TransportTaskType.BringEmpty ? null : Guid.NewGuid(),
        taskType == TransportTaskType.BringEmpty ? Guid.NewGuid() : null);

    private sealed class FakeStore : ITransportWorkflowStore
    {
        public CreateTransportRequest? CreatedCommand { get; private set; }
        public Guid? CancelledRequestId { get; private set; }
        public VariantSelection? Selection { get; private set; }

        public CreateTransportResult CreateResult { get; } = new(
            new TransportRequestSnapshot(
                Guid.NewGuid(), 1, "retry-key", TransportRequestStatus.Queued, TransportTaskType.BringEmpty,
                RequestOrigin.LineOperator, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, null,
                Guid.NewGuid(), 100, TransportReadinessState.NotRequired, null),
            false);

        public OperationalEventContext? CreateContext { get; private set; }
        public OperationalEventContext? CancelContext { get; private set; }

        public Task<CreateTransportResult> CreateAndQueueAsync(CreateTransportRequest request, OperationalEventContext? eventContext, CancellationToken cancellationToken = default)
        {
            CreatedCommand = request;
            CreateContext = eventContext;
            return Task.FromResult(CreateResult);
        }

        public Task<TransportRequestSnapshot> MarkReadyAsync(Guid requestId, Guid? actorUserId, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateResult.Request with { Id = requestId, ReadinessState = TransportReadinessState.Ready, ReadyAt = DateTimeOffset.UtcNow });

        public Task<int> ActivatePreparedPartDemandsAsync(int limit, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<TransportBatchResult> CreateBatchAndQueueAsync(CreateTransportBatchRequest request, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportBatchResult(
                Guid.NewGuid(),
                request.IdempotencyKey,
                request.MachineId,
                request.Items.Select(_ => CreateResult.Request).ToArray(),
                false));

        public Task<CancelTransportResult> CancelAsync(Guid requestId, Guid? actorUserId, OperationalEventContext? eventContext, CancellationToken cancellationToken = default)
        {
            CancelledRequestId = requestId;
            CancelContext = eventContext;
            return Task.FromResult(new CancelTransportResult(CreateResult.Request with { Id = requestId, Status = TransportRequestStatus.Cancelled }, false));
        }

        public Task<TransportRequestSnapshot> RecordExecutionTransitionAsync(Guid requestId, TransportRequestStatus nextStatus, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateResult.Request with { Id = requestId, Status = nextStatus });

        public Task<TransportRequestSnapshot> RecordExecutionOutcomeAsync(Guid requestId, ExecutionOutcome outcome, OperationalEventContext? eventContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateResult.Request with { Id = requestId, Status = outcome.Status });

        public Task<LoadStateUpdateResult> UpdateLoadStateAsync(LoadStateUpdateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LoadStateUpdateResult(request.MachineId, request.SlotId, OccupancyState.Occupied, request.TargetLoadState, 1, false));

        public Task SwitchVariantAsync(VariantSelection selection, CancellationToken cancellationToken = default)
        {
            Selection = selection;
            return Task.CompletedTask;
        }
    }
}
