using Npgsql;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresRdsIntegrationStore
{
    public async Task<IReadOnlyList<RdsPhysicalDiagnosticRecord>> ListPhysicalDiagnosticsAsync(
        Guid navigationProfileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // The report is read-only and uses the same application policy as allocation.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH endpoints AS (
                SELECT b.business_code, b.endpoint_class, NULL::uuid AS slot_id, 'NOT_TRACKED' AS occupancy,
                       b.rds_worksite_id AS worksite_id,
                       EXISTS (SELECT 1 FROM ops.transport_requests r WHERE r.destination_business_code = b.business_code
                         AND r.status IN ('CREATED','VALIDATING','QUEUED','SENT_TO_RDS','RUNNING')) AS reserved
                FROM integration.profile_shared_endpoint_bindings b WHERE b.navigation_profile_id = @nav
                UNION ALL
                SELECT s.code, s.slot_role, s.id, COALESCE(rs.occupancy_state,'UNKNOWN'),
                       integration.resolve_slot_worksite(s.id,@nav),
                       EXISTS (SELECT 1 FROM ops.transport_requests r WHERE (r.source_slot_id = s.id OR r.destination_slot_id = s.id)
                         AND r.status IN ('CREATED','VALIDATING','QUEUED','SENT_TO_RDS','RUNNING')
                         AND r.machine_slot_reservation_active)
                FROM master.layout_slots s JOIN master.layouts l ON l.id = s.layout_id
                JOIN master.navigation_profiles n ON n.id = @nav AND n.machine_id = l.machine_id
                LEFT JOIN ops.slot_runtime_state rs ON rs.layout_slot_id = s.id
            )
            SELECT e.business_code,e.endpoint_class,e.slot_id,e.occupancy,e.reserved,
                   w.id,w.rds_site_id,w.raw_state_json->>'id',w.raw_state_json->>'siteName',
                   w.rds_type,w.last_filled,w.last_locked,w.last_disabled,w.last_sync_failed,w.last_sync_at
            FROM endpoints e JOIN integration.rds_worksites w ON w.id = e.worksite_id AND w.navigation_profile_id = @nav
            ORDER BY e.business_code,e.endpoint_class,e.slot_id
            """, connection, transaction);
        command.Parameters.AddWithValue("nav", navigationProfileId);
        var result = new List<RdsPhysicalDiagnosticRecord>();
        var policy = physicalPolicy ?? new RdsPhysicalAvailabilityPolicy();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var evidence = new RdsPhysicalEvidence(Int(reader, 9), Int(reader, 10), Int(reader, 11), Int(reader, 12), Int(reader, 13),
                    reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14));
                var occupancy = reader.GetString(3);
                result.Add(new(navigationProfileId, reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2), occupancy == "NOT_TRACKED" ? null : occupancy, reader.GetBoolean(4), reader.GetGuid(5),
                    reader.GetString(6), Text(reader, 7), Text(reader, 8), evidence, policy.State(evidence),
                    occupancy == "NOT_TRACKED" ? "BUSINESS_STATE_NOT_TRACKED" : policy.Consistency(occupancy, evidence)));
            }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
