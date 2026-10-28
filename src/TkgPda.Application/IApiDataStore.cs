using TkgPda.Domain;

namespace TkgPda.Application;

public interface IApiDataStore
{
    Task<AuthUserRecord?> FindUserByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an operator by their normalized employee / registry number
    /// (see <see cref="EmployeeNumber.NormalizeForLookup"/>). The lookup is
    /// trim + case-insensitive and backed by an index; blank employee numbers
    /// never match.
    /// </summary>
    Task<AuthUserRecord?> FindUserByEmployeeNumberAsync(string normalizedEmployeeNumber, CancellationToken cancellationToken = default);

    Task<DeviceRecord?> FindDeviceAsync(string deviceCode, CancellationToken cancellationToken = default);

    Task MarkLoginSucceededAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<MachinePermissionRecord?> FindMachinePermissionAsync(
        Guid userId,
        Guid machineId,
        CancellationToken cancellationToken = default);

    Task<MachineSummaryRecord?> FindMachineSummaryAsync(
        Guid machineId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MachineSummaryRecord>> ListVisibleMachinesAsync(
        Guid userId,
        bool administrator,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// WP-02: resolves the variants on <paramref name="machineId"/> compatible
    /// with <paramref name="partId"/> inside the explicitly selected
    /// <paramref name="mainPartId"/> product-group context, derived only from existing master data
    /// (master.variants / master.product_groups / master.product_group_parts).
    /// Returns an empty list when the part has no mapping; never infers one.
    /// </summary>
    Task<IReadOnlyList<CompatibleVariantRecord>> ListCompatibleVariantsAsync(
        Guid machineId,
        Guid mainPartId,
        Guid partId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// WP-03A: resolves the machine-level main-part options (each with its
    /// Related/Alt parts), deduplicated across all of the machine's variants,
    /// derived only from existing master data. Never infers a mapping.
    /// </summary>
    Task<IReadOnlyList<MachineMainPartOptionRecord>> ListMachinePartOptionsAsync(
        Guid machineId,
        CancellationToken cancellationToken = default);

    Task<OperationalContextDataRecord?> GetOperationalContextDataAsync(
        Guid userId,
        Guid machineId,
        bool administrator,
        CancellationToken cancellationToken = default);

    Task<TransportRequestSnapshot?> FindTransportRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Customer UI — Warehouse Preparation Console: lists every
    /// WAITING_PREPARATION transport request on a machine visible to
    /// <paramref name="userId"/> (same can_operate visibility the existing
    /// mark-ready action itself requires; administrators see every machine).
    /// Deterministic, oldest first.
    /// </summary>
    Task<IReadOnlyList<WarehousePreparationRequestRecord>> ListWarehousePreparationRequestsAsync(
        Guid userId,
        bool administrator,
        CancellationToken cancellationToken = default);

    Task WriteAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
