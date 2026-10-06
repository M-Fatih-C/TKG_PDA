using TkgPda.Domain;

namespace TkgPda.Application;

public sealed class TransportWorkflowService(ITransportWorkflowStore store)
{
    public Task<CreateTransportResult> CreateAsync(
        CreateTransportRequest request,
        CancellationToken cancellationToken = default) =>
        CreateAsync(request, null, cancellationToken);

    public Task<CreateTransportResult> CreateAsync(
        CreateTransportRequest request,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateShape();
        return store.CreateAndQueueAsync(request, eventContext, cancellationToken);
    }

    public Task<TransportBatchResult> CreateBatchAsync(
        CreateTransportBatchRequest request,
        CancellationToken cancellationToken = default) =>
        CreateBatchAsync(request, null, cancellationToken);

    public Task<TransportBatchResult> CreateBatchAsync(
        CreateTransportBatchRequest request,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateShape();
        return store.CreateBatchAndQueueAsync(request, eventContext, cancellationToken);
    }

    public Task<CancelTransportResult> CancelAsync(
        Guid requestId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default) =>
        CancelAsync(requestId, actorUserId, null, cancellationToken);

    public Task<CancelTransportResult> CancelAsync(
        Guid requestId,
        Guid? actorUserId,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty)
        {
            throw new WorkflowRuleException("REQUEST_ID_REQUIRED", "A transport request ID is required.");
        }

        return store.CancelAsync(requestId, actorUserId, eventContext, cancellationToken);
    }

    public Task<TransportRequestSnapshot> MarkReadyAsync(
        Guid requestId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default) =>
        MarkReadyAsync(requestId, actorUserId, null, cancellationToken);

    public Task<TransportRequestSnapshot> MarkReadyAsync(
        Guid requestId,
        Guid? actorUserId,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty)
        {
            throw new WorkflowRuleException("REQUEST_ID_REQUIRED", "A transport request ID is required.");
        }

        return store.MarkReadyAsync(requestId, actorUserId, eventContext, cancellationToken);
    }

    public Task<TransportRequestSnapshot> RecordExecutionTransitionAsync(
        Guid requestId,
        TransportRequestStatus nextStatus,
        CancellationToken cancellationToken = default) =>
        RecordExecutionTransitionAsync(requestId, nextStatus, null, cancellationToken);

    public Task<TransportRequestSnapshot> RecordExecutionTransitionAsync(
        Guid requestId,
        TransportRequestStatus nextStatus,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty)
        {
            throw new WorkflowRuleException("REQUEST_ID_REQUIRED", "A transport request ID is required.");
        }

        if (nextStatus is not (TransportRequestStatus.SentToRds or TransportRequestStatus.Running or TransportRequestStatus.Completed))
        {
            throw new WorkflowRuleException("UNSUPPORTED_EXECUTION_UPDATE", "Phase 03 only records dispatch, running and successful completion transitions.");
        }

        return store.RecordExecutionTransitionAsync(requestId, nextStatus, eventContext, cancellationToken);
    }

    public Task<TransportRequestSnapshot> RecordExecutionOutcomeAsync(
        Guid requestId,
        ExecutionOutcome outcome,
        CancellationToken cancellationToken = default) =>
        RecordExecutionOutcomeAsync(requestId, outcome, null, cancellationToken);

    public Task<TransportRequestSnapshot> RecordExecutionOutcomeAsync(
        Guid requestId,
        ExecutionOutcome outcome,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty)
        {
            throw new WorkflowRuleException("REQUEST_ID_REQUIRED", "A transport request ID is required.");
        }

        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Status is not (TransportRequestStatus.Failed or TransportRequestStatus.Cancelled))
        {
            throw new WorkflowRuleException("UNSUPPORTED_EXECUTION_OUTCOME", "Only executor-confirmed failure and cancellation outcomes are supported.");
        }

        return store.RecordExecutionOutcomeAsync(requestId, outcome, eventContext, cancellationToken);
    }

    public Task<LoadStateUpdateResult> UpdateLoadStateAsync(
        LoadStateUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MachineId == Guid.Empty || request.SlotId == Guid.Empty || request.ActorUserId == Guid.Empty)
        {
            throw new WorkflowRuleException("ID_REQUIRED", "Machine, slot and actor identifiers are required.");
        }

        if (request.TargetLoadState == LoadState.Unknown)
        {
            throw new WorkflowRuleException("LOAD_STATE_NOT_SELECTABLE", "UNKNOWN cannot be selected by an operator.");
        }

        return store.UpdateLoadStateAsync(request, cancellationToken);
    }

    public Task SwitchVariantAsync(VariantSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return store.SwitchVariantAsync(selection, cancellationToken);
    }
}
