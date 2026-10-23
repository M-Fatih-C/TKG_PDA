using TkgPda.Domain;

namespace TkgPda.Application;

/// <summary>
/// WP-06: every mutating member carries an <see cref="OperationalEventContext"/>
/// as a required (nullable) parameter and has no default implementation, so an
/// implementation cannot compile while silently dropping the actor / device /
/// correlation identity of a lifecycle event. Pass <c>null</c> only when there
/// genuinely is no context.
/// </summary>
public interface ITransportWorkflowStore
{
    Task<CreateTransportResult> CreateAndQueueAsync(CreateTransportRequest request, OperationalEventContext? eventContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// WP-04: creates a TransportBatch and its child TransportRequests
    /// atomically (all-or-nothing) within a single transaction, reusing the
    /// same per-item validation/business rules as
    /// <see cref="CreateAndQueueAsync(CreateTransportRequest, OperationalEventContext?, CancellationToken)"/>. Replaying the same batch
    /// idempotency key returns the existing batch instead of creating
    /// duplicate children.
    /// </summary>
    Task<TransportBatchResult> CreateBatchAndQueueAsync(CreateTransportBatchRequest request, OperationalEventContext? eventContext, CancellationToken cancellationToken = default);

    Task<CancelTransportResult> CancelAsync(Guid requestId, Guid? actorUserId, OperationalEventContext? eventContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// WP-05A: transitions a request's BUSINESS readiness from
    /// WAITING_PREPARATION to READY and records ready_at. Idempotent when
    /// already READY. Never touches the execution state machine.
    /// </summary>
    Task<TransportRequestSnapshot> MarkReadyAsync(Guid requestId, Guid? actorUserId, OperationalEventContext? eventContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates ready, deferred alt-part demands whose mapped machine
    /// positions can be claimed safely.  This is system orchestration, so the
    /// operational context must identify the worker correlation when present.
    /// </summary>
    Task<int> ActivatePreparedPartDemandsAsync(int limit, OperationalEventContext? eventContext, CancellationToken cancellationToken = default);

    Task<TransportRequestSnapshot> RecordExecutionTransitionAsync(
        Guid requestId,
        TransportRequestStatus nextStatus,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default);

    Task<TransportRequestSnapshot> RecordExecutionOutcomeAsync(
        Guid requestId,
        ExecutionOutcome outcome,
        OperationalEventContext? eventContext,
        CancellationToken cancellationToken = default);

    Task<LoadStateUpdateResult> UpdateLoadStateAsync(
        LoadStateUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task SwitchVariantAsync(VariantSelection selection, CancellationToken cancellationToken = default);
}
