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

public sealed record ProductionEndAreaPriority(
    Guid MachineId,
    string MachineCode,
    string MachineName,
    Guid VariantId,
    string VariantCode,
    string VariantName,
    Guid ProductGroupId,
    string MainPartCode,
    Guid LayoutId,
    string LayoutCode,
    string LayoutName,
    Guid LayoutSlotId,
    string SlotCode,
    string SlotName,
    Guid PartId,
    string PartCode,
    string PartRole,
    int? Priority,
    DateTimeOffset? UpdatedAt);

public sealed record ProductionEndAreaPriorityUpdate(
    Guid ProductGroupId,
    Guid LayoutSlotId,
    Guid PartId,
    int? Priority);

public interface IOperationalEvidenceStore
{
    Task<TransportEvidence?> ReadTransportEvidenceAsync(Guid requestId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductionEndPriority>> ListProductionEndPrioritiesAsync(CancellationToken cancellationToken);

    Task SetProductionEndPriorityAsync(string operationCode, int? priority, Guid actorUserId, CancellationToken cancellationToken);

    Task<int> RequireProductionEndPriorityAsync(string operationCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductionEndAreaPriority>> ListProductionEndAreaPrioritiesAsync(CancellationToken cancellationToken);

    Task SetProductionEndAreaPriorityAsync(
        Guid productGroupId,
        Guid layoutSlotId,
        Guid partId,
        int? priority,
        Guid actorUserId,
        CancellationToken cancellationToken);

    Task SetProductionEndAreaPrioritiesAsync(
        IReadOnlyList<ProductionEndAreaPriorityUpdate> updates,
        Guid actorUserId,
        CancellationToken cancellationToken);
}
