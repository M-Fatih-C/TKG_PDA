using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

public sealed class PostgresApiDataStore(NpgsqlDataSource dataSource) : IApiDataStore, IOperatorTransportQueueStore
{
    private const string AuthUserSelect =
        """
        SELECT u.id,
               u.username,
               u.password_hash,
               u.full_name,
               u.employee_no,
               u.is_active,
               COALESCE(
                   array_agg(r.code ORDER BY r.code) FILTER (WHERE r.id IS NOT NULL),
                   ARRAY[]::varchar[])
        FROM auth.users u
        LEFT JOIN auth.user_roles ur ON ur.user_id = u.id
        LEFT JOIN auth.roles r ON r.id = ur.role_id AND r.is_active
        """;

    public Task<AuthUserRecord?> FindUserByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default) =>
        FindAuthUserAsync(
            "WHERE u.username = @username",
            "username",
            username,
            cancellationToken);

    public Task<AuthUserRecord?> FindUserByEmployeeNumberAsync(
        string normalizedEmployeeNumber,
        CancellationToken cancellationToken = default) =>
        FindAuthUserAsync(
            "WHERE u.employee_no IS NOT NULL AND BTRIM(u.employee_no) <> '' AND UPPER(BTRIM(u.employee_no)) = @employee_no",
            "employee_no",
            normalizedEmployeeNumber,
            cancellationToken);

    private async Task<AuthUserRecord?> FindAuthUserAsync(
        string whereClause,
        string parameterName,
        string parameterValue,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"{AuthUserSelect}\n{whereClause}\nGROUP BY u.id");
        command.Parameters.AddWithValue(parameterName, parameterValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AuthUserRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetBoolean(5),
            reader.GetFieldValue<string[]>(6));
    }

    public async Task<DeviceRecord?> FindDeviceAsync(
        string deviceCode,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, device_code, name, device_type, is_active, machine_id FROM audit.client_devices WHERE device_code = @device_code");
        command.Parameters.AddWithValue("device_code", deviceCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DeviceRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5))
            : null;
    }

    public async Task<MachineSummaryRecord?> FindMachineSummaryAsync(
        Guid machineId,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT m.id,
                   m.code,
                   m.display_name,
                   m.display_order
            FROM master.machines m
            WHERE m.id = @machine_id AND m.is_active
            """);
        command.Parameters.AddWithValue("machine_id", machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MachineSummaryRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            new MachinePermissionRecord(machineId, CanView: true, CanOperate: true, CanChangeVariant: false, CanManageSlots: false));
    }

    public async Task MarkLoginSucceededAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE auth.users SET last_login_at = now(), updated_at = now() WHERE id = @user_id AND is_active");
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MachinePermissionRecord?> FindMachinePermissionAsync(
        Guid userId,
        Guid machineId,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT machine_id, can_view, can_operate, can_change_variant, can_manage_slots
            FROM auth.user_machine_permissions
            WHERE user_id = @user_id AND machine_id = @machine_id
            """);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("machine_id", machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPermission(reader)
            : null;
    }

    public async Task<IReadOnlyList<MachineSummaryRecord>> ListVisibleMachinesAsync(
        Guid userId,
        bool administrator,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT m.id,
                   m.code,
                   m.display_name,
                   m.display_order,
                   CASE WHEN @administrator THEN true ELSE COALESCE(p.can_view, false) END,
                   CASE WHEN @administrator THEN true ELSE COALESCE(p.can_operate, false) END,
                   CASE WHEN @administrator THEN true ELSE COALESCE(p.can_change_variant, false) END,
                   CASE WHEN @administrator THEN true ELSE COALESCE(p.can_manage_slots, false) END
            FROM master.machines m
            LEFT JOIN auth.user_machine_permissions p
              ON p.machine_id = m.id AND p.user_id = @user_id
            WHERE m.is_active
              AND (@administrator OR COALESCE(p.can_view, false))
            ORDER BY m.display_order, m.code
            """);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("administrator", administrator);
        var result = new List<MachineSummaryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MachineSummaryRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                new MachinePermissionRecord(
                    reader.GetGuid(0),
                    reader.GetBoolean(4),
                    reader.GetBoolean(5),
                    reader.GetBoolean(6),
                    reader.GetBoolean(7))));
        }

        return result;
    }

    public async Task<IReadOnlyList<WarehousePreparationRequestRecord>> ListWarehousePreparationRequestsAsync(
        Guid userId,
        bool administrator,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT r.id, r.request_no, tt.code,
                   m.id, m.code, m.display_name,
                   p.part_code, ct.code,
                   src.id, src.code, src.display_name,
                   dst.id, dst.code, dst.display_name,
                   r.created_at, r.queued_at
            FROM ops.transport_requests r
            JOIN master.machines m ON m.id = r.machine_id
            JOIN master.task_types tt ON tt.id = r.task_type_id
            LEFT JOIN master.parts p ON p.id = r.part_id
            LEFT JOIN master.container_types ct ON ct.id = r.container_type_id
            LEFT JOIN master.layout_slots src ON src.id = r.source_slot_id
            LEFT JOIN master.layout_slots dst ON dst.id = r.destination_slot_id
            LEFT JOIN auth.user_machine_permissions perm
              ON perm.machine_id = r.machine_id AND perm.user_id = @user_id
            WHERE r.readiness_state = 'WAITING_PREPARATION'
              AND (@administrator OR COALESCE(perm.can_operate, false))
            ORDER BY r.created_at, r.request_no
            """);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("administrator", administrator);
        var result = new List<WarehousePreparationRequestRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WarehousePreparationRequestRecord(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                NullableString(reader, 6),
                NullableString(reader, 7),
                NullableGuid(reader, 8),
                NullableString(reader, 9),
                NullableString(reader, 10),
                NullableGuid(reader, 11),
                NullableString(reader, 12),
                NullableString(reader, 13),
                reader.GetFieldValue<DateTimeOffset>(14),
                NullableDateTimeOffset(reader, 15)));
        }

        return result;
    }

    public async Task<IReadOnlyList<CompatibleVariantRecord>> ListCompatibleVariantsAsync(
        Guid machineId,
        Guid mainPartId,
        Guid partId,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT variant_id, code, name, machine_id, product_group_id, relation_type
            FROM (
                SELECT DISTINCT ON (v.id)
                       v.id AS variant_id,
                       v.code,
                       v.name,
                       v.machine_id,
                       v.display_order,
                       pg.id AS product_group_id,
                       CASE WHEN pg.main_part_id = @part_id THEN 'MAIN' ELSE gpp.relation_type END AS relation_type
                FROM master.variants v
                JOIN master.product_groups pg ON pg.variant_id = v.id AND pg.is_active
                LEFT JOIN master.product_group_parts gpp
                       ON gpp.product_group_id = pg.id AND gpp.part_id = @part_id AND gpp.is_active
                WHERE v.machine_id = @machine_id
                  AND v.is_active
                  AND pg.main_part_id = @main_part_id
                  AND (pg.main_part_id = @part_id OR gpp.part_id = @part_id)
                ORDER BY v.id, (pg.main_part_id = @part_id) DESC, pg.display_order
            ) resolved
            ORDER BY display_order, code
            """);
        command.Parameters.AddWithValue("machine_id", machineId);
        command.Parameters.AddWithValue("main_part_id", mainPartId);
        command.Parameters.AddWithValue("part_id", partId);
        var result = new List<CompatibleVariantRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CompatibleVariantRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.GetString(5)));
        }

        return result;
    }

    public async Task<IReadOnlyList<MachineMainPartOptionRecord>> ListMachinePartOptionsAsync(
        Guid machineId,
        CancellationToken cancellationToken = default)
    {
        var mainParts = new List<(Guid PartId, string PartCode, string? Description)>();
        await using (var command = dataSource.CreateCommand(
                         """
                         SELECT DISTINCT p.id, p.part_code, p.description
                         FROM master.product_groups pg
                         JOIN master.variants v ON v.id = pg.variant_id AND v.is_active
                         JOIN master.parts p ON p.id = pg.main_part_id AND p.is_active
                         WHERE v.machine_id = @machine_id AND pg.is_active
                         ORDER BY p.part_code
                         """))
        {
            command.Parameters.AddWithValue("machine_id", machineId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                mainParts.Add((reader.GetGuid(0), reader.GetString(1), NullableString(reader, 2)));
            }
        }

        if (mainParts.Count == 0)
        {
            return [];
        }

        var relatedByMainPart = mainParts.ToDictionary(
            static main => main.PartId,
            static _ => new List<MachineRelatedPartRecord>());
        await using (var command = dataSource.CreateCommand(
                         """
                         SELECT main_part_id, part_id, part_code, description, relation_type
                         FROM (
                             SELECT DISTINCT ON (pg.main_part_id, gpp.part_id)
                                    pg.main_part_id,
                                    gpp.part_id,
                                    p2.part_code,
                                    p2.description,
                                    gpp.relation_type,
                                    gpp.display_order
                             FROM master.product_group_parts gpp
                             JOIN master.product_groups pg ON pg.id = gpp.product_group_id AND pg.is_active
                             JOIN master.variants v ON v.id = pg.variant_id AND v.is_active
                             JOIN master.parts p2 ON p2.id = gpp.part_id AND p2.is_active
                             WHERE v.machine_id = @machine_id AND gpp.is_active
                             ORDER BY pg.main_part_id, gpp.part_id, gpp.relation_type, gpp.display_order
                         ) resolved
                         ORDER BY main_part_id, relation_type, part_code
                         """))
        {
            command.Parameters.AddWithValue("machine_id", machineId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var mainPartId = reader.GetGuid(0);
                if (relatedByMainPart.TryGetValue(mainPartId, out var related))
                {
                    related.Add(new MachineRelatedPartRecord(
                        reader.GetGuid(1),
                        reader.GetString(2),
                        NullableString(reader, 3),
                        reader.GetString(4)));
                }
            }
        }

        return mainParts
            .Select(main => new MachineMainPartOptionRecord(
                main.PartId,
                main.PartCode,
                main.Description,
                relatedByMainPart[main.PartId]))
            .ToArray();
    }

    public async Task<OperationalContextDataRecord?> GetOperationalContextDataAsync(
        Guid userId,
        Guid machineId,
        bool administrator,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        MachineSummaryRecord machine;
        Guid? activeVariantId;
        string? activeVariantCode;
        string? activeVariantName;
        Guid? activeLayoutId;
        string? activeLayoutCode;
        string? activeLayoutName;
        Guid? activeNavigationId;
        string? activeNavigationCode;
        long runtimeRowVersion;
        DateTimeOffset? runtimeUpdatedAt;

        await using (var command = new NpgsqlCommand(
                         """
                         SELECT m.id, m.code, m.display_name, m.display_order,
                                CASE WHEN @administrator THEN true ELSE COALESCE(p.can_view, false) END,
                                CASE WHEN @administrator THEN true ELSE COALESCE(p.can_operate, false) END,
                                CASE WHEN @administrator THEN true ELSE COALESCE(p.can_change_variant, false) END,
                                CASE WHEN @administrator THEN true ELSE COALESCE(p.can_manage_slots, false) END,
                                rs.active_variant_id, v.code, v.name,
                                rs.active_layout_id, l.code, l.name,
                                rs.active_navigation_id, n.code,
                                COALESCE(rs.row_version, 0), rs.updated_at
                         FROM master.machines m
                         LEFT JOIN auth.user_machine_permissions p
                           ON p.machine_id = m.id AND p.user_id = @user_id
                         LEFT JOIN ops.machine_runtime_state rs ON rs.machine_id = m.id
                         LEFT JOIN master.variants v ON v.id = rs.active_variant_id
                         LEFT JOIN master.layouts l ON l.id = rs.active_layout_id
                         LEFT JOIN master.navigation_profiles n ON n.id = rs.active_navigation_id
                         WHERE m.id = @machine_id AND m.is_active
                         """,
                         connection))
        {
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("administrator", administrator);
            command.Parameters.AddWithValue("machine_id", machineId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            machine = new MachineSummaryRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                new MachinePermissionRecord(
                    reader.GetGuid(0),
                    reader.GetBoolean(4),
                    reader.GetBoolean(5),
                    reader.GetBoolean(6),
                    reader.GetBoolean(7)));
            activeVariantId = NullableGuid(reader, 8);
            activeVariantCode = NullableString(reader, 9);
            activeVariantName = NullableString(reader, 10);
            activeLayoutId = NullableGuid(reader, 11);
            activeLayoutCode = NullableString(reader, 12);
            activeLayoutName = NullableString(reader, 13);
            activeNavigationId = NullableGuid(reader, 14);
            activeNavigationCode = NullableString(reader, 15);
            runtimeRowVersion = reader.GetInt64(16);
            runtimeUpdatedAt = reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17);
        }

        var variants = new List<VariantLayoutOptionRecord>();
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT v.id, v.code, v.name,
                                l.id, l.code, l.name, a.is_default
                         FROM master.variants v
                         JOIN master.variant_layout_assignments a
                           ON a.variant_id = v.id AND a.machine_id = v.machine_id
                         JOIN master.layouts l
                           ON l.id = a.layout_id AND l.machine_id = a.machine_id
                         WHERE v.machine_id = @machine_id
                           AND v.is_active
                           AND l.status = 'PUBLISHED'
                         ORDER BY v.display_order, v.code, a.is_default DESC, l.code
                         """,
                         connection))
        {
            command.Parameters.AddWithValue("machine_id", machineId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                variants.Add(new VariantLayoutOptionRecord(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetGuid(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetBoolean(6)));
            }
        }

        var slots = new List<SlotContextDataRecord>();
        if (activeLayoutId.HasValue)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT s.id, s.code, s.display_name, s.slot_role, s.is_enabled,
                       s.default_container_type_id, dct.code,
                       s.ui_x, s.ui_y, s.ui_width, s.ui_height, s.display_order,
                       COALESCE(rs.occupancy_state, 'UNKNOWN'),
                       COALESCE(rs.load_state, 'UNKNOWN'),
                       rs.current_part_id, p.part_code,
                       rs.current_container_type_id, cct.code,
                       COALESCE(rs.row_version, 0), rs.updated_at,
                       rw.id, rw.rds_site_id, rw.area, rw.group_name, rw.rds_type,
                       rw.last_filled, rw.last_locked, rw.last_disabled,
                       rw.last_sync_failed, rw.last_content, rw.last_agv_id, rw.last_sync_at
                FROM master.layout_slots s
                LEFT JOIN master.container_types dct ON dct.id = s.default_container_type_id
                LEFT JOIN ops.slot_runtime_state rs ON rs.layout_slot_id = s.id
                LEFT JOIN master.parts p ON p.id = rs.current_part_id
                LEFT JOIN master.container_types cct ON cct.id = rs.current_container_type_id
                LEFT JOIN integration.layout_slot_rds_mapping rmap ON rmap.layout_slot_id = s.id
                LEFT JOIN integration.rds_worksites rw ON rw.id = rmap.rds_worksite_id
                WHERE s.layout_id = @layout_id
                ORDER BY s.display_order, s.code
                """,
                connection);
            command.Parameters.AddWithValue("layout_id", activeLayoutId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var role = reader.GetString(3);
                var enabled = reader.GetBoolean(4);
                var rdsWorkSite = reader.IsDBNull(20)
                    ? new RdsWorkSiteProjectionRecord(false, null, null, null, null, null, null, null, null, null, null, null)
                    : new RdsWorkSiteProjectionRecord(
                        true,
                        NullableString(reader, 21),
                        NullableString(reader, 22),
                        NullableString(reader, 23),
                        NullableInt32(reader, 24),
                        NullableInt32(reader, 25),
                        NullableInt32(reader, 26),
                        NullableInt32(reader, 27),
                        NullableInt32(reader, 28),
                        NullableString(reader, 29),
                        NullableString(reader, 30),
                        NullableDateTimeOffset(reader, 31));
                slots.Add(new SlotContextDataRecord(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    role,
                    enabled,
                    enabled && role != "MACHINE",
                    NullableGuid(reader, 5),
                    NullableString(reader, 6),
                    NullableDecimal(reader, 7),
                    NullableDecimal(reader, 8),
                    NullableDecimal(reader, 9),
                    NullableDecimal(reader, 10),
                    reader.GetInt32(11),
                    reader.GetString(12),
                    reader.GetString(13),
                    NullableGuid(reader, 14),
                    NullableString(reader, 15),
                    NullableGuid(reader, 16),
                    NullableString(reader, 17),
                    reader.GetInt64(18),
                    NullableDateTimeOffset(reader, 19),
                    rdsWorkSite));
            }
        }

        var productGroups = activeVariantId.HasValue
            ? await ReadProductGroupsAsync(connection, activeVariantId.Value, cancellationToken)
            : [];
        var activeRequests = await ReadActiveRequestsAsync(connection, machineId, userId, cancellationToken);
        var taskTemplates = activeVariantId.HasValue
            ? await ReadTaskTemplatesAsync(connection, activeVariantId.Value, cancellationToken)
            : [];

        return new OperationalContextDataRecord(
            machine,
            activeVariantId,
            activeVariantCode,
            activeVariantName,
            activeLayoutId,
            activeLayoutCode,
            activeLayoutName,
            activeNavigationId,
            activeNavigationCode,
            runtimeRowVersion,
            runtimeUpdatedAt,
            variants,
            slots,
            productGroups,
            activeRequests,
            taskTemplates);
    }

    private static async Task<IReadOnlyList<PdaProductGroupRecord>> ReadProductGroupsAsync(
        NpgsqlConnection connection,
        Guid activeVariantId,
        CancellationToken cancellationToken)
    {
        var groups = new List<PdaProductGroupRecord>();
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT pg.id, pg.main_part_id, p.part_code, pg.name, pg.display_order
                         FROM master.product_groups pg
                         JOIN master.parts p ON p.id = pg.main_part_id AND p.is_active
                         WHERE pg.variant_id = @variant_id AND pg.is_active
                         ORDER BY pg.display_order, p.part_code
                         """,
                         connection))
        {
            command.Parameters.AddWithValue("variant_id", activeVariantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                groups.Add(new PdaProductGroupRecord(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    NullableString(reader, 3),
                    reader.GetInt32(4),
                    []));
            }
        }

        if (groups.Count == 0)
        {
            return groups;
        }

        var partsByGroup = groups.ToDictionary(static group => group.Id, static _ => new List<PdaProductGroupPartRecord>());
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT gpp.product_group_id, gpp.id, gpp.part_id, p.part_code,
                                gpp.container_type_id, ct.code, gpp.relation_type,
                                gpp.lot_quantity, gpp.display_order
                         FROM master.product_group_parts gpp
                         JOIN master.product_groups pg ON pg.id = gpp.product_group_id
                         JOIN master.parts p ON p.id = gpp.part_id AND p.is_active
                         LEFT JOIN master.container_types ct ON ct.id = gpp.container_type_id
                         WHERE pg.variant_id = @variant_id
                           AND pg.is_active
                           AND gpp.is_active
                         ORDER BY pg.display_order, gpp.display_order, p.part_code
                         """,
                         connection))
        {
            command.Parameters.AddWithValue("variant_id", activeVariantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (partsByGroup.TryGetValue(reader.GetGuid(0), out var parts))
                {
                    parts.Add(new PdaProductGroupPartRecord(
                        reader.GetGuid(1),
                        reader.GetGuid(2),
                        reader.GetString(3),
                        NullableGuid(reader, 4),
                        NullableString(reader, 5),
                        reader.GetString(6),
                        NullableDecimal(reader, 7),
                        reader.GetInt32(8)));
                }
            }
        }

        return groups.Select(group => group with { Parts = partsByGroup[group.Id] }).ToArray();
    }

    private static async Task<IReadOnlyList<ActiveTransportDataRecord>> ReadActiveRequestsAsync(
        NpgsqlConnection connection,
        Guid machineId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var result = new List<ActiveTransportDataRecord>();
        await using var command = new NpgsqlCommand(
            """
            -- WP-07: queue_position is TKG's deterministic ordering of this machine's
            -- active requests (same status order as the global operator queue). It is
            -- not an RDS queue position and says nothing about robot location.
            WITH ranked AS (
                SELECT q.id,
                       (row_number() OVER (
                           ORDER BY CASE q.status
                                        WHEN 'RUNNING' THEN 0
                                        WHEN 'SENT_TO_RDS' THEN 1
                                        WHEN 'QUEUED' THEN 2
                                        ELSE 3 END,
                                    q.created_at, q.request_no))::integer AS queue_position
                FROM ops.transport_requests q
                WHERE q.machine_id = @machine_id
                  AND q.status IN ('CREATED','VALIDATING','QUEUED','SENT_TO_RDS','RUNNING')
            )
            SELECT r.id, r.request_no, tt.code, r.status,
                   r.requested_by, u.username,
                   r.source_slot_id, r.destination_slot_id,
                   r.product_group_id, r.part_id, r.container_type_id,
                   r.priority, r.created_at, r.failure_code, r.failure_message,
                   r.correlation_id,
                   rt.id, rt.rds_task_record_id, rt.rds_task_label,
                   rt.out_order_no, rt.agv_id, rt.rds_status, rt.ended_reason,
                   rt.sent_at, rt.started_at, rt.ended_at, rt.last_sync_at,
                   p.part_code, p.description,
                   CASE
                       WHEN r.part_id IS NULL OR r.product_group_id IS NULL THEN NULL
                       WHEN pg.main_part_id = r.part_id THEN 'MAIN_PART'
                       ELSE pgp.relation_type
                   END AS relation_type,
                   r.readiness_state,
                   ranked.queue_position,
                   GREATEST(0, floor(extract(epoch FROM (clock_timestamp() - r.created_at))))::bigint AS age_seconds,
                   CASE WHEN rt.started_at IS NULL THEN NULL
                        ELSE GREATEST(0, floor(extract(epoch FROM (COALESCE(rt.ended_at, clock_timestamp()) - rt.started_at))))::bigint
                   END AS execution_elapsed_seconds,
                   CASE
                       WHEN EXISTS (
                           SELECT 1
                           FROM ops.transport_request_dependencies d
                           JOIN ops.transport_requests pred ON pred.id = d.predecessor_request_id
                           WHERE d.successor_request_id = r.id
                             AND pred.status <> 'COMPLETED') THEN 'WAITING_PREDECESSOR'
                       WHEN EXISTS (
                           SELECT 1
                           FROM ops.transport_request_dependencies d
                           WHERE d.predecessor_request_id = r.id) THEN 'REMOVING_EXISTING'
                       WHEN r.readiness_state = 'WAITING_PREPARATION'
                            AND EXISTS (
                                SELECT 1 FROM ops.transport_request_dependencies d
                                WHERE d.successor_request_id = r.id) THEN 'PREPARING_SUCCESSOR'
                       WHEN r.readiness_state = 'WAITING_PREPARATION' THEN 'PREPARING'
                       ELSE NULL
                   END AS chain_state
            FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            JOIN ranked ON ranked.id = r.id
            LEFT JOIN auth.users u ON u.id = r.requested_by
            LEFT JOIN master.parts p ON p.id = r.part_id
            LEFT JOIN master.product_groups pg ON pg.id = r.product_group_id
            LEFT JOIN master.product_group_parts pgp
                   ON pgp.product_group_id = r.product_group_id AND pgp.part_id = r.part_id
            LEFT JOIN integration.rds_tasks rt ON rt.transport_request_id = r.id
            WHERE r.machine_id = @machine_id
              AND r.status IN ('CREATED','VALIDATING','QUEUED','SENT_TO_RDS','RUNNING')
            ORDER BY r.created_at DESC, r.request_no DESC
            """,
            connection);
        command.Parameters.AddWithValue("machine_id", machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var requestedBy = NullableGuid(reader, 4);
            var rdsTask = reader.IsDBNull(16)
                ? null
                : new RdsTaskDataRecord(
                    NullableString(reader, 17),
                    NullableString(reader, 18),
                    NullableString(reader, 19),
                    NullableString(reader, 20),
                    NullableInt32(reader, 21),
                    NullableString(reader, 22),
                    NullableDateTimeOffset(reader, 23),
                    NullableDateTimeOffset(reader, 24),
                    NullableDateTimeOffset(reader, 25),
                    NullableDateTimeOffset(reader, 26));
            result.Add(new ActiveTransportDataRecord(
                reader.GetGuid(0),
                reader.GetInt64(1),
                WorkflowCode.ParseTaskType(reader.GetString(2)),
                WorkflowCode.ParseTransportStatus(reader.GetString(3)),
                requestedBy,
                NullableString(reader, 5),
                requestedBy == userId,
                NullableGuid(reader, 6),
                NullableGuid(reader, 7),
                NullableGuid(reader, 8),
                NullableGuid(reader, 9),
                NullableGuid(reader, 10),
                reader.GetInt32(11),
                reader.GetFieldValue<DateTimeOffset>(12),
                NullableString(reader, 13),
                NullableString(reader, 14),
                reader.GetGuid(15),
                rdsTask,
                NullableString(reader, 27),
                NullableString(reader, 28),
                NullableString(reader, 29),
                reader.GetString(30),
                reader.IsDBNull(31) ? null : reader.GetInt32(31),
                reader.GetInt64(32),
                reader.IsDBNull(33) ? null : reader.GetInt64(33),
                NullableString(reader, 34)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<PdaTaskTemplateRecord>> ReadTaskTemplatesAsync(
        NpgsqlConnection connection,
        Guid activeVariantId,
        CancellationToken cancellationToken)
    {
        var result = new List<PdaTaskTemplateRecord>();
        await using var command = new NpgsqlCommand(
            """
            SELECT t.id, t.variant_id, t.product_group_id, t.task_type_id,
                   tt.code, t.source_slot_group_id, t.destination_slot_group_id,
                   t.rds_task_label, t.priority
            FROM master.task_templates t
            JOIN master.task_types tt ON tt.id = t.task_type_id AND tt.is_active
            WHERE t.variant_id = @variant_id AND t.is_active
            ORDER BY t.product_group_id NULLS FIRST, tt.code, t.priority, t.id
            """,
            connection);
        command.Parameters.AddWithValue("variant_id", activeVariantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var label = NullableString(reader, 7);
            result.Add(new PdaTaskTemplateRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                NullableGuid(reader, 2),
                reader.GetGuid(3),
                reader.GetString(4),
                NullableGuid(reader, 5),
                NullableGuid(reader, 6),
                // The label is read only to derive the readiness flag; it is not
                // carried into the PDA contract.
                !string.IsNullOrWhiteSpace(label),
                reader.GetInt32(8)));
        }

        return result;
    }

    public async Task<TransportRequestSnapshot?> FindTransportRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT r.id, r.request_no, r.idempotency_key, r.status, tt.code,
                   r.request_origin, r.requested_by, r.machine_id, r.variant_id,
                   r.layout_id, r.navigation_id, r.product_group_id, r.part_id,
                   r.container_type_id, r.source_slot_id, r.destination_slot_id,
                   r.priority, r.readiness_state, r.ready_at
            FROM ops.transport_requests r
            JOIN master.task_types tt ON tt.id = r.task_type_id
            WHERE r.id = @request_id
            """);
        command.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TransportRequestSnapshot(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetString(2),
                WorkflowCode.ParseTransportStatus(reader.GetString(3)),
                WorkflowCode.ParseTaskType(reader.GetString(4)),
                WorkflowCode.ParseRequestOrigin(reader.GetString(5)),
                NullableGuid(reader, 6),
                reader.GetGuid(7),
                reader.GetGuid(8),
                reader.GetGuid(9),
                NullableGuid(reader, 10),
                NullableGuid(reader, 11),
                NullableGuid(reader, 12),
                NullableGuid(reader, 13),
                NullableGuid(reader, 14),
                NullableGuid(reader, 15),
                reader.GetInt32(16),
                WorkflowCode.ParseReadinessState(reader.GetString(17)),
                reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18))
            : null;
    }

    public async Task WriteAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO audit.audit_logs (
                actor_user_id, device_id, action, entity_type, entity_id,
                before_json, after_json, ip_address, user_agent, correlation_id)
            VALUES (
                @actor_user_id, @device_id, @action, @entity_type, @entity_id,
                @before_json, @after_json, CAST(@ip_address AS inet), @user_agent, @correlation_id)
            """);
        AddNullableUuid(command, "actor_user_id", entry.ActorUserId);
        AddNullableUuid(command, "device_id", entry.DeviceId);
        command.Parameters.AddWithValue("action", entry.Action);
        command.Parameters.AddWithValue("entity_type", entry.EntityType);
        AddNullableText(command, "entity_id", entry.EntityId);
        AddNullableJson(command, "before_json", entry.BeforeJson);
        AddNullableJson(command, "after_json", entry.AfterJson);
        AddNullableText(command, "ip_address", entry.IpAddress);
        AddNullableText(command, "user_agent", entry.UserAgent);
        command.Parameters.AddWithValue("correlation_id", entry.CorrelationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OperatorQueuePageRecord> ListAsync(
        OperatorQueueQuery query,
        CancellationToken cancellationToken = default)
    {
        var terminalPredicate = query.Terminal
            ? "r.status IN ('COMPLETED','FAILED','CANCELLED') AND r.created_at >= @terminal_since"
            : "r.status IN ('CREATED','VALIDATING','QUEUED','SENT_TO_RDS','RUNNING')";
        var ordering = query.Terminal
            ? "r.created_at DESC, r.request_no DESC"
            : "CASE r.status WHEN 'RUNNING' THEN 0 WHEN 'SENT_TO_RDS' THEN 1 WHEN 'QUEUED' THEN 2 ELSE 3 END, r.created_at, r.request_no";
        await using var command = dataSource.CreateCommand(
            $"""
             WITH part_positions AS (
                 SELECT lsp.part_id,
                        ls.layout_id,
                        ls.id AS slot_id,
                        row_number() OVER (
                            PARTITION BY lsp.part_id, ls.layout_id
                            ORDER BY ls.display_order, ls.code, ls.id)::integer AS logical_position
                 FROM master.layout_slot_parts lsp
                 JOIN master.layout_slots ls ON ls.id = lsp.layout_slot_id
                 WHERE ls.is_enabled
                   AND ls.slot_role NOT IN ('MACHINE', 'EMPTY_STORAGE')
             ), selected AS (
                 SELECT r.id AS request_id,
                        r.request_no,
                        m.code AS machine_code,
                        m.display_name AS machine_name,
                        p.part_code,
                        p.description,
                        pp.logical_position,
                        tt.code AS operation,
                        r.created_at,
                        r.status,
                        r.readiness_state,
                        rt.rds_status,
                        rt.agv_id,
                        rt.started_at,
                        rt.last_status_json,
                        row_number() OVER (ORDER BY {ordering})::integer AS queue_position
                 FROM ops.transport_requests r
                 JOIN master.machines m ON m.id = r.machine_id
                 JOIN master.task_types tt ON tt.id = r.task_type_id
                 LEFT JOIN master.parts p ON p.id = r.part_id
                 LEFT JOIN integration.rds_tasks rt ON rt.transport_request_id = r.id
                 LEFT JOIN part_positions pp
                   ON pp.part_id = r.part_id
                  AND pp.layout_id = r.layout_id
                  AND pp.slot_id = CASE WHEN tt.code = 'BRING_EMPTY' THEN r.destination_slot_id ELSE r.source_slot_id END
                 WHERE {terminalPredicate}
             )
             SELECT request_id, request_no, machine_code, machine_name, part_code, description,
                    logical_position, operation, created_at, status, rds_status,
                    agv_id, started_at, last_status_json, queue_position, readiness_state
             FROM selected
             ORDER BY queue_position
             OFFSET @offset LIMIT @take
             """);
        command.Parameters.AddWithValue("offset", query.Offset);
        command.Parameters.AddWithValue("take", query.Limit + 1);
        command.Parameters.AddWithValue("terminal_since", DateTimeOffset.UtcNow.AddHours(-Math.Clamp(query.TerminalMaxAgeHours, 1, 168)));
        var rows = new List<OperatorQueueItemRecord>();
        var eta = new TransportEtaService();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var requestId = reader.GetGuid(0);
            var machineCode = reader.GetString(2);
            var machineName = reader.GetString(3);
            var partCode = NullableString(reader, 4);
            var description = NullableString(reader, 5);
            var logicalPos = NullableInt32(reader, 6);
            var operation = reader.GetString(7);
            var createdAt = reader.GetFieldValue<DateTimeOffset>(8);
            var status = WorkflowCode.ParseTransportStatus(reader.GetString(9));
            var rdsStatus = NullableInt32(reader, 10);
            var agvId = NullableString(reader, 11);
            var startedAt = NullableDateTimeOffset(reader, 12);
            var lastStatusJson = NullableJsonElement(reader, 13);
            var queuePosition = reader.GetInt32(14);
            var readinessState = reader.IsDBNull(15)
                ? TransportReadinessState.NotRequired
                : WorkflowCode.ParseReadinessState(reader.GetString(15));

            var state = QueueState(status, rdsStatus, agvId, readinessState);
            var etaProjection = eta.Project(
                status,
                rdsStatus,
                agvId,
                startedAt,
                queuePosition,
                lastStatusJson,
                readinessState: readinessState);

            var machine = $"{machineCode} · {machineName}";
            var part = string.IsNullOrWhiteSpace(description)
                ? partCode ?? "Parça bilgisi yok"
                : $"{partCode} · {description}";

            rows.Add(new OperatorQueueItemRecord(
                queuePosition,
                machine,
                part,
                logicalPos,
                BusinessAction(operation, readinessState),
                createdAt,
                state,
                etaProjection.Seconds,
                etaProjection.State,
                requestId));
        }

        var hasMore = rows.Count > query.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new OperatorQueuePageRecord(query.Terminal, query.Offset, query.Limit, hasMore, rows);
    }

    private static string BusinessAction(string operation, TransportReadinessState readinessState) => (operation, readinessState) switch
    {
        ("BRING_EMPTY", TransportReadinessState.WaitingPreparation) => "Boş kasa (Depo hazırlığı bekleniyor)",
        ("BRING_EMPTY", _) => "Boş kasa getiriliyor",
        ("BRING_FULL", TransportReadinessState.WaitingPreparation) => "Malzeme (Depo hazırlığı bekleniyor)",
        ("BRING_FULL", _) => "Dolu kasa getiriliyor",
        ("REMOVE_FULL", _) => "Dolu kasa kaldırılıyor",
        ("REMOVE_EMPTY", _) => "Boş kasa kaldırılıyor",
        _ => "Taşıma işlemi"
    };

    private static string QueueState(TransportRequestStatus status, int? rdsStatus, string? agvId, TransportReadinessState readinessState)
    {
        if (status == TransportRequestStatus.Completed || rdsStatus == 1003) return "COMPLETED";
        if (status == TransportRequestStatus.Failed || rdsStatus is 1004 or 1005 or 1006) return "ERROR";
        if (status == TransportRequestStatus.Cancelled) return "CANCELLED";
        if (readinessState == TransportReadinessState.WaitingPreparation) return "WAITING_PREPARATION";
        return status switch
        {
            TransportRequestStatus.Created or TransportRequestStatus.Validating => "REQUESTED",
            TransportRequestStatus.Queued => "QUEUED",
            TransportRequestStatus.SentToRds when !string.IsNullOrWhiteSpace(agvId) => "ROBOT_ASSIGNED",
            TransportRequestStatus.SentToRds => "QUEUED",
            TransportRequestStatus.Running => "IN_PROGRESS",
            TransportRequestStatus.Cancelled => "CANCELLED",
            _ => "REQUESTED"
        };
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            return await command.ExecuteScalarAsync(cancellationToken) is 1;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    private static MachinePermissionRecord ReadPermission(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetBoolean(1),
        reader.GetBoolean(2),
        reader.GetBoolean(3),
        reader.GetBoolean(4));

    private static Guid? NullableGuid(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static decimal? NullableDecimal(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static int? NullableInt32(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static DateTimeOffset? NullableDateTimeOffset(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value.HasValue ? value.Value : DBNull.Value;

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value is null ? DBNull.Value : value;

    private static JsonElement? NullableJsonElement(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var text = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddNullableJson(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Jsonb).Value = value is null ? DBNull.Value : value;
}
