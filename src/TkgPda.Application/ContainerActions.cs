using System.Text.Json;
using TkgPda.Domain;

namespace TkgPda.Application;

public enum OperatorContainerAction
{
    Full,
    Empty
}

public static class OperatorContainerActionCode
{
    public static string ToApi(this OperatorContainerAction value) => value switch
    {
        OperatorContainerAction.Full => "FULL",
        OperatorContainerAction.Empty => "EMPTY",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static OperatorContainerAction Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "FULL" => OperatorContainerAction.Full,
        "EMPTY" => OperatorContainerAction.Empty,
        _ => throw new WorkflowRuleException("CONTAINER_ACTION_INVALID", "Container action must be FULL or EMPTY.")
    };
}

public sealed record ContainerActionResolution(TransportTaskType Operation, bool MarkFullBeforeCreate, bool RequiresPreparation = false);

/// <summary>
/// WP-33 customer business-event policy. It consumes only TKG runtime state;
/// RDS loaded/filled/container/goodsId evidence is deliberately absent.
/// </summary>
public static class ContainerActionResolutionPolicy
{
    public static ContainerActionResolution ResolveSingle(
        OperatorContainerAction action,
        OccupancyState occupancy,
        LoadState load) => action switch
    {
        OperatorContainerAction.Full when occupancy == OccupancyState.Occupied && load == LoadState.Empty =>
            new(TransportTaskType.RemoveFull, MarkFullBeforeCreate: true, RequiresPreparation: false),
        OperatorContainerAction.Full when occupancy == OccupancyState.Occupied && load == LoadState.Full =>
            new(TransportTaskType.RemoveFull, MarkFullBeforeCreate: false, RequiresPreparation: false),
        OperatorContainerAction.Empty when occupancy == OccupancyState.Vacant && load == LoadState.Unknown =>
            new(TransportTaskType.BringEmpty, MarkFullBeforeCreate: false, RequiresPreparation: false),
        OperatorContainerAction.Empty when occupancy == OccupancyState.Occupied =>
            throw new WorkflowRuleException("CONTAINER_POSITION_OCCUPIED", "Bu konumda zaten bir kasa bulunuyor."),
        // A VACANT position is only ever stored with LoadState.Unknown (enforced by
        // ck_slot_load_requires_occupancy), so this arm must be evaluated BEFORE the
        // broad unknown-state arm below. Otherwise "Kasa Doldu" on an empty position
        // reports the generic BUSINESS_RULE_NOT_CONFIGURED instead of the specific,
        // actionable reason, and this arm is unreachable for every DB-valid state.
        // Rejection itself is unchanged — only the reason becomes accurate.
        OperatorContainerAction.Full when occupancy == OccupancyState.Vacant =>
            throw new WorkflowRuleException("OCCUPIED_CONTAINER_REQUIRED", "Dolu bildirimi için konumda bir kasa bulunmalıdır."),
        _ when occupancy == OccupancyState.Unknown || load == LoadState.Unknown =>
            throw new WorkflowRuleException("BUSINESS_RULE_NOT_CONFIGURED", "Kasa konumunun mevcut durumu güvenli bir işlem seçmek için yeterli değildir."),
        _ => throw new WorkflowRuleException("CONTAINER_STATE_CONFLICT", "Kasa konumunun mevcut durumu bu işlemle uyumlu değildir.")
    };

    public static TransportTaskType? ResolveRemoveAll(OccupancyState occupancy, LoadState load) =>
        occupancy switch
        {
            OccupancyState.Vacant when load == LoadState.Unknown => null,
            OccupancyState.Occupied when load == LoadState.Full => TransportTaskType.RemoveFull,
            OccupancyState.Occupied when load == LoadState.Empty => TransportTaskType.RemoveEmpty,
            OccupancyState.Unknown => throw new WorkflowRuleException(
                "REMOVE_ALL_POSITION_UNKNOWN",
                "En az bir kasa konumunun doluluk durumu bilinmiyor."),
            _ => throw new WorkflowRuleException(
                "REMOVE_ALL_POSITION_CONFLICT",
                "En az bir kasa konumunun doluluk/yük durumu çelişkilidir.")
        };
}

public sealed record ContainerPositionQuery(
    Guid MachineId,
    Guid VariantId,
    Guid LayoutId,
    Guid ProductGroupId,
    Guid PartId);

public sealed record ContainerActionCommand(
    string IdempotencyKey,
    Guid ActorUserId,
    Guid MachineId,
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    Guid ProductGroupId,
    Guid PartId,
    int PositionNumber,
    OperatorContainerAction Action,
    int Priority = 100,
    Guid? ProductionSessionId = null,
    OperationalEventContext? EventContext = null);

public sealed record RemoveAllContainersCommand(
    string IdempotencyKey,
    Guid ActorUserId,
    Guid MachineId,
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    int Priority = 100,
    Guid? ProductionSessionId = null,
    OperationalEventContext? EventContext = null);

public sealed record ContainerActionBatchItem(
    Guid ProductGroupId,
    Guid PartId,
    int PositionNumber,
    OperatorContainerAction Action);

public sealed record ContainerActionBatchCommand(
    string IdempotencyKey,
    Guid ActorUserId,
    Guid MachineId,
    Guid VariantId,
    Guid LayoutId,
    Guid? NavigationProfileId,
    IReadOnlyList<ContainerActionBatchItem> Items,
    int Priority = 100,
    Guid? ProductionSessionId = null,
    OperationalEventContext? EventContext = null);

public sealed record ContainerActionAvailabilityRecord(
    bool Enabled,
    string Status,
    string? DisabledCode,
    string? DisabledMessage);

public sealed record ContainerPositionRecord(
    int PositionNumber,
    string Label,
    ContainerActionAvailabilityRecord FullAction,
    ContainerActionAvailabilityRecord EmptyAction,
    string TransportState,
    int? QueuePosition,
    int? EtaSeconds,
    string EtaState);

public sealed record ContainerActionResult(
    int PositionNumber,
    string Action,
    TransportRequestSnapshot Request,
    bool WasIdempotentReplay);

public sealed record RemoveAllPositionIssue(int PositionNumber, string Code, string Message);

public sealed record RemoveAllContainersResult(
    TransportBatchResult Batch,
    IReadOnlyList<int> IgnoredVacantPositions);

public interface IContainerActionStore
{
    Task<IReadOnlyList<ContainerPositionRecord>> ListPositionsAsync(
        ContainerPositionQuery query,
        PdaActorContext actor,
        bool canOperate,
        CancellationToken cancellationToken = default);

    Task<ContainerActionResult> CreateAsync(
        ContainerActionCommand command,
        CancellationToken cancellationToken = default);

    Task<RemoveAllContainersResult> RemoveAllAsync(
        RemoveAllContainersCommand command,
        CancellationToken cancellationToken = default);

    Task<TransportBatchResult> CreateBatchAsync(
        ContainerActionBatchCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class ContainerActionService(IContainerActionStore store)
{
    public Task<IReadOnlyList<ContainerPositionRecord>> ListPositionsAsync(
        ContainerPositionQuery query,
        PdaActorContext actor,
        bool canOperate,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(query.MachineId, query.VariantId, query.LayoutId, query.PartId);
        if (query.ProductGroupId == Guid.Empty)
        {
            throw new WorkflowRuleException("PRODUCT_GROUP_REQUIRED", "A product group is required.");
        }

        return store.ListPositionsAsync(query, actor, canOperate, cancellationToken);
    }

    public Task<ContainerActionResult> CreateAsync(
        ContainerActionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateContext(command.MachineId, command.VariantId, command.LayoutId, command.PartId);
        ValidateIdempotency(command.IdempotencyKey);
        if (command.ActorUserId == Guid.Empty || command.ProductGroupId == Guid.Empty)
        {
            throw new WorkflowRuleException("ID_REQUIRED", "Actor and product-group identifiers are required.");
        }

        if (command.PositionNumber < 1 || command.Priority < 0)
        {
            throw new WorkflowRuleException("CONTAINER_POSITION_INVALID", "Container position must be a positive number and priority cannot be negative.");
        }

        return store.CreateAsync(command, cancellationToken);
    }

    public Task<RemoveAllContainersResult> RemoveAllAsync(
        RemoveAllContainersCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateRuntimeContext(command.MachineId, command.VariantId, command.LayoutId);
        ValidateIdempotency(command.IdempotencyKey);
        if (command.ActorUserId == Guid.Empty || command.Priority < 0)
        {
            throw new WorkflowRuleException("ID_REQUIRED", "Actor identifier is required and priority cannot be negative.");
        }

        return store.RemoveAllAsync(command, cancellationToken);
    }

    public Task<TransportBatchResult> CreateBatchAsync(
        ContainerActionBatchCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateRuntimeContext(command.MachineId, command.VariantId, command.LayoutId);
        ValidateIdempotency(command.IdempotencyKey);
        if (command.ActorUserId == Guid.Empty || command.Priority < 0)
        {
            throw new WorkflowRuleException("ID_REQUIRED", "Actor identifier is required and priority cannot be negative.");
        }

        if (command.Items is null || command.Items.Count is < 1 or > 50)
        {
            throw new WorkflowRuleException("BATCH_ITEMS_REQUIRED", "A container-action batch must contain between 1 and 50 items.");
        }

        foreach (var item in command.Items)
        {
            if (item.ProductGroupId == Guid.Empty || item.PartId == Guid.Empty || item.PositionNumber < 1)
            {
                throw new WorkflowRuleException("CONTAINER_BATCH_ITEM_INVALID", "Every batch item requires product group, part and positive position identifiers.");
            }
        }

        return store.CreateBatchAsync(command, cancellationToken);
    }

    private static void ValidateContext(Guid machineId, Guid variantId, Guid layoutId, Guid partId)
    {
        if (machineId == Guid.Empty || variantId == Guid.Empty || layoutId == Guid.Empty || partId == Guid.Empty)
        {
            throw new WorkflowRuleException("ID_REQUIRED", "Machine, variant, layout and part identifiers are required.");
        }
    }

    private static void ValidateRuntimeContext(Guid machineId, Guid variantId, Guid layoutId)
    {
        if (machineId == Guid.Empty || variantId == Guid.Empty || layoutId == Guid.Empty)
        {
            throw new WorkflowRuleException("ID_REQUIRED", "Machine, variant and layout identifiers are required.");
        }
    }

    private static void ValidateIdempotency(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_REQUIRED", "An idempotency key is required.");
        }

        if (key.Length > 200)
        {
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_TOO_LONG", "The idempotency key cannot exceed 200 characters.");
        }
    }
}

public sealed record OperatorQueueQuery(bool Terminal, int Offset = 0, int Limit = 50, int TerminalMaxAgeHours = 8);

public sealed record OperatorQueueItemRecord(
    int QueuePosition,
    string Machine,
    string Part,
    int? LogicalContainerNumber,
    string BusinessAction,
    DateTimeOffset RequestedAt,
    string State,
    int? EtaSeconds,
    string EtaState,
    Guid? RequestId = null);

public sealed record OperatorQueuePageRecord(
    bool Terminal,
    int Offset,
    int Limit,
    bool HasMore,
    IReadOnlyList<OperatorQueueItemRecord> Items);

public interface IOperatorTransportQueueStore
{
    Task<OperatorQueuePageRecord> ListAsync(OperatorQueueQuery query, CancellationToken cancellationToken = default);
}

public sealed class OperatorTransportQueueService(IOperatorTransportQueueStore store)
{
    public Task<OperatorQueuePageRecord> ListAsync(OperatorQueueQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 100)
        {
            throw new WorkflowRuleException("QUEUE_PAGE_INVALID", "Queue offset must be non-negative and limit must be between 1 and 100.");
        }

        if (query.TerminalMaxAgeHours is < 1 or > 168)
        {
            throw new WorkflowRuleException("QUEUE_PAGE_INVALID", "Terminal max age must be between 1 and 168 hours.");
        }

        return store.ListAsync(query, cancellationToken);
    }
}

public sealed record TransportEtaProjection(int? Seconds, string State);

/// <summary>
/// Deterministic and conservative ETA projection. Consumes real RDSCore remaining_time
/// telemetry when available. Runtime elapsed time and queue position do not prove an ETA.
/// </summary>
public sealed class TransportEtaService
{
    public TransportEtaProjection Unavailable() => new(null, "UNAVAILABLE");

    public TransportEtaProjection FromReliableRemainingTime(int? seconds) =>
        seconds is >= 0 ? new(seconds, "AVAILABLE") : Unavailable();

    public TransportEtaProjection Project(
        TransportRequestStatus status,
        int? rdsStatus,
        string? agvId,
        DateTimeOffset? startedAt,
        int? queuePosition,
        JsonElement? lastStatusJson = null,
        DateTimeOffset? now = null,
        TransportReadinessState? readinessState = null)
    {
        if (status is TransportRequestStatus.Completed or TransportRequestStatus.Failed or TransportRequestStatus.Cancelled
            || rdsStatus is 1001 or 1003 or 1004 or 1005 or 1006
            || readinessState == TransportReadinessState.WaitingPreparation)
        {
            return Unavailable();
        }

        if (lastStatusJson.HasValue && lastStatusJson.Value.ValueKind == JsonValueKind.Object)
        {
            if (TryExtractRemainingTime(lastStatusJson.Value, out var explicitSeconds) && explicitSeconds >= 0)
            {
                return new(explicitSeconds, "AVAILABLE");
            }
        }

        return Unavailable();
    }

    private static bool TryExtractRemainingTime(JsonElement element, out int seconds)
    {
        seconds = 0;
        if (element.TryGetProperty("remaining_time", out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            if (prop.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 && d <= int.MaxValue)
            {
                seconds = (int)Math.Round(d);
                return true;
            }
        }
        if (element.TryGetProperty("remainingTime", out prop) && prop.ValueKind == JsonValueKind.Number)
        {
            if (prop.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 && d <= int.MaxValue)
            {
                seconds = (int)Math.Round(d);
                return true;
            }
        }
        return false;
    }
}
