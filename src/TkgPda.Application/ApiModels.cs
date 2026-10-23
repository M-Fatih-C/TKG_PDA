using TkgPda.Domain;

namespace TkgPda.Application;

public sealed record AuthUserRecord(
    Guid Id,
    string Username,
    string PasswordHash,
    string FullName,
    string? EmployeeNo,
    bool IsActive,
    IReadOnlyList<string> Roles);

public sealed record DeviceRecord(
    Guid Id,
    string Code,
    string Name,
    string Type,
    bool IsActive,
    Guid? MachineId = null);

public sealed record MachinePermissionRecord(
    Guid MachineId,
    bool CanView,
    bool CanOperate,
    bool CanChangeVariant,
    bool CanManageSlots);

public sealed record MachineSummaryRecord(
    Guid Id,
    string Code,
    string DisplayName,
    int DisplayOrder,
    MachinePermissionRecord Permissions);

/// <summary>
/// WP-02: a variant compatible with a selected part on an authorized machine,
/// derived only from existing normalized master data (never inferred).
/// RelationType is "MAIN" when the part is the product group's main part,
/// otherwise the product_group_parts relation_type (RELATED/ALT_PART/
/// CONSUMABLE/OTHER).
/// </summary>
public sealed record CompatibleVariantRecord(
    Guid VariantId,
    string VariantCode,
    string VariantName,
    Guid MachineId,
    Guid ProductGroupId,
    string RelationType);

/// <summary>
/// WP-03A: a Related/Alt part of a machine-level main part option. RelationType
/// is the product_group_parts.relation_type (RELATED/ALT_PART/CONSUMABLE/OTHER).
/// Any of these part IDs (main or related) is a valid partId for
/// GET /api/v1/machines/{machineId}/parts/{partId}/compatible-variants?mainPartId=...
/// </summary>
public sealed record MachineRelatedPartRecord(
    Guid PartId,
    string PartCode,
    string? Description,
    string RelationType,
    bool Requestable = true,
    IReadOnlyList<string>? Blockers = null);

/// <summary>
/// WP-03A: a machine-level main-part selection option, deduplicated across all
/// of the machine's variants. Derived only from existing master data
/// (master.product_groups / master.product_group_parts); no variant selection
/// is required to see this projection.
/// </summary>
public sealed record MachineMainPartOptionRecord(
    Guid PartId,
    string PartCode,
    string? Description,
    IReadOnlyList<MachineRelatedPartRecord> RelatedParts);

public sealed record VariantLayoutOptionRecord(
    Guid VariantId,
    string VariantCode,
    string VariantName,
    Guid LayoutId,
    string LayoutCode,
    string LayoutName,
    bool IsDefault);

public sealed record RdsWorkSiteProjectionRecord(
    bool IsMapped,
    string? SiteId,
    string? Area,
    string? GroupName,
    int? Type,
    int? Filled,
    int? Locked,
    int? Disabled,
    int? SyncFailed,
    string? Content,
    string? AgvId,
    DateTimeOffset? LastSyncAt,
    string? PhysicalState = null,
    string? ConsistencyResult = null);

public sealed record SlotContextDataRecord(
    Guid Id,
    string Code,
    string DisplayName,
    string Role,
    bool IsEnabled,
    bool TransportEligible,
    Guid? DefaultContainerTypeId,
    string? DefaultContainerCode,
    decimal? X,
    decimal? Y,
    decimal? Width,
    decimal? Height,
    int DisplayOrder,
    string OccupancyState,
    string LoadState,
    Guid? CurrentPartId,
    string? CurrentPartCode,
    Guid? CurrentContainerTypeId,
    string? CurrentContainerCode,
    long RowVersion,
    DateTimeOffset? RuntimeUpdatedAt,
    RdsWorkSiteProjectionRecord RdsWorkSite);

public sealed record PdaAvailableActionRecord(
    string Action,
    bool Enabled,
    string? DisabledCode,
    string? DisabledMessage);

public sealed record PdaExecutionReadinessRecord(
    bool RdsWorkSiteMapped,
    bool IsExecutionReady,
    string? ReadinessCode,
    string? ReadinessMessage,
    string? ConsistencyResult = null);

public sealed record SlotContextRecord(
    Guid Id,
    string Code,
    string DisplayName,
    string Role,
    bool IsEnabled,
    bool TransportEligible,
    Guid? DefaultContainerTypeId,
    string? DefaultContainerCode,
    decimal? X,
    decimal? Y,
    decimal? Width,
    decimal? Height,
    int DisplayOrder,
    string OccupancyState,
    string LoadState,
    Guid? CurrentPartId,
    string? CurrentPartCode,
    Guid? CurrentContainerTypeId,
    string? CurrentContainerCode,
    long RowVersion,
    DateTimeOffset? RuntimeUpdatedAt,
    // RdsWorkSite is deliberately absent from the serialized PDA contract. The raw
    // WorkSite SiteId, area, group, content, AGV id and filled/locked/disabled/
    // syncFailed flags are RDS technical evidence, not business state, and they are
    // not TKG Occupancy/Load truth. The business-safe distillation the Operator
    // actually needs is ExecutionReadiness below; the raw record stays on
    // SlotContextDataRecord for server-side readiness evaluation and is available
    // to Admin through the technical RDS endpoints.
    PdaExecutionReadinessRecord ExecutionReadiness,
    IReadOnlyList<PdaAvailableActionRecord> AvailableActions,
    IReadOnlyList<PdaAvailableActionRecord> LoadStateActions);

public sealed record PdaProductGroupPartRecord(
    Guid Id,
    Guid PartId,
    string PartCode,
    Guid? ContainerTypeId,
    string? ContainerCode,
    string RelationType,
    decimal? LotQuantity,
    int DisplayOrder);

public sealed record PdaProductGroupRecord(
    Guid Id,
    Guid MainPartId,
    string MainPartCode,
    string? Name,
    int DisplayOrder,
    IReadOnlyList<PdaProductGroupPartRecord> Parts);

public sealed record PdaTaskTemplateRecord(
    Guid Id,
    Guid VariantId,
    Guid? ProductGroupId,
    Guid TaskTypeId,
    string Operation,
    Guid? SourceSlotGroupId,
    Guid? DestinationSlotGroupId,
    // The raw RDS task label is deliberately absent from the PDA contract: it is
    // RDS technical configuration and belongs to Admin diagnostics. The Operator
    // only needs to know whether a label is configured, which is the readiness
    // signal below. The label itself stays server-side for dispatch.
    bool RdsTaskLabelConfigured,
    int Priority);

public sealed record RdsTaskDataRecord(
    string? TaskRecordId,
    string? TaskLabel,
    string? OutOrderNo,
    string? AgvId,
    int? Status,
    string? EndedReason,
    DateTimeOffset? SentAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset? LastSyncAt);

public sealed record ActiveTransportDataRecord(
    Guid RequestId,
    long RequestNo,
    TransportTaskType Operation,
    TransportRequestStatus Status,
    Guid? RequestedBy,
    string? RequestedByUsername,
    bool IsMine,
    Guid? SourceSlotId,
    Guid? DestinationSlotId,
    Guid? ProductGroupId,
    Guid? PartId,
    Guid? ContainerTypeId,
    int Priority,
    DateTimeOffset CreatedAt,
    string? FailureCode,
    string? FailureMessage,
    Guid CorrelationId,
    RdsTaskDataRecord? RdsTask,
    // WP-07 projection-only values. Everything below is derived server-side from
    // PostgreSQL (master data, request row, server clock); none of it is persisted.
    string? PartCode = null,
    string? PartDescription = null,
    // MAIN_PART, ALT_PART, RELATED, CONSUMABLE, OTHER, or null when master data
    // gives no evidence. Never guessed.
    string? RelationType = null,
    string ReadinessState = "NOT_REQUIRED",
    // TKG's deterministic ordering of this machine's active requests (1-based).
    // It is not an RDS queue position and not a robot position.
    int? QueuePosition = null,
    long AgeSeconds = 0,
    long? ExecutionElapsedSeconds = null,
    string? ChainState = null,
    bool ProductionEnd = false,
    // Deferred warehouse demands are visible in the active queue before they
    // own the physical machine position. Consumers must use this flag instead
    // of treating every endpoint on every active request as reserved.
    bool MachineSlotReservationActive = true);

/// <summary>
/// Business execution view for the PDA. Raw RDS identifiers — taskRecordId,
/// taskLabel, outOrderNo, AGV id and the numeric RDS status — are deliberately
/// absent: this is a business contract served to every PDA role, and a Supervisor
/// happening to hold a PDA does not make it a technical surface. Those values
/// remain available to AdminWeb through the technical RDS endpoints.
/// <see cref="State"/> is the friendly execution state derived from TKG status and
/// refined by persisted RDS status server-side.
///
/// There is deliberately no TechnicalMessage. It previously carried the raw
/// TKG FailureMessage or the raw RDS EndedReason to a device-bound Supervisor or
/// Admin, which handed unbounded RDS text to a business contract. Every failure
/// now crosses the boundary only as the normalised triple below
/// (<see cref="FailureCode"/>, <see cref="FailureCategory"/>,
/// <see cref="OperatorMessage"/>), and <see cref="CorrelationId"/> is the key an
/// authorised user follows into AdminWeb or the server audit log for the
/// technical detail.
/// </summary>
public sealed record PdaExecutionProjectionRecord(
    string State,
    DateTimeOffset? LastSyncAt,
    string? FailureCode,
    string? FailureCategory,
    string? OperatorMessage,
    Guid CorrelationId);

public sealed record ActiveTransportRequestRecord(
    Guid RequestId,
    long RequestNo,
    string Operation,
    string Status,
    Guid? RequestedBy,
    string? RequestedByUsername,
    bool IsMine,
    Guid? SourceSlotId,
    Guid? DestinationSlotId,
    Guid? ProductGroupId,
    Guid? PartId,
    Guid? ContainerTypeId,
    int Priority,
    DateTimeOffset CreatedAt,
    string? PartCode,
    string? PartDescription,
    string? RelationType,
    string ReadinessState,
    int? QueuePosition,
    long AgeSeconds,
    long? ExecutionElapsedSeconds,
    bool CanCancel,
    string? CancelDisabledCode,
    string? CancelDisabledMessage,
    PdaExecutionProjectionRecord Execution,
    string? ChainState = null,
    bool ProductionEnd = false);

/// <summary>
/// Customer UI — Warehouse Preparation Console: the minimum, self-contained
/// projection of a WAITING_PREPARATION transport request needed by a
/// warehouse-authorized user to prepare it, independent of any single
/// machine's operational context. Never includes RDS/technical identifiers.
/// </summary>
public sealed record WarehousePreparationRequestRecord(
    Guid RequestId,
    long RequestNo,
    string Operation,
    Guid MachineId,
    string MachineCode,
    string MachineDisplayName,
    string? PartCode,
    string? ContainerTypeCode,
    Guid? SourceSlotId,
    string? SourceSlotCode,
    string? SourceSlotDisplayName,
    Guid? DestinationSlotId,
    string? DestinationSlotCode,
    string? DestinationSlotDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? QueuedAt);

public sealed record OperationalContextDataRecord(
    MachineSummaryRecord Machine,
    Guid? ActiveVariantId,
    string? ActiveVariantCode,
    string? ActiveVariantName,
    Guid? ActiveLayoutId,
    string? ActiveLayoutCode,
    string? ActiveLayoutName,
    Guid? ActiveNavigationProfileId,
    string? ActiveNavigationProfileCode,
    long RuntimeRowVersion,
    DateTimeOffset? RuntimeUpdatedAt,
    IReadOnlyList<VariantLayoutOptionRecord> VariantLayouts,
    IReadOnlyList<SlotContextDataRecord> Slots,
    IReadOnlyList<PdaProductGroupRecord> ProductGroups,
    IReadOnlyList<ActiveTransportDataRecord> ActiveTransportRequests,
    IReadOnlyList<PdaTaskTemplateRecord> TaskTemplates);

public sealed record OperationalContextRecord(
    MachineSummaryRecord Machine,
    Guid? ActiveVariantId,
    string? ActiveVariantCode,
    string? ActiveVariantName,
    Guid? ActiveLayoutId,
    string? ActiveLayoutCode,
    string? ActiveLayoutName,
    Guid? ActiveNavigationProfileId,
    string? ActiveNavigationProfileCode,
    long RuntimeRowVersion,
    DateTimeOffset? RuntimeUpdatedAt,
    IReadOnlyList<VariantLayoutOptionRecord> VariantLayouts,
    IReadOnlyList<SlotContextRecord> Slots,
    IReadOnlyList<PdaProductGroupRecord> ProductGroups,
    IReadOnlyList<ActiveTransportRequestRecord> ActiveTransportRequests,
    IReadOnlyList<PdaTaskTemplateRecord> TaskTemplates);

public sealed record AuditEntry(
    Guid? ActorUserId,
    Guid? DeviceId,
    string Action,
    string EntityType,
    string? EntityId,
    string? BeforeJson,
    string? AfterJson,
    string? IpAddress,
    string? UserAgent,
    Guid CorrelationId);

public enum MachineAccessKind
{
    View,
    Operate,
    ChangeVariant,
    ManageSlots
}
