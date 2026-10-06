namespace TkgPda.Domain;

/// <summary>
/// Alt-part demand is a material request. The operator does not choose a
/// container, a load, or a slot. The server reads the locked machine area and
/// either brings material or removes the pallet that is already there first.
/// </summary>
public static class PartDemandRules
{
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

public sealed record PartDemandPlan(TransportTaskType? Predecessor, TransportTaskType Successor);
