using TkgPda.Domain;

namespace TkgPda.Application;

public static class WarehousePreparationScopes
{
    public const string SemiFinished = "SEMI_FINISHED";
    public const string FinishedGoods = "FINISHED_GOODS";

    public static string Normalize(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        FinishedGoods => FinishedGoods,
        SemiFinished or null or "" => SemiFinished,
        _ => throw new WorkflowRuleException("PREPARATION_SCOPE_INVALID", "Depo hazırlık türü geçerli değil.")
    };
}

/// <summary>
/// A warehouse preparation area as the tablet sees it: the area and whatever
/// pallet currently stands on it. Pallet is null when the area is free.
/// </summary>
public sealed record PreparationAreaRecord(
    Guid Id,
    int AreaNumber,
    string DisplayName,
    bool IsEnabled,
    PreparationPalletRecord? Pallet,
    string WarehouseScope = WarehousePreparationScopes.SemiFinished)
{
    public bool IsFree => Pallet is null;
}

public sealed record PreparationPalletRecord(
    Guid Id,
    Guid? ContainerTypeId,
    string? ContainerCode,
    string Load,
    Guid? PartId,
    string? PartCode,
    Guid? TransportRequestId,
    long? RequestNo,
    string? TargetMachineCode,
    DateTimeOffset PlacedAt,
    Guid PlacedBy,
    DateTimeOffset? ReadyAt,
    long RowVersion,
    string? RequestStatus = null)
{
    /// <summary>PLACED until the operator confirms it, then READY.</summary>
    public string Status => ReadyAt is null ? "PLACED" : "READY";
}

/// <summary>
/// A machine's outstanding request, as the preparation side needs to read it:
/// what to prepare, for which machine, and since when. RequestedAt is the
/// server's own timestamp; a tablet never supplies or corrects it.
/// </summary>
public sealed record WarehouseDemandRecord(
    Guid RequestId,
    long RequestNo,
    Guid MachineId,
    string MachineCode,
    Guid? PartId,
    string? PartCode,
    Guid? ContainerTypeId,
    string? ContainerCode,
    string? ContainerDimensions,
    string Operation,
    string RequestedLoad,
    DateTimeOffset RequestedAt,
    Guid? ClaimedByAreaId,
    int? ClaimedByAreaNumber,
    string? PartDescription = null,
    string? VariantCode = null,
    string? VariantName = null,
    string? ProductGroupName = null,
    string WarehouseScope = WarehousePreparationScopes.SemiFinished);

/// <summary>
/// TransportRequestId may be null only for semi-finished empty stock staged
/// ahead of demand. Finished-goods empty-container preparation and filled
/// material name the exact request they serve from the moment they are placed.
/// </summary>
public sealed record PlacePalletCommand(
    Guid AreaId,
    Guid? ContainerTypeId,
    LoadState Load,
    Guid? PartId,
    Guid? TransportRequestId,
    Guid ActorUserId,
    Guid DeviceId,
    string IdempotencyKey,
    Guid? CorrelationId = null,
    string WarehouseScope = WarehousePreparationScopes.SemiFinished);

/// <summary>
/// ExpectedRowVersion is the version the tablet was showing. A screen left open
/// on a pallet that has since been collected and replaced must not confirm the
/// new one.
/// </summary>
public sealed record MarkPalletReadyCommand(
    Guid PalletId,
    long ExpectedRowVersion,
    Guid ActorUserId,
    Guid DeviceId,
    Guid? CorrelationId = null);

/// <summary>
/// Reason is COLLECTED when the operator confirms the robot physically took the
/// pallet, WITHDRAWN when the preparation is being undone.
/// </summary>
public sealed record ReleaseAreaCommand(
    Guid PalletId,
    long ExpectedRowVersion,
    string Reason,
    Guid ActorUserId,
    Guid DeviceId);

public sealed record PreparationPalletResult(PreparationPalletRecord Pallet, bool WasIdempotentReplay);

/// <summary>
/// Implementations run every check and every write of one call inside a single
/// transaction, taking the preparation-area lock in a fixed order relative to
/// the transport workflow's own locks. The domain validator in
/// <see cref="WarehousePreparationRules"/> is not a concurrency mechanism: the
/// single-pallet-per-area and single-pallet-per-request rules are held by the
/// database's own unique indexes, and a violation surfaces as a rule error
/// rather than a lost write.
///
/// Nothing here dispatches, completes or cancels a transport, and nothing here
/// calls RDS. Marking a pallet ready makes its request eligible for dispatch
/// under the existing gates; it is not a delivery and not a success report.
/// </summary>
public interface IWarehousePreparationStore
{
    Task<IReadOnlyList<PreparationAreaRecord>> ListAreasAsync(
        string warehouseScope = WarehousePreparationScopes.SemiFinished,
        CancellationToken cancellationToken = default);

    /// <summary>Requests still waiting for warehouse preparation, oldest first.</summary>
    Task<IReadOnlyList<WarehouseDemandRecord>> ListDemandsAsync(
        string warehouseScope = WarehousePreparationScopes.SemiFinished,
        CancellationToken cancellationToken = default);

    Task<PreparationPalletResult> PlacePalletAsync(PlacePalletCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the pallet ready and, when it serves a request, moves that
    /// request's readiness to Ready in the same transaction.
    /// </summary>
    Task<PreparationPalletRecord> MarkReadyAsync(MarkPalletReadyCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Frees the area. Physical collection is the operator's statement; a
    /// request reaching a status never frees an area on its own.
    /// </summary>
    Task<PreparationPalletRecord> ReleaseAsync(ReleaseAreaCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Input validation and the rules that do not need stored evidence. Everything
/// that depends on what is actually in the database is re-checked inside the
/// store's transaction against rows it loaded itself, because anything checked
/// here was true only at the moment it was read.
/// </summary>
public sealed class WarehousePreparationService(IWarehousePreparationStore store)
{
    public static readonly string[] ReleaseReasons = ["COLLECTED", "WITHDRAWN"];

    public Task<IReadOnlyList<PreparationAreaRecord>> ListAreasAsync(
        string warehouseScope = WarehousePreparationScopes.SemiFinished,
        CancellationToken cancellationToken = default) =>
        store.ListAreasAsync(WarehousePreparationScopes.Normalize(warehouseScope), cancellationToken);

    public Task<IReadOnlyList<WarehouseDemandRecord>> ListDemandsAsync(
        string warehouseScope = WarehousePreparationScopes.SemiFinished,
        CancellationToken cancellationToken = default) =>
        store.ListDemandsAsync(WarehousePreparationScopes.Normalize(warehouseScope), cancellationToken);

    public Task<PreparationPalletResult> PlacePalletAsync(
        PlacePalletCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireIds(command.AreaId, command.ActorUserId, command.DeviceId);
        RequireIdempotencyKey(command.IdempotencyKey);

        var content = new WarehousePalletContent(command.ContainerTypeId, command.Load, command.PartId);
        var scope = WarehousePreparationScopes.Normalize(command.WarehouseScope);
        switch (command.Load)
        {
            case LoadState.Empty:
                if (command.PartId is not null)
                {
                    throw new WorkflowRuleException(
                        "EMPTY_PALLET_HAS_MATERIAL",
                        "Boş palet içinde malzeme varmış gibi kaydedilemez.");
                }

                if (command.TransportRequestId is not null && scope != WarehousePreparationScopes.FinishedGoods)
                {
                    // Matching empty stock to a request is the ready step's job,
                    // against evidence loaded server-side. Accepting a binding
                    // here would record one nothing has verified.
                    throw new WorkflowRuleException(
                        "EMPTY_PREPARATION_NOT_REQUEST_BOUND",
                        "Boş kap talebe bağlı olarak yerleştirilemez; talep eşleşmesi hazır onayında yapılır.");
                }

                if (scope == WarehousePreparationScopes.FinishedGoods
                    && (command.TransportRequestId is not { } emptyRequestId || emptyRequestId == Guid.Empty))
                {
                    throw new WorkflowRuleException(
                        "PREPARATION_DEMAND_REQUIRED",
                        "Boş kasa yalnız gelen talep için hazırlanabilir.");
                }

                if (scope != WarehousePreparationScopes.FinishedGoods)
                {
                    // Semi-finished empty stock is staged before a demand and
                    // therefore has no request from which to recover identity.
                    WarehousePreparationRules.EnsurePalletMatchesDemand(content, demand: null);
                }

                if (command.ContainerTypeId is { } suppliedEmptyContainerTypeId
                    && suppliedEmptyContainerTypeId == Guid.Empty)
                {
                    throw new WorkflowRuleException(
                        "PREPARATION_CONTAINER_REQUIRED",
                        "Taşıma kabı kimliği geçerli olmalıdır.");
                }

                break;

            case LoadState.Full:
                if (command.ContainerTypeId is not { } fullContainerTypeId || fullContainerTypeId == Guid.Empty)
                {
                    throw new WorkflowRuleException(
                        "PREPARATION_CONTAINER_REQUIRED",
                        "Dolu paletteki taşıma kabı tipi bilinmelidir.");
                }

                // The helper cannot be used here: with no demand loaded it
                // always reports one missing. Only the shape is checked now;
                // the store re-reads the real request and matches part,
                // container and readiness inside its transaction.
                if (command.PartId is not { } partId || partId == Guid.Empty)
                {
                    throw new WorkflowRuleException(
                        "PREPARATION_PART_REQUIRED",
                        "Dolu paletteki parça bilinmelidir.");
                }

                if (command.TransportRequestId is not { } requestId || requestId == Guid.Empty)
                {
                    throw new WorkflowRuleException(
                        "PREPARATION_DEMAND_REQUIRED",
                        "Yarı mamul yalnız gelen talep için hazırlanabilir.");
                }

                break;

            default:
                throw new WorkflowRuleException(
                    "PREPARATION_LOAD_UNKNOWN",
                    "Hazırlanan paletin boş veya dolu olduğu bilinmelidir.");
        }

        return store.PlacePalletAsync(command with { WarehouseScope = scope }, cancellationToken);
    }

    public Task<PreparationPalletRecord> MarkReadyAsync(
        MarkPalletReadyCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireIds(command.PalletId, command.ActorUserId, command.DeviceId);
        RequireRowVersion(command.ExpectedRowVersion);
        return store.MarkReadyAsync(command, cancellationToken);
    }

    public Task<PreparationPalletRecord> ReleaseAsync(
        ReleaseAreaCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireIds(command.PalletId, command.ActorUserId, command.DeviceId);
        RequireRowVersion(command.ExpectedRowVersion);
        if (!ReleaseReasons.Contains(command.Reason, StringComparer.Ordinal))
        {
            throw new WorkflowRuleException(
                "PREPARATION_RELEASE_REASON_INVALID",
                "Alanı boşaltma nedeni COLLECTED veya WITHDRAWN olmalıdır.");
        }

        return store.ReleaseAsync(command, cancellationToken);
    }

    private static void RequireIds(params Guid[] ids)
    {
        if (ids.Any(id => id == Guid.Empty))
        {
            throw new WorkflowRuleException(
                "PREPARATION_IDENTITY_REQUIRED",
                "Alan, palet, kullanıcı ve cihaz kimlikleri geçerli olmalıdır.");
        }
    }

    private static void RequireRowVersion(long rowVersion)
    {
        if (rowVersion < 0)
        {
            throw new WorkflowRuleException(
                "PREPARATION_ROW_VERSION_REQUIRED",
                "Güncel kayıt sürümü gereklidir; ekranı yenileyin.");
        }
    }

    private static void RequireIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new WorkflowRuleException(
                "IDEMPOTENCY_KEY_REQUIRED",
                "Hazırlık kaydı için tekil istek anahtarı gereklidir.");
        }

        if (key.Length > 200)
        {
            throw new WorkflowRuleException("IDEMPOTENCY_KEY_TOO_LONG", "İstek anahtarı 200 karakteri geçemez.");
        }
    }
}
