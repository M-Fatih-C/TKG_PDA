using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Infrastructure.Postgres;

/// <summary>
/// The finished-goods store: what the robot left, and what the operator took.
///
/// A container appears here when the transport that carried it completes, from
/// inside that completion's own transaction, so the store can never disagree
/// with the transport that filled it. It leaves only when the operator states
/// they physically removed it; no transport status removes anything.
/// </summary>
public sealed partial class PostgresTransportWorkflowStore : IFinishedGoodsStore
{
    /// <summary>
    /// Called from the completion transition while it holds the request lock.
    /// Silently does nothing when the transport did not deliver into the store,
    /// which is every operation except a full container leaving a machine.
    ///
    /// REMOVE_FULL requests created by the machine-side container flow can never
    /// reach the "unlinked destination" silent exit: WP-08 refuses to create them
    /// unless their destination is already linked to an enabled store location
    /// (ResolveFinishedGoodsDestinationAsync). The exit remains only for REMOVE_FULL
    /// requests that bypass that flow (explicit-slot callers and legacy rows).
    /// Only a genuine COMPLETED transition calls this, inside its own transaction.
    /// </summary>
    private static async Task RecordStoreDeliveryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransportRequestSnapshot request,
        CancellationToken cancellationToken)
    {
        if (request.TaskType != TransportTaskType.RemoveFull) return;
        if (request.DestinationSlotId is not { } destinationSlotId) return;

        Guid locationId;
        await using (var lookup = new NpgsqlCommand("""
            SELECT store_location_id FROM ops.store_location_delivery_slots
            WHERE layout_slot_id = @slot
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("slot", destinationSlotId);
            if (await lookup.ExecuteScalarAsync(cancellationToken) is not Guid found) return;
            locationId = found;
        }

        Guid unitId;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ops.store_units
                (store_location_id, transport_request_id, part_id, container_type_id, machine_id)
            VALUES (@location, @request, @part, @container, @machine)
            -- Reconciliation can replay a completion. One delivery per
            -- transport, or the store looks twice as full as it is.
            ON CONFLICT (transport_request_id) WHERE transport_request_id IS NOT NULL
            DO NOTHING
            RETURNING id
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("location", locationId);
            insert.Parameters.AddWithValue("request", request.Id);
            AddNullableUuid(insert, "part", request.PartId);
            AddNullableUuid(insert, "container", request.ContainerTypeId);
            insert.Parameters.AddWithValue("machine", request.MachineId);
            if (await insert.ExecuteScalarAsync(cancellationToken) is not Guid inserted) return;
            unitId = inserted;
        }

        await InsertStoreEventAsync(connection, transaction, locationId, unitId, "UNIT_DELIVERED",
            request.PartId, request.ContainerTypeId, request.Id,
            actorUserId: null, deviceId: null, new { source = "TRANSPORT_COMPLETED" }, cancellationToken);
        await EnqueueFinishedGoodsNotificationAsync(
            connection, transaction, locationId, unitId, request, cancellationToken);
    }

    private static async Task EnqueueFinishedGoodsNotificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid locationId,
        Guid unitId,
        TransportRequestSnapshot request,
        CancellationToken cancellationToken)
    {
        string displayName = "";
        string? partCode = null;
        string? machineCode = null;
        await using (var lookup = new NpgsqlCommand(
            """
            SELECT l.display_name, p.part_code, m.code
            FROM ops.store_locations l
            LEFT JOIN master.parts p ON p.id = @part
            LEFT JOIN master.machines m ON m.id = @machine
            WHERE l.id = @location
            """,
            connection,
            transaction))
        {
            lookup.Parameters.AddWithValue("location", locationId);
            AddNullableUuid(lookup, "part", request.PartId);
            lookup.Parameters.AddWithValue("machine", request.MachineId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                displayName = reader.GetString(0);
                partCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                machineCode = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            reader.Close();
        }

        var data = new Dictionary<string, string>
        {
            ["notificationType"] = FinishedGoodsNotificationCopy.NotificationType,
            ["unitId"] = unitId.ToString("D"),
            ["locationId"] = locationId.ToString("D"),
            ["locationName"] = displayName,
            ["requestNo"] = request.RequestNo.ToString()
        };
        if (!string.IsNullOrWhiteSpace(partCode)) data["partCode"] = partCode;
        if (!string.IsNullOrWhiteSpace(machineCode)) data["machineCode"] = machineCode;

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO integration.notification_outbox
                (event_key, registration_id, notification_type, title, body, data_json)
            SELECT @event_key, reg.id, @notification_type, @title, @body, @data
            FROM integration.device_notification_registrations reg
            JOIN auth.users u ON u.id = reg.user_id AND u.is_active
            JOIN audit.client_devices d ON d.id = reg.device_id AND d.is_active
            WHERE reg.is_active
              AND EXISTS (
                  SELECT 1
                  FROM auth.user_roles ur
                  JOIN auth.roles role ON role.id = ur.role_id
                  WHERE ur.user_id = reg.user_id
                    AND role.code = ANY(@roles))
            ON CONFLICT (event_key, registration_id) DO NOTHING
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("event_key", FinishedGoodsNotificationCopy.EventKey(unitId));
        insert.Parameters.AddWithValue("notification_type", FinishedGoodsNotificationCopy.NotificationType);
        insert.Parameters.AddWithValue("title", FinishedGoodsNotificationCopy.Title(displayName));
        insert.Parameters.AddWithValue("body", FinishedGoodsNotificationCopy.Body(partCode, machineCode, request.RequestNo));
        insert.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(data) });
        insert.Parameters.Add(new NpgsqlParameter("roles", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = FinishedGoodsNotificationAuthorization.RecipientRoles
        });
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string FinishedGoodsDestinationNotConfiguredMessage =
        "Bitmiş ürün teslim alanı henüz yapılandırılmamış. Sistem yöneticisine başvurun.";

    /// <summary>
    /// WP-08. Resolves, server-side only, the physical slot a Kasa Doldu
    /// REMOVE_FULL will be delivered to, and proves it is linked to an enabled
    /// finished-goods store location — so a completed delivery can never leave
    /// the warehouse unaware of it.
    ///
    /// Resolution mirrors the dispatch resolver exactly (PostgresRdsExecutionStore
    /// destination lookup) so the stored destination is the one dispatch would
    /// have chosen anyway: the configured task template's destination slot
    /// group by priority, otherwise the layout's enabled DROP position. No
    /// slot, WorkSite or location is invented; a missing or disabled link
    /// fails closed with FINISHED_GOODS_DESTINATION_NOT_CONFIGURED. The mapping
    /// and location rows are locked FOR SHARE so a concurrent disable or unlink
    /// cannot slip between this check and the commit.
    /// </summary>
    private static async Task<Guid> ResolveFinishedGoodsDestinationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CreateTransportRequest request,
        CancellationToken cancellationToken)
    {
        var destinationSlotId = request.DestinationSlotId
            ?? await ResolveFinishedGoodsDestinationSlotAsync(
                connection, transaction, request.LayoutId, request.VariantId, request.ProductGroupId, cancellationToken);
        if (destinationSlotId is not { } slotId
            || !await IsFinishedGoodsDeliverySlotMappedAsync(connection, transaction, slotId, lockRows: transaction is not null, cancellationToken))
        {
            throw new WorkflowRuleException("FINISHED_GOODS_DESTINATION_NOT_CONFIGURED", FinishedGoodsDestinationNotConfiguredMessage);
        }

        return slotId;
    }

    private static async Task<Guid?> ResolveFinishedGoodsDestinationSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid layoutId,
        Guid variantId,
        Guid? productGroupId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH template AS (
                SELECT t.destination_slot_group_id
                FROM master.task_templates t
                JOIN master.task_types tt ON tt.id = t.task_type_id AND tt.code = 'REMOVE_FULL'
                WHERE t.variant_id = @variant_id
                  AND t.is_active
                  AND (t.product_group_id = @product_group_id OR t.product_group_id IS NULL)
                ORDER BY (t.product_group_id = @product_group_id) DESC, t.id
                LIMIT 1
            )
            SELECT ls.id
            FROM master.layout_slots ls
            LEFT JOIN master.slot_group_members sgm ON sgm.layout_slot_id = ls.id
            LEFT JOIN template ON true
            WHERE ls.layout_id = @layout_id
              AND ls.is_enabled
              AND ls.slot_role <> 'MACHINE'
              AND ((template.destination_slot_group_id IS NOT NULL
                    AND sgm.slot_group_id = template.destination_slot_group_id)
                   OR (template.destination_slot_group_id IS NULL AND ls.slot_role = 'DROP'))
            ORDER BY sgm.priority, ls.display_order, ls.id
            LIMIT 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("layout_id", layoutId);
        command.Parameters.AddWithValue("variant_id", variantId);
        AddNullableUuid(command, "product_group_id", productGroupId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id : null;
    }

    private static async Task<bool> IsFinishedGoodsDeliverySlotMappedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid layoutSlotId,
        bool lockRows,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
             SELECT 1
             FROM ops.store_location_delivery_slots d
             JOIN ops.store_locations sl ON sl.id = d.store_location_id
             WHERE d.layout_slot_id = @slot_id AND sl.is_enabled
             {(lockRows ? "FOR SHARE OF d, sl" : string.Empty)}
             """,
            connection,
            transaction);
        command.Parameters.AddWithValue("slot_id", layoutSlotId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// Read-only form for the PDA projection: whether a Kasa Doldu for this
    /// product group could currently resolve a mapped finished-goods destination.
    /// </summary>
    private static async Task<bool> IsFinishedGoodsDestinationConfiguredAsync(
        NpgsqlConnection connection,
        Guid layoutId,
        Guid variantId,
        Guid productGroupId,
        CancellationToken cancellationToken)
    {
        var slotId = await ResolveFinishedGoodsDestinationSlotAsync(
            connection, null, layoutId, variantId, productGroupId, cancellationToken);
        return slotId is { } id
            && await IsFinishedGoodsDeliverySlotMappedAsync(connection, null, id, lockRows: false, cancellationToken);
    }

    public async Task<IReadOnlyList<StoreLocationRecord>> ListLocationsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadLocationsAsync(connection, null, locationId: null, cancellationToken);
    }

    public async Task<StoreLocationRecord> RemoveUnitAsync(
        RemoveStoreUnitCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ValidateFinishedGoodsActorAsync(connection, transaction, command.ActorUserId, command.DeviceId, cancellationToken);

        Guid locationId;
        Guid? partId;
        Guid? containerTypeId;
        Guid? requestId;
        long rowVersion;
        bool alreadyRemoved;
        await using (var locking = new NpgsqlCommand("""
            SELECT store_location_id, part_id, container_type_id, transport_request_id,
                   row_version, removed_at IS NOT NULL
            FROM ops.store_units WHERE id = @id FOR UPDATE
            """, connection, transaction))
        {
            locking.Parameters.AddWithValue("id", command.UnitId);
            await using var reader = await locking.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new WorkflowRuleException("STORE_UNIT_NOT_FOUND", "Depo kaydı bulunamadı.");
            }

            locationId = reader.GetGuid(0);
            partId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            containerTypeId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
            requestId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
            rowVersion = reader.GetInt64(4);
            alreadyRemoved = reader.GetBoolean(5);
        }

        if (alreadyRemoved)
        {
            // A retry removes nothing twice.
            var current = await ReadLocationsAsync(connection, transaction, locationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return current.Single();
        }

        if (rowVersion != command.ExpectedRowVersion)
        {
            throw new WorkflowRuleException(
                "STORE_VERSION_CONFLICT", "Bu kayıt siz bakarken değişti; ekranı yenileyin.");
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE ops.store_units
            SET removed_at = GREATEST(clock_timestamp(), delivered_at), removed_by = @actor,
                removed_device_id = @device, row_version = row_version + 1
            WHERE id = @id AND removed_at IS NULL
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", command.UnitId);
            update.Parameters.AddWithValue("actor", command.ActorUserId);
            update.Parameters.AddWithValue("device", command.DeviceId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertStoreEventAsync(connection, transaction, locationId, command.UnitId, "UNIT_REMOVED",
            partId, containerTypeId, requestId, command.ActorUserId, command.DeviceId,
            new { }, cancellationToken);

        var refreshed = await ReadLocationsAsync(connection, transaction, locationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return refreshed.Single();
    }

    public async Task<StoreLocationRecord> ClearLocationAsync(
        ClearStoreLocationCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ValidateFinishedGoodsActorAsync(connection, transaction, command.ActorUserId, command.DeviceId, cancellationToken);

        await using (var exists = new NpgsqlCommand(
            "SELECT 1 FROM ops.store_locations WHERE id = @id", connection, transaction))
        {
            exists.Parameters.AddWithValue("id", command.LocationId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new WorkflowRuleException("STORE_LOCATION_NOT_FOUND", "Depo konumu bulunamadı.");
            }
        }

        // Lock every standing unit before touching any, so a delivery arriving
        // mid-clear is either fully included or left for the next clear.
        var cleared = new List<Guid>();
        await using (var locking = new NpgsqlCommand("""
            SELECT id FROM ops.store_units
            WHERE store_location_id = @location AND removed_at IS NULL
            ORDER BY delivered_at
            FOR UPDATE
            """, connection, transaction))
        {
            locking.Parameters.AddWithValue("location", command.LocationId);
            await using var reader = await locking.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) cleared.Add(reader.GetGuid(0));
        }

        if (cleared.Count > 0)
        {
            await using (var update = new NpgsqlCommand("""
                UPDATE ops.store_units
                SET removed_at = GREATEST(clock_timestamp(), delivered_at), removed_by = @actor,
                    removed_device_id = @device, row_version = row_version + 1
                WHERE id = ANY(@ids)
                """, connection, transaction))
            {
                update.Parameters.AddWithValue("ids", cleared.ToArray());
                update.Parameters.AddWithValue("actor", command.ActorUserId);
                update.Parameters.AddWithValue("device", command.DeviceId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertStoreEventAsync(connection, transaction, command.LocationId, storeUnitId: null,
                "LOCATION_CLEARED", partId: null, containerTypeId: null, transportRequestId: null,
                command.ActorUserId, command.DeviceId, new { removed_units = cleared.Count },
                cancellationToken);
        }

        var refreshed = await ReadLocationsAsync(connection, transaction, command.LocationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return refreshed.Single();
    }

    public async Task<StoreLocationRecord> RecordEmptyContainerAsync(
        RecordEmptyContainerCommand command, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ValidateFinishedGoodsActorAsync(connection, transaction, command.ActorUserId, command.DeviceId, cancellationToken);
        var key = command.IdempotencyKey.Trim();

        await using (var replay = new NpgsqlCommand("""
            SELECT 1 FROM ops.store_events
            WHERE store_location_id = @location
              AND event_type = 'EMPTY_CONTAINER_RECORDED'
              AND data_json->>'idempotency_key' = @key
            """, connection, transaction))
        {
            replay.Parameters.AddWithValue("location", command.LocationId);
            replay.Parameters.AddWithValue("key", key);
            if (await replay.ExecuteScalarAsync(cancellationToken) is not null)
            {
                var existing = await ReadLocationsAsync(connection, transaction, command.LocationId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return existing.Single();
            }
        }

        Guid unitId;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ops.store_units (store_location_id, load_state)
            SELECT id, 'EMPTY' FROM ops.store_locations
            WHERE id = @location AND is_enabled
            RETURNING id
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("location", command.LocationId);
            if (await insert.ExecuteScalarAsync(cancellationToken) is not Guid created)
            {
                throw new WorkflowRuleException("STORE_LOCATION_NOT_FOUND", "Depo konumu bulunamadı veya kapalı.");
            }

            unitId = created;
        }

        await InsertStoreEventAsync(connection, transaction, command.LocationId, unitId, "EMPTY_CONTAINER_RECORDED",
            partId: null, containerTypeId: null, transportRequestId: null,
            command.ActorUserId, command.DeviceId, new { idempotency_key = key }, cancellationToken);
        var refreshed = await ReadLocationsAsync(connection, transaction, command.LocationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return refreshed.Single();
    }

    private static async Task<List<StoreLocationRecord>> ReadLocationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid? locationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT l.id, l.code, l.display_name, l.capacity, l.is_enabled,
                   u.id, u.transport_request_id, r.request_no, u.part_id, p.part_code,
                   u.container_type_id, ct.code, u.machine_id, m.code,
                   u.delivered_at, u.row_version, u.load_state
            FROM ops.store_locations l
            LEFT JOIN ops.store_units u
                   ON u.store_location_id = l.id AND u.removed_at IS NULL
            LEFT JOIN ops.transport_requests r ON r.id = u.transport_request_id
            LEFT JOIN master.parts p ON p.id = u.part_id
            LEFT JOIN master.container_types ct ON ct.id = u.container_type_id
            LEFT JOIN master.machines m ON m.id = u.machine_id
            WHERE (@location::uuid IS NULL OR l.id = @location)
            ORDER BY l.code, u.delivered_at
            """, connection, transaction);
        AddNullableUuid(command, "location", locationId);

        var locations = new List<StoreLocationRecord>();
        var units = new Dictionary<Guid, List<StoreUnitRecord>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            if (!units.ContainsKey(id))
            {
                units[id] = [];
                locations.Add(new StoreLocationRecord(
                    Id: id,
                    Code: reader.GetString(1),
                    DisplayName: reader.GetString(2),
                    Capacity: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    IsEnabled: reader.GetBoolean(4),
                    Units: units[id]));
            }

            if (reader.IsDBNull(5)) continue;
            units[id].Add(new StoreUnitRecord(
                Id: reader.GetGuid(5),
                TransportRequestId: reader.IsDBNull(6) ? null : reader.GetGuid(6),
                RequestNo: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                PartId: reader.IsDBNull(8) ? null : reader.GetGuid(8),
                PartCode: reader.IsDBNull(9) ? null : reader.GetString(9),
                ContainerTypeId: reader.IsDBNull(10) ? null : reader.GetGuid(10),
                ContainerCode: reader.IsDBNull(11) ? null : reader.GetString(11),
                MachineId: reader.IsDBNull(12) ? null : reader.GetGuid(12),
                MachineCode: reader.IsDBNull(13) ? null : reader.GetString(13),
                DeliveredAt: reader.GetFieldValue<DateTimeOffset>(14),
                RowVersion: reader.GetInt64(15),
                LoadState: reader.IsDBNull(16) ? "FULL" : reader.GetString(16)));
        }

        return locations;
    }

    private static async Task InsertStoreEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid locationId,
        Guid? storeUnitId, string eventType, Guid? partId, Guid? containerTypeId,
        Guid? transportRequestId, Guid? actorUserId, Guid? deviceId, object data,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ops.store_events
                (store_location_id, store_unit_id, event_type, part_id, container_type_id,
                 transport_request_id, actor_user_id, device_id, data_json)
            VALUES (@location, @unit, @type, @part, @container, @request, @actor, @device, @data)
            """, connection, transaction);
        command.Parameters.AddWithValue("location", locationId);
        AddNullableUuid(command, "unit", storeUnitId);
        command.Parameters.AddWithValue("type", eventType);
        AddNullableUuid(command, "part", partId);
        AddNullableUuid(command, "container", containerTypeId);
        AddNullableUuid(command, "request", transportRequestId);
        AddNullableUuid(command, "actor", actorUserId);
        AddNullableUuid(command, "device", deviceId);
        command.Parameters.Add("data", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(data);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateFinishedGoodsActorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid actorId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM auth.users u
            WHERE u.id = @actor AND u.is_active
              AND EXISTS (SELECT 1 FROM audit.client_devices d WHERE d.id = @device AND d.is_active)
              AND EXISTS (SELECT 1 FROM auth.user_roles ur JOIN auth.roles r ON r.id = ur.role_id
                          WHERE ur.user_id = u.id AND r.code IN ('ADMIN','SUPERVISOR','WAREHOUSE_OPERATOR','FINISHED_GOODS_WAREHOUSE_OPERATOR'))
            """, connection, transaction);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("device", deviceId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new WorkflowRuleException(
                "STORE_ACCESS_DENIED", "Kullanıcı veya cihaz bitmiş ürün deposu işlemi yapmaya yetkili değil.");
        }
    }
}
