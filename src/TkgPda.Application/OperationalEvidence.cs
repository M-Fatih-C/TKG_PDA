namespace TkgPda.Application;

public sealed record TransportFailureEvidence(
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string? Message);

public sealed record RobotIntegrationEvidence(
    string? TaskRecordId,
    int? RdsStatus,
    string? EndedReason,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt);

public sealed record TransportEvidence(
    Guid RequestId,
    long? TotalSeconds,
    long? WarehouseResponseSeconds,
    long? RobotExecutionSeconds,
    IReadOnlyList<TransportFailureEvidence> Failures,
    RobotIntegrationEvidence? Robot);

public sealed record ProductionEndPriority(string OperationCode, int? Priority, DateTimeOffset? UpdatedAt);

public interface IOperationalEvidenceStore
{
    Task<TransportEvidence?> ReadTransportEvidenceAsync(Guid requestId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductionEndPriority>> ListProductionEndPrioritiesAsync(CancellationToken cancellationToken);

    Task SetProductionEndPriorityAsync(string operationCode, int? priority, Guid actorUserId, CancellationToken cancellationToken);

    Task<int> RequireProductionEndPriorityAsync(string operationCode, CancellationToken cancellationToken);
}
