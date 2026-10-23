using System.Text.Json;

namespace TkgPda.Api;

public sealed record AdminLoginResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresAt,
    AuthenticatedUserResponse User,
    string ClientType);

public sealed record CreateAdminUserRequest(
    string Username,
    string Password,
    string FullName,
    string? EmployeeNo,
    IReadOnlyCollection<string> Roles);

public sealed record UpdateAdminUserRequest(string FullName, string? EmployeeNo, bool IsActive);
public sealed record ResetPasswordRequest(string NewPassword);
public sealed record SetRolesRequest(IReadOnlyCollection<string> Roles);

public sealed record CreateAdminDeviceRequest(
    string Code,
    string Name,
    string Type,
    bool IsActive,
    JsonElement Metadata,
    Guid? MachineId = null);

public sealed record UpdateAdminDeviceRequest(
    string Name,
    string Type,
    bool IsActive,
    JsonElement Metadata,
    Guid? MachineId = null);

public sealed record UpdateAdminMachineRequest(string DisplayName, int DisplayOrder, bool IsActive);

public sealed record SetMachinePermissionRequest(
    bool CanView,
    bool CanOperate,
    bool CanChangeVariant,
    bool CanManageSlots);

public sealed record CreateAdminVariantRequest(Guid MachineId, string Code, string Name, int DisplayOrder);
public sealed record UpdateAdminVariantRequest(string Name, int DisplayOrder, bool IsActive);
public sealed record SetDefaultLayoutRequest(Guid LayoutId);

public sealed record CreateAdminLayoutRequest(Guid MachineId, string Code, string Name);
public sealed record UpdateAdminLayoutRequest(string Name);

public sealed record CreateAdminSlotRequest(
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

public sealed record UpdateAdminSlotRequest(
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
    JsonElement Metadata);

public sealed record CreateAdminPartRequest(string Code, string? Description);
public sealed record UpdateAdminPartRequest(string? Description, bool IsActive);

public sealed record CreateAdminProductGroupRequest(
    Guid VariantId,
    Guid MainPartId,
    string? Name,
    int DisplayOrder);

public sealed record UpdateAdminProductGroupRequest(string? Name, int DisplayOrder, bool IsActive);

public sealed record SetAdminProductGroupPartRequest(
    Guid? ContainerTypeId,
    string RelationType,
    decimal? LotQuantity,
    int DisplayOrder,
    bool IsActive);

public sealed record CreateAdminContainerRequest(
    string Code,
    string Name,
    string Category,
    int? LengthMm,
    int? WidthMm,
    int? HeightMm,
    decimal? CapacityQuantity);

public sealed record UpdateAdminContainerRequest(
    string Name,
    string Category,
    int? LengthMm,
    int? WidthMm,
    int? HeightMm,
    decimal? CapacityQuantity,
    bool IsActive);

public sealed record SyncRdsWorkSitesRequest(Guid NavigationProfileId);
public sealed record SetRdsWorkSiteMappingRequest(Guid RdsWorksiteId);
public sealed record ValidateRdsTaskTemplateRequest(Guid TaskTemplateId);
