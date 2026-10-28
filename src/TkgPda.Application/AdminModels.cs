using System.Text.Json;

namespace TkgPda.Application;

public static class KnownRoleCodes
{
    public const string Admin = "ADMIN";
    public const string Supervisor = "SUPERVISOR";
    public const string LineOperator = "LINE_OPERATOR";
    public const string WarehouseOperator = "WAREHOUSE_OPERATOR";
    public const string SemiFinishedWarehouseOperator = "SEMI_FINISHED_WAREHOUSE_OPERATOR";
    public const string FinishedGoodsWarehouseOperator = "FINISHED_GOODS_WAREHOUSE_OPERATOR";

    public static readonly string[] All =
    [
        Admin,
        Supervisor,
        LineOperator,
        WarehouseOperator,
        SemiFinishedWarehouseOperator,
        FinishedGoodsWarehouseOperator
    ];
}

public sealed record AdminMutationContext(
    Guid ActorUserId,
    Guid CorrelationId,
    string? IpAddress,
    string? UserAgent);

public sealed record AdminRoleRecord(
    Guid Id,
    string Code,
    string Name,
    bool IsSystem,
    bool IsActive);

public sealed record AdminUserRecord(
    Guid Id,
    string Username,
    string FullName,
    string? EmployeeNo,
    bool IsActive,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> Roles,
    IReadOnlyList<MachinePermissionRecord> MachinePermissions,
    long ConcurrencyToken);

public sealed record AdminDeviceRecord(
    Guid Id,
    string Code,
    string Name,
    string Type,
    bool IsActive,
    JsonElement Metadata,
    long ConcurrencyToken,
    Guid? MachineId = null);

public sealed record AdminMachineRecord(
    Guid Id,
    string Code,
    string DisplayName,
    int DisplayOrder,
    bool IsActive,
    long ConcurrencyToken);

public sealed record AdminVariantRecord(
    Guid Id,
    Guid MachineId,
    string Code,
    string Name,
    int DisplayOrder,
    bool IsActive,
    Guid? DefaultLayoutId,
    long ConcurrencyToken);

public sealed record AdminLayoutRecord(
    Guid Id,
    Guid MachineId,
    string Code,
    string Name,
    int Version,
    string Status,
    int SlotCount,
    int AssignedVariantCount,
    DateTimeOffset? PublishedAt,
    long ConcurrencyToken);

public sealed record AdminSlotRecord(
    Guid Id,
    Guid LayoutId,
    string Code,
    string DisplayName,
    string Role,
    Guid? DefaultContainerTypeId,
    decimal? X,
    decimal? Y,
    decimal? Width,
    decimal? Height,
    int DisplayOrder,
    bool AllowsEmptyLoad,
    bool AllowsFullLoad,
    bool IsEnabled,
    JsonElement Metadata,
    long ConcurrencyToken);

public sealed record AdminPartRecord(
    Guid Id,
    string Code,
    string? Description,
    bool IsActive,
    long ConcurrencyToken);

public sealed record AdminProductGroupPartRecord(
    Guid PartId,
    string PartCode,
    Guid? ContainerTypeId,
    string? ContainerCode,
    string RelationType,
    decimal? LotQuantity,
    int DisplayOrder,
    bool IsActive);

/// <summary>
/// Customer UX — Automatic Machine Slot Resolution: a curated part <->
/// layout slot assignment (master.layout_slot_parts), so a customer
/// correction no longer requires a SQL edit. Carries no execution/runtime
/// state — occupancy/load remain exclusively in ops.slot_runtime_state.
/// </summary>
public sealed record AdminSlotPartRecord(Guid PartId, string PartCode, string? Description);

public sealed record AdminProductGroupRecord(
    Guid Id,
    Guid VariantId,
    Guid MainPartId,
    string MainPartCode,
    string? Name,
    int DisplayOrder,
    bool IsActive,
    IReadOnlyList<AdminProductGroupPartRecord> Parts,
    long ConcurrencyToken);

public sealed record AdminContainerRecord(
    Guid Id,
    string Code,
    string Name,
    string Category,
    int? LengthMm,
    int? WidthMm,
    int? HeightMm,
    decimal? CapacityQuantity,
    bool IsActive,
    long ConcurrencyToken);

public sealed record AdminDataIssueRecord(
    string Kind,
    string Severity,
    string EntityType,
    string EntityId,
    string Code,
    string Message);

public sealed record AdminAuditRecord(
    long Id,
    Guid? ActorUserId,
    string? ActorUsername,
    string Action,
    string EntityType,
    string? EntityId,
    JsonElement? Before,
    JsonElement? After,
    string? IpAddress,
    string? UserAgent,
    Guid? CorrelationId,
    DateTimeOffset CreatedAt);

public sealed record CreateUserCommand(
    string Username,
    string Password,
    string FullName,
    string? EmployeeNo,
    IReadOnlyCollection<string> Roles);

public sealed record UpdateUserCommand(
    string FullName,
    string? EmployeeNo,
    bool IsActive,
    long ExpectedVersion);

public sealed record CreateDeviceCommand(
    string Code,
    string Name,
    string Type,
    bool IsActive,
    JsonElement Metadata,
    Guid? MachineId = null);

public sealed record UpdateDeviceCommand(
    string Name,
    string Type,
    bool IsActive,
    JsonElement Metadata,
    long ExpectedVersion,
    Guid? MachineId = null);

public sealed record UpdateMachineCommand(
    string DisplayName,
    int DisplayOrder,
    bool IsActive,
    long ExpectedVersion);

public sealed record CreateVariantCommand(
    Guid MachineId,
    string Code,
    string Name,
    int DisplayOrder);

public sealed record UpdateVariantCommand(
    string Name,
    int DisplayOrder,
    bool IsActive,
    long ExpectedVersion);

public sealed record CreateLayoutCommand(Guid MachineId, string Code, string Name);

public sealed record UpdateLayoutCommand(string Name, long ExpectedVersion);

public sealed record CreateSlotCommand(
    string Code,
    string DisplayName,
    string Role,
    Guid? DefaultContainerTypeId,
    decimal? X,
    decimal? Y,
    decimal? Width,
    decimal? Height,
    int DisplayOrder,
    bool AllowsEmptyLoad,
    bool AllowsFullLoad,
    JsonElement Metadata);

public sealed record UpdateSlotCommand(
    string DisplayName,
    string Role,
    Guid? DefaultContainerTypeId,
    decimal? X,
    decimal? Y,
    decimal? Width,
    decimal? Height,
    int DisplayOrder,
    bool AllowsEmptyLoad,
    bool AllowsFullLoad,
    bool IsEnabled,
    JsonElement Metadata,
    long ExpectedVersion,
    long ExpectedLayoutVersion);

public sealed record CreatePartCommand(string Code, string? Description);

public sealed record UpdatePartCommand(string? Description, bool IsActive, long ExpectedVersion);

public sealed record CreateProductGroupCommand(
    Guid VariantId,
    Guid MainPartId,
    string? Name,
    int DisplayOrder);

public sealed record UpdateProductGroupCommand(
    string? Name,
    int DisplayOrder,
    bool IsActive,
    long ExpectedVersion);

public sealed record SetProductGroupPartCommand(
    Guid? ContainerTypeId,
    string RelationType,
    decimal? LotQuantity,
    int DisplayOrder,
    bool IsActive,
    long ExpectedGroupVersion);

public sealed record CreateContainerCommand(
    string Code,
    string Name,
    string Category,
    int? LengthMm,
    int? WidthMm,
    int? HeightMm,
    decimal? CapacityQuantity);

public sealed record UpdateContainerCommand(
    string Name,
    string Category,
    int? LengthMm,
    int? WidthMm,
    int? HeightMm,
    decimal? CapacityQuantity,
    bool IsActive,
    long ExpectedVersion);

public interface IPasswordHashService
{
    string Hash(string password);
}
