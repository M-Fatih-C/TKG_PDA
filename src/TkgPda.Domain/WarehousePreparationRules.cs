namespace TkgPda.Domain;

/// <summary>
/// Physical pallet content, not the product for which an empty container will
/// later be used. An empty pallet has no material PartId. No quantity is inferred.
/// </summary>
public sealed record WarehousePalletContent(Guid? ContainerTypeId, LoadState Load, Guid? PartId);

/// <summary>
/// Evidence to be loaded by the application from a persisted inbound demand,
/// never accepted as authoritative client input. This is not a new transport
/// operation and does not make REMOVE_FULL an inbound material request.
/// </summary>
public sealed record WarehousePreparationDemand(
    Guid RequestId, Guid MachineId, Guid PartId, Guid? ContainerTypeId,
    LoadState RequestedLoad, TransportRequestStatus Status, TransportReadinessState Readiness);

/// <summary>
/// Pure rules for the approved four shared, single-pallet preparation positions.
/// These checks do not allocate, persist, release a position or dispatch a robot.
/// A future store must load authoritative evidence and run checks and writes in
/// one transaction; passing these checks alone is not a concurrency guarantee.
/// </summary>
public static class WarehousePreparationRules
{
    // Explicit customer-approved logical capacity, not RDS IDs or coordinates.
    public const int AreaCount = 4;

    public static void EnsureValidAreaNumber(int areaNumber)
    {
        if (areaNumber is < 1 or > AreaCount)
            throw new WorkflowRuleException("PREPARATION_AREA_INVALID", "Geçerli bir hazırlık alanı seçilmelidir.");
    }

    public static void EnsureAreaCanReceivePallet(int areaNumber, OccupancyState occupancy, bool hasActiveAllocation)
    {
        EnsureValidAreaNumber(areaNumber);
        if (occupancy != OccupancyState.Vacant)
            throw new WorkflowRuleException("PREPARATION_AREA_NOT_VACANT", "Hazırlık alanının boş olduğu doğrulanmalıdır.");
        if (hasActiveAllocation)
            throw new WorkflowRuleException("PREPARATION_AREA_ALLOCATED", "Hazırlık alanı başka bir hazırlık için ayrılmış.");
    }

    public static void EnsurePalletMatchesDemand(WarehousePalletContent pallet, WarehousePreparationDemand? demand)
    {
        ArgumentNullException.ThrowIfNull(pallet);
        if (pallet.Load is not (LoadState.Empty or LoadState.Full))
            throw new WorkflowRuleException("PREPARATION_LOAD_UNKNOWN", "Hazırlanan paletin boş veya dolu olduğu bilinmelidir.");
        if (pallet.Load == LoadState.Full)
        {
            if (pallet.ContainerTypeId is not { } fullContainerTypeId || fullContainerTypeId == Guid.Empty)
                throw new WorkflowRuleException("PREPARATION_CONTAINER_REQUIRED", "Dolu paletteki taşıma kabı tipi bilinmelidir.");
            if (pallet.PartId is not { } partId || partId == Guid.Empty)
                throw new WorkflowRuleException("PREPARATION_PART_REQUIRED", "Dolu paletteki parça bilinmelidir.");
            if (demand is null)
                throw new WorkflowRuleException("PREPARATION_DEMAND_REQUIRED", "Yarı mamul yalnız gelen talep için hazırlanabilir.");
        }
        else if (pallet.PartId is not null)
        {
            throw new WorkflowRuleException("EMPTY_PALLET_HAS_MATERIAL", "Boş palet içinde malzeme varmış gibi kaydedilemez.");
        }

        if (demand is null)
        {
            if (pallet.ContainerTypeId is not { } emptyContainerTypeId || emptyContainerTypeId == Guid.Empty)
                throw new WorkflowRuleException("PREPARATION_CONTAINER_REQUIRED", "Talebe bağlı olmayan boş paletin taşıma kabı tipi bilinmelidir.");
            return; // Unassigned EMPTY stock is explicitly allowed when its type is known.
        }

        RequireIdentity(demand.RequestId, demand.MachineId, demand.PartId);
        if (demand.RequestedLoad != pallet.Load)
            throw new WorkflowRuleException("PREPARATION_LOAD_MISMATCH", "Hazırlanan içerik talebin boş/dolu gereksinimiyle uyuşmuyor.");
        if (demand.ContainerTypeId != pallet.ContainerTypeId)
            throw new WorkflowRuleException("PREPARATION_CONTAINER_MISMATCH", "Hazırlanan taşıma kabı taleple uyuşmuyor.");
        if (pallet.Load == LoadState.Full && demand.PartId != pallet.PartId)
            throw new WorkflowRuleException("PREPARATION_PART_MISMATCH", "Hazırlanan parça taleple uyuşmuyor.");
        EnsureDemandAwaitingPreparation(demand);
    }

    public static void EnsureCanMarkReady(OccupancyState occupancy, WarehousePalletContent pallet,
        WarehousePreparationDemand? demand)
    {
        if (occupancy != OccupancyState.Occupied)
            throw new WorkflowRuleException("PREPARATION_PALLET_NOT_PLACED", "Hazır onayı için paletin alana yerleştirildiği doğrulanmalıdır.");
        if (demand is null)
            throw new WorkflowRuleException("PREPARATION_DEMAND_REQUIRED", "Taşımaya hazır onayı için palet bir taleple eşleştirilmelidir.");
        EnsurePalletMatchesDemand(pallet, demand);
        // Do not change occupancy or request execution state here. A prepared
        // pallet remains in its area until physical collection is verified.
    }

    public static void EnsureMachineCanReceiveFilledPallet(SlotState machineSlot, bool hasActiveReservation)
    {
        ArgumentNullException.ThrowIfNull(machineSlot);
        if (machineSlot.Occupancy != OccupancyState.Vacant || machineSlot.Load != LoadState.Unknown
            || machineSlot.CurrentPartId is not null || machineSlot.CurrentContainerTypeId is not null)
            throw new WorkflowRuleException("FILLED_SUPPLY_POSITION_NOT_VACANT", "Dolu kasa talebinden önce ilgili konumdaki kasa kaldırılmış olmalıdır.");
        if (hasActiveReservation)
            throw new WorkflowRuleException("FILLED_SUPPLY_POSITION_RESERVED", "İlgili konumda devam eden bir taşıma var.");
    }

    private static void EnsureDemandAwaitingPreparation(WarehousePreparationDemand demand)
    {
        if (demand.Status is not (TransportRequestStatus.Created or TransportRequestStatus.Validating or TransportRequestStatus.Queued))
            throw new WorkflowRuleException("PREPARATION_REQUEST_NOT_WAITING", "Bu talep için yeni hazırlık veya hazır onayı yapılamaz.");
        if (demand.Readiness != TransportReadinessState.WaitingPreparation)
            throw new WorkflowRuleException("PREPARATION_READINESS_CONFLICT", "Talep hazırlık beklemiyor; güncel durumu yenileyin.");
    }

    private static void RequireIdentity(params Guid[] ids)
    {
        if (ids.Any(id => id == Guid.Empty))
            throw new WorkflowRuleException("PREPARATION_IDENTITY_REQUIRED", "Talep, makine, parça ve taşıma kabı kimlikleri geçerli olmalıdır.");
    }
}
