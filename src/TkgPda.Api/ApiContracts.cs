using TkgPda.Application;

namespace TkgPda.Api;

public sealed record LoginRequest(string? Username, string? Password = null, string? EmployeeNumber = null);

public sealed record LoginResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresAt,
    AuthenticatedUserResponse User,
    DeviceResponse Device);

public sealed record AuthenticatedUserResponse(
    Guid Id,
    string Username,
    string FullName,
    string? EmployeeNo,
    IReadOnlyList<string> Roles);

public sealed record DeviceResponse(Guid Id, string Code, string Name, string Type, Guid? MachineId = null);

public sealed record HeartbeatResponse(Guid SessionId, DateTimeOffset LeaseExpiresAt, DateTimeOffset TokenExpiresAt);

public sealed record CreateTransportHttpRequest(
    Guid MachineId,
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    Guid? ProductGroupId,
    Guid? PartId,
    Guid? ContainerTypeId,
    string Operation,
    // SourceSlotId / DestinationSlotId are deliberately absent from the Operator
    // request contract. A normal Operator expresses business intent
    // (machine -> part -> operation); the backend resolves the physical machine-side
    // slot from master.layout_slot_parts and allocates the EMPTY_STORAGE buffer with
    // FOR UPDATE ... SKIP LOCKED. Accepting these fields let an API caller choose
    // physical routing even though the PDA always sent null. The resolved values are
    // still returned on the response so the client can show what was chosen.
    int Priority = 100,
    bool RequiresPreparation = false);

public sealed record CreateTransportBatchItemHttpRequest(
    string IdempotencyKey,
    Guid MachineId,
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    Guid? ProductGroupId,
    Guid? PartId,
    Guid? ContainerTypeId,
    string Operation,
    // SourceSlotId / DestinationSlotId are deliberately absent from the Operator
    // request contract. A normal Operator expresses business intent
    // (machine -> part -> operation); the backend resolves the physical machine-side
    // slot from master.layout_slot_parts and allocates the EMPTY_STORAGE buffer with
    // FOR UPDATE ... SKIP LOCKED. Accepting these fields let an API caller choose
    // physical routing even though the PDA always sent null. The resolved values are
    // still returned on the response so the client can show what was chosen.
    int Priority = 100,
    bool RequiresPreparation = false);

public sealed record CreateTransportBatchHttpRequest(
    string IdempotencyKey,
    Guid MachineId,
    IReadOnlyList<CreateTransportBatchItemHttpRequest> Items);

public sealed record TransportBatchHttpResponse(
    Guid BatchId,
    Guid MachineId,
    bool IdempotentReplay,
    IReadOnlyList<TransportHttpResponse> Items);

public sealed record CreateContainerActionHttpRequest(
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    Guid ProductGroupId,
    Guid PartId,
    int PositionNumber,
    string Action,
    int Priority = 100,
    Guid? ProductionSessionId = null);

public sealed record RemoveAllContainersHttpRequest(
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    int Priority = 100,
    Guid? ProductionSessionId = null);

public sealed record CreateContainerActionBatchItemHttpRequest(
    Guid ProductGroupId,
    Guid PartId,
    int PositionNumber,
    string Action);

public sealed record CreateContainerActionBatchHttpRequest(
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    IReadOnlyList<CreateContainerActionBatchItemHttpRequest> Items,
    int Priority = 100,
    Guid? ProductionSessionId = null);

public sealed record PartDemandHttpRequest(Guid PartId);

public sealed record PartDemandBatchHttpRequest(IReadOnlyList<Guid> PartIds);

public sealed record PartDemandHttpResponse(
    long RequestNo,
    string TaskType,
    string Status,
    string Readiness,
    long? PredecessorRequestNo,
    string? PredecessorTaskType,
    bool IdempotentReplay);

public sealed record PartDemandBatchHttpResponse(
    bool IdempotentReplay,
    IReadOnlyList<PartDemandHttpResponse> Items);

public sealed record ContainerActionHttpResponse(
    int PositionNumber,
    string Action,
    long RequestNo,
    string Status,
    bool IdempotentReplay);

public sealed record RemoveAllContainerItemHttpResponse(
    long RequestNo,
    string Action,
    string Status);

public sealed record RemoveAllContainersHttpResponse(
    bool IdempotentReplay,
    IReadOnlyList<int> IgnoredVacantPositions,
    IReadOnlyList<RemoveAllContainerItemHttpResponse> Items);

public sealed record VariantSwitchHttpRequest(
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId);

public sealed record UpdateLoadStateHttpRequest(string LoadState);

/// <summary>
/// Operator-safe view of the RDS content synchronization outcome. The raw RDS
/// WorkSite SiteId that <see cref="RdsLoadContentSyncResult"/> carries internally is
/// deliberately not serialized: this endpoint is reachable by Line and Warehouse
/// Operators, and the site identifier is RDS technical evidence.
/// </summary>
public sealed record LoadContentSyncHttpResult(string Status, string? Message)
{
    public static LoadContentSyncHttpResult From(RdsLoadContentSyncResult result) =>
        new(result.Status, result.Message);
}

public sealed record UpdateLoadStateHttpResponse(
    Guid MachineId,
    Guid SlotId,
    string OccupancyState,
    string LoadState,
    long RowVersion,
    bool IdempotentReplay,
    LoadContentSyncHttpResult RdsContentSync);

public sealed record TransportHttpResponse(
    Guid Id,
    long RequestNo,
    string Status,
    string Operation,
    Guid MachineId,
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    Guid? ProductGroupId,
    Guid? PartId,
    Guid? ContainerTypeId,
    Guid? SourceSlotId,
    Guid? DestinationSlotId,
    int Priority,
    bool IdempotentReplay,
    bool RdsCancellationPending,
    string ReadinessState,
    DateTimeOffset? ReadyAt);
