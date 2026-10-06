using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

public sealed partial class PostgresTransportWorkflowStore : IProductionSessionStore
{
    private static async Task ValidateExpectedProductionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid machineId, Guid? expectedId, Guid[]? replayIds, CancellationToken cancellationToken)
    {
        if (expectedId is null) return; // Legacy clients remain compatible.
        if (replayIds is not null)
        {
            await using var replay = new NpgsqlCommand("SELECT count(*) FROM ops.transport_requests WHERE id = ANY(@ids) AND production_session_id IS DISTINCT FROM @expected", connection, transaction);
            replay.Parameters.AddWithValue("ids", replayIds);
            replay.Parameters.AddWithValue("expected", expectedId.Value);
            if ((long)(await replay.ExecuteScalarAsync(cancellationToken))! != 0)
                throw new WorkflowRuleException("IDEMPOTENCY_KEY_REUSED", "Talep anahtarı başka bir üretim oturumuna ait.");
            return;
        }
        // Caller already owns the workflow machine lock, shared with start/end.
        var active = await ReadProductionAsync(connection, transaction, machineId, "ended_at IS NULL", null, cancellationToken);
        if (active?.Id != expectedId)
            throw new WorkflowRuleException("PRODUCTION_SESSION_CHANGED", "Üretim değişti veya bitirildi. Talep gönderilmedi; durumu yenileyin.");
    }

    private const string ProductionColumns = "id, machine_id, variant_id, product_group_id, main_part_id, started_at, started_by, started_device_id, ended_at, ended_by, ended_device_id, status";

    public async Task<ProductionSessionRecord?> FindActiveAsync(Guid machineId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadProductionAsync(connection, null, machineId, "ended_at IS NULL", null, cancellationToken);
    }

    public async Task<ProductionSessionResult> StartAsync(StartProductionSession command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Keys are machine-scoped. No request/slot locks are acquired before this.
        await LockMachineRuntimeAsync(connection, transaction, command.MachineId, cancellationToken);
        await ValidateProductionActorAsync(connection, transaction, command.MachineId, command.ActorUserId, command.DeviceId, cancellationToken);
        var replay = await ReadProductionAsync(connection, transaction, command.MachineId, "start_key = @value", command.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.VariantId != command.VariantId || replay.ProductGroupId != command.ProductGroupId
                || replay.MainPartId != command.MainPartId || replay.StartedBy != command.ActorUserId
                || replay.StartedDeviceId != command.DeviceId)
                throw new WorkflowRuleException("IDEMPOTENCY_KEY_REUSED", "İstek anahtarı farklı bir üretim başlangıcına ait.");
            await transaction.CommitAsync(cancellationToken);
            return new(replay, true);
        }

        if (await ReadProductionAsync(connection, transaction, command.MachineId, "ended_at IS NULL", null, cancellationToken) is not null)
            throw new WorkflowRuleException("PRODUCTION_SESSION_ALREADY_ACTIVE", "Bu makinede zaten açık bir üretim var.");
        await EnsureNoProductionTransportAsync(connection, transaction, command.MachineId, cancellationToken);

        await using (var validate = new NpgsqlCommand("""
            SELECT s.active_layout_id, s.active_navigation_id
            FROM ops.machine_runtime_state s
            JOIN master.machines m ON m.id = s.machine_id AND m.is_active
            JOIN master.variants v ON v.id = s.active_variant_id AND v.machine_id = m.id AND v.is_active
            JOIN master.product_groups g ON g.variant_id = v.id AND g.is_active
            JOIN master.parts p ON p.id = g.main_part_id AND p.is_active
            WHERE m.id = @machine AND v.id = @variant AND g.id = @group AND p.id = @part
            """, connection, transaction))
        {
            validate.Parameters.AddWithValue("machine", command.MachineId);
            validate.Parameters.AddWithValue("variant", command.VariantId);
            validate.Parameters.AddWithValue("group", command.ProductGroupId);
            validate.Parameters.AddWithValue("part", command.MainPartId);
            Guid layout;
            Guid? navigation;
            await using (var reader = await validate.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
                    throw new WorkflowRuleException("PRODUCTION_CONTEXT_MISMATCH", "Ana ürün, ürün grubu ve varyant makinenin etkin üretim bağlamıyla uyuşmuyor.");
                layout = reader.GetGuid(0);
                navigation = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            }
            await ValidateVariantLayoutAssignmentAsync(connection, transaction, command.MachineId, command.VariantId, layout, navigation, cancellationToken);
        }

        ProductionSessionRecord session;
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO ops.production_sessions (machine_id, variant_id, product_group_id, main_part_id, start_key, started_by, started_device_id)
            VALUES (@machine, @variant, @group, @part, @key, @actor, @device)
            RETURNING {ProductionColumns}
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("machine", command.MachineId);
            insert.Parameters.AddWithValue("variant", command.VariantId);
            insert.Parameters.AddWithValue("group", command.ProductGroupId);
            insert.Parameters.AddWithValue("part", command.MainPartId);
            insert.Parameters.AddWithValue("key", command.IdempotencyKey);
            insert.Parameters.AddWithValue("actor", command.ActorUserId);
            insert.Parameters.AddWithValue("device", command.DeviceId);
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            session = ReadProduction(reader);
        }
        await AuditProductionAsync(connection, transaction, "PRODUCTION_STARTED", command.ActorUserId, command.DeviceId, null, session, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(session, false);
    }

    public async Task<ProductionSessionResult> EndAsync(EndProductionSession command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockMachineRuntimeAsync(connection, transaction, command.MachineId, cancellationToken);
        await ValidateProductionActorAsync(connection, transaction, command.MachineId, command.ActorUserId, command.DeviceId, cancellationToken);
        var result = await PlanProductionEndAsync(connection, transaction, command, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task ValidateProductionActorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid machineId, Guid actorId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM auth.users u
            WHERE u.id = @actor AND u.is_active
              AND EXISTS (SELECT 1 FROM audit.client_devices d WHERE d.id = @device AND d.is_active)
              AND EXISTS (SELECT 1 FROM auth.user_roles ur JOIN auth.roles r ON r.id = ur.role_id
                          WHERE ur.user_id = u.id AND r.code IN ('ADMIN','SUPERVISOR','LINE_OPERATOR'))
              AND (EXISTS (SELECT 1 FROM auth.user_roles ur JOIN auth.roles r ON r.id = ur.role_id
                           WHERE ur.user_id = u.id AND r.code = 'ADMIN')
                   OR EXISTS (SELECT 1 FROM auth.user_machine_permissions p
                              WHERE p.user_id = u.id AND p.machine_id = @machine AND p.can_operate))
            """, connection, transaction);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("device", deviceId);
        command.Parameters.AddWithValue("machine", machineId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new WorkflowRuleException("PRODUCTION_ACCESS_DENIED", "Kullanıcı veya cihaz bu makinede üretim yönetmeye yetkili değil.");
    }

    private static async Task EnsureNoProductionTransportAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid machineId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT 1 FROM ops.transport_requests WHERE machine_id = @machine AND status IN ({ActiveStatusesSql}) LIMIT 1", connection, transaction);
        command.Parameters.AddWithValue("machine", machineId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not null)
            throw new WorkflowRuleException("ACTIVE_TRANSPORT_EXISTS", "Açık taşıma talepleri tamamlanmadan veya ayrıca iptal edilmeden üretim başlatılamaz/bitirilemez.");
    }

    private static async Task<ProductionSessionRecord?> ReadProductionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid machineId, string predicate, object? value, CancellationToken cancellationToken)
    {
        // Predicate is internal static SQL, never supplied by a client.
        await using var command = new NpgsqlCommand($"SELECT {ProductionColumns} FROM ops.production_sessions WHERE machine_id = @machine AND {predicate}", connection, transaction);
        command.Parameters.AddWithValue("machine", machineId);
        if (value is not null) command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProduction(reader) : null;
    }

    private static ProductionSessionRecord ReadProduction(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetGuid(4), r.GetFieldValue<DateTimeOffset>(5), r.GetGuid(6), r.GetGuid(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetGuid(9), r.IsDBNull(10) ? null : r.GetGuid(10))
    {
        Status = r.GetString(11)
    };

    private static async Task AuditProductionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string action,
        Guid actor, Guid device, ProductionSessionRecord? before, ProductionSessionRecord after, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO audit.audit_logs (actor_user_id, device_id, action, entity_type, entity_id, before_json, after_json)
            VALUES (@actor, @device, @action, 'PRODUCTION_SESSION', @id, @before, @after)
            """, connection, transaction);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("device", device);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("id", after.Id.ToString());
        command.Parameters.Add("before", NpgsqlDbType.Jsonb).Value = before is null ? DBNull.Value : JsonSerializer.Serialize(before);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(after));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Caller holds the machine lock; no session/slot/request lock inversion.
    private static async Task RejectIfProductionEndingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid machineId,
        CancellationToken cancellationToken)
    {
        var active = await ReadProductionAsync(connection, transaction, machineId, "ended_at IS NULL", null, cancellationToken);
        if (active?.Status == "ENDING")
            throw new WorkflowRuleException("PRODUCTION_ENDING", "Üretim bitiriliyor. Yeni malzeme talebi açılamaz.");
    }

    private static async Task ValidateProductionTransportContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CreateTransportRequest request, CancellationToken cancellationToken)
    {
        var active = await ReadProductionAsync(connection, transaction, request.MachineId, "ended_at IS NULL", null, cancellationToken);
        if (active?.Status == "ENDING")
            throw new WorkflowRuleException("PRODUCTION_ENDING", "Üretim bitiriliyor. Yeni malzeme talebi açılamaz.");
        if (active is not null && (active.VariantId != request.VariantId || active.ProductGroupId != request.ProductGroupId || request.PartId is null))
            throw new WorkflowRuleException("PRODUCTION_CONTEXT_MISMATCH", "Taşıma talebi açık üretimin ana ürün bağlamına ait olmalıdır.");
    }

    private static async Task GuardProductionVariantSwitchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        VariantSelection selection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM ops.machine_runtime_state m
            WHERE m.machine_id = @machine
              AND EXISTS (SELECT 1 FROM ops.production_sessions s WHERE s.machine_id = m.machine_id AND s.ended_at IS NULL)
              AND (m.active_variant_id IS DISTINCT FROM @variant OR m.active_layout_id IS DISTINCT FROM @layout
                   OR m.active_navigation_id IS DISTINCT FROM @navigation)
            """, connection, transaction);
        command.Parameters.AddWithValue("machine", selection.MachineId);
        command.Parameters.AddWithValue("variant", selection.VariantId);
        command.Parameters.AddWithValue("layout", selection.LayoutId);
        AddNullableUuid(command, "navigation", selection.NavigationProfileId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not null)
            throw new WorkflowRuleException("PRODUCTION_SESSION_ACTIVE", "Üretim bitirilmeden makinenin etkin varyantı veya yerleşimi değiştirilemez.");
    }
}
