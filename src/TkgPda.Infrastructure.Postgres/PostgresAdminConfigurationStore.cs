using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresAdminConfigurationStore(NpgsqlDataSource dataSource) : IAdminConfigurationStore
{
    private const string ActiveStatusesSql = "'CREATED','VALIDATING','QUEUED','SENT_TO_RDS','RUNNING'";

    public async Task<IReadOnlyList<AdminRoleRecord>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<AdminRoleRecord>();
        await using var command = dataSource.CreateCommand(
            "SELECT id, code, name, is_system, is_active FROM auth.roles ORDER BY code");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AdminRoleRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminUserRecord>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var ids = new List<Guid>();
        await using (var command = new NpgsqlCommand("SELECT id FROM auth.users ORDER BY username", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        var result = new List<AdminUserRecord>(ids.Count);
        foreach (var id in ids)
        {
            result.Add(await ReadUserAsync(connection, null, id, cancellationToken));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminDeviceRecord>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<AdminDeviceRecord>();
        await using var command = dataSource.CreateCommand(
            "SELECT id, device_code, name, device_type, is_active, metadata_json::text, (xmin::text)::bigint, machine_id FROM audit.client_devices ORDER BY device_code");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDevice(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminMachineRecord>> ListMachinesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<AdminMachineRecord>();
        await using var command = dataSource.CreateCommand(
            "SELECT id, code, display_name, display_order, is_active, (xmin::text)::bigint FROM master.machines ORDER BY display_order, code");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadMachine(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminVariantRecord>> ListVariantsAsync(Guid? machineId, CancellationToken cancellationToken = default)
    {
        var result = new List<AdminVariantRecord>();
        await using var command = dataSource.CreateCommand(
            """
            SELECT v.id, v.machine_id, v.code, v.name, v.display_order, v.is_active,
                   (SELECT a.layout_id FROM master.variant_layout_assignments a WHERE a.variant_id = v.id AND a.is_default),
                   (v.xmin::text)::bigint
            FROM master.variants v
            WHERE @machine_id IS NULL OR v.machine_id = @machine_id
            ORDER BY v.machine_id, v.display_order, v.code
            """);
        AddNullableUuid(command, "machine_id", machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadVariant(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminLayoutRecord>> ListLayoutsAsync(Guid? machineId, CancellationToken cancellationToken = default)
    {
        var result = new List<AdminLayoutRecord>();
        await using var command = dataSource.CreateCommand(LayoutSelectSql + " WHERE @machine_id IS NULL OR l.machine_id = @machine_id ORDER BY l.machine_id, l.code, l.version");
        AddNullableUuid(command, "machine_id", machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadLayout(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminSlotRecord>> ListSlotsAsync(Guid layoutId, CancellationToken cancellationToken = default)
    {
        var result = new List<AdminSlotRecord>();
        await using var command = dataSource.CreateCommand(SlotSelectSql + " WHERE s.layout_id = @layout_id ORDER BY s.display_order, s.code");
        command.Parameters.AddWithValue("layout_id", layoutId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadSlot(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminPartRecord>> ListPartsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<AdminPartRecord>();
        await using var command = dataSource.CreateCommand(
            "SELECT id, part_code, description, is_active, (xmin::text)::bigint FROM master.parts ORDER BY part_code");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadPart(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminProductGroupRecord>> ListProductGroupsAsync(Guid? variantId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var ids = new List<Guid>();
        await using (var command = new NpgsqlCommand(
                         "SELECT id FROM master.product_groups WHERE @variant_id IS NULL OR variant_id = @variant_id ORDER BY variant_id, display_order, id",
                         connection))
        {
            AddNullableUuid(command, "variant_id", variantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        var result = new List<AdminProductGroupRecord>(ids.Count);
        foreach (var id in ids)
        {
            result.Add(await ReadProductGroupAsync(connection, null, id, cancellationToken));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminContainerRecord>> ListContainersAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<AdminContainerRecord>();
        await using var command = dataSource.CreateCommand(
            "SELECT id, code, name, category, length_mm, width_mm, height_mm, capacity_qty, is_active, (xmin::text)::bigint FROM master.container_types ORDER BY code");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadContainer(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminDataIssueRecord>> ListDataIssuesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<AdminDataIssueRecord>();
        await using var command = dataSource.CreateCommand(
            """
            SELECT issue_kind, severity, entity_type, entity_id, code, message
            FROM (
                SELECT 'PLANNING_SOURCE_NOTE' AS issue_kind, 'MEDIUM' AS severity,
                       'PART_PLANNING_METRIC' AS entity_type, ppm.id::text AS entity_id,
                       p.part_code AS code, ppm.source_note AS message
                FROM master.part_planning_metrics ppm
                JOIN master.parts p ON p.id = ppm.part_id
                WHERE ppm.source_note IS NOT NULL AND btrim(ppm.source_note) <> ''
                UNION ALL
                SELECT 'UNKNOWN_CONTAINER_FOOTPRINT', 'MEDIUM', 'CONTAINER_TYPE', c.id::text, c.code,
                       'Kasa tipinin bir veya daha fazla fiziksel ölçüsü tanımlanmamış.'
                FROM master.container_types c
                WHERE c.length_mm IS NULL OR c.width_mm IS NULL OR c.height_mm IS NULL
                UNION ALL
                SELECT 'UNRESOLVED_SLOT_PART', 'HIGH', 'LAYOUT_SLOT', s.id::text, s.code,
                       'The source layout did not prove a canonical part association.'
                FROM master.layout_slots s
                WHERE COALESCE((s.metadata_json->>'part_association_unresolved')::boolean, false)
                UNION ALL
                SELECT 'UNRESOLVED_SLOT_CONTAINER', 'HIGH', 'LAYOUT_SLOT', s.id::text, s.code,
                       'The source layout did not prove a container association.'
                FROM master.layout_slots s
                WHERE COALESCE((s.metadata_json->>'container_association_unresolved')::boolean, false)
                UNION ALL
                SELECT 'LAYOUT_CONFIDENCE', 'MEDIUM', 'LAYOUT_SLOT', s.id::text, s.code,
                       'Physical-layout extraction confidence is ' || (s.metadata_json->>'confidence') || '.'
                FROM master.layout_slots s
                WHERE s.metadata_json->>'confidence' IN ('MEDIUM','LOW')
                UNION ALL
                SELECT 'ALT_PART_CONTAINER_MISSING', 'HIGH', 'PRODUCT_GROUP_PART', gpp.id::text, p.part_code,
                       'Alt parça taşıma kabı bilgisi yok; talep açılamaz.'
                FROM master.product_group_parts gpp
                JOIN master.parts p ON p.id = gpp.part_id
                WHERE gpp.relation_type = 'ALT_PART' AND gpp.is_active AND gpp.container_type_id IS NULL
            ) issues
            ORDER BY severity DESC, issue_kind, code, entity_id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AdminDataIssueRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        return result;
    }

    public async Task<IReadOnlyList<AdminAuditRecord>> ListAuditAsync(
        int limit,
        Guid? actorUserId,
        string? entityType,
        CancellationToken cancellationToken = default,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? action = null,
        Guid? correlationId = null,
        string? entityId = null,
        string? actor = null)
    {
        var result = new List<AdminAuditRecord>();
        await using var command = dataSource.CreateCommand(
            """
            SELECT a.id, a.actor_user_id, u.username, a.action, a.entity_type, a.entity_id,
                   a.before_json::text, a.after_json::text, host(a.ip_address), a.user_agent,
                   a.correlation_id, a.created_at
            FROM audit.audit_logs a
            LEFT JOIN auth.users u ON u.id = a.actor_user_id
            WHERE (@actor_user_id IS NULL OR a.actor_user_id = @actor_user_id)
              AND (@entity_type IS NULL OR a.entity_type = @entity_type)
              AND (@from IS NULL OR a.created_at >= @from)
              AND (@to IS NULL OR a.created_at < @to)
              AND (@action IS NULL OR a.action = @action)
              AND (@correlation_id IS NULL OR a.correlation_id = @correlation_id)
              AND (@entity_id IS NULL OR a.entity_id = @entity_id)
              AND (@actor IS NULL OR u.username ILIKE @actor OR u.employee_no ILIKE @actor)
            ORDER BY a.created_at DESC, a.id DESC
            LIMIT @limit
            """);
        AddNullableUuid(command, "actor_user_id", actorUserId);
        AddNullableText(command, "entity_type", entityType);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from.HasValue ? from.Value : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to.HasValue ? to.Value : DBNull.Value });
        AddNullableText(command, "action", action);
        AddNullableUuid(command, "correlation_id", correlationId);
        AddNullableText(command, "entity_id", entityId);
        AddNullableText(command, "actor", string.IsNullOrWhiteSpace(actor) ? null : $"%{actor.Trim()}%");
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AdminAuditRecord(
                reader.GetInt64(0), NullableGuid(reader, 1), NullableString(reader, 2), reader.GetString(3), reader.GetString(4), NullableString(reader, 5),
                NullableJson(reader, 6), NullableJson(reader, 7), NullableString(reader, 8), NullableString(reader, 9), NullableGuid(reader, 10), reader.GetFieldValue<DateTimeOffset>(11)));
        }

        return result;
    }

    private static async Task<AdminUserRecord> ReadUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        Guid id;
        string username;
        string fullName;
        string? employeeNo;
        bool isActive;
        DateTimeOffset? lastLogin;
        long version;
        await using (var command = new NpgsqlCommand(
                         "SELECT id, username, full_name, employee_no, is_active, last_login_at, (xmin::text)::bigint FROM auth.users WHERE id = @id",
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("id", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw NotFound("USER_NOT_FOUND", "The requested user does not exist.");
            }

            id = reader.GetGuid(0);
            username = reader.GetString(1);
            fullName = reader.GetString(2);
            employeeNo = NullableString(reader, 3);
            isActive = reader.GetBoolean(4);
            lastLogin = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5);
            version = reader.GetInt64(6);
        }

        var roles = new List<string>();
        await using (var command = new NpgsqlCommand(
                         "SELECT r.code FROM auth.user_roles ur JOIN auth.roles r ON r.id = ur.role_id WHERE ur.user_id = @id ORDER BY r.code",
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("id", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                roles.Add(reader.GetString(0));
            }
        }

        var permissions = new List<MachinePermissionRecord>();
        await using (var command = new NpgsqlCommand(
                         "SELECT machine_id, can_view, can_operate, can_change_variant, can_manage_slots FROM auth.user_machine_permissions WHERE user_id = @id ORDER BY machine_id",
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("id", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                permissions.Add(new MachinePermissionRecord(reader.GetGuid(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4)));
            }
        }

        return new AdminUserRecord(id, username, fullName, employeeNo, isActive, lastLogin, roles, permissions, version);
    }

    private static async Task<AdminProductGroupRecord> ReadProductGroupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid groupId,
        CancellationToken cancellationToken)
    {
        Guid id;
        Guid variantId;
        Guid mainPartId;
        string mainPartCode;
        string? name;
        int displayOrder;
        bool isActive;
        long version;
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT g.id, g.variant_id, g.main_part_id, p.part_code, g.name, g.display_order, g.is_active, (g.xmin::text)::bigint
                         FROM master.product_groups g JOIN master.parts p ON p.id = g.main_part_id
                         WHERE g.id = @id
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("id", groupId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw NotFound("PRODUCT_GROUP_NOT_FOUND", "The product group does not exist.");
            }

            id = reader.GetGuid(0);
            variantId = reader.GetGuid(1);
            mainPartId = reader.GetGuid(2);
            mainPartCode = reader.GetString(3);
            name = NullableString(reader, 4);
            displayOrder = reader.GetInt32(5);
            isActive = reader.GetBoolean(6);
            version = reader.GetInt64(7);
        }

        var parts = new List<AdminProductGroupPartRecord>();
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT gp.part_id, p.part_code, gp.container_type_id, c.code, gp.relation_type,
                                gp.lot_quantity, gp.display_order, gp.is_active
                         FROM master.product_group_parts gp
                         JOIN master.parts p ON p.id = gp.part_id
                         LEFT JOIN master.container_types c ON c.id = gp.container_type_id
                         WHERE gp.product_group_id = @id
                         ORDER BY gp.display_order, p.part_code
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("id", groupId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                parts.Add(new AdminProductGroupPartRecord(
                    reader.GetGuid(0), reader.GetString(1), NullableGuid(reader, 2), NullableString(reader, 3), reader.GetString(4),
                    NullableDecimal(reader, 5), reader.GetInt32(6), reader.GetBoolean(7)));
            }
        }

        return new AdminProductGroupRecord(id, variantId, mainPartId, mainPartCode, name, displayOrder, isActive, parts, version);
    }

    private static AdminDeviceRecord ReadDevice(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), ParseJson(reader.GetString(5)), reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetGuid(7));

    private static AdminMachineRecord ReadMachine(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4), reader.GetInt64(5));

    private static AdminVariantRecord ReadVariant(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetBoolean(5), NullableGuid(reader, 6), reader.GetInt64(7));

    private static AdminLayoutRecord ReadLayout(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8), reader.GetInt64(9));

    private static AdminSlotRecord ReadSlot(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), NullableGuid(reader, 5),
        NullableDecimal(reader, 6), NullableDecimal(reader, 7), NullableDecimal(reader, 8), NullableDecimal(reader, 9), reader.GetInt32(10),
        reader.GetBoolean(11), reader.GetBoolean(12), reader.GetBoolean(13), ParseJson(reader.GetString(14)), reader.GetInt64(15));

    private static AdminPartRecord ReadPart(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), NullableString(reader, 2), reader.GetBoolean(3), reader.GetInt64(4));

    private static AdminContainerRecord ReadContainer(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), NullableInt(reader, 4), NullableInt(reader, 5), NullableInt(reader, 6),
        NullableDecimal(reader, 7), reader.GetBoolean(8), reader.GetInt64(9));

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AdminMutationContext context,
        string action,
        string entityType,
        string entityId,
        object? before,
        object? after,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_logs (
                actor_user_id, action, entity_type, entity_id, before_json, after_json,
                ip_address, user_agent, correlation_id)
            VALUES (@actor_user_id, @action, @entity_type, @entity_id, @before_json, @after_json,
                    CAST(@ip_address AS inet), @user_agent, @correlation_id)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("actor_user_id", context.ActorUserId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entity_type", entityType);
        command.Parameters.AddWithValue("entity_id", entityId);
        AddNullableJson(command, "before_json", before is null ? null : JsonSerializer.Serialize(before));
        AddNullableJson(command, "after_json", after is null ? null : JsonSerializer.Serialize(after));
        AddNullableText(command, "ip_address", context.IpAddress);
        AddNullableText(command, "user_agent", context.UserAgent);
        command.Parameters.AddWithValue("correlation_id", context.CorrelationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AdminRuleException NotFound(string code, string message) => new(code, message);
    private static AdminRuleException Conflict(string code, string message) => new(code, message);
    private static AdminRuleException Rule(string code, string message) => new(code, message);

    private static JsonElement ParseJson(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static JsonElement? NullableJson(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ParseJson(reader.GetString(ordinal));
    private static Guid? NullableGuid(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static decimal? NullableDecimal(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    private static int? NullableInt(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value.HasValue ? value.Value : DBNull.Value;

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value is null ? DBNull.Value : value;

    private static void AddNullableJson(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Jsonb).Value = value is null ? DBNull.Value : value;

    private const string LayoutSelectSql =
        """
        SELECT l.id, l.machine_id, l.code, l.name, l.version, l.status,
               (SELECT count(*)::int FROM master.layout_slots s WHERE s.layout_id = l.id),
               (SELECT count(*)::int FROM master.variant_layout_assignments a WHERE a.layout_id = l.id),
               l.published_at, (l.xmin::text)::bigint
        FROM master.layouts l
        """;

    private const string SlotSelectSql =
        """
        SELECT s.id, s.layout_id, s.code, s.display_name, s.slot_role, s.default_container_type_id,
               s.ui_x, s.ui_y, s.ui_width, s.ui_height, s.display_order,
               s.allows_empty_load, s.allows_full_load, s.is_enabled,
               s.metadata_json::text, (s.xmin::text)::bigint
        FROM master.layout_slots s
        """;
}
