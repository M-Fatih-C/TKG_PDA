using System.Data;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresAdminConfigurationStore
{
    public async Task<AdminUserRecord> CreateUserAsync(
        CreateUserCommand command,
        string passwordHash,
        AdminMutationContext context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
                         """
                         INSERT INTO auth.users (id, username, password_hash, full_name, employee_no)
                         VALUES (@id, @username, @password_hash, @full_name, @employee_no)
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("username", command.Username);
            insert.Parameters.AddWithValue("password_hash", passwordHash);
            insert.Parameters.AddWithValue("full_name", command.FullName);
            AddNullableText(insert, "employee_no", command.EmployeeNo);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceRolesAsync(connection, transaction, id, command.Roles, cancellationToken);
        var created = await ReadUserAsync(connection, transaction, id, cancellationToken);
        await InsertAuditAsync(connection, transaction, context, "ADMIN_USER_CREATED", "AUTH_USER", id.ToString(), null, created, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<AdminUserRecord> UpdateUserAsync(
        Guid userId,
        UpdateUserCommand command,
        AdminMutationContext context,
        CancellationToken cancellationToken = default)
    {
        if (userId == context.ActorUserId && !command.IsActive)
        {
            throw Conflict("SELF_DEACTIVATION_FORBIDDEN", "An administrator cannot deactivate the current account.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var before = await ReadUserAsync(connection, transaction, userId, cancellationToken);
        await using (var update = new NpgsqlCommand(
                         """
                         UPDATE auth.users
                         SET full_name = @full_name, employee_no = @employee_no, is_active = @is_active, updated_at = now()
                         WHERE id = @id AND (xmin::text)::bigint = @expected
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("id", userId);
            update.Parameters.AddWithValue("full_name", command.FullName);
            AddNullableText(update, "employee_no", command.EmployeeNo);
            update.Parameters.AddWithValue("is_active", command.IsActive);
            update.Parameters.AddWithValue("expected", command.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("CONCURRENCY_CONFLICT", "The user was changed by another administrator.");
            }
        }

        var after = await ReadUserAsync(connection, transaction, userId, cancellationToken);
        await InsertAuditAsync(connection, transaction, context, command.IsActive ? "ADMIN_USER_UPDATED" : "ADMIN_USER_ARCHIVED", "AUTH_USER", userId.ToString(), before, after, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return after;
    }

    public async Task<AdminUserRecord> ResetPasswordAsync(
        Guid userId,
        string passwordHash,
        long expectedVersion,
        AdminMutationContext context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        _ = await ReadUserAsync(connection, transaction, userId, cancellationToken);
        await using (var update = new NpgsqlCommand(
                         """
                         UPDATE auth.users SET password_hash = @password_hash, updated_at = now()
                         WHERE id = @id AND (xmin::text)::bigint = @expected
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("id", userId);
            update.Parameters.AddWithValue("password_hash", passwordHash);
            update.Parameters.AddWithValue("expected", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("CONCURRENCY_CONFLICT", "The user was changed before the password reset completed.");
            }
        }

        var after = await ReadUserAsync(connection, transaction, userId, cancellationToken);
        await InsertAuditAsync(
            connection,
            transaction,
            context,
            "ADMIN_USER_PASSWORD_RESET",
            "AUTH_USER",
            userId.ToString(),
            new { passwordReset = false },
            new { passwordReset = true },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return after;
    }

    public async Task<AdminUserRecord> SetUserRolesAsync(
        Guid userId,
        IReadOnlyCollection<string> roles,
        long expectedVersion,
        AdminMutationContext context,
        CancellationToken cancellationToken = default)
    {
        if (userId == context.ActorUserId && !roles.Contains(KnownRoleCodes.Admin, StringComparer.Ordinal))
        {
            throw Conflict("SELF_ADMIN_REMOVAL_FORBIDDEN", "An administrator cannot remove ADMIN from the current account.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var before = await ReadUserAsync(connection, transaction, userId, cancellationToken);
        await TouchUserAsync(connection, transaction, userId, expectedVersion, cancellationToken);
        await ReplaceRolesAsync(connection, transaction, userId, roles, cancellationToken);
        var after = await ReadUserAsync(connection, transaction, userId, cancellationToken);
        await InsertAuditAsync(connection, transaction, context, "ADMIN_USER_ROLES_CHANGED", "AUTH_USER", userId.ToString(), new { before.Roles }, new { after.Roles }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return after;
    }

    public async Task<AdminDeviceRecord> CreateDeviceAsync(
        CreateDeviceCommand command,
        AdminMutationContext context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (command.MachineId.HasValue)
        {
            await using var check = new NpgsqlCommand("SELECT 1 FROM master.machines WHERE id = @id AND is_active", connection, transaction);
            check.Parameters.AddWithValue("id", command.MachineId.Value);
            if (await check.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw Rule("DEVICE_MACHINE_BINDING_INVALID", "Belirtilen makine bulunamadı veya aktif değil.");
            }
        }

        AdminDeviceRecord created;
        await using (var insert = new NpgsqlCommand(
                         """
                         INSERT INTO audit.client_devices (device_code, name, device_type, is_active, metadata_json, machine_id)
                         VALUES (@code, @name, @type, @is_active, @metadata, @machine_id)
                         RETURNING id, device_code, name, device_type, is_active, metadata_json::text, (xmin::text)::bigint, machine_id
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("code", command.Code);
            insert.Parameters.AddWithValue("name", command.Name);
            insert.Parameters.AddWithValue("type", command.Type);
            insert.Parameters.AddWithValue("is_active", command.IsActive);
            insert.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = command.Metadata.GetRawText();
            insert.Parameters.AddWithValue("machine_id", (object?)command.MachineId ?? DBNull.Value);
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            created = ReadDevice(reader);
        }

        await InsertAuditAsync(connection, transaction, context, "ADMIN_DEVICE_CREATED", "CLIENT_DEVICE", created.Id.ToString(), null, created, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<AdminDeviceRecord> UpdateDeviceAsync(
        Guid deviceId,
        UpdateDeviceCommand command,
        AdminMutationContext context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (command.MachineId.HasValue)
        {
            await using var check = new NpgsqlCommand("SELECT 1 FROM master.machines WHERE id = @id AND is_active", connection, transaction);
            check.Parameters.AddWithValue("id", command.MachineId.Value);
            if (await check.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw Rule("DEVICE_MACHINE_BINDING_INVALID", "Belirtilen makine bulunamadı veya aktif değil.");
            }
        }

        var before = await ReadDeviceAsync(connection, transaction, deviceId, cancellationToken);
        AdminDeviceRecord after;
        await using (var update = new NpgsqlCommand(
                         """
                         UPDATE audit.client_devices
                         SET name = @name, device_type = @type, is_active = @is_active,
                             metadata_json = @metadata, machine_id = @machine_id, updated_at = now()
                         WHERE id = @id AND (xmin::text)::bigint = @expected
                         RETURNING id, device_code, name, device_type, is_active, metadata_json::text, (xmin::text)::bigint, machine_id
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("id", deviceId);
            update.Parameters.AddWithValue("name", command.Name);
            update.Parameters.AddWithValue("type", command.Type);
            update.Parameters.AddWithValue("is_active", command.IsActive);
            update.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = command.Metadata.GetRawText();
            update.Parameters.AddWithValue("expected", command.ExpectedVersion);
            update.Parameters.AddWithValue("machine_id", (object?)command.MachineId ?? DBNull.Value);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw Conflict("CONCURRENCY_CONFLICT", "The device was changed by another administrator.");
            }

            after = ReadDevice(reader);
        }

        await InsertAuditAsync(connection, transaction, context, command.IsActive ? "ADMIN_DEVICE_UPDATED" : "ADMIN_DEVICE_ARCHIVED", "CLIENT_DEVICE", deviceId.ToString(), before, after, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return after;
    }

    public async Task<AdminMachineRecord> UpdateMachineAsync(
        Guid machineId,
        UpdateMachineCommand command,
        AdminMutationContext context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var before = await ReadMachineAsync(connection, transaction, machineId, cancellationToken);
        if (!command.IsActive && await HasActiveTransportAsync(connection, transaction, "machine_id", machineId, cancellationToken))
        {
            throw Conflict("CONFIGURATION_IN_USE", "The machine cannot be archived while transport requests are active.");
        }

        AdminMachineRecord after;
        await using (var update = new NpgsqlCommand(
                         """
                         UPDATE master.machines
                         SET display_name = @name, display_order = @display_order, is_active = @is_active, updated_at = now()
                         WHERE id = @id AND (xmin::text)::bigint = @expected
                         RETURNING id, code, display_name, display_order, is_active, (xmin::text)::bigint
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("id", machineId);
            update.Parameters.AddWithValue("name", command.DisplayName);
            update.Parameters.AddWithValue("display_order", command.DisplayOrder);
            update.Parameters.AddWithValue("is_active", command.IsActive);
            update.Parameters.AddWithValue("expected", command.ExpectedVersion);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw Conflict("CONCURRENCY_CONFLICT", "The machine was changed by another administrator.");
            }

            after = ReadMachine(reader);
        }

        await InsertAuditAsync(connection, transaction, context, command.IsActive ? "ADMIN_MACHINE_UPDATED" : "ADMIN_MACHINE_ARCHIVED", "MACHINE", machineId.ToString(), before, after, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return after;
    }

    public async Task<MachinePermissionRecord> SetMachinePermissionAsync(
        Guid userId,
        MachinePermissionRecord permission,
        long expectedUserVersion,
        AdminMutationContext context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var before = await FindPermissionAsync(connection, transaction, userId, permission.MachineId, cancellationToken);
        await TouchUserAsync(connection, transaction, userId, expectedUserVersion, cancellationToken);
        await using (var upsert = new NpgsqlCommand(
                         """
                         INSERT INTO auth.user_machine_permissions (
                             user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
                         VALUES (@user_id, @machine_id, @can_view, @can_operate, @can_change_variant, @can_manage_slots)
                         ON CONFLICT (user_id, machine_id) DO UPDATE SET
                             can_view = EXCLUDED.can_view,
                             can_operate = EXCLUDED.can_operate,
                             can_change_variant = EXCLUDED.can_change_variant,
                             can_manage_slots = EXCLUDED.can_manage_slots
                         """,
                         connection,
                         transaction))
        {
            upsert.Parameters.AddWithValue("user_id", userId);
            upsert.Parameters.AddWithValue("machine_id", permission.MachineId);
            upsert.Parameters.AddWithValue("can_view", permission.CanView);
            upsert.Parameters.AddWithValue("can_operate", permission.CanOperate);
            upsert.Parameters.AddWithValue("can_change_variant", permission.CanChangeVariant);
            upsert.Parameters.AddWithValue("can_manage_slots", permission.CanManageSlots);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, context, "ADMIN_MACHINE_PERMISSION_SET", "USER_MACHINE_PERMISSION", $"{userId}:{permission.MachineId}", before, permission, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return permission;
    }

    public async Task RemoveMachinePermissionAsync(
        Guid userId,
        Guid machineId,
        long expectedUserVersion,
        AdminMutationContext context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var before = await FindPermissionAsync(connection, transaction, userId, machineId, cancellationToken)
            ?? throw NotFound("MACHINE_PERMISSION_NOT_FOUND", "The machine permission does not exist.");
        await TouchUserAsync(connection, transaction, userId, expectedUserVersion, cancellationToken);
        await using (var delete = new NpgsqlCommand(
                         "DELETE FROM auth.user_machine_permissions WHERE user_id = @user_id AND machine_id = @machine_id",
                         connection,
                         transaction))
        {
            delete.Parameters.AddWithValue("user_id", userId);
            delete.Parameters.AddWithValue("machine_id", machineId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(connection, transaction, context, "ADMIN_MACHINE_PERMISSION_REMOVED", "USER_MACHINE_PERMISSION", $"{userId}:{machineId}", before, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ReplaceRolesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken)
    {
        await using (var delete = new NpgsqlCommand("DELETE FROM auth.user_roles WHERE user_id = @id", connection, transaction))
        {
            delete.Parameters.AddWithValue("id", userId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO auth.user_roles (user_id, role_id)
            SELECT @user_id, id FROM auth.roles WHERE code = ANY(@roles) AND is_active
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("roles", NpgsqlDbType.Array | NpgsqlDbType.Varchar, roles.ToArray());
        if (await insert.ExecuteNonQueryAsync(cancellationToken) != roles.Count)
        {
            throw NotFound("ROLE_NOT_FOUND", "One or more roles are missing or inactive.");
        }
    }

    private static async Task TouchUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE auth.users SET updated_at = now() WHERE id = @id AND (xmin::text)::bigint = @expected",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("expected", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("CONCURRENCY_CONFLICT", "The user was changed by another administrator.");
        }
    }

    private static async Task<AdminDeviceRecord> ReadDeviceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT id, device_code, name, device_type, is_active, metadata_json::text, (xmin::text)::bigint, machine_id FROM audit.client_devices WHERE id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDevice(reader) : throw NotFound("DEVICE_NOT_FOUND", "The requested device does not exist.");
    }

    private static async Task<AdminMachineRecord> ReadMachineAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT id, code, display_name, display_order, is_active, (xmin::text)::bigint FROM master.machines WHERE id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMachine(reader) : throw NotFound("MACHINE_NOT_FOUND", "The requested machine does not exist.");
    }

    private static async Task<MachinePermissionRecord?> FindPermissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        Guid machineId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT machine_id, can_view, can_operate, can_change_variant, can_manage_slots FROM auth.user_machine_permissions WHERE user_id = @user_id AND machine_id = @machine_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("machine_id", machineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MachinePermissionRecord(reader.GetGuid(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4))
            : null;
    }

    private static async Task<bool> HasActiveTransportAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string column,
        Guid id,
        CancellationToken cancellationToken)
    {
        var allowedColumn = column switch
        {
            "machine_id" => "machine_id",
            "variant_id" => "variant_id",
            "layout_id" => "layout_id",
            "part_id" => "part_id",
            "product_group_id" => "product_group_id",
            "container_type_id" => "container_type_id",
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };
        await using var command = new NpgsqlCommand(
            $"SELECT 1 FROM ops.transport_requests WHERE {allowedColumn} = @id AND status IN ({ActiveStatusesSql}) LIMIT 1",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
