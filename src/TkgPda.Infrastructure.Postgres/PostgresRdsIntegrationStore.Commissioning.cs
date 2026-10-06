using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresRdsIntegrationStore
{
    private const string CommissioningRegistry = "tkg_rds_variant_map_naming_v1";

    public async Task<RdsProfileSlotMappingRecord> SetProfileSlotMappingAsync(
        Guid slotId,
        Guid navigationProfileId,
        Guid rdsWorkSiteId,
        AdminMutationContext context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var slot = await ReadCommissioningSlotAsync(connection, transaction, slotId, cancellationToken)
            ?? throw new RdsIntegrationRuleException("LAYOUT_SLOT_NOT_FOUND", "The layout slot does not exist.");
        if (slot.Role == "MACHINE")
        {
            throw new RdsIntegrationRuleException("MACHINE_REGION_MAPPING_FORBIDDEN", "A machine display region cannot receive an RDS WorkSite.");
        }

        if (!slot.IsEnabled)
        {
            throw new RdsIntegrationRuleException("SLOT_NOT_TRANSPORT_ELIGIBLE", "Only an enabled transport slot can receive a profile-scoped WorkSite.");
        }

        var profileMachineId = await ReadProfileMachineAsync(connection, transaction, navigationProfileId, cancellationToken)
            ?? throw new RdsIntegrationRuleException("NAVIGATION_PROFILE_NOT_FOUND", "The navigation profile does not exist.");
        if (profileMachineId != slot.MachineId)
        {
            throw new RdsIntegrationRuleException("RDS_WORKSITE_MACHINE_CONFLICT", "The navigation profile belongs to another machine.");
        }

        var workSite = await ReadCommissioningWorkSiteAsync(connection, transaction, rdsWorkSiteId, cancellationToken)
            ?? throw new RdsIntegrationRuleException("RDS_WORKSITE_NOT_FOUND", "The mirrored RDS WorkSite does not exist.");
        if (workSite.Type != 1)
        {
            throw new RdsIntegrationRuleException("RDS_WORKSITE_NOT_PHYSICAL", "Only physical RDS WorkSites can be mapped to logistics slots.");
        }

        if (workSite.NavigationProfileId != navigationProfileId)
        {
            throw new RdsIntegrationRuleException("RDS_WORKSITE_PROFILE_CONFLICT", "The RDS WorkSite belongs to another navigation profile.");
        }

        var plannedName = await ReadPlannedSiteIdAsync(connection, transaction, slotId, navigationProfileId, cancellationToken);
        if (plannedName is not null && !string.Equals(plannedName, workSite.SiteId, StringComparison.Ordinal))
        {
            throw new RdsIntegrationRuleException(
                "RDS_WORKSITE_PLAN_NAME_MISMATCH",
                "The live RDS WorkSite name does not exactly match the planned WorkSite name.");
        }

        await using (var command = new NpgsqlCommand(
                         """
                         INSERT INTO integration.layout_slot_rds_profile_mapping
                             (layout_slot_id, navigation_profile_id, rds_worksite_id)
                         VALUES (@slot_id, @navigation_id, @worksite_id)
                         ON CONFLICT (layout_slot_id, navigation_profile_id) DO UPDATE
                         SET rds_worksite_id = EXCLUDED.rds_worksite_id
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("slot_id", slotId);
            command.Parameters.AddWithValue("navigation_id", navigationProfileId);
            command.Parameters.AddWithValue("worksite_id", rdsWorkSiteId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            context,
            "RDS_PROFILE_WORKSITE_MAPPING_SET",
            slotId,
            null,
            new { slotId, navigationProfileId, rdsWorkSiteId, rdsSiteId = workSite.SiteId, plannedRdsSiteId = plannedName },
            cancellationToken,
            "LAYOUT_SLOT_RDS_PROFILE_MAPPING");
        await transaction.CommitAsync(cancellationToken);
        return new RdsProfileSlotMappingRecord(slotId, navigationProfileId, rdsWorkSiteId, workSite.SiteId);
    }

    public async Task<IReadOnlyList<VariantCommissioningReport>> ListVariantCommissioningAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT m.code,
                   v.code,
                   n.config_json ->> 'rdsAreaName',
                   n.robot_map_name,
                   n.code,
                   n.id,
                   n.status,
                   COALESCE(n.config_json ->> 'commissioningState', 'PLANNED'),
                   COALESCE((n.config_json ->> 'geometryVerified')::boolean, false),
                   n.config_json ->> 'layoutBindingState',
                   (SELECT count(*)::int FROM integration.rds_worksite_plans p WHERE p.navigation_profile_id = n.id),
                   (SELECT count(*)::int FROM integration.rds_worksite_plans p
                     WHERE p.navigation_profile_id = n.id AND p.commissioning_state <> 'PLANNED'),
                   (SELECT count(*)::int
                      FROM integration.layout_slot_rds_plan_bindings b
                      JOIN integration.rds_worksite_plans p ON p.id = b.worksite_plan_id
                      JOIN integration.layout_slot_rds_profile_mapping map
                        ON map.layout_slot_id = b.layout_slot_id
                       AND map.navigation_profile_id = b.navigation_profile_id
                      JOIN integration.rds_worksites w ON w.id = map.rds_worksite_id
                     WHERE b.navigation_profile_id = n.id
                       AND p.commissioning_state IN ('VERIFIED', 'COMMISSIONED')
                       AND w.rds_site_id = p.planned_rds_site_id
                       AND w.navigation_profile_id = n.id),
                   (SELECT count(*)::int
                      FROM integration.layout_slot_rds_plan_bindings b
                      JOIN integration.rds_worksite_plans p ON p.id = b.worksite_plan_id
                     WHERE b.navigation_profile_id = n.id
                       AND NOT EXISTS (
                           SELECT 1
                           FROM integration.layout_slot_rds_profile_mapping map
                           JOIN integration.rds_worksites w ON w.id = map.rds_worksite_id
                           WHERE map.layout_slot_id = b.layout_slot_id
                             AND map.navigation_profile_id = b.navigation_profile_id
                             AND w.navigation_profile_id = n.id
                             AND w.rds_site_id = p.planned_rds_site_id
                             AND w.last_sync_at IS NOT NULL
                             AND COALESCE(w.last_disabled, 0) <> 1
                             AND COALESCE(w.last_sync_failed, 0) <> 1)),
                   a.is_default,
                   (SELECT count(*)::int FROM master.task_templates t WHERE t.variant_id = v.id AND t.is_active),
                   (SELECT count(*)::int
                      FROM integration.layout_slot_rds_profile_mapping map
                      JOIN master.layout_slots s ON s.id = map.layout_slot_id
                      JOIN master.layouts l ON l.id = s.layout_id
                     WHERE map.navigation_profile_id = n.id
                       AND l.machine_id <> n.machine_id),
                   (SELECT count(*)::int
                      FROM integration.layout_slot_rds_profile_mapping map
                      JOIN integration.rds_worksites w ON w.id = map.rds_worksite_id
                     WHERE map.navigation_profile_id = n.id
                       AND w.navigation_profile_id IS DISTINCT FROM n.id),
                   (SELECT an.code
                      FROM ops.machine_runtime_state rs
                      LEFT JOIN master.navigation_profiles an ON an.id = rs.active_navigation_id
                     WHERE rs.machine_id = m.id),
                   (SELECT s.status
                      FROM ops.production_sessions s
                     WHERE s.machine_id = m.id AND s.ended_at IS NULL
                     LIMIT 1)
            FROM master.navigation_profiles n
            JOIN master.machines m ON m.id = n.machine_id
            JOIN master.variant_navigation_profile_assignments a
              ON a.navigation_profile_id = n.id AND a.machine_id = n.machine_id
            JOIN master.variants v ON v.id = a.variant_id AND v.machine_id = a.machine_id
            WHERE n.config_json ->> 'commissioningRegistry' = @registry
            ORDER BY m.display_order, m.code, v.code
            """);
        command.Parameters.AddWithValue("registry", CommissioningRegistry);
        var result = new List<VariantCommissioningReport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var layoutBinding = reader.IsDBNull(9) ? null : reader.GetString(9);
            var status = reader.GetString(6);
            var state = reader.GetString(7);
            var missing = reader.GetInt32(13);
            var crossMachine = reader.GetInt32(16);
            var crossProfile = reader.GetInt32(17);
            var ready = status == "PUBLISHED"
                && state == "COMMISSIONED"
                && layoutBinding == "SOURCE_BACKED"
                && missing == 0
                && crossMachine == 0
                && crossProfile == 0;
            result.Add(new VariantCommissioningReport(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetGuid(5),
                status,
                state,
                reader.GetBoolean(8),
                layoutBinding,
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                missing,
                reader.GetBoolean(14),
                ready,
                reader.GetInt32(15),
                crossMachine,
                crossProfile,
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetString(19)));
        }

        return result;
    }

    public async Task<WorkSiteNameDiff> ReadWorkSiteNameDiffAsync(
        string machineCode,
        string variantCode,
        CancellationToken cancellationToken = default)
    {
        var target = await LoadTargetAsync(machineCode, variantCode, cancellationToken);
        var planned = await ReadNamesAsync(
            """
            SELECT planned_rds_site_id
            FROM integration.rds_worksite_plans
            WHERE navigation_profile_id = @profile_id
            """,
            target.ProfileId,
            cancellationToken);
        var actual = await ReadNamesAsync(
            """
            SELECT rds_site_id
            FROM integration.rds_worksites
            WHERE navigation_profile_id = @profile_id
            """,
            target.ProfileId,
            cancellationToken);
        var plannedSet = planned.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var mapped = await ScalarCountAsync(
            """
            SELECT count(*)
            FROM integration.rds_worksite_plans
            WHERE navigation_profile_id = @profile_id
              AND commissioning_state <> 'PLANNED'
            """,
            target.ProfileId,
            cancellationToken);
        return new WorkSiteNameDiff(
            machineCode,
            variantCode,
            target.NavigationCode,
            plannedSet.Count,
            actualSet.Count,
            plannedSet.Intersect(actualSet, StringComparer.Ordinal).Count(),
            mapped,
            plannedSet.Except(actualSet, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            actualSet.Except(plannedSet, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    public Task<CommissioningDecision> VerifyVariantCommissioningAsync(
        string machineCode,
        string variantCode,
        bool apply,
        CancellationToken cancellationToken = default) =>
        DecideAsync(machineCode, variantCode, apply, promote: false, cancellationToken);

    public Task<CommissioningDecision> PromoteVariantCommissioningAsync(
        string machineCode,
        string variantCode,
        bool apply,
        CancellationToken cancellationToken = default) =>
        DecideAsync(machineCode, variantCode, apply, promote: true, cancellationToken);

    private async Task<CommissioningDecision> DecideAsync(
        string machineCode,
        string variantCode,
        bool apply,
        bool promote,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var target = await LoadTargetAsync(connection, transaction, machineCode, variantCode, cancellationToken);
        var blockers = await CollectBlockersAsync(connection, transaction, target, cancellationToken);
        if (promote && target.CommissioningState != "VERIFIED")
        {
            blockers.Add("COMMISSIONING_NOT_VERIFIED");
        }

        var applied = false;
        if (apply && blockers.Count == 0)
        {
            if (promote)
            {
                await PromoteAsync(connection, transaction, target, cancellationToken);
            }
            else if (target.CommissioningState is "PLANNED" or "MAPPED")
            {
                await MarkVerifiedAsync(connection, transaction, target.ProfileId, cancellationToken);
            }

            applied = true;
            target = await LoadTargetAsync(connection, transaction, machineCode, variantCode, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CommissioningDecision(
            applied,
            machineCode,
            variantCode,
            target.NavigationCode,
            target.ProfileStatus,
            target.CommissioningState,
            target.GeometryVerified,
            target.IsDefault,
            blockers);
    }

    private static async Task MarkExactPlanMatchesMappedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid navigationProfileId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE integration.rds_worksite_plans p
            SET commissioning_state = 'MAPPED',
                updated_at = now()
            WHERE p.navigation_profile_id = @profile_id
              AND p.commissioning_state = 'PLANNED'
              AND EXISTS (
                  SELECT 1
                  FROM integration.rds_worksites w
                  WHERE w.navigation_profile_id = p.navigation_profile_id
                    AND w.rds_site_id = p.planned_rds_site_id)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("profile_id", navigationProfileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string>> CollectBlockersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommissioningTarget target,
        CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        var expectedMap = target.NavigationCode.EndsWith("_NAV", StringComparison.Ordinal)
            ? target.NavigationCode[..^4] + "_MAP"
            : null;
        var expectedArea = expectedMap?.EndsWith("_MAP", StringComparison.Ordinal) == true
            ? expectedMap[..^4]
            : null;
        if (!string.Equals(target.RobotMapName, expectedMap, StringComparison.Ordinal)
            || !string.Equals(target.AreaName, expectedArea, StringComparison.Ordinal))
        {
            blockers.Add("MAP_IDENTITY_MISMATCH");
        }

        if (target.LayoutBindingState == "PENDING_SOURCE_LAYOUT")
        {
            blockers.Add("PENDING_SOURCE_LAYOUT");
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT p.planned_rds_site_id,
                   w.rds_site_id,
                   w.navigation_profile_id,
                   w.rds_type,
                   w.last_disabled,
                   w.last_sync_failed,
                   w.last_sync_at,
                   l.machine_id
            FROM integration.layout_slot_rds_plan_bindings b
            JOIN integration.rds_worksite_plans p ON p.id = b.worksite_plan_id
            JOIN master.layout_slots s ON s.id = b.layout_slot_id
            JOIN master.layouts l ON l.id = s.layout_id
            LEFT JOIN integration.layout_slot_rds_profile_mapping map
              ON map.layout_slot_id = b.layout_slot_id
             AND map.navigation_profile_id = b.navigation_profile_id
            LEFT JOIN integration.rds_worksites w ON w.id = map.rds_worksite_id
            WHERE b.navigation_profile_id = @profile_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("profile_id", target.ProfileId);
        var bindings = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bindings++;
            var planned = reader.GetString(0);
            if (reader.GetGuid(7) != target.MachineId)
            {
                AddBlocker(blockers, "CROSS_MACHINE_MAPPING");
            }

            if (reader.IsDBNull(1) || reader.IsDBNull(6))
            {
                AddBlocker(blockers, "BINDING_INCOMPLETE");
                continue;
            }

            if (reader.GetGuid(2) != target.ProfileId)
            {
                AddBlocker(blockers, "CROSS_PROFILE_WORKSITE");
            }

            if (!string.Equals(reader.GetString(1), planned, StringComparison.Ordinal))
            {
                AddBlocker(blockers, "RDS_WORKSITE_PLAN_NAME_MISMATCH");
            }

            if (reader.IsDBNull(3) || reader.GetInt32(3) != 1)
            {
                AddBlocker(blockers, "RDS_WORKSITE_NOT_PHYSICAL");
            }

            if (!reader.IsDBNull(4) && reader.GetInt32(4) == 1)
            {
                AddBlocker(blockers, "WORKSITE_DISABLED");
            }

            if (!reader.IsDBNull(5) && reader.GetInt32(5) == 1)
            {
                AddBlocker(blockers, "WORKSITE_SYNC_FAILED");
            }
        }

        if (target.LayoutBindingState == "SOURCE_BACKED" && bindings == 0)
        {
            AddBlocker(blockers, "BINDING_INCOMPLETE");
        }

        return blockers;
    }

    private static async Task MarkVerifiedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using (var plans = new NpgsqlCommand(
                         """
                         UPDATE integration.rds_worksite_plans p
                         SET commissioning_state = 'VERIFIED', updated_at = now()
                         FROM integration.layout_slot_rds_plan_bindings b
                         WHERE b.worksite_plan_id = p.id
                           AND b.navigation_profile_id = @profile_id
                           AND p.commissioning_state IN ('PLANNED', 'MAPPED')
                         """,
                         connection,
                         transaction))
        {
            plans.Parameters.AddWithValue("profile_id", profileId);
            await plans.ExecuteNonQueryAsync(cancellationToken);
        }

        await SetCommissioningStateAsync(connection, transaction, profileId, "VERIFIED", cancellationToken);
    }

    private static async Task PromoteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommissioningTarget target,
        CancellationToken cancellationToken)
    {
        await using (var plans = new NpgsqlCommand(
                         """
                         UPDATE integration.rds_worksite_plans p
                         SET commissioning_state = 'COMMISSIONED', updated_at = now()
                         FROM integration.layout_slot_rds_plan_bindings b
                         WHERE b.worksite_plan_id = p.id
                           AND b.navigation_profile_id = @profile_id
                           AND p.commissioning_state = 'VERIFIED'
                         """,
                         connection,
                         transaction))
        {
            plans.Parameters.AddWithValue("profile_id", target.ProfileId);
            await plans.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var profile = new NpgsqlCommand(
                         """
                         UPDATE master.navigation_profiles
                         SET status = 'PUBLISHED',
                             published_at = COALESCE(published_at, now()),
                             config_json = jsonb_set(config_json, '{commissioningState}', '"COMMISSIONED"'::jsonb)
                         WHERE id = @profile_id
                         """,
                         connection,
                         transaction))
        {
            profile.Parameters.AddWithValue("profile_id", target.ProfileId);
            await profile.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clear = new NpgsqlCommand(
                         """
                         UPDATE master.variant_navigation_profile_assignments
                         SET is_default = false
                         WHERE variant_id = @variant_id
                           AND navigation_profile_id <> @profile_id
                           AND is_default
                         """,
                         connection,
                         transaction))
        {
            clear.Parameters.AddWithValue("variant_id", target.VariantId);
            clear.Parameters.AddWithValue("profile_id", target.ProfileId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var assign = new NpgsqlCommand(
                         """
                         UPDATE master.variant_navigation_profile_assignments
                         SET is_default = true
                         WHERE variant_id = @variant_id
                           AND navigation_profile_id = @profile_id
                         """,
                         connection,
                         transaction))
        {
            assign.Parameters.AddWithValue("variant_id", target.VariantId);
            assign.Parameters.AddWithValue("profile_id", target.ProfileId);
            await assign.ExecuteNonQueryAsync(cancellationToken);
        }

        await SetCommissioningStateAsync(connection, transaction, target.ProfileId, "COMMISSIONED", cancellationToken);
    }

    private static async Task SetCommissioningStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        string state,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE master.navigation_profiles
            SET config_json = jsonb_set(config_json, '{commissioningState}', to_jsonb(@state::text))
            WHERE id = @profile_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("profile_id", profileId);
        command.Parameters.AddWithValue("state", state);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<CommissioningTarget> LoadTargetAsync(string machineCode, string variantCode, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await LoadTargetAsync(connection, null, machineCode, variantCode, cancellationToken);
    }

    private static async Task<CommissioningTarget> LoadTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string machineCode,
        string variantCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT n.id, v.id, m.id, n.code, n.status,
                   COALESCE(n.config_json ->> 'commissioningState', 'PLANNED'),
                   COALESCE((n.config_json ->> 'geometryVerified')::boolean, false),
                   n.config_json ->> 'layoutBindingState',
                   n.robot_map_name,
                   n.config_json ->> 'rdsAreaName',
                   a.is_default
            FROM master.machines m
            JOIN master.variants v ON v.machine_id = m.id AND v.code = @variant_code
            JOIN master.variant_navigation_profile_assignments a
              ON a.variant_id = v.id AND a.machine_id = m.id
            JOIN master.navigation_profiles n ON n.id = a.navigation_profile_id
            WHERE m.code = @machine_code
              AND n.config_json ->> 'commissioningRegistry' = @registry
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("machine_code", machineCode);
        command.Parameters.AddWithValue("variant_code", variantCode);
        command.Parameters.AddWithValue("registry", CommissioningRegistry);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        CommissioningTarget? target = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (target is not null)
            {
                throw new RdsIntegrationRuleException("NAVIGATION_PROFILE_AMBIGUOUS", "This machine variant has more than one commissioning navigation profile.");
            }

            target = new CommissioningTarget(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetBoolean(10));
        }

        return target ?? throw new RdsIntegrationRuleException("NAVIGATION_PROFILE_NOT_FOUND", "This machine variant has no commissioning navigation profile.");
    }

    private async Task<List<string>> ReadNamesAsync(string sql, Guid profileId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("profile_id", profileId);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<int> ScalarCountAsync(string sql, Guid profileId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("profile_id", profileId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<CommissioningSlot?> ReadCommissioningSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid slotId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT s.slot_role, s.is_enabled, l.machine_id
            FROM master.layout_slots s
            JOIN master.layouts l ON l.id = s.layout_id
            WHERE s.id = @id
            FOR UPDATE OF s
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", slotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CommissioningSlot(reader.GetString(0), reader.GetBoolean(1), reader.GetGuid(2))
            : null;
    }

    private static async Task<Guid?> ReadProfileMachineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT machine_id FROM master.navigation_profiles WHERE id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", profileId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid machineId ? machineId : null;
    }

    private static async Task<CommissioningWorkSite?> ReadCommissioningWorkSiteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT rds_site_id, rds_type, navigation_profile_id
            FROM integration.rds_worksites
            WHERE id = @id
            FOR UPDATE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CommissioningWorkSite(reader.GetString(0), Int(reader, 1), Uuid(reader, 2))
            : null;
    }

    private static async Task<string?> ReadPlannedSiteIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid slotId,
        Guid navigationProfileId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT p.planned_rds_site_id
            FROM integration.layout_slot_rds_plan_bindings b
            JOIN integration.rds_worksite_plans p ON p.id = b.worksite_plan_id
            WHERE b.layout_slot_id = @slot_id
              AND b.navigation_profile_id = @navigation_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("slot_id", slotId);
        command.Parameters.AddWithValue("navigation_id", navigationProfileId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static void AddBlocker(List<string> blockers, string code)
    {
        if (!blockers.Contains(code, StringComparer.Ordinal))
        {
            blockers.Add(code);
        }
    }

    private sealed record CommissioningTarget(
        Guid ProfileId,
        Guid VariantId,
        Guid MachineId,
        string NavigationCode,
        string ProfileStatus,
        string CommissioningState,
        bool GeometryVerified,
        string? LayoutBindingState,
        string? RobotMapName,
        string? AreaName,
        bool IsDefault);

    private sealed record CommissioningSlot(string Role, bool IsEnabled, Guid MachineId);
    private sealed record CommissioningWorkSite(string SiteId, int? Type, Guid? NavigationProfileId);
}
