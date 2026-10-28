using Npgsql;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresTransportWorkflowStore
{
    private async Task<string?> SlotPhysicalBlockAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, Guid slotId, Guid? navigationId, string occupancy,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT w.rds_type, w.last_filled, w.last_locked, w.last_disabled,
                   w.last_sync_failed, w.last_sync_at
            FROM integration.rds_worksites w
            WHERE w.id = integration.resolve_slot_worksite(@slot, @nav)
              AND (@nav::uuid IS NULL OR w.navigation_profile_id = @nav)
            {(transaction is null ? "" : "FOR SHARE OF w")}
            """, connection, transaction);
        command.Parameters.AddWithValue("slot", slotId);
        AddNullableUuid(command, "nav", navigationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        // Preserve the established unmapped/noncommissioned bookkeeping path.
        // Actual dispatch still requires an explicit physical binding.
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var evidence = new RdsPhysicalEvidence(
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
        if (_physicalPolicy.Consistency(occupancy, evidence) == "PHYSICAL_STATE_MISMATCH")
            return "RDS_WORKSITE_OCCUPANCY_CONFLICT";
        return RdsPhysicalAvailabilityPolicy.ErrorCode(_physicalPolicy.State(evidence))
            ?? (_physicalPolicy.Consistency(occupancy, evidence) == "PHYSICAL_STATE_UNKNOWN"
                ? "RDS_WORKSITE_STATE_UNKNOWN" : null);
    }

    private async Task RequireSlotPhysicalConsistencyAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, SlotSnapshot slot, Guid? navigationId, CancellationToken cancellationToken)
    {
        var code = await SlotPhysicalBlockAsync(connection, transaction, slot.Id, navigationId,
            slot.State.Occupancy.ToDatabase(), cancellationToken);
        if (code is not null) throw new WorkflowRuleException(code,
            "TKG konumu ile fiziksel RDS kanıtını kontrol edin. Alanı doğrulayıp RDS senkronundan sonra yeniden deneyin.");
    }
}
