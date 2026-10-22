using TkgPda.Domain;

namespace TkgPda.Application;

/// <summary>
/// One container standing in the finished-goods store, as the warehouse tablet
/// reads it: what was delivered, by which transport, from which machine, when.
/// </summary>
public sealed record StoreUnitRecord(
    Guid Id,
    Guid? TransportRequestId,
    long? RequestNo,
    Guid? PartId,
    string? PartCode,
    Guid? ContainerTypeId,
    string? ContainerCode,
    Guid? MachineId,
    string? MachineCode,
    DateTimeOffset DeliveredAt,
    long RowVersion,
    string LoadState = "FULL");

/// <summary>
/// A shared finished-goods position and what currently stands in it.
///
/// Capacity is null until the customer states how many containers fit. A client
/// must then show no fill ratio rather than inventing a denominator, which is
/// why <see cref="FillRatio"/> is null in that case too.
/// </summary>
public sealed record StoreLocationRecord(
    Guid Id,
    string Code,
    string DisplayName,
    int? Capacity,
    bool IsEnabled,
    IReadOnlyList<StoreUnitRecord> Units,
    bool IsReserved = false,
    string? ReservationType = null,
    Guid? ReservationRequestId = null)
{
    public int UnitCount => Units.Count;

    public bool IsEmpty => Units.Count == 0;

    public double? FillRatio =>
        Capacity is { } capacity && capacity > 0 ? UnitCount / (double)capacity : null;

    /// <summary>
    /// True once the location is at or over the capacity the customer stated.
    /// Never true while capacity is unknown.
    /// </summary>
    public bool IsFull => Capacity is { } capacity && UnitCount >= capacity;
}

/// <summary>
/// The operator states that they physically removed one container. Removing the
/// last one leaves the location empty; nothing else empties it.
/// </summary>
public sealed record RemoveStoreUnitCommand(
    Guid UnitId,
    long ExpectedRowVersion,
    Guid ActorUserId,
    Guid DeviceId);

/// <summary>
/// Clears every container standing in one location in a single statement, for
/// the common case of a lorry taking the lot.
/// </summary>
public sealed record ClearStoreLocationCommand(
    Guid LocationId,
    Guid ActorUserId,
    Guid DeviceId);

public sealed record RecordEmptyContainerCommand(
    Guid LocationId,
    Guid ActorUserId,
    Guid DeviceId,
    string IdempotencyKey);

/// <summary>
/// Implementations run each call in one transaction and write the movement
/// history alongside the state change. Nothing here dispatches, completes or
/// cancels a transport, and nothing here calls RDS.
///
/// Deliveries are not written through this interface: a container appears in
/// the store when the transport that carried it completes, inside that
/// completion's own transaction, so the store can never disagree with the
/// transport that filled it.
/// </summary>
public interface IFinishedGoodsStore
{
    Task<IReadOnlyList<StoreLocationRecord>> ListLocationsAsync(CancellationToken cancellationToken = default);

    Task<StoreLocationRecord> RemoveUnitAsync(RemoveStoreUnitCommand command, CancellationToken cancellationToken = default);

    Task<StoreLocationRecord> ClearLocationAsync(ClearStoreLocationCommand command, CancellationToken cancellationToken = default);

    Task<StoreLocationRecord> RecordEmptyContainerAsync(RecordEmptyContainerCommand command, CancellationToken cancellationToken = default);
}

public sealed class FinishedGoodsStoreService(IFinishedGoodsStore store)
{
    public Task<IReadOnlyList<StoreLocationRecord>> ListLocationsAsync(
        CancellationToken cancellationToken = default) => store.ListLocationsAsync(cancellationToken);

    public Task<StoreLocationRecord> RemoveUnitAsync(
        RemoveStoreUnitCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireIds(command.UnitId, command.ActorUserId, command.DeviceId);
        RequireRowVersion(command.ExpectedRowVersion);
        return store.RemoveUnitAsync(command, cancellationToken);
    }

    public Task<StoreLocationRecord> ClearLocationAsync(
        ClearStoreLocationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireIds(command.LocationId, command.ActorUserId, command.DeviceId);
        return store.ClearLocationAsync(command, cancellationToken);
    }

    public Task<StoreLocationRecord> RecordEmptyContainerAsync(
        RecordEmptyContainerCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireIds(command.LocationId, command.ActorUserId, command.DeviceId);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Trim().Length > 200)
        {
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_REQUIRED", "Boş kasa kaydı için bir idempotency anahtarı gerekir.");
        }

        return store.RecordEmptyContainerAsync(command, cancellationToken);
    }

    private static void RequireIds(params Guid[] ids)
    {
        if (ids.Any(id => id == Guid.Empty))
        {
            throw new WorkflowRuleException(
                "STORE_IDENTITY_REQUIRED",
                "Depo konumu, kayıt, kullanıcı ve cihaz kimlikleri geçerli olmalıdır.");
        }
    }

    private static void RequireRowVersion(long rowVersion)
    {
        if (rowVersion < 0)
        {
            throw new WorkflowRuleException(
                "STORE_ROW_VERSION_REQUIRED",
                "Güncel kayıt sürümü gereklidir; ekranı yenileyin.");
        }
    }
}
