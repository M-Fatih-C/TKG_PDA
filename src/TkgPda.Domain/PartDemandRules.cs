namespace TkgPda.Domain;

/// <summary>
/// Alt-part demand is a material request. The operator does not choose a
/// container, a load, or a slot. The server reads the locked machine area and
/// either brings material or removes the pallet that is already there first.
/// </summary>
public static class PartDemandRules
{
    /// <summary>
    /// Chooses the machine area for an alt-part demand. Mapping rows only say
    /// a slot can hold the part. <see cref="PartAreaCandidate.CurrentPartId"/>
    /// is the physical identity.
    /// </summary>
    public static PartDemandSelection Select(Guid requestedPartId, IReadOnlyList<PartAreaCandidate> candidates)
    {
        if (requestedPartId == Guid.Empty)
        {
            throw new WorkflowRuleException("ID_REQUIRED", "The requested alt part is required.");
        }

        if (candidates.Count == 0)
        {
            throw new WorkflowRuleException(
                "PART_SLOT_NOT_CONFIGURED",
                "Bu alt parça için makine alanı tanımlı değil.");
        }

        if (candidates.Any(candidate =>
                candidate.Occupancy == OccupancyState.Unknown
                || (candidate.Occupancy == OccupancyState.Occupied && candidate.Load == LoadState.Unknown)))
        {
            throw new WorkflowRuleException(
                "PART_AREA_STATE_UNKNOWN",
                "Alan durumu bilinmiyor. Mevcut palet için boşaltma veya dolu getirme tahmini yapılmadı.");
        }

        var samePart = candidates
            .Where(candidate =>
                candidate.Occupancy == OccupancyState.Occupied
                && candidate.CurrentPartId == requestedPartId)
            .ToArray();
        if (samePart.Length > 1)
        {
            throw new WorkflowRuleException(
                "PART_AREA_AMBIGUOUS",
                "Bu alt parça birden fazla dolu alanda duruyor. Hangisinin kaldırılacağı bilinmiyor.");
        }

        if (samePart.Length == 1)
        {
            var area = samePart[0];
            return new PartDemandSelection(area.SlotId, Decide(area.Occupancy, area.Load));
        }

        var vacant = candidates.FirstOrDefault(candidate =>
            candidate.Occupancy == OccupancyState.Vacant && candidate.Load == LoadState.Unknown);
        if (vacant is not null)
        {
            return new PartDemandSelection(vacant.SlotId, new PartDemandPlan(null, TransportTaskType.BringFull));
        }

        if (candidates.Any(candidate =>
                candidate.Occupancy == OccupancyState.Occupied
                && candidate.CurrentPartId is not null
                && candidate.CurrentPartId != requestedPartId))
        {
            throw new WorkflowRuleException(
                "PART_AREA_OCCUPIED_BY_OTHER_PART",
                "Bu alandaki palet istenen alt parça değil. Farklı palet kaldırılmadı.");
        }

        if (candidates.Any(candidate =>
                candidate.Occupancy == OccupancyState.Occupied && candidate.CurrentPartId is null))
        {
            throw new WorkflowRuleException(
                "PART_AREA_CURRENT_PART_UNKNOWN",
                "Dolu alandaki parçanın kimliği bilinmiyor. Palet kaldırılmadı.");
        }

        throw new WorkflowRuleException(
            "PART_AREA_STATE_CONFLICT",
            "Alan durumu bu alt parça talebi için güvenli bir taşıma seçmeye yetmiyor.");
    }

    public static PartDemandPlan Decide(OccupancyState occupancy, LoadState load)
    {
        if (occupancy == OccupancyState.Unknown
            || (occupancy == OccupancyState.Occupied && load == LoadState.Unknown))
        {
            throw new WorkflowRuleException(
                "PART_AREA_STATE_UNKNOWN",
                "Alan durumu bilinmiyor. Mevcut palet için boşaltma veya dolu getirme tahmini yapılmadı.");
        }

        if (occupancy == OccupancyState.Vacant && load == LoadState.Unknown)
        {
            return new PartDemandPlan(null, TransportTaskType.BringFull);
        }

        if (occupancy == OccupancyState.Occupied && load == LoadState.Full)
        {
            return new PartDemandPlan(TransportTaskType.RemoveFull, TransportTaskType.BringFull);
        }

        if (occupancy == OccupancyState.Occupied && load == LoadState.Empty)
        {
            return new PartDemandPlan(TransportTaskType.RemoveEmpty, TransportTaskType.BringFull);
        }

        throw new WorkflowRuleException(
            "PART_AREA_STATE_CONFLICT",
            "Alan durumu bu alt parça talebi için güvenli bir taşıma seçmeye yetmiyor.");
    }
}

public sealed record PartAreaCandidate(
    Guid SlotId,
    OccupancyState Occupancy,
    LoadState Load,
    Guid? CurrentPartId);

public sealed record PartDemandSelection(Guid SlotId, PartDemandPlan Plan);

public sealed record PartDemandPlan(TransportTaskType? Predecessor, TransportTaskType Successor);
