namespace TkgPda.Application;

public interface IAdminConfigurationStore
{
    Task<IReadOnlyList<AdminRoleRecord>> ListRolesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AdminUserRecord>> ListUsersAsync(CancellationToken cancellationToken = default);
    Task<AdminUserRecord> CreateUserAsync(CreateUserCommand command, string passwordHash, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminUserRecord> UpdateUserAsync(Guid userId, UpdateUserCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminUserRecord> ResetPasswordAsync(Guid userId, string passwordHash, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminUserRecord> SetUserRolesAsync(Guid userId, IReadOnlyCollection<string> roles, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminDeviceRecord>> ListDevicesAsync(CancellationToken cancellationToken = default);
    Task<AdminDeviceRecord> CreateDeviceAsync(CreateDeviceCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminDeviceRecord> UpdateDeviceAsync(Guid deviceId, UpdateDeviceCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminMachineRecord>> ListMachinesAsync(CancellationToken cancellationToken = default);
    Task<AdminMachineRecord> UpdateMachineAsync(Guid machineId, UpdateMachineCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<MachinePermissionRecord> SetMachinePermissionAsync(Guid userId, MachinePermissionRecord permission, long expectedUserVersion, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task RemoveMachinePermissionAsync(Guid userId, Guid machineId, long expectedUserVersion, AdminMutationContext context, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminVariantRecord>> ListVariantsAsync(Guid? machineId, CancellationToken cancellationToken = default);
    Task<AdminVariantRecord> CreateVariantAsync(CreateVariantCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminVariantRecord> UpdateVariantAsync(Guid variantId, UpdateVariantCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminVariantRecord> SetDefaultLayoutAsync(Guid variantId, Guid layoutId, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminLayoutRecord>> ListLayoutsAsync(Guid? machineId, CancellationToken cancellationToken = default);
    Task<AdminLayoutRecord> CreateLayoutAsync(CreateLayoutCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminLayoutRecord> CreateLayoutVersionAsync(Guid sourceLayoutId, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminLayoutRecord> UpdateLayoutAsync(Guid layoutId, UpdateLayoutCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminLayoutRecord> PublishLayoutAsync(Guid layoutId, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminLayoutRecord> ArchiveLayoutAsync(Guid layoutId, long expectedVersion, AdminMutationContext context, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminSlotRecord>> ListSlotsAsync(Guid layoutId, CancellationToken cancellationToken = default);
    Task<AdminSlotRecord> CreateSlotAsync(Guid layoutId, CreateSlotCommand command, long expectedLayoutVersion, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminSlotRecord> UpdateSlotAsync(Guid layoutId, Guid slotId, UpdateSlotCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);

    /// <summary>Customer UX — Automatic Machine Slot Resolution: curated master.layout_slot_parts CRUD.</summary>
    Task<IReadOnlyList<AdminSlotPartRecord>> ListSlotPartsAsync(Guid slotId, CancellationToken cancellationToken = default);
    Task AssignSlotPartAsync(Guid slotId, Guid partId, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task RemoveSlotPartAsync(Guid slotId, Guid partId, AdminMutationContext context, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminPartRecord>> ListPartsAsync(CancellationToken cancellationToken = default);
    Task<AdminPartRecord> CreatePartAsync(CreatePartCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminPartRecord> UpdatePartAsync(Guid partId, UpdatePartCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminProductGroupRecord>> ListProductGroupsAsync(Guid? variantId, CancellationToken cancellationToken = default);
    Task<AdminProductGroupRecord> CreateProductGroupAsync(CreateProductGroupCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminProductGroupRecord> UpdateProductGroupAsync(Guid groupId, UpdateProductGroupCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminProductGroupRecord> SetProductGroupPartAsync(Guid groupId, Guid partId, SetProductGroupPartCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task RemoveProductGroupPartAsync(Guid groupId, Guid partId, long expectedGroupVersion, AdminMutationContext context, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminContainerRecord>> ListContainersAsync(CancellationToken cancellationToken = default);
    Task<AdminContainerRecord> CreateContainerAsync(CreateContainerCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);
    Task<AdminContainerRecord> UpdateContainerAsync(Guid containerId, UpdateContainerCommand command, AdminMutationContext context, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminDataIssueRecord>> ListDataIssuesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AdminAuditRecord>> ListAuditAsync(
        int limit,
        Guid? actorUserId,
        string? entityType,
        CancellationToken cancellationToken = default,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? action = null,
        Guid? correlationId = null,
        string? entityId = null,
        string? actor = null);
}
