using System.Text.Json;

namespace TkgPda.Application;

public sealed class AdminRuleException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class AdminConfigurationService(IAdminConfigurationStore store, IPasswordHashService passwordHasher)
{
    private static readonly HashSet<string> DeviceTypes = ["PDA", "ADMIN_WEB", "SERVICE", "OTHER"];
    private static readonly HashSet<string> SlotRoles = ["MACHINE", "STAGING", "EMPTY_STORAGE", "FULL_STORAGE", "WAREHOUSE", "PICKUP", "DROP", "SHARED", "OTHER"];
    private static readonly HashSet<string> ContainerCategories = ["CONTAINER", "PALLET", "CART", "BOX", "MIX_PALLET", "OTHER"];
    private static readonly HashSet<string> RelationTypes = ["ALT_PART", "RELATED", "CONSUMABLE", "OTHER"];

    public Task<IReadOnlyList<AdminRoleRecord>> ListRolesAsync(CancellationToken cancellationToken = default) =>
        store.ListRolesAsync(cancellationToken);

    public Task<IReadOnlyList<AdminUserRecord>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        store.ListUsersAsync(cancellationToken);

    public Task<AdminUserRecord> CreateUserAsync(CreateUserCommand command, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        var normalized = command with
        {
            Username = Required(command.Username, 100, "username").ToLowerInvariant(),
            FullName = Required(command.FullName, 200, "fullName"),
            EmployeeNo = Optional(command.EmployeeNo, 100),
            Roles = NormalizeRoles(command.Roles)
        };
        ValidatePassword(command.Password);
        return store.CreateUserAsync(normalized, passwordHasher.Hash(command.Password), context, cancellationToken);
    }

    public Task<AdminUserRecord> UpdateUserAsync(Guid userId, UpdateUserCommand command, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.UpdateUserAsync(RequiredId(userId), command with
        {
            FullName = Required(command.FullName, 200, "fullName"),
            EmployeeNo = Optional(command.EmployeeNo, 100)
        }, context, cancellationToken);

    public Task<AdminUserRecord> ResetPasswordAsync(Guid userId, string password, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        return store.ResetPasswordAsync(RequiredId(userId), passwordHasher.Hash(password), RequiredVersion(expectedVersion), context, cancellationToken);
    }

    public Task<AdminUserRecord> SetUserRolesAsync(Guid userId, IReadOnlyCollection<string> roles, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.SetUserRolesAsync(RequiredId(userId), NormalizeRoles(roles), RequiredVersion(expectedVersion), context, cancellationToken);

    public Task<IReadOnlyList<AdminDeviceRecord>> ListDevicesAsync(CancellationToken cancellationToken = default) =>
        store.ListDevicesAsync(cancellationToken);

    public Task<AdminDeviceRecord> CreateDeviceAsync(CreateDeviceCommand command, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        if (command.MachineId.HasValue && !string.Equals(command.Type, "PDA", StringComparison.OrdinalIgnoreCase))
        {
            throw new AdminRuleException("DEVICE_MACHINE_BINDING_INVALID", "Makine bağlantısı yalnızca PDA tipi cihazlar için geçerlidir.");
        }
        return store.CreateDeviceAsync(command with
        {
            Code = NormalizeCode(command.Code, 120, "code"),
            Name = Required(command.Name, 200, "name"),
            Type = Allowed(command.Type, DeviceTypes, "device type"),
            Metadata = ValidObject(command.Metadata, "metadata")
        }, context, cancellationToken);
    }

    public Task<AdminDeviceRecord> UpdateDeviceAsync(Guid deviceId, UpdateDeviceCommand command, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        if (command.MachineId.HasValue && !string.Equals(command.Type, "PDA", StringComparison.OrdinalIgnoreCase))
        {
            throw new AdminRuleException("DEVICE_MACHINE_BINDING_INVALID", "Makine bağlantısı yalnızca PDA tipi cihazlar için geçerlidir.");
        }
        return store.UpdateDeviceAsync(RequiredId(deviceId), command with
        {
            Name = Required(command.Name, 200, "name"),
            Type = Allowed(command.Type, DeviceTypes, "device type"),
            Metadata = ValidObject(command.Metadata, "metadata"),
            ExpectedVersion = RequiredVersion(command.ExpectedVersion)
        }, context, cancellationToken);
    }

    public Task<IReadOnlyList<AdminMachineRecord>> ListMachinesAsync(CancellationToken cancellationToken = default) =>
        store.ListMachinesAsync(cancellationToken);

    public Task<AdminMachineRecord> UpdateMachineAsync(Guid machineId, UpdateMachineCommand command, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.UpdateMachineAsync(RequiredId(machineId), command with
        {
            DisplayName = Required(command.DisplayName, 150, "displayName"),
            ExpectedVersion = RequiredVersion(command.ExpectedVersion)
        }, context, cancellationToken);

    public Task<MachinePermissionRecord> SetMachinePermissionAsync(Guid userId, MachinePermissionRecord permission, long expectedUserVersion, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        if (!permission.CanView && (permission.CanOperate || permission.CanChangeVariant || permission.CanManageSlots))
        {
            throw new AdminRuleException(
                "MACHINE_VIEW_PERMISSION_REQUIRED",
                "Operasyon, varyant veya alan yönetimi yetkisi için Görüntüle yetkisi zorunludur.");
        }

        return store.SetMachinePermissionAsync(
            RequiredId(userId),
            permission with { MachineId = RequiredId(permission.MachineId) },
            RequiredVersion(expectedUserVersion),
            context,
            cancellationToken);
    }

    public Task RemoveMachinePermissionAsync(Guid userId, Guid machineId, long expectedUserVersion, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.RemoveMachinePermissionAsync(RequiredId(userId), RequiredId(machineId), RequiredVersion(expectedUserVersion), context, cancellationToken);

    public Task<IReadOnlyList<AdminVariantRecord>> ListVariantsAsync(Guid? machineId, CancellationToken cancellationToken = default) =>
        store.ListVariantsAsync(machineId, cancellationToken);

    public Task<AdminVariantRecord> CreateVariantAsync(CreateVariantCommand command, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.CreateVariantAsync(command with
        {
            MachineId = RequiredId(command.MachineId),
            Code = NormalizeCode(command.Code, 50, "code"),
            Name = Required(command.Name, 150, "name")
        }, context, cancellationToken);

    public Task<AdminVariantRecord> UpdateVariantAsync(Guid variantId, UpdateVariantCommand command, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.UpdateVariantAsync(RequiredId(variantId), command with
        {
            Name = Required(command.Name, 150, "name"),
            ExpectedVersion = RequiredVersion(command.ExpectedVersion)
        }, context, cancellationToken);

    public Task<AdminVariantRecord> SetDefaultLayoutAsync(Guid variantId, Guid layoutId, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.SetDefaultLayoutAsync(RequiredId(variantId), RequiredId(layoutId), RequiredVersion(expectedVersion), context, cancellationToken);

    public Task<IReadOnlyList<AdminLayoutRecord>> ListLayoutsAsync(Guid? machineId, CancellationToken cancellationToken = default) =>
        store.ListLayoutsAsync(machineId, cancellationToken);

    public Task<AdminLayoutRecord> CreateLayoutAsync(CreateLayoutCommand command, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.CreateLayoutAsync(command with
        {
            MachineId = RequiredId(command.MachineId),
            Code = NormalizeCode(command.Code, 100, "code"),
            Name = Required(command.Name, 200, "name")
        }, context, cancellationToken);

    public Task<AdminLayoutRecord> CreateLayoutVersionAsync(Guid sourceLayoutId, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.CreateLayoutVersionAsync(RequiredId(sourceLayoutId), RequiredVersion(expectedVersion), context, cancellationToken);

    public Task<AdminLayoutRecord> UpdateLayoutAsync(Guid layoutId, UpdateLayoutCommand command, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.UpdateLayoutAsync(RequiredId(layoutId), command with
        {
            Name = Required(command.Name, 200, "name"),
            ExpectedVersion = RequiredVersion(command.ExpectedVersion)
        }, context, cancellationToken);

    public Task<AdminLayoutRecord> PublishLayoutAsync(Guid layoutId, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.PublishLayoutAsync(RequiredId(layoutId), RequiredVersion(expectedVersion), context, cancellationToken);

    public Task<AdminLayoutRecord> ArchiveLayoutAsync(Guid layoutId, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.ArchiveLayoutAsync(RequiredId(layoutId), RequiredVersion(expectedVersion), context, cancellationToken);

    public Task<IReadOnlyList<AdminSlotRecord>> ListSlotsAsync(Guid layoutId, CancellationToken cancellationToken = default) =>
        store.ListSlotsAsync(RequiredId(layoutId), cancellationToken);

    public Task<AdminSlotRecord> CreateSlotAsync(Guid layoutId, CreateSlotCommand command, long expectedLayoutVersion, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        ValidateGeometry(command.X, command.Y, command.Width, command.Height);
        return store.CreateSlotAsync(RequiredId(layoutId), command with
        {
            Code = NormalizeCode(command.Code, 120, "code"),
            DisplayName = Required(command.DisplayName, 200, "displayName"),
            Role = Allowed(command.Role, SlotRoles, "slot role"),
            Metadata = ValidObject(command.Metadata, "metadata")
        }, RequiredVersion(expectedLayoutVersion), context, cancellationToken);
    }

    public Task<AdminSlotRecord> UpdateSlotAsync(Guid layoutId, Guid slotId, UpdateSlotCommand command, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        ValidateGeometry(command.X, command.Y, command.Width, command.Height);
        return store.UpdateSlotAsync(RequiredId(layoutId), RequiredId(slotId), command with
        {
            DisplayName = Required(command.DisplayName, 200, "displayName"),
            Role = Allowed(command.Role, SlotRoles, "slot role"),
            Metadata = ValidObject(command.Metadata, "metadata"),
            ExpectedVersion = RequiredVersion(command.ExpectedVersion),
            ExpectedLayoutVersion = RequiredVersion(command.ExpectedLayoutVersion)
        }, context, cancellationToken);
    }

    /// <summary>Customer UX — Automatic Machine Slot Resolution: curated master.layout_slot_parts CRUD.</summary>
    public Task<IReadOnlyList<AdminSlotPartRecord>> ListSlotPartsAsync(Guid slotId, CancellationToken cancellationToken = default) =>
        store.ListSlotPartsAsync(RequiredId(slotId), cancellationToken);

    public Task AssignSlotPartAsync(Guid slotId, Guid partId, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.AssignSlotPartAsync(RequiredId(slotId), RequiredId(partId), context, cancellationToken);

    public Task RemoveSlotPartAsync(Guid slotId, Guid partId, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.RemoveSlotPartAsync(RequiredId(slotId), RequiredId(partId), context, cancellationToken);

    public Task<IReadOnlyList<AdminPartRecord>> ListPartsAsync(CancellationToken cancellationToken = default) =>
        store.ListPartsAsync(cancellationToken);

    public Task<AdminPartRecord> CreatePartAsync(CreatePartCommand command, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.CreatePartAsync(command with
        {
            Code = NormalizeCode(command.Code, 120, "code"),
            Description = Optional(command.Description, 4000)
        }, context, cancellationToken);

    public Task<AdminPartRecord> UpdatePartAsync(Guid partId, UpdatePartCommand command, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.UpdatePartAsync(RequiredId(partId), command with
        {
            Description = Optional(command.Description, 4000),
            ExpectedVersion = RequiredVersion(command.ExpectedVersion)
        }, context, cancellationToken);

    public Task<IReadOnlyList<AdminProductGroupRecord>> ListProductGroupsAsync(Guid? variantId, CancellationToken cancellationToken = default) =>
        store.ListProductGroupsAsync(variantId, cancellationToken);

    public Task<AdminProductGroupRecord> CreateProductGroupAsync(CreateProductGroupCommand command, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.CreateProductGroupAsync(command with
        {
            VariantId = RequiredId(command.VariantId),
            MainPartId = RequiredId(command.MainPartId),
            Name = Optional(command.Name, 200)
        }, context, cancellationToken);

    public Task<AdminProductGroupRecord> UpdateProductGroupAsync(Guid groupId, UpdateProductGroupCommand command, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.UpdateProductGroupAsync(RequiredId(groupId), command with
        {
            Name = Optional(command.Name, 200),
            ExpectedVersion = RequiredVersion(command.ExpectedVersion)
        }, context, cancellationToken);

    public Task<AdminProductGroupRecord> SetProductGroupPartAsync(Guid groupId, Guid partId, SetProductGroupPartCommand command, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.SetProductGroupPartAsync(RequiredId(groupId), RequiredId(partId), command with
        {
            RelationType = Allowed(command.RelationType, RelationTypes, "relation type"),
            ExpectedGroupVersion = RequiredVersion(command.ExpectedGroupVersion)
        }, context, cancellationToken);

    public Task RemoveProductGroupPartAsync(Guid groupId, Guid partId, long expectedGroupVersion, AdminMutationContext context, CancellationToken cancellationToken = default) =>
        store.RemoveProductGroupPartAsync(RequiredId(groupId), RequiredId(partId), RequiredVersion(expectedGroupVersion), context, cancellationToken);

    public Task<IReadOnlyList<AdminContainerRecord>> ListContainersAsync(CancellationToken cancellationToken = default) =>
        store.ListContainersAsync(cancellationToken);

    public Task<AdminContainerRecord> CreateContainerAsync(CreateContainerCommand command, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        ValidateContainer(command.LengthMm, command.WidthMm, command.HeightMm, command.CapacityQuantity);
        return store.CreateContainerAsync(command with
        {
            Code = NormalizeCode(command.Code, 100, "code"),
            Name = Required(command.Name, 150, "name"),
            Category = Allowed(command.Category, ContainerCategories, "container category")
        }, context, cancellationToken);
    }

    public Task<AdminContainerRecord> UpdateContainerAsync(Guid containerId, UpdateContainerCommand command, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        ValidateContainer(command.LengthMm, command.WidthMm, command.HeightMm, command.CapacityQuantity);
        return store.UpdateContainerAsync(RequiredId(containerId), command with
        {
            Name = Required(command.Name, 150, "name"),
            Category = Allowed(command.Category, ContainerCategories, "container category"),
            ExpectedVersion = RequiredVersion(command.ExpectedVersion)
        }, context, cancellationToken);
    }

    public Task<IReadOnlyList<AdminDataIssueRecord>> ListDataIssuesAsync(CancellationToken cancellationToken = default) =>
        store.ListDataIssuesAsync(cancellationToken);

    public Task<IReadOnlyList<AdminAuditRecord>> ListAuditAsync(
        int limit,
        Guid? actorUserId,
        string? entityType,
        CancellationToken cancellationToken = default,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? action = null,
        Guid? correlationId = null,
        string? entityId = null,
        string? actor = null) =>
        store.ListAuditAsync(
            Math.Clamp(limit, 1, 500),
            actorUserId,
            Optional(entityType, 120),
            cancellationToken,
            from,
            to,
            Optional(action, 120),
            correlationId,
            Optional(entityId, 200),
            Optional(actor, 120));

    private static IReadOnlyCollection<string> NormalizeRoles(IReadOnlyCollection<string> roles)
    {
        if (roles is null || roles.Count == 0)
        {
            throw new AdminRuleException("ROLE_REQUIRED", "At least one role is required.");
        }

        var normalized = roles.Select(role => role.Trim().ToUpperInvariant()).Distinct().Order().ToArray();
        if (normalized.Any(role => !KnownRoleCodes.All.Contains(role, StringComparer.Ordinal)))
        {
            throw new AdminRuleException("UNKNOWN_ROLE", "Only the four seeded system roles may be assigned.");
        }

        return normalized;
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 12 or > 256)
        {
            throw new AdminRuleException("WEAK_PASSWORD", "Passwords must contain between 12 and 256 characters.");
        }
    }

    private static void ValidateGeometry(decimal? x, decimal? y, decimal? width, decimal? height)
    {
        var values = new[] { x, y, width, height };
        if (values.All(value => value is null))
        {
            return;
        }

        if (values.Any(value => value is null) || x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1 || y + height > 1)
        {
            throw new AdminRuleException("INVALID_SLOT_GEOMETRY", "Slot geometry must be fully NULL or form a positive rectangle inside normalized 0.0-1.0 bounds.");
        }
    }

    private static void ValidateContainer(int? length, int? width, int? height, decimal? capacity)
    {
        if (length <= 0 || width <= 0 || height <= 0 || capacity <= 0)
        {
            throw new AdminRuleException("INVALID_CONTAINER_DIMENSIONS", "Known container dimensions and capacity must be positive; unknown values remain NULL.");
        }
    }

    private static JsonElement ValidObject(JsonElement value, string field)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new AdminRuleException("INVALID_JSON_OBJECT", $"{field} must be a JSON object.");
        }

        return value.Clone();
    }

    private static string NormalizeCode(string value, int maxLength, string field) =>
        Required(value, maxLength, field).ToUpperInvariant();

    private static string Allowed(string value, HashSet<string> allowed, string field)
    {
        var normalized = Required(value, 100, field).ToUpperInvariant();
        return allowed.Contains(normalized)
            ? normalized
            : throw new AdminRuleException("INVALID_ENUM_VALUE", $"Unsupported {field} value.");
    }

    private static string Required(string value, int maxLength, string field)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 && normalized.Length <= maxLength
            ? normalized
            : throw new AdminRuleException("INVALID_FIELD", $"{field} is required and must contain at most {maxLength} characters.");
    }

    private static string? Optional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength
            ? normalized
            : throw new AdminRuleException("INVALID_FIELD", $"Optional text must contain at most {maxLength} characters.");
    }

    private static Guid RequiredId(Guid id) => id != Guid.Empty
        ? id
        : throw new AdminRuleException("ID_REQUIRED", "A non-empty entity ID is required.");

    private static long RequiredVersion(long version) => version > 0
        ? version
        : throw new AdminRuleException("CONCURRENCY_TOKEN_REQUIRED", "A positive concurrency token is required.");
}
