using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresRdsIntegrationStore(NpgsqlDataSource dataSource) : IRdsIntegrationStore
{
    public async Task<IReadOnlyList<RdsNavigationProfileRecord>> ListNavigationProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT n.id, n.machine_id, m.code, n.code, n.name, n.version, n.status, n.robot_map_name
            FROM master.navigation_profiles n
            JOIN master.machines m ON m.id = n.machine_id
            ORDER BY m.display_order, m.code, n.code, n.version DESC
            """);
        var result = new List<RdsNavigationProfileRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return result;
    }

    public async Task<(int Created, int Updated)> SyncWorkSitesAsync(
        Guid navigationProfileId,
        IReadOnlyList<RdsWorkSiteSnapshot> workSites,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken = default)
    {
        var normalized = workSites
            .GroupBy(static site => site.SiteId, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .OrderBy(static site => site.SiteId, StringComparer.Ordinal)
            .ToArray();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var profile = new NpgsqlCommand(
            "SELECT 1 FROM master.navigation_profiles WHERE id = @id FOR SHARE",
            connection,
            transaction))
        {
            profile.Parameters.AddWithValue("id", navigationProfileId);
            if (await profile.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new RdsIntegrationRuleException("NAVIGATION_PROFILE_NOT_FOUND", "The selected navigation profile does not exist.");
            }
        }

        var existing = new HashSet<string>(StringComparer.Ordinal);
        await using (var query = new NpgsqlCommand(
            "SELECT rds_site_id FROM integration.rds_worksites WHERE navigation_profile_id = @profile_id FOR UPDATE",
            connection,
            transaction))
        {
            query.Parameters.AddWithValue("profile_id", navigationProfileId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(0));
        }

        foreach (var site in normalized)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO integration.rds_worksites (
                    navigation_profile_id, rds_site_id, area, group_name, rds_type,
                    last_filled, last_locked, last_disabled, last_content, last_agv_id,
                    last_sync_failed, last_sync_at, raw_state_json)
                VALUES (
                    @profile_id, @site_id, @area, @group_name, @type,
                    @filled, @locked, @disabled, @content, @agv_id,
                    @sync_failed, @synced_at, @raw)
                ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE
                SET area = EXCLUDED.area,
                    group_name = EXCLUDED.group_name,
                    rds_type = EXCLUDED.rds_type,
                    last_filled = EXCLUDED.last_filled,
                    last_locked = EXCLUDED.last_locked,
                    last_disabled = EXCLUDED.last_disabled,
                    last_content = EXCLUDED.last_content,
                    last_agv_id = EXCLUDED.last_agv_id,
                    last_sync_failed = EXCLUDED.last_sync_failed,
                    last_sync_at = EXCLUDED.last_sync_at,
                    raw_state_json = EXCLUDED.raw_state_json
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("profile_id", navigationProfileId);
            command.Parameters.AddWithValue("site_id", site.SiteId);
            AddText(command, "area", site.Area);
            AddText(command, "group_name", site.GroupName);
            AddInt(command, "type", site.Type);
            AddInt(command, "filled", site.Filled);
            AddInt(command, "locked", site.Locked);
            AddInt(command, "disabled", site.Disabled);
            AddText(command, "content", site.Content);
            AddText(command, "agv_id", site.AgvId);
            AddInt(command, "sync_failed", site.SyncFailed);
            command.Parameters.AddWithValue("synced_at", syncedAt);
            command.Parameters.Add("raw", NpgsqlDbType.Jsonb).Value = site.RawState.GetRawText();
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var updated = normalized.Count(site => existing.Contains(site.SiteId));
        return (normalized.Length - updated, updated);
    }

    public async Task<IReadOnlyList<RdsMirroredWorkSiteRecord>> ListWorkSitesAsync(
        RdsWorkSiteFilter filter,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, navigation_profile_id, rds_site_id, area, group_name, rds_type,
                   last_filled, last_locked, last_disabled, last_sync_failed,
                   last_content, last_agv_id, last_sync_at,
                   (rds_type = 1 AND last_sync_at IS NOT NULL AND coalesce(last_disabled, 0) <> 1
                    AND coalesce(last_sync_failed, 0) <> 1 AND coalesce(last_locked, 0) <> 1)
            FROM integration.rds_worksites
            WHERE navigation_profile_id = @profile_id
              AND (@site_id::text IS NULL OR rds_site_id ILIKE '%' || @site_id || '%')
              AND (@area::text IS NULL OR area ILIKE '%' || @area || '%')
              AND (@group_name::text IS NULL OR group_name ILIKE '%' || @group_name || '%')
              AND (@type::integer IS NULL OR rds_type = @type)
              AND (@filled::integer IS NULL OR last_filled = @filled)
              AND (@locked::integer IS NULL OR last_locked = @locked)
              AND (@disabled::integer IS NULL OR last_disabled = @disabled)
              AND (@sync_failed::integer IS NULL OR last_sync_failed = @sync_failed)
            ORDER BY rds_site_id
            """);
        command.Parameters.AddWithValue("profile_id", filter.NavigationProfileId);
        AddText(command, "site_id", Normalize(filter.SiteId));
        AddText(command, "area", Normalize(filter.Area));
        AddText(command, "group_name", Normalize(filter.GroupName));
        AddInt(command, "type", filter.Type);
        AddInt(command, "filled", filter.Filled);
        AddInt(command, "locked", filter.Locked);
        AddInt(command, "disabled", filter.Disabled);
        AddInt(command, "sync_failed", filter.SyncFailed);

        var result = new List<RdsMirroredWorkSiteRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), Text(reader, 3), Text(reader, 4),
                Int(reader, 5), Int(reader, 6), Int(reader, 7), Int(reader, 8), Int(reader, 9),
                Text(reader, 10), Text(reader, 11), Timestamp(reader, 12), reader.GetBoolean(13)));
        }

        return result;
    }

    public async Task<IReadOnlyList<RdsSlotMappingRecord>> ListSlotMappingsAsync(Guid? layoutId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT s.id, s.layout_id, s.code, s.display_name, s.slot_role, l.machine_id,
                   rw.id, rw.rds_site_id, rw.navigation_profile_id, rw.rds_type,
                   rw.last_filled, rw.last_locked, rw.last_disabled, rw.last_sync_failed, rw.last_sync_at,
                   coalesce(rw.rds_type = 1 AND rw.last_sync_at IS NOT NULL AND coalesce(rw.last_disabled, 0) <> 1
                     AND coalesce(rw.last_sync_failed, 0) <> 1 AND coalesce(rw.last_locked, 0) <> 1, false)
            FROM master.layout_slots s
            JOIN master.layouts l ON l.id = s.layout_id
            LEFT JOIN integration.layout_slot_rds_mapping map ON map.layout_slot_id = s.id
            LEFT JOIN integration.rds_worksites rw ON rw.id = map.rds_worksite_id
            WHERE (@layout_id::uuid IS NULL OR s.layout_id = @layout_id)
            ORDER BY l.code, l.version, s.display_order, s.code
            """);
        AddUuid(command, "layout_id", layoutId);
        var result = new List<RdsSlotMappingRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadMapping(reader));
        return result;
    }

    public async Task<RdsSlotMappingRecord> SetSlotMappingAsync(
        Guid slotId,
        Guid rdsWorkSiteId,
        AdminMutationContext context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var slot = await ReadSlotForUpdateAsync(connection, transaction, slotId, cancellationToken)
            ?? throw new RdsIntegrationRuleException("LAYOUT_SLOT_NOT_FOUND", "The layout slot does not exist.");
        if (slot.Role == "MACHINE")
        {
            throw new RdsIntegrationRuleException("MACHINE_REGION_MAPPING_FORBIDDEN", "Machine regions cannot be mapped as RDS transport WorkSites.");
        }

        var workSite = await ReadWorkSiteForUpdateAsync(connection, transaction, rdsWorkSiteId, cancellationToken)
            ?? throw new RdsIntegrationRuleException("RDS_WORKSITE_NOT_FOUND", "The mirrored RDS WorkSite does not exist.");
        if (workSite.Type != 1)
        {
            throw new RdsIntegrationRuleException("RDS_WORKSITE_NOT_PHYSICAL", "Only physical RDS WorkSites can be mapped to logistics slots.");
        }

        if (workSite.MachineId != slot.MachineId)
        {
            throw new RdsIntegrationRuleException("RDS_WORKSITE_MACHINE_CONFLICT", "The RDS WorkSite navigation profile belongs to another machine.");
        }

        var before = await ReadExistingMappingAsync(connection, transaction, slotId, cancellationToken);
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO integration.layout_slot_rds_mapping (layout_slot_id, rds_worksite_id)
            VALUES (@slot_id, @worksite_id)
            ON CONFLICT (layout_slot_id) DO UPDATE
            SET rds_worksite_id = EXCLUDED.rds_worksite_id, created_at = now()
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("slot_id", slotId);
            command.Parameters.AddWithValue("worksite_id", rdsWorkSiteId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            context,
            "RDS_WORKSITE_MAPPING_SET",
            slotId,
            before,
            new { slotId, rdsWorkSiteId, rdsSiteId = workSite.SiteId },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var mappings = await ListSlotMappingsAsync(slot.LayoutId, cancellationToken);
        return mappings.Single(item => item.SlotId == slotId);
    }

    public async Task RemoveSlotMappingAsync(Guid slotId, AdminMutationContext context, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        _ = await ReadSlotForUpdateAsync(connection, transaction, slotId, cancellationToken)
            ?? throw new RdsIntegrationRuleException("LAYOUT_SLOT_NOT_FOUND", "The layout slot does not exist.");
        var before = await ReadExistingMappingAsync(connection, transaction, slotId, cancellationToken);
        if (before is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using (var command = new NpgsqlCommand(
            "DELETE FROM integration.layout_slot_rds_mapping WHERE layout_slot_id = @slot_id",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("slot_id", slotId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, context, "RDS_WORKSITE_MAPPING_REMOVED", slotId, before, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<SlotIdentity?> ReadSlotForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid slotId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT s.layout_id, s.slot_role, l.machine_id
            FROM master.layout_slots s JOIN master.layouts l ON l.id = s.layout_id
            WHERE s.id = @id FOR UPDATE OF s
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", slotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2))
            : null;
    }

    private static async Task<WorkSiteIdentity?> ReadWorkSiteForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT rw.rds_site_id, rw.rds_type, n.machine_id
            FROM integration.rds_worksites rw
            JOIN master.navigation_profiles n ON n.id = rw.navigation_profile_id
            WHERE rw.id = @id FOR UPDATE OF rw
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), Int(reader, 1), reader.GetGuid(2))
            : null;
    }

    private static async Task<object?> ReadExistingMappingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid slotId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT map.rds_worksite_id, rw.rds_site_id
            FROM integration.layout_slot_rds_mapping map
            JOIN integration.rds_worksites rw ON rw.id = map.rds_worksite_id
            WHERE map.layout_slot_id = @slot_id
            FOR UPDATE OF map
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("slot_id", slotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new { slotId, rdsWorkSiteId = reader.GetGuid(0), rdsSiteId = reader.GetString(1) }
            : null;
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AdminMutationContext context,
        string action,
        Guid slotId,
        object? before,
        object? after,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_logs (
                actor_user_id, action, entity_type, entity_id, before_json, after_json,
                ip_address, user_agent, correlation_id)
            VALUES (@actor, @action, 'LAYOUT_SLOT_RDS_MAPPING', @entity_id, @before, @after,
                    CAST(@ip AS inet), @agent, @correlation)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("actor", context.ActorUserId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entity_id", slotId.ToString());
        AddJson(command, "before", before);
        AddJson(command, "after", after);
        AddText(command, "ip", context.IpAddress);
        AddText(command, "agent", context.UserAgent);
        command.Parameters.AddWithValue("correlation", context.CorrelationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static RdsSlotMappingRecord ReadMapping(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetGuid(5),
        Uuid(reader, 6), Text(reader, 7), Uuid(reader, 8), Int(reader, 9), Int(reader, 10), Int(reader, 11), Int(reader, 12),
        Int(reader, 13), Timestamp(reader, 14), reader.GetBoolean(15));

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? Int(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static Guid? Uuid(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    private static DateTimeOffset? Timestamp(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    private static void AddText(NpgsqlCommand command, string name, string? value) => command.Parameters.Add(name, NpgsqlDbType.Text).Value = value is null ? DBNull.Value : value;
    private static void AddInt(NpgsqlCommand command, string name, int? value) => command.Parameters.Add(name, NpgsqlDbType.Integer).Value = value.HasValue ? value.Value : DBNull.Value;
    private static void AddUuid(NpgsqlCommand command, string name, Guid? value) => command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value.HasValue ? value.Value : DBNull.Value;
    private static void AddJson(NpgsqlCommand command, string name, object? value) => command.Parameters.Add(name, NpgsqlDbType.Jsonb).Value = value is null ? DBNull.Value : JsonSerializer.Serialize(value);

    public async Task<IReadOnlyList<RdsActiveLockFingerprint>> GetActiveTkgLockFingerprintsAsync(
        CancellationToken cancellationToken = default)
    {
        // Bounded on both time and row count. An unreleased lock record is only
        // meaningful for correlating a lock that is still held right now; without a
        // bound this scan grows with the audit table on a polled Admin path.
        // Correlation itself is decided by the captured fingerprint, never by the
        // mere existence of a historical row.
        const string sql =
            """
            SELECT a.after_json
            FROM audit.audit_logs a
            WHERE a.entity_type = 'TRANSPORT_REQUEST'
              AND a.action = 'MAP_PREP_LOCK_ACQUIRED'
              AND a.created_at >= now() - @lookback
              AND NOT EXISTS (
                  SELECT 1 FROM audit.audit_logs r
                  WHERE r.entity_type = 'TRANSPORT_REQUEST'
                    AND r.entity_id = a.entity_id
                    AND r.action = 'MAP_PREP_LOCK_RELEASED'
                    AND r.created_at >= a.created_at
              )
            ORDER BY a.created_at DESC
            LIMIT @max_rows;
            """;
        var list = new List<RdsActiveLockFingerprint>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add("lookback", NpgsqlDbType.Interval).Value = ActiveLockLookback;
        command.Parameters.AddWithValue("max_rows", ActiveLockMaxRows);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0)) continue;
            var jsonStr = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(jsonStr)) continue;
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;
                var vehicle = ReadString(root, "vehicle");
                var requiredMap = ReadString(root, "requiredMap");
                list.Add(new(vehicle, requiredMap, ReadCapturedLockInfo(root)));
            }
            catch (JsonException)
            {
                // A malformed audit payload yields no fingerprint, so it can never
                // correlate. Skipping it keeps the fail-closed posture.
            }
        }
        return list;
    }

    /// <summary>How far back an unreleased map-prep lock record stays correlatable.</summary>
    private static readonly TimeSpan ActiveLockLookback = TimeSpan.FromDays(7);

    private const int ActiveLockMaxRows = 200;

    /// <summary>
    /// Rebuilds the verbatim SEER fingerprint captured when TKG acquired the lock.
    /// Returns null when no fingerprint was captured (for example the post-lock
    /// telemetry query failed), which makes the record uncorrelatable by design.
    /// </summary>
    private static RdsCoreRobotLockInfo? ReadCapturedLockInfo(JsonElement root)
    {
        if (!root.TryGetProperty("lockInfo", out var li) || li.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var locked = li.TryGetProperty("locked", out var lockedValue)
            && lockedValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            && lockedValue.GetBoolean();

        return new RdsCoreRobotLockInfo(
            locked,
            ReadString(li, "ip"),
            ReadString(li, "nickName") ?? ReadString(li, "nick_name"),
            ReadInt(li, "port"),
            ReadString(li, "timeT") ?? ReadString(li, "time_t"),
            ReadInt(li, "type"),
            ReadString(li, "desc"));
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private sealed record SlotIdentity(Guid LayoutId, string Role, Guid MachineId);
    private sealed record WorkSiteIdentity(string SiteId, int? Type, Guid MachineId);
}
