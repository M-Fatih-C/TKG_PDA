using TkgPda.Domain;

namespace TkgPda.Application;

/// <summary>
/// Fleet providers supported by the application boundary. Provider choice is
/// commissioning data and is fixed before a transport request is dispatched.
/// </summary>
public enum FleetProvider
{
    Rds,
    M4
}

public enum FleetLocationKind
{
    Point,
    StorageBin
}

public enum FleetStepOperation
{
    Navigate,
    Load,
    Unload
}

public enum FleetOrderState
{
    Unknown,
    Queued,
    Allocated,
    Pending,
    Executing,
    Completed,
    Cancelling,
    Cancelled,
    Failed
}

public enum FleetCreateDisposition
{
    Created,
    Existing,
    Uncertain
}

/// <summary>
/// Immutable provider selection resolved from machine/profile commissioning.
/// Runtime routing must never fall back to another provider when the selected
/// provider is missing or unavailable.
/// </summary>
public sealed record FleetProviderAssignment(
    FleetProvider Provider,
    bool Enabled,
    bool ReadEnabled,
    bool DispatchEnabled,
    bool SimulationMode,
    string? ExternalScopeId = null);

public sealed record FleetRuntimeGates(
    bool DispatchEnabled,
    bool EmergencyDispatchBlocked);

public sealed record FleetDispatchEndpoint(
    Guid? SlotId,
    string? SlotCode,
    string ExternalLocation,
    FleetLocationKind LocationKind,
    PhysicalEndpointEvidence Evidence);

public sealed record FleetDispatchContext(
    TransportRequestSnapshot Request,
    string MachineCode,
    string VariantCode,
    string? ProductGroupCode,
    string? PartCode,
    string? ContainerCode,
    string ExternalScopeId,
    string ProviderTemplate,
    int Priority,
    FleetDispatchEndpoint Source,
    FleetDispatchEndpoint Destination);

public sealed record FleetOrderStep(
    string StepId,
    string Location,
    FleetLocationKind LocationKind,
    FleetStepOperation Operation,
    string? ActionTemplate = null);

public sealed record FleetCreateOrderRequest(
    Guid TransportRequestId,
    string ExternalId,
    string ExternalScopeId,
    string ProviderTemplate,
    int Priority,
    string? ContainerId,
    IReadOnlyList<FleetOrderStep> Steps,
    IReadOnlyDictionary<string, object?> InputParameters);

public sealed record FleetCreateOrderResult(
    FleetCreateDisposition Disposition,
    string ExternalId,
    string? ExternalOrderId,
    string Code,
    string Message);

public sealed record FleetCancellationResult(
    string ExternalOrderId,
    bool Accepted,
    bool AlreadyTerminal,
    string Code,
    string Message);

public sealed record FleetOrderSnapshot(
    string ExternalId,
    string? ExternalOrderId,
    FleetOrderState State,
    string? RobotName,
    bool Faulted,
    string? FaultReason,
    DateTimeOffset? UpdatedAt);

public sealed record FleetCallbackEvent(
    FleetProvider Provider,
    string ExternalId,
    string? ExternalOrderId,
    string EventType,
    DateTimeOffset? EventTimestamp,
    string EventHash);

/// <summary>
/// Provider-independent physical observation. RESERVED is deliberately absent:
/// reservation belongs to TKG workflow state, not physical occupancy.
/// </summary>
public sealed record PhysicalEndpointEvidence(
    OccupancyState Occupancy,
    LoadState Load,
    bool? Locked,
    bool? Enabled,
    bool? SyncFailed,
    DateTimeOffset? ObservedAt);

public sealed record FleetProviderHealth(
    FleetProvider Provider,
    bool Reachable,
    string Code,
    string Message,
    DateTimeOffset ObservedAt);

public sealed record FleetSceneSnapshot(
    string ExternalSceneId,
    string Name,
    bool Enabled,
    bool Simulated);

public sealed record FleetRobotSnapshot(
    string Name,
    string? Group,
    bool Online,
    bool Dispatchable,
    bool Simulated,
    string? CurrentLocation,
    IReadOnlyList<string> Alarms);

public sealed record FleetEndpointSnapshot(
    string ExternalSceneId,
    string Location,
    FleetLocationKind Kind,
    bool Enabled,
    PhysicalEndpointEvidence Evidence);

public sealed record FleetExternalOrderRecord(
    Guid TransportRequestId,
    FleetProvider Provider,
    string ExternalId,
    string? ExternalOrderId,
    FleetOrderState State,
    bool ResultUncertain);

public interface IFleetExecutionClient
{
    FleetProvider Provider { get; }
    bool DispatchEnabled { get; }

    Task<FleetCreateOrderResult> CreateOrderAsync(
        FleetCreateOrderRequest request,
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task<FleetCancellationResult> CancelOrderAsync(
        string externalOrderId,
        Guid correlationId,
        CancellationToken cancellationToken = default);
}

public interface IFleetReadClient
{
    FleetProvider Provider { get; }
    bool ReadEnabled { get; }

    Task<FleetProviderHealth> GetHealthAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task<FleetOrderSnapshot?> FindOrderByExternalIdAsync(
        string externalId,
        Guid correlationId,
        CancellationToken cancellationToken = default);
}

public interface IFleetCommissioningClient
{
    FleetProvider Provider { get; }

    Task<IReadOnlyList<FleetSceneSnapshot>> ListScenesAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetEndpointSnapshot>> ListEndpointsAsync(
        string externalSceneId,
        Guid correlationId,
        CancellationToken cancellationToken = default);
}

public interface IFleetTelemetryProvider
{
    FleetProvider Provider { get; }

    Task<IReadOnlyList<FleetRobotSnapshot>> ListRobotsAsync(
        string externalSceneId,
        Guid correlationId,
        CancellationToken cancellationToken = default);
}

public interface IFleetExecutionStore
{
    Task<FleetExternalOrderRecord?> FindByRequestAsync(
        Guid transportRequestId,
        CancellationToken cancellationToken = default);

    Task<FleetExternalOrderRecord?> FindByExternalIdAsync(
        FleetProvider provider,
        string externalId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        FleetExternalOrderRecord record,
        CancellationToken cancellationToken = default);
}

public interface IFleetProviderRouter
{
    IFleetReadClient ResolveReadClient(FleetProviderAssignment assignment);

    IFleetExecutionClient ResolveExecutionClient(
        FleetProviderAssignment assignment,
        FleetRuntimeGates runtimeGates);

    IFleetExecutionClient ResolveCancellationClient(FleetProviderAssignment assignment);
}
