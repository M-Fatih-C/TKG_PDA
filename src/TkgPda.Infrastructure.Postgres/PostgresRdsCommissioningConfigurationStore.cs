using Npgsql;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed class PostgresRdsCommissioningConfigurationStore(NpgsqlDataSource dataSource)
    : IRdsCommissioningConfigurationStore
{
    public async Task<RdsCommissioningConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT s.id, l.id, l.machine_id, m.code, l.code, s.code,
                   s.slot_role, s.is_enabled, rw.rds_site_id
            FROM master.layout_slots s
            JOIN master.layouts l ON l.id = s.layout_id
            JOIN master.machines m ON m.id = l.machine_id
            -- Context-free inventory stays on the legacy one-row mapping.
            -- Planned names and profile-scoped live mappings are resolved only
            -- when a navigation profile is known.
            LEFT JOIN integration.layout_slot_rds_mapping map ON map.layout_slot_id = s.id
            LEFT JOIN integration.rds_worksites rw ON rw.id = map.rds_worksite_id
            ORDER BY m.code, l.code, l.version, s.code, s.id
            """;

        var slots = new List<RdsCommissioningSlotConfiguration>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                slots.Add(new(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetBoolean(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        var templates = await new PostgresRdsExecutionStore(dataSource)
            .ListTaskTemplatesAsync(cancellationToken);

        // There is no confirmed schema source for either contract yet. Empty
        // collections deliberately produce NOT_EVALUATED findings.
        return new(slots, templates, [], []);
    }
}
